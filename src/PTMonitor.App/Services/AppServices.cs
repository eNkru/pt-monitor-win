using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
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

    public SettingsStore Settings { get; }
    public ISecretStore Secrets { get; }
    public MonitorStore Monitor { get; }

    private readonly TrayIcon _trayIcon;
    private MainPanelWindow? _panel;
    private PreferencesWindow? _preferences;
    private FloatingSpeedWindow? _floating;
    private bool _quitRequested;

    private AppServices()
    {
        // Assign first: the first-run preferences window opens synchronously from
        // this constructor and reaches back through AppServices.Instance.
        Instance = this;

        Settings = new SettingsStore(AppPaths.SettingsFile);
        Secrets = new DpapiSecretStore(AppPaths.SecretsFile);
        Monitor = new MonitorStore(Settings, Secrets);

        L10n.SetLanguage(Settings.AppLanguage);
        Settings.Changed += OnSettingsChanged;

        _trayIcon = new TrayIcon
        {
            Icon = TrayIconFactory.Idle(),
            ToolTipText = "PT Monitor",
            Menu = BuildMenu(),
        };
        TrayIcon.SetIcons(GetApp(), [_trayIcon]);

        // Poll loops publish from a thread-pool thread; tray updates must
        // reach the UI thread (see .trellis/spec/frontend/state-management.md).
        Monitor.StatusesChanged += (_, _) => Dispatcher.UIThread.Post(UpdateTray);
        UpdateTray();
        Monitor.Start();

        // Left-click the tray icon to toggle the dropdown panel (right click
        // opens the native menu above).
        _trayIcon.Clicked += (_, _) => TogglePanel();

        SyncFloatingWindow();

        if (Settings.Servers.Count == 0)
        {
            // First run: open the preferences directly (AC4).
            ShowPreferences();
        }
    }

    public static void Initialize()
    {
        if (Instance is not null) return; // idempotent
        _ = new AppServices();
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

    private void UpdateTray()
    {
        var state = Monitor.MenuBarState;
        _trayIcon.Icon = state switch
        {
            MenuBarState.Error => TrayIconFactory.Error(),
            MenuBarState.Active => TrayIconFactory.Active(),
            _ => TrayIconFactory.Idle(),
        };

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
            return;
        }

        if (_floating is null) _floating = new FloatingSpeedWindow();
        if (!_floating.IsVisible) _floating.Show();
        else _floating.RefreshLocalization();
        UpdateFloatingRates();
    }

    private void UpdateFloatingRates()
    {
        if (_floating is not { IsVisible: true } floating) return;
        var aggregate = Monitor.Aggregate;
        floating.ShowRates(aggregate.DownSpeed, aggregate.UpSpeed, aggregate.OnlineServers > 0);
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

    public void Quit()
    {
        _quitRequested = true;
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
