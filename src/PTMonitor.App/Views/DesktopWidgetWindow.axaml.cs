using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    /// Writes the summary line: how many servers are configured and online,
    /// and how many torrents are active.
    /// </summary>
    public void ShowSummary(int servers, int online, int active) =>
        SummaryText.Text = L10n.WidgetSummary(servers, online, active);

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
        // Glyphs, not words — taken from the formatter so they cannot drift
        // from the panel's arrows.
        DownArrow.Text = ByteRateFormatter.DownArrow;
        UpArrow.Text = ByteRateFormatter.UpArrow;
        RefreshMenu();
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
