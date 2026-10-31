using Avalonia.Controls;
using Avalonia.Interactivity;
using PTMonitor.App.Services;
using PTMonitor.Core.Model;

namespace PTMonitor.App.Views;

public sealed class ServerRowItem
{
    public required ServerConfig Server { get; init; }
    public Guid Id => Server.Id;
    public bool IsEnabled
    {
        get => Server.IsEnabled;
        set { }
    }
    public string DisplayName => Server.DisplayName;
    public string Endpoint => $"{(Server.UseTls ? "https" : "http")}://{Server.EndpointDescription} · {Server.Kind.DisplayName()}";
}

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyLocalization();
        Reload();
    }

    private bool _loadingUi;

    private void ApplyLocalization()
    {
        _loadingUi = true;
        try
        {
            Title = L10n.S("PT Monitor Preferences");
            ServersLabel.Text = L10n.S("Servers");
            EmptyHelp.Text = L10n.S("No servers configured. Add a qBittorrent or Transmission server to start monitoring.");
            AddServerButton.Content = L10n.S("Add Server…");
            ShowSpeedsToggle.Content = L10n.S("Show speeds in the tray tooltip");
            FloatingSpeedsToggle.Content = L10n.S("Show floating speed window");
            ListModeLabel.Text = L10n.S("Torrents shown in the panel");
            ModeAll.Content = L10n.ListMode(TorrentListMode.All);
            ModeDown.Content = L10n.ListMode(TorrentListMode.Downloading);
            ModeUp.Content = L10n.ListMode(TorrentListMode.Uploading);
            ModeActive.Content = L10n.ListMode(TorrentListMode.Active);
            LanguageLabel.Text = L10n.S("Language");

            // Drop the selection before clearing, or the selection model trips
            // over the removed index.
            LanguagePicker.SelectedIndex = -1;
            LanguagePicker.Items.Clear();
            LanguagePicker.Items.Add(new LanguageItem(AppLanguage.System, L10n.S("Follow System")));
            LanguagePicker.Items.Add(new LanguageItem(AppLanguage.English, "English"));
            LanguagePicker.Items.Add(new LanguageItem(AppLanguage.Chinese, "简体中文"));
            LanguagePicker.SelectedIndex = (int)AppServices.Instance.Settings.AppLanguage;
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void UpdatePollLabel()
    {
        PollLabel.Text = string.Format(
            L10n.S("Poll every %lld seconds").Replace("%lld", "{0}"),
            (int)AppServices.Instance.Settings.PollInterval);
    }

    private void Reload()
    {
        var services = AppServices.Instance;
        var rows = services.Settings.Servers
            .Select(server => new ServerRowItem { Server = server })
            .ToList();
        ServerList.ItemsSource = rows;
        EmptyHelp.IsVisible = rows.Count == 0;
        ServerList.IsVisible = rows.Count > 0;

        PollLabel.Text = string.Format(
            L10n.S("Poll every %lld seconds").Replace("%lld", "{0}"),
            (int)services.Settings.PollInterval);
        PollInterval.Value = (int)services.Settings.PollInterval;
        ShowSpeedsToggle.IsChecked = services.Settings.ShowSpeedsInMenuBar;
        FloatingSpeedsToggle.IsChecked = services.Settings.ShowFloatingSpeeds;
        var mode = services.Settings.TorrentListMode;
        ModeAll.IsChecked = mode == TorrentListMode.All;
        ModeDown.IsChecked = mode == TorrentListMode.Downloading;
        ModeUp.IsChecked = mode == TorrentListMode.Uploading;
        ModeActive.IsChecked = mode == TorrentListMode.Active;
        LanguagePicker.SelectedIndex = (int)services.Settings.AppLanguage;
    }

    // MARK: - Server actions

    private void OnAddServer(object? sender, RoutedEventArgs e) => OpenEditor(null);

    private void OnEditServer(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ServerRowItem row) OpenEditor(row.Server);
    }

    private void OpenEditor(ServerConfig? existing)
    {
        var editor = new ServerEditorWindow(existing);
        editor.ShowDialog(this);
        editor.Closed += (_, _) => Reload();
    }

    private void OnToggleServer(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ServerRowItem row) return;
        AppServices.Instance.Settings.SetEnabled(!row.Server.IsEnabled, row.Server.Id);
        Reload();
    }

    private void OnDeleteServer(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ServerRowItem row) return;
        AppServices.Instance.Settings.Delete(row.Server.Id);
        AppServices.Instance.Secrets.Delete(row.Server.Id);
        Reload();
    }

    // MARK: - Preferences

    private void OnPollIntervalChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (IsLoaded is false) return;
        if (e.NewValue is { } value) AppServices.Instance.Settings.SetPollInterval((double)value);
        UpdatePollLabel();
    }

    private void OnShowSpeedsToggled(object? sender, RoutedEventArgs e)
    {
        AppServices.Instance.Settings.SetShowSpeedsInMenuBar(ShowSpeedsToggle.IsChecked == true);
    }

    private void OnFloatingSpeedsToggled(object? sender, RoutedEventArgs e)
    {
        AppServices.Instance.Settings.SetShowFloatingSpeeds(FloatingSpeedsToggle.IsChecked == true);
    }

    private void OnListModeClicked(object? sender, RoutedEventArgs e)
    {
        var mode = sender switch
        {
            _ when sender == ModeAll => TorrentListMode.All,
            _ when sender == ModeDown => TorrentListMode.Downloading,
            _ when sender == ModeUp => TorrentListMode.Uploading,
            _ when sender == ModeActive => TorrentListMode.Active,
            _ => TorrentListMode.All,
        };
        AppServices.Instance.Settings.SetTorrentListMode(mode);
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        if (LanguagePicker.SelectedItem is not LanguageItem item) return;
        // Applies immediately: every window re-localizes when it next opens.
        AppServices.Instance.Settings.SetAppLanguage(item.Language);
        L10n.SetLanguage(item.Language);
        ApplyLocalization();
    }

    private sealed record LanguageItem(AppLanguage Language, string Title)
    {
        public override string ToString() => Title;
    }
}
