using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using PTMonitor.Core.Client;
using PTMonitor.Core.Diagnostics;
using PTMonitor.Core.Model;
using PTMonitor.Core.Store;
using PTMonitor.App.Views;

namespace PTMonitor.App.Services;

/// <summary>
/// Composition root: owns the stores, the polling monitor, the tray icon,
/// and the two windows. The app has no main window — the tray icon is the
/// only persistent UI (the Windows equivalent of the macOS menu bar agent).
/// </summary>
public sealed class AppServices
{
    public static AppServices Instance { get; private set; } = null!;

    /// <summary>The last certificate thumbprint logged per server, so a steady handshake is not re-logged.</summary>
    private static readonly Dictionary<Guid, string> LoggedCertificates = [];

    public SettingsStore Settings { get; }
    public ISecretStore Secrets { get; }
    public MonitorStore Monitor { get; }

    private readonly TrayIcon _trayIcon;
    private readonly DispatcherTimer _floatingTicker;
    private MainPanelWindow? _panel;
    private PreferencesWindow? _preferences;
    private FloatingSpeedWindow? _floating;
    private MenuBarState? _trayState;
    private bool? _trayDark;
    private bool _quitRequested;

    private AppServices()
    {
        // Assign first: the first-run preferences window (now posted, but built
        // from a callback that can run as soon as the loop pumps) reaches back
        // through AppServices.Instance.
        Instance = this;

        Settings = new SettingsStore(AppPaths.SettingsFile);
        // Before BuildMenu below: the menu titles are localized.
        L10n.SetLanguage(Settings.AppLanguage);

        // Graph mode samples once a second so the line rolls even while the
        // rates are steady (StatusesChanged only fires on a real change).
        _floatingTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _floatingTicker.Tick += (_, _) => PushFloatingSample();

        // The tray icon is the app, so it gets set before everything that can
        // block (DPAPI) or paint (windows). That is the whole launch target.
        _trayIcon = new TrayIcon
        {
            Icon = TrayIconFactory.Icon(TrayState.Idle, CurrentTheme()),
            ToolTipText = "PT Monitor",
            Menu = BuildMenu(),
        };
        StartupTrace.Mark("glyph");
        TrayIcon.SetIcons(GetApp(), [_trayIcon]);
        StartupTrace.Mark("tray-set");

        // The secret store follows the icon on purpose: DPAPI is the last thing
        // that used to sit in front of a visible tray. Second half of the
        // startup event (Program.cs logs version, OS and the data directory):
        // the store is created here, so its state is only known now.
        Secrets = new DpapiSecretStore(AppPaths.SecretsFile);
        StartupTrace.Mark("stores");
        Log.Info("startup", $"secret store: {Secrets.State}");

        Monitor = new MonitorStore(Settings, Secrets, ClientFactory);

        // The tray glyph follows the taskbar, so it has to be redrawn when the
        // OS switches between light and dark. Avalonia's Win32 backend reports
        // that as WM_SETTINGCHANGE/"ImmersiveColorSet". Next to the icon it
        // affects.
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) => UpdateTray();

        // Poll loops publish from a thread-pool thread; tray updates must
        // reach the UI thread (see .trellis/spec/frontend/state-management.md).
        Monitor.StatusesChanged += (_, _) => Dispatcher.UIThread.Post(UpdateTray);
        // Only now: OnSettingsChanged assigns _trayIcon.Menu and calls through
        // the ticker and the monitor, and all three exist by this point.
        Settings.Changed += OnSettingsChanged;
        UpdateTray();
        Monitor.Start();

        // Left-click the tray icon to toggle the dropdown panel (right click
        // opens the native menu above).
        _trayIcon.Clicked += (_, _) => TogglePanel();

