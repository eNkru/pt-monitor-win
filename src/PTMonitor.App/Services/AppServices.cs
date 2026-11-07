using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

    /// <summary>
    /// How often the desktop attach is retried (every two seconds) before the
    /// widget gives up and stays an always-on-top window.
    /// </summary>
    private const int DesktopAttachAttempts = 30;

    /// <summary>The last certificate thumbprint logged per server, so a steady handshake is not re-logged.</summary>
    private static readonly Dictionary<Guid, string> LoggedCertificates = [];

    public SettingsStore Settings { get; }
    public ISecretStore Secrets { get; }
    public MonitorStore Monitor { get; }

    private readonly TrayIcon _trayIcon;
    private readonly DispatcherTimer _ticker;
    private readonly DispatcherTimer _desktopAttachTimer;
    private MainPanelWindow? _panel;
    private PreferencesWindow? _preferences;
    private FloatingSpeedWindow? _floating;
    private DesktopWidgetWindow? _desktopWidget;
    private MenuBarState? _trayState;
    private bool _quitRequested;
    // How many times the desktop attach was retried after a shell restart.
    private int _desktopAttachAttempts;

    private AppServices()
    {
        // Assign first: the first-run preferences window (now posted, but built
        // from a callback that can run as soon as the loop pumps) reaches back
        // through AppServices.Instance.
        Instance = this;

        Settings = new SettingsStore(AppPaths.SettingsFile);
        // Before BuildMenu below: the menu titles are localized.
        L10n.SetLanguage(Settings.AppLanguage);

        // One sample per second, so the rolling graphs keep moving even while
        // the rates are steady (StatusesChanged only fires on a real change).
        // Shared by the floating chip and the desktop widget.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => PushSamples();

        // The shell can restart and take the desktop host (and the card that
        // lives in it) down with it. The card is then put back into the
        // desktop layer as soon as the host is there again.
        _desktopAttachTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _desktopAttachTimer.Tick += (_, _) => RetryDesktopAttach();

        // The tray icon is the app, so it gets set before everything that can
        // block (DPAPI) or paint (windows). That is the whole launch target.
        _trayIcon = new TrayIcon
        {
            Icon = TrayIconFactory.Icon(TrayState.Idle),
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

        // Poll loops publish from a thread-pool thread; tray updates must
        // reach the UI thread (see .trellis/spec/frontend/state-management.md).
        // The tray glyph is the same in the light and the dark theme -- a
        // bright tile with the arrow knocked out of it -- so an OS theme
        // switch does not change it and needs no redraw hook here.
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
                SyncDesktopWidget();
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
        var desktopWidgetItem = new NativeMenuItem
        {
            Header = L10n.S("Show desktop widget"),
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = Settings.ShowDesktopWidget,
        };
        desktopWidgetItem.Click += (_, _) => Dispatcher.UIThread.Post(
            () => Settings.SetShowDesktopWidget(!Settings.ShowDesktopWidget));
        menu.Items.Add(desktopWidgetItem);
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
        SyncDesktopWidget();
    }

    private void UpdateTray()
    {
        var state = Monitor.MenuBarState;
        // Only touch Icon on a real transition. Reassigning it every poll drew a
        // new bitmap (never disposed) and rebuilt the native icon each time --
        // ~1800/hour at the default 2 s interval -- until CreateIcon failed and
        // the exception took the process down (event log 1026).
        if (state != _trayState)
        {
            // The state is recorded *before* the assignment on purpose: a failed
            // assignment must not be retried on every poll, or the native icon
            // would be rebuilt over and over -- the churn that crashed the app
            // (event log 1026, .trellis/tasks/11-01-tray-icon-crash). The icon
            // is refreshed on the next real transition instead.
            _trayState = state;
            try
            {
                _trayIcon.Icon = TrayIconFactory.Icon(
                    state switch
                    {
                        MenuBarState.Error => TrayState.Error,
                        MenuBarState.Active => TrayState.Active,
                        _ => TrayState.Idle,
                    });
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
        UpdateDesktopWidget();
    }

    private void SyncFloatingWindow()
    {
        if (!Settings.ShowFloatingSpeeds)
        {
            if (_floating is { IsVisible: true } visible) visible.Hide();
            SyncTicker();
            return;
        }

        if (_floating is null) _floating = new FloatingSpeedWindow();
        if (!_floating.IsVisible) _floating.Show();
        else _floating.RefreshLocalization();

        _floating.SetDisplayMode(Settings.FloatingDisplay);
        SyncTicker();
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

    /// <summary>
    /// The one-second sample tick: the floating chip's graph and the desktop
    /// widget's graph both need it. It runs while either one is on screen.
    /// </summary>
    private void SyncTicker()
    {
        var chipGraph = _floating is { IsVisible: true } && Settings.FloatingDisplay == FloatingDisplayMode.Graph;
        var widget = _desktopWidget is { IsVisible: true };
        if (!chipGraph && !widget)
        {
            _ticker.Stop();
            return;
        }
        if (_ticker.IsEnabled) return;
        PushSamples();
        _ticker.Start();
    }

    private void PushSamples()
    {
        PushFloatingSample();
        PushDesktopSample();
    }

    // MARK: - Desktop widget

    /// <summary>
    /// Shows or hides the desktop widget, following the setting. The widget is
    /// a second, independent surface: the floating chip is untouched by it.
    /// </summary>
    private void SyncDesktopWidget()
    {
        if (!Settings.ShowDesktopWidget)
        {
            if (_desktopWidget is { IsVisible: true } visible) visible.Hide();
            SyncTicker();
            return;
        }

        if (_desktopWidget is null)
        {
            _desktopWidget = new DesktopWidgetWindow();
            // An Explorer restart kills the desktop host and the card with it.
            _desktopWidget.Closed += (_, _) => OnDesktopWidgetClosed();
        }
        if (!_desktopWidget.IsVisible)
        {
            // Place it before it appears, so the card never flashes in the
            // screen corner.
            _desktopWidget.PreparePlacement();
            _desktopWidget.Show();
        }
        else _desktopWidget.RefreshLocalization();

        _desktopWidget.SetLocked(Settings.DesktopWidgetLocked);
        UpdateDesktopWidget();
        SyncTicker();
        SyncDesktopAttach();
    }

    /// <summary>
    /// The card's window was closed — the shell restarted and took the desktop
    /// host (and the card with it) down. The setting is still on, so the card
    /// comes back and is attached to the new host.
    /// </summary>
    private void OnDesktopWidgetClosed()
    {
        _desktopWidget = null;
        _desktopAttachTimer.Stop();
        _desktopAttachAttempts = 0;
        // The ticker may have been running for the card alone.
        SyncTicker();
        if (_quitRequested || !Settings.ShowDesktopWidget) return;
        // Recreate on the next dispatcher turn: the shell needs a moment to
        // bring its desktop host back, and the new card looks for it again.
        Dispatcher.UIThread.Post(() =>
        {
            if (_quitRequested || !Settings.ShowDesktopWidget) return;
            SyncDesktopWidget();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Puts the card back into the desktop layer when the shell came back
    /// without it. The attempt is bounded: a machine without a desktop host
    /// must not spin on it.
    /// </summary>
    private void RetryDesktopAttach()
    {
        if (_desktopWidget is not { IsVisible: true } widget || widget.IsInDesktopLayer)
        {
            _desktopAttachTimer.Stop();
            _desktopAttachAttempts = 0;
            return;
        }

        _desktopAttachAttempts++;
        if (widget.TryAttachToDesktop())
        {
            _desktopAttachTimer.Stop();
            _desktopAttachAttempts = 0;
            return;
        }
        if (_desktopAttachAttempts >= DesktopAttachAttempts)
        {
            _desktopAttachTimer.Stop();
            Log.Warn("widget", "the desktop host stayed away; the widget stays on top of other windows");
        }
    }

    /// <summary>
    /// Starts the attach retry when the card is on screen but not in the
    /// desktop layer (a shell restart, or a host that was not there yet).
    /// </summary>
    private void SyncDesktopAttach()
    {
        var needsRetry = DesktopLayer.IsSupported
            && _desktopWidget is { IsVisible: true } widget
            && !widget.IsInDesktopLayer;
        if (!needsRetry)
        {
            _desktopAttachTimer.Stop();
            _desktopAttachAttempts = 0;
            return;
        }
        if (!_desktopAttachTimer.IsEnabled)
        {
            _desktopAttachAttempts = 0;
            _desktopAttachTimer.Start();
        }
    }

    private void UpdateDesktopWidget()
    {
        if (_desktopWidget is not { IsVisible: true } widget) return;
        var aggregate = Monitor.Aggregate;
        widget.ShowRates(aggregate.DownSpeed, aggregate.UpSpeed, aggregate.OnlineServers > 0);
        widget.ShowSummary(Settings.Servers.Count, aggregate.OnlineServers, aggregate.ActiveTorrents);
    }

    private void PushDesktopSample()
    {
        if (_desktopWidget is not { IsVisible: true } widget) return;
        var aggregate = Monitor.Aggregate;
        widget.PushSample(aggregate.DownSpeed, aggregate.UpSpeed);
    }

    /// <summary>
    /// Hides the desktop widget and remembers the choice, so it stays hidden
    /// across restarts. The tray menu toggle brings it back.
    /// </summary>
    public void HideDesktopWidget() => Settings.SetShowDesktopWidget(false);

    private static string ByteRateFormatterSpeedText(MonitorAggregate aggregate) =>
        PTMonitor.Core.Format.ByteRateFormatter.SpeedText(aggregate.DownSpeed, aggregate.UpSpeed);

    // MARK: - Window management

    public void TogglePanel()
    {
        if (_panel is { IsVisible: true } visible)
        {
            visible.HidePanel();
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

    /// <summary>The global cursor position in physical screen pixels, or null
    /// off-Windows (where the panel falls back to edge docking).</summary>
    private static PixelPoint? CursorPosition() => DesktopLayer.CursorPosition();

    public void HidePanel()
    {
        if (_panel is { IsVisible: true } panel) panel.HidePanel();
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
        _ticker.Stop();
        _desktopAttachTimer.Stop();
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
