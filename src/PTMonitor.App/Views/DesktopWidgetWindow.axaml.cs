using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PTMonitor.App.Services;
using PTMonitor.Core.Diagnostics;
using PTMonitor.Core.Format;

namespace PTMonitor.App.Views;

/// <summary>
/// The desktop widget: a small card that lives on the Windows desktop, behind
/// every application window. It shows the aggregate down/up rates, the rolling
/// two-series graph and a summary line. It can be dragged anywhere, and the
/// lock in its header freezes the position — the rates, the graph, the
/// double-click shortcut and the context menu keep working while it is locked.
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
    private const double CardWidth = 248;
    private const double CardHeight = 152;
    // The corner radius, in logical pixels. It is also the radius of the
    // window region that rounds the window itself (see AttachToDesktop): a
    // layered window cannot be a child of the desktop host, so the corners
    // are cut out of an opaque window instead.
    private const double CardRadius = 14;

    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private bool _pressed;
    private bool _dragged;
    // The last completed click, so the next one can be paired with it.
    private ulong? _lastClickTime;
    private Point _lastClickPoint;

    // The desktop layer: the card is a child of the shell's desktop host.
    private IntPtr _handle;
    private IntPtr _host;
    private bool _attached;

    // A child window is not moved by the window manager, so the card follows
    // the cursor itself: the press-time cursor and the press-time screen
    // position anchor the drag.
    private bool _dragging;
    private PixelPoint _pressCursor;
    private PixelPoint _dragOrigin;

    // The card's screen position in physical pixels (the saved position).
    private PixelPoint _screen;

    private bool _locked;
    // The fallback reason is logged once, not on every retry.
    private bool _fallbackLogged;

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
        LockButton.Content = text;
        ToolTip.SetTip(LockButton, text);
        RefreshMenu();
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
        // The cursor position anchors the manual drag of a card that lives in
        // the desktop layer.
        _pressCursor = DesktopLayer.CursorPosition() ?? default;
        _dragOrigin = _screen;
        // Capture on the card so the moves still arrive if the press landed on
        // a child (a digit or the graph) or the cursor leaves the card.
        e.Pointer.Capture(Card);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // The lock comes first: a locked card never moves, not even while a
        // drag is already running.
        if (_locked)
        {
            _dragging = false;
            _dragged = false;
            return;
        }
        if (_dragging)
        {
            UpdateDrag();
            return;
        }
        if (!_pressed || _dragged) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (_attached)
        {
            // A child window is not moved by the window manager, so the card
            // follows the cursor itself.
            var cursor = DesktopLayer.CursorPosition();
            if (cursor is not { } position) return;
            var dx = position.X - _pressCursor.X;
            var dy = position.Y - _pressCursor.Y;
            if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
            StartDrag(manual: true);
            // The card follows the cursor from this move on.
            UpdateDrag();
            return;
        }

        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
        // Hand the press to the window manager; the move loop owns the pointer
        // until the drag ends, so the click path is skipped. A drag also
        // breaks a click pair: it is not a click at all.
        StartDrag(manual: false);
        if (_pressArgs is { } args) BeginMoveDrag(args);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDrag = _dragged;
        var wasClick = _pressed && !_dragged;
        _pressed = false;
        _dragged = false;
        _dragging = false;
        _pressArgs = null;
        e.Pointer.Capture(null);
        if (wasDrag) SavePosition();
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
    /// Starts the drag: the click pair is broken, a drag is not a click. A
    /// manual drag (the desktop layer) follows the cursor itself.
    /// </summary>
    private void StartDrag(bool manual)
    {
        _dragged = true;
        _pressed = false;
        _lastClickTime = null;
        _dragging = manual;
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
        if (_attached) return;
        var target = TargetPosition();
        _screen = target;
        Position = target;
    }

    /// <summary>Places the card and puts it into the desktop layer.</summary>
    private void Place()
    {
        var target = TargetPosition();
        _screen = target;

        if (_attached)
        {
            // Already a child of the desktop host: Avalonia's Position would
            // move the card inside the host, so only the placement is redone.
            MoveTo(target.X, target.Y);
            return;
        }

        Position = target;
        AttachToDesktop(target, (int)(CardWidth * RenderScaling), (int)(CardHeight * RenderScaling));
        Log.Info("widget", $"desktop widget placed at {target.X},{target.Y}");
    }

    /// <summary>Whether the card currently lives in the desktop layer.</summary>
    public bool IsInDesktopLayer => _attached;

    /// <summary>
    /// Tries to put the card into the desktop layer again. The shell may have
    /// been restarted (the desktop host died with it) and be back by now.
    /// </summary>
    public bool TryAttachToDesktop()
    {
        if (_attached) return true;
        AttachToDesktop(_screen,
            (int)(CardWidth * RenderScaling), (int)(CardHeight * RenderScaling));
        return _attached;
    }

    /// <summary>
    /// Puts the card into the desktop layer. When the shell's desktop host is
    /// not there (or the attach cannot be verified) the card stays a normal
    /// always-on-top window: the widget is never lost.
    /// </summary>
    private void AttachToDesktop(PixelPoint screen, int width, int height)
    {
        _handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_handle == IntPtr.Zero)
        {
            FallBackToTopmost("no native window handle");
            return;
        }

        // Clicking the card must never activate it or raise it above the app
        // the user is working in.
        DesktopLayer.MakeNonActivating(_handle);

        // The rounded corners of a desktop widget, cut out of an opaque
        // window: the desktop shows through them.
        DesktopLayer.SetRoundedRegion(_handle, width, height, (int)(CardRadius * RenderScaling));

        if (!DesktopLayer.Attach(_handle, out _host))
        {
            FallBackToTopmost("the desktop host is not available");
            return;
        }

        // The attach and the placement happen in the same turn, so the card is
        // never painted between them.
        if (!DesktopLayer.ScreenToHost(_host, screen.X, screen.Y, out var hostX, out var hostY)
            || !DesktopLayer.PlaceInHost(_handle, _host, hostX, hostY, width, height))
        {
            DesktopLayer.Detach(_handle);
            // The card is a top-level window again, so the coordinates are
            // screen ones now: put it back where it was.
            DesktopLayer.PlaceOnScreen(_handle, screen.X, screen.Y, width, height);
            FallBackToTopmost("the card could not be placed in the desktop layer");
            return;
        }

        _attached = true;
        // A child of the desktop host is behind every application window by
        // construction; it must not sit in the topmost band as well.
        Topmost = false;
        DesktopLayer.SetTopmost(_handle, false);
        // A child window inherits the host's DPI context, so the card can end
        // up with a different pixel size than the one asked for: the rounded
        // region is cut again, this time for the size it really has.
        if (DesktopLayer.WindowSize(_handle, out var actualWidth, out var actualHeight))
        {
            DesktopLayer.SetRoundedRegion(_handle, actualWidth, actualHeight,
                (int)(CardRadius * RenderScaling));
        }
        Log.Info("widget", "desktop widget attached to the desktop layer");
    }

    /// <summary>
    /// The card stays usable without the desktop layer: it becomes a normal
    /// always-on-top window, and the log says why.
    /// </summary>
    private void FallBackToTopmost(string reason)
    {
        _attached = false;
        Topmost = true;
        if (_handle != IntPtr.Zero) DesktopLayer.SetTopmost(_handle, true);
        // The reason is logged once: a retry loop would repeat it every few
        // seconds.
        if (_fallbackLogged) return;
        _fallbackLogged = true;
        Log.Warn("widget", $"desktop layer unavailable ({reason}); the widget stays on top of other windows");
    }

    /// <summary>Moves the card to a screen position and keeps it inside the work area.</summary>
    private void MoveTo(int x, int y)
    {
        var workArea = WorkAreaFor(x, y);
        var scaling = RenderScaling;
        var gap = EdgeGap * scaling;
        var minX = workArea.X + gap;
        var maxX = Math.Max(minX, workArea.Right - CardWidth * scaling - gap);
        var minY = workArea.Y + gap;
        var maxY = Math.Max(minY, workArea.Bottom - CardHeight * scaling - gap);
        var clamped = new PixelPoint(
            (int)Math.Clamp(x, minX, maxX),
            (int)Math.Clamp(y, minY, maxY));
        _screen = clamped;

        if (!_attached)
        {
            Position = clamped;
            return;
        }
        if (!DesktopLayer.ScreenToHost(_host, clamped.X, clamped.Y, out var hostX, out var hostY)) return;
        DesktopLayer.PlaceInHost(_handle, _host, hostX, hostY,
            (int)(CardWidth * scaling), (int)(CardHeight * scaling));
    }

    /// <summary>The work area of the screen holding the point, else the primary one.</summary>
    private PixelRect WorkAreaFor(int x, int y)
    {
        var screens = Screens?.All;
        var screen = screens?.FirstOrDefault(candidate =>
            candidate.WorkingArea.Contains(new PixelPoint(x, y)))
            ?? Screens?.Primary
            ?? screens?.FirstOrDefault();
        return screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1040);
    }

    /// <summary>Moves the card with the cursor (the desktop-layer drag).</summary>
    private void UpdateDrag()
    {
        var cursor = DesktopLayer.CursorPosition();
        if (cursor is not { } position) return;
        MoveTo(_dragOrigin.X + (position.X - _pressCursor.X),
            _dragOrigin.Y + (position.Y - _pressCursor.Y));
    }

    /// <summary>Writes the card's position back to the settings (once per drag).</summary>
    private void SavePosition()
    {
        if (_attached)
        {
            AppServices.Instance.Settings.SetDesktopWidgetPosition(_screen.X, _screen.Y);
            return;
        }
        var position = Position;
        AppServices.Instance.Settings.SetDesktopWidgetPosition(position.X, position.Y);
    }

    // MARK: - Localization

    private void ApplyLocalization()
    {
        Title = L10n.S("Desktop widget");
        ToolTip.SetTip(Card, L10n.S("Double-click to open the web console"));
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
