using System.Text.Json;
using System.Text.Json.Serialization;
using PTMonitor.Core.Model;

namespace PTMonitor.Core.Store;

/// <summary>
/// JSON-file-backed settings, stored under <c>%APPDATA%\PTMonitor\settings.json</c>.
/// Passwords are deliberately not part of this file (see <see cref="ISecretStore"/>).
/// </summary>
public sealed class SettingsStore
{
    public const double DefaultPollInterval = 2;
    public const double MinPollInterval = 1;
    public const double MaxPollInterval = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();
    private readonly string _path;
    private List<ServerConfig> _servers = [];
    private double _pollInterval = DefaultPollInterval;
    private bool _showSpeedsInMenuBar = true;
    private bool _showFloatingSpeeds;
    private int? _floatingPositionX;
    private int? _floatingPositionY;
    private TorrentListMode _torrentListMode = TorrentListMode.All;
    private AppLanguage _appLanguage = AppLanguage.System;

    public event EventHandler? Changed;

    public SettingsStore(string path)
    {
        _path = path;
        Load();
    }

    public string FilePath => _path;

    public IReadOnlyList<ServerConfig> Servers
    {
        get { lock (_gate) return _servers.ToList(); }
    }

    public double PollInterval
    {
        get { lock (_gate) return _pollInterval; }
    }

    public bool ShowSpeedsInMenuBar
    {
        get { lock (_gate) return _showSpeedsInMenuBar; }
    }

    public bool ShowFloatingSpeeds
    {
        get { lock (_gate) return _showFloatingSpeeds; }
    }

    public int? FloatingPositionX
    {
        get { lock (_gate) return _floatingPositionX; }
    }

    public int? FloatingPositionY
    {
        get { lock (_gate) return _floatingPositionY; }
    }

    public TorrentListMode TorrentListMode
    {
        get { lock (_gate) return _torrentListMode; }
    }

    public AppLanguage AppLanguage
    {
        get { lock (_gate) return _appLanguage; }
    }

    public ServerConfig? Server(Guid id)
    {
        lock (_gate) return _servers.FirstOrDefault(server => server.Id == id);
    }

    public void ReplaceServers(IEnumerable<ServerConfig> servers)
    {
        lock (_gate)
        {
            var next = servers.ToList();
            if (next.SequenceEqual(_servers)) return;
            _servers = next;
        }
        PersistAndNotify();
    }

    public void Add(ServerConfig server)
    {
        lock (_gate)
        {
            _servers.Add(server);
        }
        PersistAndNotify();
    }

    public void Update(ServerConfig server)
    {
        lock (_gate)
        {
            var index = _servers.FindIndex(existing => existing.Id == server.Id);
            if (index < 0) return;
            _servers[index] = server;
        }
        PersistAndNotify();
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            if (_servers.RemoveAll(server => server.Id == id) == 0) return;
        }
        PersistAndNotify();
    }

    public void SetEnabled(bool enabled, Guid id)
    {
        lock (_gate)
        {
            var server = _servers.FirstOrDefault(existing => existing.Id == id);
            if (server is null || server.IsEnabled == enabled) return;
            server.IsEnabled = enabled;
        }
        PersistAndNotify();
    }

    public void SetPollInterval(double interval)
    {
        var clamped = Clamp(interval);
        lock (_gate)
        {
            if (Math.Abs(clamped - _pollInterval) < 0.0001) return;
            _pollInterval = clamped;
        }
        PersistAndNotify();
    }

    public void SetShowSpeedsInMenuBar(bool enabled)
    {
        lock (_gate)
        {
            if (_showSpeedsInMenuBar == enabled) return;
            _showSpeedsInMenuBar = enabled;
        }
        PersistAndNotify();
    }

    public void SetShowFloatingSpeeds(bool enabled)
    {
        lock (_gate)
        {
            if (_showFloatingSpeeds == enabled) return;
            _showFloatingSpeeds = enabled;
        }
        PersistAndNotify();
    }

    public void SetFloatingPosition(int x, int y)
    {
        lock (_gate)
        {
            if (_floatingPositionX == x && _floatingPositionY == y) return;
            _floatingPositionX = x;
            _floatingPositionY = y;
        }
        PersistAndNotify();
    }

    public void SetTorrentListMode(TorrentListMode mode)
    {
        lock (_gate)
        {
            if (_torrentListMode == mode) return;
            _torrentListMode = mode;
        }
        PersistAndNotify();
    }

    public void SetAppLanguage(AppLanguage language)
    {
        lock (_gate)
        {
            if (_appLanguage == language) return;
            _appLanguage = language;
        }
        PersistAndNotify();
    }

    public static double Clamp(double interval) =>
        Math.Clamp(interval, MinPollInterval, MaxPollInterval);

    private void PersistAndNotify()
    {
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Persist()
    {
        try
        {
            SettingsFile snapshot;
            lock (_gate)
            {
                snapshot = new SettingsFile
                {
                    Servers = _servers,
                    PollInterval = _pollInterval,
                    ShowSpeedsInMenuBar = _showSpeedsInMenuBar,
                    ShowFloatingSpeeds = _showFloatingSpeeds,
                    FloatingPositionX = _floatingPositionX,
                    FloatingPositionY = _floatingPositionY,
                    TorrentListMode = _torrentListMode,
                    AppLanguage = _appLanguage,
                };
            }
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, _path, true);
        }
        catch (Exception)
        {
            // Keep the in-memory value; settings persistence must not crash the app.
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_path), JsonOptions);
            if (file is null) return;
            _servers = file.Servers ?? [];
            _pollInterval = Clamp(file.PollInterval ?? DefaultPollInterval);
            _showSpeedsInMenuBar = file.ShowSpeedsInMenuBar ?? true;
            _showFloatingSpeeds = file.ShowFloatingSpeeds ?? false;
            _floatingPositionX = file.FloatingPositionX;
            _floatingPositionY = file.FloatingPositionY;
            _torrentListMode = file.TorrentListMode ?? TorrentListMode.All;
            _appLanguage = file.AppLanguage ?? AppLanguage.System;
        }
        catch (Exception)
        {
            _servers = [];
            _pollInterval = DefaultPollInterval;
            _showSpeedsInMenuBar = true;
            _showFloatingSpeeds = false;
            _floatingPositionX = null;
            _floatingPositionY = null;
            _torrentListMode = TorrentListMode.All;
            _appLanguage = AppLanguage.System;
        }
    }

    private sealed class SettingsFile
    {
        public List<ServerConfig>? Servers { get; set; }
        public double? PollInterval { get; set; }
        public bool? ShowSpeedsInMenuBar { get; set; }
        public bool? ShowFloatingSpeeds { get; set; }
        public int? FloatingPositionX { get; set; }
        public int? FloatingPositionY { get; set; }
        public TorrentListMode? TorrentListMode { get; set; }
        public AppLanguage? AppLanguage { get; set; }
    }
}