        // Optional windows are off the critical path: posted at Background
        // priority so the icon's first frame and the message loop win the
        // queue. First-run Preferences still opens unconditionally -- the
        // server count is read now, not at construction time.
        Dispatcher.UIThread.Post(
            () =>
            {
                SyncFloatingWindow();
                if (Settings.Servers.Count == 0) ShowPreferences();
                StartupTrace.Mark("windows");
                StartupTrace.Flush();
            },
            DispatcherPriority.Background);
    }

    public static void Initialize()
    {
        if (Instance is not null) return; // idempotent
        _ = new AppServices();
    }

    /// <summary>
    /// Client factory that records an accepted self-signed certificate. Without
    /// it the poll loops would build clients with no observer at all, and the
    /// "allow self-signed certificate" toggle would leave no trace.
    /// </summary>
    private static ITorrentClient ClientFactory(ServerConfig config, string? password)
    {
        void Observe(string thumbprint) => LogCertificate(config, thumbprint);
        return config.Kind == ServerKind.Transmission
            ? new TransmissionClient(config, password, Observe)
            : new QBittorrentClient(config, password, Observe);
    }

    /// <summary>
    /// One line per server and thumbprint: the same certificate on every
    /// handshake is not news, a changed one is.
    /// </summary>
    private static void LogCertificate(ServerConfig config, string thumbprint)
    {
        lock (LoggedCertificates)
        {
            if (LoggedCertificates.TryGetValue(config.Id, out var logged) && logged == thumbprint) return;
            LoggedCertificates[config.Id] = thumbprint;
        }
        Log.Warn("tls", $"{config.Name}: accepted the server certificate {thumbprint}");
    }

    private static Application GetApp() =>
        (Application.Current ?? throw new InvalidOperationException("Application not initialized"));

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        menu.Items.Add(NewItem("PT Monitor", () => TogglePanel(), isEnabled: false));
        menu.Items.Add(NewItem(L10n.S("Refresh Now"), RefreshNow));
        var floatingItem = new NativeMenuItem
        {
            Header = L10n.S("Show floating speed window"),
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = Settings.ShowFloatingSpeeds,
        };
        // Posted, not called inline: the settings write rebuilds this native
        // menu, and doing that while the popup is still dispatching its click
        // is not safe.
        floatingItem.Click += (_, _) => Dispatcher.UIThread.Post(
            () => Settings.SetShowFloatingSpeeds(!Settings.ShowFloatingSpeeds));
        menu.Items.Add(floatingItem);
        menu.Items.Add(NewItem(L10n.S("Preferences…"), ShowPreferences));
        menu.Items.Add(NewMenuItemSeparator());
        menu.Items.Add(NewItem(L10n.S("Quit PT Monitor"), Quit));
        return menu;
    }

    private NativeMenuItem NewItem(string header, Action action, bool isEnabled = true)
    {
        var item = new NativeMenuItem { Header = header, IsEnabled = isEnabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static NativeMenuItemSeparator NewMenuItemSeparator() => new();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (Settings.AppLanguage != L10n.Language)
            L10n.SetLanguage(Settings.AppLanguage);
        // Rebuild the menu so its titles follow the language.
        _trayIcon.Menu = BuildMenu();
        UpdateTray();
        SyncFloatingWindow();
    }

    /// <summary>
    /// The OS light/dark variant. The app requests the default variant, so
    /// this follows the Windows setting -- and the taskbar with it.
    /// </summary>
    private static ThemeVariant CurrentTheme() =>
        Application.Current?.ActualThemeVariant ?? ThemeVariant.Light;

    private void UpdateTray()
    {
        var state = Monitor.MenuBarState;
        var dark = CurrentTheme() == ThemeVariant.Dark;
        // Only touch Icon on a real transition. Reassigning it every poll drew a
        // new bitmap (never disposed) and rebuilt the native icon each time --
        // ~1800/hour at the default 2 s interval -- until CreateIcon failed and
        // the exception took the process down (event log 1026).
        if (state != _trayState || dark != _trayDark)
        {
            // The pair is recorded *before* the assignment on purpose: a failed
            // assignment must not be retried on every poll, or the native icon
            // would be rebuilt over and over -- the churn that crashed the app
            // (event log 1026, .trellis/tasks/11-01-tray-icon-crash). The icon
            // is refreshed on the next real transition instead.
            _trayState = state;
            _trayDark = dark;
            try
            {
                _trayIcon.Icon = TrayIconFactory.Icon(
                    state switch
                    {
                        MenuBarState.Error => TrayState.Error,
                        MenuBarState.Active => TrayState.Active,
                        _ => TrayState.Idle,
                    },
                    dark ? ThemeVariant.Dark : ThemeVariant.Light);
            }
            catch (Exception exception)
            {
                // The icon is cosmetic: keep the previous one and stay alive.
                // A failure here must never terminate the agent.
                Log.Warn("tray", $"tray icon update failed: {exception.Message}");
            }
        }

        var aggregate = Monitor.Aggregate;
        var text = $"PT Monitor — {L10n.ServerCount(Settings.Servers.Count, aggregate.OnlineServers)}";
        if (Settings.ShowSpeedsInMenuBar && aggregate.OnlineServers > 0)
        {
            var speed = ByteRateFormatterSpeedText(aggregate);
            text = $"PT Monitor {speed} — {L10n.ServerCount(Settings.Servers.Count, aggregate.OnlineServers)}";
        }
        _trayIcon.ToolTipText = text;
        UpdateFloatingRates();
    }

    private void SyncFloatingWindow()
    {
        if (!Settings.ShowFloatingSpeeds)
        {
            if (_floating is { IsVisible: true } visible) visible.Hide();
            _floatingTicker.Stop();
            return;
        }

        if (_floating is null) _floating = new FloatingSpeedWindow();
        if (!_floating.IsVisible) _floating.Show();
        else _floating.RefreshLocalization();

        _floating.SetDisplayMode(Settings.FloatingDisplay);
        if (Settings.FloatingDisplay == FloatingDisplayMode.Graph)
        {
            if (!_floatingTicker.IsEnabled)
            {
                PushFloatingSample();
                _floatingTicker.Start();
            }
        }
        else
        {
            _floatingTicker.Stop();
        }

        UpdateFloatingRates();
    }

    private void UpdateFloatingRates()
    {
        if (_floating is not { IsVisible: true } floating) return;
        var aggregate = Monitor.Aggregate;
        floating.ShowRates(aggregate.DownSpeed, aggregate.UpSpeed, aggregate.OnlineServers > 0);
    }

    private void PushFloatingSample()
    {
        if (_floating is not { IsVisible: true } floating) return;
        if (Settings.FloatingDisplay != FloatingDisplayMode.Graph) return;
        var aggregate = Monitor.Aggregate;
        floating.PushSample(aggregate.DownSpeed, aggregate.UpSpeed);
    }

    private static string ByteRateFormatterSpeedText(MonitorAggregate aggregate) =>
        PTMonitor.Core.Format.ByteRateFormatter.SpeedText(aggregate.DownSpeed, aggregate.UpSpeed);

    // MARK: - Window management

    public void TogglePanel()
    {
        if (_panel is { IsVisible: true } visible)
        {
            visible.Hide();
            return;
        }
        ShowPanel();
    }

    /// <summary>Shows and activates the panel (the floating chip always opens it).</summary>
    public void ShowPanel()
    {
        if (_panel is { IsVisible: true } visible)
        {
            visible.Activate();
            return;
        }
        _panel ??= new MainPanelWindow();
        // The cursor is on the tray icon right after the click, so it anchors
        // the panel to the icon's real position (see MainPanelWindow.PositionNearTray).
        _panel.TrayAnchor = CursorPosition();
        _panel.Show();
        _panel.Activate();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>The global cursor position in physical screen pixels, or null
    /// off-Windows (where the panel falls back to edge docking).</summary>
    private static PixelPoint? CursorPosition()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;
    }

    public void HidePanel()
    {
        if (_panel is { IsVisible: true } panel) panel.Hide();
    }

    public void ShowPreferences()
    {
        HidePanel();
        if (_preferences is null)
        {
            _preferences = new PreferencesWindow();
            _preferences.Closed += (_, _) => _preferences = null;
        }
        _preferences.Show();
        _preferences.Activate();
    }

    public void RefreshNow()
    {
        _ = Monitor.RefreshNowAsync();
    }

    /// <summary>
    /// Opens the web console of the first enabled server in the default
    /// browser. The URL is the one the model owns
    /// (<see cref="ServerConfig.WebUiUri"/>): qBittorrent serves it at the
    /// root, Transmission under /transmission/web/.
    /// </summary>
    public void OpenFirstServerWebConsole()
    {
        var server = Settings.Servers.FirstOrDefault(candidate => candidate.IsEnabled);
        if (server is null) return;
        Uri uri;
        try { uri = server.WebUiUri(); }
        catch
        {
            // An unusable host silently does nothing (the menu item stays).
            return;
        }
        UrlLauncher.Open(uri);
    }

    /// <summary>
    /// Hides the floating speed widget and remembers the choice, so it stays
    /// hidden across restarts. The tray menu toggle brings it back.
    /// </summary>
    public void HideFloatingSpeedWindow() => Settings.SetShowFloatingSpeeds(false);

    public void Quit()
    {
        _quitRequested = true;
        _floatingTicker.Stop();
        Monitor.StopAsync();
        if (GetApp().ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public bool CanCloseWithoutQuitting(Window window)
    {
        // Closing a window only hides it; the agent keeps running in the tray.
        return !_quitRequested;
    }
}
