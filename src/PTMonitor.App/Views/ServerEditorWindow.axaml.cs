using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PTMonitor.App.Services;
using PTMonitor.Core.Client;
using PTMonitor.Core.Diagnostics;
using PTMonitor.Core.Model;
using PTMonitor.Core.Store;

namespace PTMonitor.App.Views;

/// <summary>
/// Add/edit form for one server: validation, Test Connection, and the
/// password hand-off — the password is only ever written to the secret
/// store (DPAPI), never into the persisted <see cref="ServerConfig"/>.
/// </summary>
public partial class ServerEditorWindow : Window
{
    private readonly ServerConfig? _existing;
    private readonly Guid _id;
    private bool _isTesting;
    private bool _loadingUi = true;
    private string _noticeHost = "";
    private IReadOnlyList<EditorNotice> _normalizationNotices = [];

    public ServerEditorWindow(ServerConfig? existing)
    {
        _existing = existing;
        _id = existing?.Id ?? Guid.NewGuid();
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyLocalization();
        Load();
    }

    private void ApplyLocalization()
    {
        HeaderLabel.Text = _existing is null ? L10n.S("Add Server") : L10n.S("Edit Server");
        Title = HeaderLabel.Text;
        TypeLabel.Text = L10n.S("Type");
        NameLabel.Text = L10n.S("Name");
        HostLabel.Text = L10n.S("Host");
        PortLabel.Text = L10n.S("Port");
        TlsToggle.Content = L10n.S("Use HTTPS");
        InsecureToggle.Content = L10n.S("Allow self-signed certificate");
        EnabledToggle.Content = L10n.S("Enabled");
        UserLabel.Text = L10n.S("User name");
        PasswordLabel.Text = L10n.S("Password");
        TestButton.Content = L10n.S("Test Connection");
        CancelButton.Content = L10n.S("Cancel");
        SaveButton.Content = L10n.S("Save");
    }

    private void Load()
    {
        _loadingUi = true;
        _normalizationNotices = [];
        KindPicker.Items.Clear();
        KindPicker.Items.Add(ServerKind.Qbittorrent.DisplayName());
        KindPicker.Items.Add(ServerKind.Transmission.DisplayName());

        if (_existing is null)
        {
            KindPicker.SelectedIndex = 0;
            NameField.Text = "";
            HostField.Text = "";
            PortField.Text = ServerKind.Qbittorrent.DefaultPort().ToString();
            TlsToggle.IsChecked = true;
            InsecureToggle.IsChecked = false;
            EnabledToggle.IsChecked = true;
            UserField.Text = "";
            PasswordField.Text = "";
        }
        else
        {
            KindPicker.SelectedIndex = _existing.Kind == ServerKind.Qbittorrent ? 0 : 1;
            NameField.Text = _existing.Name;
            HostField.Text = _existing.Host;
            PortField.Text = _existing.Port.ToString();
            TlsToggle.IsChecked = _existing.UseTls;
            InsecureToggle.IsChecked = _existing.AllowInsecureTls;
            EnabledToggle.IsChecked = _existing.IsEnabled;
            UserField.Text = _existing.Username;
            PasswordField.Text = AppServices.Instance.Secrets.Get(_existing.Id) ?? "";
        }

        _noticeHost = HostField.Text ?? "";
        _loadingUi = false;
        RefreshNotices();
    }

    private ServerKind CurrentKind =>
        KindPicker.SelectedIndex == 1 ? ServerKind.Transmission : ServerKind.Qbittorrent;

    private ServerConfig ProvisionalConfig() => new()
    {
        Id = _id,
        Kind = CurrentKind,
        Name = NameField.Text ?? "",
        Host = HostField.Text ?? "",
        Port = int.TryParse(PortField.Text?.Trim(), out var port) ? port : CurrentKind.DefaultPort(),
        UseTls = TlsToggle.IsChecked == true,
        AllowInsecureTls = InsecureToggle.IsChecked == true,
        Username = UserField.Text ?? "",
        IsEnabled = EnabledToggle.IsChecked == true,
    };

    private EffectiveServerForm EffectiveForm() => ServerConfigValidator.Evaluate(
        ProvisionalConfig(), PortField.Text ?? "", PasswordField.Text);

    /// <summary>Test and Save use exactly the state applied to the visible fields.</summary>
    private EffectiveServerForm? Validate()
    {
        var result = NormalizeForm();
        if (result.Error is { } error)
        {
            ShowMessage(error.Message, isError: true);
            return null;
        }
        HideMessage();
        return result;
    }

