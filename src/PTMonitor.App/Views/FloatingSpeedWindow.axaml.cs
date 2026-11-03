using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PTMonitor.App.Services;
using PTMonitor.Core.Format;
using PTMonitor.Core.Model;

namespace PTMonitor.App.Views;

/// <summary>
/// Optional always-on-top chip with the aggregate down/up rates. A click opens
/// the panel, a drag moves the chip and persists the new position, a
/// right-click opens a menu (web console / hide / quit).
/// </summary>
public partial class FloatingSpeedWindow : Window
{
    private const double EdgeGap = 12;
    private const double DragThreshold = 4;

    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private bool _pressed;
    private bool _dragged;

    // The menu is built in code: the first item names the server it opens and
    // is disabled when there is none, which XAML cannot express.
    private readonly MenuItem _openWebConsoleItem = new();
    private readonly MenuItem _hideItem = new();
    private readonly MenuItem _quitItem = new();

    public FloatingSpeedWindow()
    {
        InitializeComponent();
        // The system hand cursor: the OS owns the shape, the app does not.
        Cursor = new Cursor(StandardCursorType.Hand);
        BuildMenu();
    }

    private void BuildMenu()
    {
        _openWebConsoleItem.Click += (_, _) => AppServices.Instance.OpenFirstServerWebConsole();
        _hideItem.Click += (_, _) => AppServices.Instance.HideFloatingSpeedWindow();
        _quitItem.Click += (_, _) => AppServices.Instance.Quit();
        Chip.ContextMenu = new ContextMenu
        {
            ItemsSource = new object[] { _openWebConsoleItem, _hideItem, new Separator(), _quitItem },
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyLocalization();
        Place();
    }

    /// <summary>Re-applies the localized title, tooltip and menu after a language change.</summary>
    public void RefreshLocalization() => ApplyLocalization();

    /// <summary>Writes the aggregate rates; an offline aggregate renders a dash.</summary>
    public void ShowRates(long down, long up, bool online)
    {
        var downText = online ? ByteRateFormatter.Rate(down) : "—";
        var upText = online ? ByteRateFormatter.Rate(up) : "—";
        DownText.Text = downText;
        UpText.Text = upText;
        // The graph-mode corner readout mirrors the numbers-mode readout.
        GraphDownText.Text = downText;
        GraphUpText.Text = upText;
    }

    /// <summary>Switches between the numeric readout and the rolling graph.</summary>
    public void SetDisplayMode(FloatingDisplayMode mode)
    {
        var graph = mode == FloatingDisplayMode.Graph;
        NumbersView.IsVisible = !graph;
        Graph.IsVisible = graph;
        GraphRates.IsVisible = graph;
    }

    /// <summary>Appends one sample to the rolling graph.</summary>
    public void PushSample(long down, long up) => Graph.Push(down, up);

    // MARK: - Gesture

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _dragged = false;
        _pressPoint = e.GetPosition(this);
        _pressArgs = e;
        // Capture on the chip so moves still arrive if the press landed on a
        // child (a digit or the graph) or the cursor leaves the square.
        e.Pointer.Capture(Chip);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || _dragged) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        // Hand the press to the window manager; the move loop owns the pointer
        // until the drag ends, so the click path is skipped.
        _dragged = true;
        _pressed = false;
        if (_pressArgs is { } args) BeginMoveDrag(args);
        SavePosition();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasClick = _pressed && !_dragged;
        if (_dragged) SavePosition();
        _pressed = false;
        _dragged = false;
        _pressArgs = null;
        e.Pointer.Capture(null);
        if (wasClick) AppServices.Instance.ShowPanel();
    }

    // MARK: - Placement

    private void Place()
    {
        var screens = Screens?.All;
        var settings = AppServices.Instance.Settings;

        var target = screens?.FirstOrDefault(screen =>
                settings.FloatingPositionX is { } x
                && settings.FloatingPositionY is { } y
                && screen.WorkingArea.Contains(new PixelPoint(x, y)))
            ?? Screens?.Primary;

        var workArea = target?.WorkingArea ?? new PixelRect(0, 0, 1920, 1040);
        var scaling = RenderScaling;
        var gap = EdgeGap * scaling;

        // SizeToContent leaves Bounds empty until the first layout pass, so use
        // a fallback and scale the logical size to physical pixels.
        var width = (Bounds.Width > 1 ? Bounds.Width : 56) * scaling;
        var height = (Bounds.Height > 1 ? Bounds.Height : 56) * scaling;

        double x, y;
        if (settings.FloatingPositionX is { } savedX && settings.FloatingPositionY is { } savedY
            && target is not null)
        {
            x = savedX;
            y = savedY;
        }
        else
        {
            x = workArea.Right - width - gap;
            y = workArea.Bottom - height - gap;
        }

        var minX = workArea.X + gap;
        var maxX = Math.Max(minX, workArea.Right - width - gap);
        var minY = workArea.Y + gap;
        var maxY = Math.Max(minY, workArea.Bottom - height - gap);
        Position = new PixelPoint((int)Math.Clamp(x, minX, maxX), (int)Math.Clamp(y, minY, maxY));
    }

    private void SavePosition() =>
        AppServices.Instance.Settings.SetFloatingPosition(Position.X, Position.Y);

    private void ApplyLocalization()
    {
        Title = L10n.S("PT Monitor speeds");
        ToolTip.SetTip(Chip, L10n.S("Open PT Monitor"));
        // Glyphs, not words — taken from the formatter so they cannot drift
        // from the panel's arrows.
        DownArrow.Text = ByteRateFormatter.DownArrow;
        UpArrow.Text = ByteRateFormatter.UpArrow;
        GraphDownArrow.Text = ByteRateFormatter.DownArrow;
        GraphUpArrow.Text = ByteRateFormatter.UpArrow;
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
        _hideItem.Header = L10n.S("Hide floating speed window");
        _quitItem.Header = L10n.S("Quit PT Monitor");
    }
}
