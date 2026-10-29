using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PTMonitor.App.Services;
using PTMonitor.App.ViewModels;
using PTMonitor.Core.Model;

namespace PTMonitor.App.Views;

/// <summary>
/// The tray dropdown panel: aggregate stats, per-server torrent sections,
/// and the footer actions. Closing (Esc, deactivate, click-away) only hides
/// it — the agent keeps running in the tray.
/// </summary>
public partial class MainPanelWindow : Window
{
    private const double EdgeGap = 12;

    /// <summary>Set before <see cref="Window.Show"/> by the tray-click handler:
    /// the tray icon's screen position (physical pixels), used to anchor the
    /// panel to the actual icon rather than a taskbar corner.</summary>
    public PixelPoint? TrayAnchor { get; set; }

    private PanelViewModel ViewModel { get; }
    private readonly DispatcherTimer _freshnessTimer;

    public MainPanelWindow()
    {
        InitializeComponent();
        var services = AppServices.Instance;
        ViewModel = new PanelViewModel(services.Settings, services.Monitor);
        DataContext = ViewModel;

        _freshnessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _freshnessTimer.Tick += (_, _) => ViewModel.RefreshFreshness();

        Deactivated += (_, _) => Hide();
        Opened += (_, _) => Dispatcher.UIThread.Post(PositionNearTray, DispatcherPriority.Background);
        KeyDown += OnKeyDown;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyLocalization();
        ViewModel.Rebuild();
        _freshnessTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _freshnessTimer.Stop();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Cancel) Hide();
    }

    private void PositionNearTray()
    {
        // Avalonia cannot report the tray icon's coordinates, so two sources
        // are used: the taskbar location (the screen whose WorkingArea is inset
        // relative to its Bounds carries the taskbar) decides which side of the
        // taskbar the panel opens on, and TrayAnchor (the cursor at click time)
        // decides the vertical position, so a top-pinned icon opens the panel
        // beside the top, not the bottom.
        var screens = Screens?.All;
        var taskbarScreen = screens?.FirstOrDefault(screen => screen.WorkingArea != screen.Bounds)
            ?? Screens?.Primary;

        var bounds = taskbarScreen?.Bounds ?? new PixelRect(0, 0, 1920, 1040);
        var workArea = taskbarScreen?.WorkingArea ?? bounds;

        var scaling = RenderScaling;
        var gap = EdgeGap;

        // SizeToContent="Height" leaves Height as NaN until the first layout
        // pass, so read the rendered size from Bounds (logical pixels) and
        // fall back before layout runs. Position is in physical pixels, so
        // scale the panel size up to match.
        var panelWidth = (Bounds.Width > 1 ? Bounds.Width : 400) * scaling;
        var panelHeight = (Bounds.Height > 1 ? Bounds.Height : 360) * scaling;

        var bottom = workArea.Bottom < bounds.Bottom;
        var top = workArea.Y > bounds.Y;
        var left = workArea.X > bounds.X;
        var right = !bottom && !top && !left;

        double x, y;
        if (TrayAnchor is { } anchor)
        {
            // Open on the work-area side of the taskbar, vertically centered on
            // the icon. Clamping below keeps it on-screen if the icon is at an
            // extreme edge.
            if (right) x = anchor.X - panelWidth - gap;
            else if (left) x = anchor.X + gap;
            else x = anchor.X - panelWidth / 2;
            y = anchor.Y - panelHeight / 2;
        }
        else
        {
            // No anchor (e.g. non-Windows): dock to the tray end of the taskbar.
            if (bottom) { x = bounds.Right - panelWidth - gap; y = workArea.Bottom - panelHeight - gap; }
            else if (top) { x = bounds.Right - panelWidth - gap; y = workArea.Y + gap; }
            else if (left) { x = workArea.X + gap; y = workArea.Bottom - panelHeight - gap; }
            else { x = workArea.Right - panelWidth - gap; y = workArea.Bottom - panelHeight - gap; }
        }

        // Keep the panel inside the work area (never over the taskbar or off-screen).
        var minX = workArea.X + gap;
        var maxX = Math.Max(minX, workArea.Right - panelWidth - gap);
        var minY = workArea.Y + gap;
        var maxY = Math.Max(minY, workArea.Bottom - panelHeight - gap);
        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        Position = new PixelPoint((int)x, (int)y);
    }

    // MARK: - Localization (applied on every open, so a language change in
    // Preferences is visible the next time the panel opens).

    private void ApplyLocalization()
    {
        DownLabel.Text = L10n.S("↓ Speed");
        UpLabel.Text = L10n.S("↑ Speed");
        ActiveLabel.Text = L10n.S("Active Torrents");
        FreeLabel.Text = L10n.S("Free Space");
        ModeAll.Content = L10n.ListMode(TorrentListMode.All);
        ModeDown.Content = L10n.ListMode(TorrentListMode.Downloading);
        ModeUp.Content = L10n.ListMode(TorrentListMode.Uploading);
        ModeActive.Content = L10n.ListMode(TorrentListMode.Active);
        EmptyText.Text = L10n.S("No servers configured");
        AddServerButton.Content = L10n.S("Add Server…");
        ((TextBlock)((StackPanel)RefreshButton.Content!).Children[1]).Text = L10n.S("Refresh Now");
        ((TextBlock)((StackPanel)PreferencesButton.Content!).Children[1]).Text = L10n.S("Preferences…");
        ((TextBlock)((StackPanel)QuitButton.Content!).Children[1]).Text = L10n.S("Quit PT Monitor");
        SyncModeSelection();
    }

    private void SyncModeSelection()
    {
        var mode = AppServices.Instance.Settings.TorrentListMode;
        ModeAll.IsChecked = mode == TorrentListMode.All;
        ModeDown.IsChecked = mode == TorrentListMode.Downloading;
        ModeUp.IsChecked = mode == TorrentListMode.Uploading;
        ModeActive.IsChecked = mode == TorrentListMode.Active;
    }

    // MARK: - Actions

    private void OnModeClicked(object? sender, RoutedEventArgs e)
    {
        var mode = sender switch
        {
            _ when sender == ModeAll => TorrentListMode.All,
            _ when sender == ModeDown => TorrentListMode.Downloading,
            _ when sender == ModeUp => TorrentListMode.Uploading,
            _ when sender == ModeActive => TorrentListMode.Active,
            _ => TorrentListMode.All,
        };
        ViewModel.SelectMode(mode);
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The web-UI button consumes its own press; it must not toggle the row.
        if (e.Source is Button) return;
        if ((sender as Avalonia.Controls.Control)?.DataContext is not TorrentRowViewModel row) return;
        ViewModel.ToggleExpanded(row);
    }

    private void OnOpenWebUi(object? sender, RoutedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Control)?.DataContext is not TorrentRowViewModel row) return;
        OpenUrl(row.WebUiUri);
        e.Handled = true; // the button's click must not also toggle the row
    }

    private static void OpenUrl(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // An unusable host silently does nothing (the row itself still works).
        }
    }

    private void OnAddServer(object? sender, RoutedEventArgs e) => AppServices.Instance.ShowPreferences();

    private void OnRefreshNow(object? sender, RoutedEventArgs e)
    {
        AppServices.Instance.RefreshNow();
        ViewModel.Rebuild();
    }

    private void OnPreferences(object? sender, RoutedEventArgs e) => AppServices.Instance.ShowPreferences();

    private void OnQuit(object? sender, RoutedEventArgs e) => AppServices.Instance.Quit();
}
