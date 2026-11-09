using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PTMonitor.App.Services;
using PTMonitor.Core.Diagnostics;
using PTMonitor.Core.Format;

namespace PTMonitor.App.Views;

/// <summary>
/// The desktop widget: a transparent, square readout that lives on the
/// desktop, behind every application window. It shows the aggregate down/up
/// rates, the rolling two-series graph and a summary line, and can be dragged
/// anywhere. The lock in its header freezes the position only — the rates, the
/// graph, the double-click shortcut and the context menu keep working.
/// </summary>
public partial class DesktopWidgetWindow : Window
{
    private const double EdgeGap = 12;
    private const double DragThreshold = 4;
    // Two clicks are one double click when they land in the same spot within
    // this window. The platform double-click time is not used: the card
    // captures the pointer while it is pressed, so the native double-click
    // message cannot be relied on.
    private const ulong DoubleClickIntervalMs = 400;
    private const double DoubleClickRadius = 6;
    // The card's logical size; the window is fixed to it.
    private const double CardWidth = 250;
    private const double CardHeight = 150;
    // The header fades out this long after the pointer leaves, so merely
    // grazing the card does not flash it.
    private const int HeaderExitGraceMs = 250;
    // The fade-in itself. The header only starts taking hits when it has
    // finished fading in, so a press that lands in the corner while the
    // header is still invisible drags the card instead of toggling the lock.
    private const int HeaderFadeInMs = 170;

    // The status dot's colours: neutral when nothing is configured, success
    // while every server is online, danger otherwise.
    private static readonly IBrush NeutralDot = new SolidColorBrush(Color.Parse("#B3FFFFFF"));
    private static readonly IBrush OnlineDot = new SolidColorBrush(Color.Parse("#2D7D46"));
    private static readonly IBrush OfflineDot = new SolidColorBrush(Color.Parse("#E5484D"));

    // The last status counters, so a language change can re-derive the line
    // instead of leaving a stale one behind.
    private int _servers;
    private int _online;
    private int _active;

    // Hides the header after the exit grace; created once, stopped on enter
    // and on the tick.
    private readonly DispatcherTimer _exitTimer;
    // Arms the header's hit testing once the fade-in has finished.
    private readonly DispatcherTimer _revealTimer;
    // Whether the header is supposed to be shown right now: the pointer is
    // over the card and no fade-out is pending.
    private bool _headerShown;

    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private bool _pressed;
    private bool _dragged;
    // The last completed click, so the next one can be paired with it.
    private ulong? _lastClickTime;
    private Point _lastClickPoint;

    // The native window and whether the Win32 setup already ran, so it is
    // done once per window and not on every sample.
    private IntPtr _handle;
    private bool _prepared;
    // The pin failure is logged once, not on every sample.
    private bool _pinLogged;

    private bool _locked;

    // The menu is built in code: the first item names the server it opens and
    // is disabled when there is none, which XAML cannot express.
    private readonly MenuItem _openWebConsoleItem = new();
    private readonly MenuItem _lockItem = new();
    private readonly MenuItem _hideItem = new();
    private readonly MenuItem _quitItem = new();