    private EffectiveServerForm NormalizeForm()
    {
        var result = EffectiveForm();
        if (result.Error is null)
        {
            _loadingUi = true;
            try
            {
                if (result.Notices.Count > 0) _normalizationNotices = result.Notices;
                _noticeHost = result.Config.Host;
                HostField.Text = result.Config.Host;
                PortField.Text = result.Config.Port.ToString(CultureInfo.InvariantCulture);
                TlsToggle.IsChecked = result.Config.UseTls;
                UserField.Text = result.Config.Username;
                PasswordField.Text = result.Password;
            }
            finally { _loadingUi = false; }
        }
        RefreshNotices();
        return result;
    }

    private void RefreshNotices()
    {
        if (_loadingUi) return;
        var form = EffectiveForm();
        var notices = _normalizationNotices.Concat(form.Notices).Distinct()
            .Select(notice => notice.Port is { } port
                ? L10n.Format(notice.Key, port) : L10n.S(notice.Key)).ToArray();
        var parts = EditorNotices.Compose(notices, form.Config.UseTls, form.Password)
            .Select(L10n.S).ToList();
        NoticeText.IsVisible = parts.Count > 0;
        NoticeText.Text = string.Join("\n", parts);
    }

    private void OnHostChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        if (_noticeHost != (HostField.Text ?? ""))
        {
            _normalizationNotices = [];
            _noticeHost = HostField.Text ?? "";
        }
        RefreshNotices();
    }

    private void OnHostLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_loadingUi) NormalizeForm();
    }

    private void OnNoticeInputChanged(object? sender, RoutedEventArgs e) => RefreshNotices();

    private void ShowMessage(string text, bool isError)
    {
        MessageText.IsVisible = true;
        MessageText.Text = text;
        MessageText.Foreground = isError ? new SolidColorBrush(Color.Parse("#E5484D"))
            : new SolidColorBrush(Color.Parse("#2D7D46"));
    }

    private void HideMessage() => MessageText.IsVisible = false;

    private void OnKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        // Keep the port in step with the type, but never clobber a hand-typed one.
        var knownDefaults = new[]
        {
            ServerKind.Qbittorrent.DefaultPort().ToString(),
            ServerKind.Transmission.DefaultPort().ToString(),
        };
        var text = PortField.Text;
        if (string.IsNullOrEmpty(text) || knownDefaults.Contains(text))
            PortField.Text = CurrentKind.DefaultPort().ToString();
    }

    private async void OnTest(object? sender, RoutedEventArgs e)
    {
        if (_isTesting) return;
        if (Validate() is not { } validated) return;

        _isTesting = true;
        TestButton.Content = L10n.S("Testing…");
        TestButton.IsEnabled = false;
        string? acceptedThumbprint = null;
        void Observe(string thumbprint) => acceptedThumbprint = thumbprint;
        try
        {
            var config = validated.Config;
            var insecure = validated.Config.AllowInsecureTls;
            var password = validated.Password;
            ITorrentClient client = config.Kind == ServerKind.Qbittorrent
                ? new QBittorrentClient(config, password, Observe)
                : new TransmissionClient(config, password, Observe);
            await using (client)
            {
                var version = await client.TestConnectionAsync(CancellationToken.None);
                var message = L10n.Connected(version);
                if (EditorNotices.AcceptedCertificate(insecure, acceptedThumbprint) is { } thumbprint)
                    message += $" · {L10n.Format("cert SHA-256 %@", thumbprint)}";
                ShowMessage(message, isError: false);
            }
        }
        catch (ServerFailureException failure)
        {
            ShowMessage(L10n.Failed(failure.Failure.Message), isError: true);
        }
        catch (Exception exception)
        {
            ShowMessage(L10n.Failed(exception.Message), isError: true);
        }
        finally
        {
            _isTesting = false;
            TestButton.Content = L10n.S("Test Connection");
            TestButton.IsEnabled = true;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Validate() is not { } validated) return;
        var config = validated.Config;
        var password = validated.Password;

        var services = AppServices.Instance;
        try
        {
            // The secret write happens BEFORE the configuration write, so a
            // failed DPAPI write cannot leave an enabled server polling with
            // an empty password (which trips qBittorrent's IP ban). A failure
            // throws; the `false` result only means "empty password, nothing to
            // store", which is not an error.
            _ = services.Secrets.Set(password, config.Id);
        }
        catch (Exception exception)
        {
            // The save is aborted, so nothing reaches settings.json — the only
            // record of why is this line.
            Log.Warn("settings", $"server {config.Name}: password write failed, save aborted: {exception.Message}");
            ShowMessage(L10n.Format("Could not save the password (%@). Nothing was saved.", exception.Message),
                isError: true);
            return;
        }

        if (_existing is null) services.Settings.Add(config);
        else services.Settings.Update(config);

        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
