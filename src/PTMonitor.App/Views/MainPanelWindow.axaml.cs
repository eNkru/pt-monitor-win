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
        Opened += (_, _) => PositionNearTray();
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
        // The tray lives in the taskbar; Avalonia cannot ask the icon where it
        // is, so the panel docks to the bottom-right corner of the primary
        // screen's work area — where the Windows tray always is.
        var screen = Screens?.Primary;
        var workArea = screen?.WorkingArea
            ?? new PixelRect(0, 0, (int)(Width * 1.5), (int)(Height + 200));

        var scaling = RenderScaling;
        var width = Width;
        var height = DesiredSize.Height > 0 ? DesiredSize.Height : Height;
        if (double.IsNaN(height) || height < 120) height = 360;

        var x = workArea.X / scaling + (workArea.Width / scaling) - width - EdgeGap;
        var y = workArea.Y / scaling + (workArea.Height / scaling) - height - EdgeGap;
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