    public DesktopWidgetWindow()
    {
        InitializeComponent();
        // The system hand cursor: the OS owns the shape, the app does not.
        Cursor = new Cursor(StandardCursorType.Hand);
        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HeaderExitGraceMs) };
        _exitTimer.Tick += (_, _) =>
        {
            _exitTimer.Stop();
            if (IsVisible) HideHeader();
        };
        _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HeaderFadeInMs) };
        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer.Stop();
            // The pointer may have left again during the fade-in; then the
            // header stays inert.
            if (_headerShown && IsVisible) Header.IsHitTestVisible = true;
        };
        Card.PointerEntered += (_, _) => ShowHeader();
        Card.PointerExited += (_, _) =>
        {
            _headerShown = false;
            _exitTimer.Stop();
            _exitTimer.Start();
        };
        BuildMenu();
    }

    private void BuildMenu()
    {
        _openWebConsoleItem.Click += (_, _) => AppServices.Instance.OpenFirstServerWebConsole();
        _lockItem.Click += (_, _) => ToggleLock();
        _hideItem.Click += (_, _) => AppServices.Instance.HideDesktopWidget();
        _quitItem.Click += (_, _) => AppServices.Instance.Quit();
        Card.ContextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                _openWebConsoleItem, _lockItem, _hideItem, new Separator(), _quitItem,
            },
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // A re-shown card opens bare, without a fade from the last value.
        ResetHeader();
        ApplyLocalization();
        SetLocked(AppServices.Instance.Settings.DesktopWidgetLocked);
        Place();
        EnsureDesktopPin();
    }

    /// <summary>Re-applies the localized labels and menu after a language change.</summary>
    public void RefreshLocalization()
    {
        ApplyLocalization();
        SetLocked(_locked);
    }

    /// <summary>Writes the aggregate rates; an offline aggregate renders a dash.</summary>
    public void ShowRates(long down, long up, bool online)
    {
        DownText.Text = online ? ByteRateFormatter.Rate(down) : "—";
        UpText.Text = online ? ByteRateFormatter.Rate(up) : "—";
    }

    /// <summary>
    /// Writes the status line: a dot and one number — not configured, the
    /// active count, or <c>M/N online</c>. The counters are cached so a
    /// language change can re-derive the line.
    /// </summary>
    public void ShowStatus(int servers, int online, int active)
    {
        _servers = servers;
        _online = online;
        _active = active;
        ApplyStatus();
    }

    private void ApplyStatus()
    {
        StatusText.Text = L10n.WidgetStatus(_servers, _online, _active);
        StatusDot.Fill = _servers <= 0
            ? NeutralDot
            : _online >= _servers
                ? OnlineDot
                : OfflineDot;
    }

    /// <summary>Appends one sample to the rolling graph.</summary>
    public void PushSample(long down, long up) => Graph.Push(down, up);

    /// <summary>
    /// Freezes (or releases) the position. The lock covers the position only:
    /// everything else on the card keeps working.
    /// </summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        var text = locked ? L10n.S("Unlock position") : L10n.S("Lock position");
        LockText.Text = text;
        ToolTip.SetTip(LockButton, text);
        RefreshMenu();
    }

    /// <summary>
    /// Keeps the card directly above the desktop and below every application
    /// window. Cheap enough to call on every sample: the z-order only moves on
    /// a shell restart or a show, and a no-op call paints nothing.
    /// </summary>
    public void EnsureDesktopPin()
    {
        if (!DesktopLayer.IsSupported) return;
        _handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_handle == IntPtr.Zero) return;

        if (!_prepared)
        {
            _prepared = true;
            // Clicking the card must never activate it or raise it above the
            // app the user is working in.
            DesktopLayer.MakeNonActivating(_handle);
        }

        if (!DesktopLayer.PinAboveDesktop(_handle))
        {
            // Logged once: a retry would repeat it every second.
            if (_pinLogged) return;
            _pinLogged = true;
            Log.Warn("widget", "the desktop window is not there; the card keeps its normal z-order");
        }
    }

    /// <summary>
    /// Win+D ("Show desktop") minimizes every top-level window. The card
    /// belongs to the desktop, so it comes straight back.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        if (WindowState != WindowState.Minimized) return;
        Dispatcher.UIThread.Post(
            () =>
            {
                // Hidden (the tray toggle) or gone (quit): nothing to restore.
                if (!IsVisible || WindowState != WindowState.Minimized) return;
                WindowState = WindowState.Normal;
                EnsureDesktopPin();
            },
            DispatcherPriority.Background);
    }

    // MARK: - Gesture

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // The lock button has its own handler; a press on it is not a drag.
        if (IsOnLockButton(e.Source)) return;

        _pressed = true;
        _dragged = false;
        _pressPoint = e.GetPosition(this);
        _pressArgs = e;
        // Capture on the card so the moves still arrive if the press landed on
        // a child (a digit or the graph) or the cursor leaves the card.
        e.Pointer.Capture(Card);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || _dragged) return;
        // A locked card never moves: the press is not even a drag.
        if (_locked) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        // Hand the press to the window manager; the move loop owns the pointer
        // until the drag ends, so the click path is skipped. A drag also
        // breaks a click pair: it is not a click at all.
        _dragged = true;
        _pressed = false;
        _lastClickTime = null;
        if (_pressArgs is { } args) BeginMoveDrag(args);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDrag = _dragged;
        var wasClick = _pressed && !_dragged;
        _pressed = false;
        _dragged = false;
        _pressArgs = null;
        e.Pointer.Capture(null);
        if (wasDrag)
        {
            SavePosition();
            // A move can change the card's place in the z-order.
            EnsureDesktopPin();
            return;
        }
        if (!wasClick) return;

        // Two quick clicks in the same spot open the web console in the
        // default browser.
        if (IsDoubleClick(e.Timestamp))
        {
            // A third click must not open a second browser tab.
            _lastClickTime = null;
            AppServices.Instance.OpenFirstServerWebConsole();
            return;
        }
        _lastClickTime = e.Timestamp;
        _lastClickPoint = _pressPoint;
    }

    /// <summary>
    /// Whether the click that just ended pairs with the previous one: same
    /// spot, close enough in time.
    /// </summary>
    private bool IsDoubleClick(ulong timestamp)
    {
        if (_lastClickTime is not { } last) return false;
        if (timestamp < last || timestamp - last > DoubleClickIntervalMs) return false;
        return Distance(_pressPoint, _lastClickPoint) <= DoubleClickRadius;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Whether the press landed on the lock button rather than on the card.</summary>
    private bool IsOnLockButton(object? source) =>
        source is Visual visual && LockButton.IsVisualAncestorOf(visual);

    private void OnLockClicked(object? sender, RoutedEventArgs e) => ToggleLock();

    private void ToggleLock() =>
        AppServices.Instance.Settings.SetDesktopWidgetLocked(!_locked);

    // MARK: - Placement

    /// <summary>
    /// Puts the card where it belongs: the saved position when it still lands
    /// on a screen's work area, otherwise the bottom-right corner of the
    /// primary work area — clamped inside it either way.
    /// </summary>
    private PixelPoint TargetPosition()
    {
        var settings = AppServices.Instance.Settings;
        var screens = Screens?.All;

        // The saved position counts only while it still lands on a screen's
        // work area; otherwise the card is docked to the bottom-right of the
        // primary work area (the rule the floating chip documents).
        var saved = settings.DesktopWidgetPositionX is { } savedX
            && settings.DesktopWidgetPositionY is { } savedY
            ? new PixelPoint(savedX, savedY)
            : (PixelPoint?)null;
        var onScreen = saved is { } point
            && screens?.FirstOrDefault(screen => screen.WorkingArea.Contains(point)) is not null;

        var screen = (saved is { } kept
                ? screens?.FirstOrDefault(candidate => candidate.WorkingArea.Contains(kept))
                : null)
            ?? Screens?.Primary
            ?? screens?.FirstOrDefault();

        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1040);
        var scaling = RenderScaling;
        var gap = EdgeGap * scaling;
        var width = CardWidth * scaling;
        var height = CardHeight * scaling;

        int x, y;
        if (onScreen && saved is { } restored)
        {
            x = restored.X;
            y = restored.Y;
        }
        else
        {
            x = (int)(workArea.Right - width - gap);
            y = (int)(workArea.Bottom - height - gap);
        }

        var minX = workArea.X + gap;
        var maxX = Math.Max(minX, workArea.Right - width - gap);
        var minY = workArea.Y + gap;
        var maxY = Math.Max(minY, workArea.Bottom - height - gap);
        return new PixelPoint(
            (int)Math.Clamp(x, minX, maxX),
            (int)Math.Clamp(y, minY, maxY));
    }

    /// <summary>
    /// Places the card before it is shown, so it never flashes in the screen
    /// corner before it is moved to its place.
    /// </summary>
    public void PreparePlacement()
    {
        var target = TargetPosition();
        Position = target;
    }

    private void Place()
    {
        var target = TargetPosition();
        Position = target;
        Log.Info("widget", $"desktop widget placed at {target.X},{target.Y}");
    }

    /// <summary>Writes the card's position back to the settings (once per drag).</summary>
    private void SavePosition()
    {
        var position = Position;
        AppServices.Instance.Settings.SetDesktopWidgetPosition(position.X, position.Y);
    }

    // MARK: - Localization

    private void ApplyLocalization()
    {
        Title = L10n.S("Desktop widget");
        ToolTip.SetTip(Card, L10n.S("Double-click to open the web console"));
        // The product name is intentionally not localized.
        TitleText.Text = "PT Monitor";
        // The dot colour follows the counters, so a language change cannot
        // leave the line mismatched.
        ApplyStatus();
        // Glyphs, not words — taken from the formatter so they cannot drift
        // from the panel's arrows.
        DownArrow.Text = ByteRateFormatter.DownArrow;
        UpArrow.Text = ByteRateFormatter.UpArrow;
        RefreshMenu();
    }

    // MARK: - Header

    /// <summary>
    /// Fades the header in and makes it clickable. Hovering the card is
    /// tracked by <c>Card</c>, which keeps its hit surface at all times.
    /// </summary>
    private void ShowHeader()
    {
        _exitTimer.Stop();
        _headerShown = true;
        Header.Opacity = 1;
        // The header is not hit-testable yet: it is still fading in, and an
        // invisible lock button must not swallow a drag.
        Header.IsHitTestVisible = false;
        _revealTimer.Stop();
        _revealTimer.Start();
    }

    /// <summary>
    /// Fades the header out and lets presses fall through to the card, so an
    /// invisible header never swallows a drag.
    /// </summary>
    private void HideHeader()
    {
        _exitTimer.Stop();
        _revealTimer.Stop();
        _headerShown = false;
        Header.Opacity = 0;
        Header.IsHitTestVisible = false;
    }

    /// <summary>Hides the header without animating: the style's transition is
    /// dropped for the reset and put back afterwards.</summary>
    private void ResetHeader()
    {
        Header.Transitions = null;
        HideHeader();
        Header.ClearValue(Animatable.TransitionsProperty);
    }

    /// <summary>
    /// Refreshes the menu headers. The first one names the server it opens,
    /// so it follows the configuration and not only the language.
    /// </summary>
    private void RefreshMenu()
    {
        var server = AppServices.Instance.Settings.Servers
            .FirstOrDefault(candidate => candidate.IsEnabled);
        _openWebConsoleItem.Header = server is null
            ? L10n.S("Open web console")
            : L10n.OpenInWebUi(server.DisplayName);
        _openWebConsoleItem.IsEnabled = server is not null;
        _lockItem.Header = _locked ? L10n.S("Unlock position") : L10n.S("Lock position");
        _hideItem.Header = L10n.S("Hide desktop widget");
        _quitItem.Header = L10n.S("Quit PT Monitor");
    }
}
