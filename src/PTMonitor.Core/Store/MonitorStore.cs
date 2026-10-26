using PTMonitor.Core.Client;
using PTMonitor.Core.Model;

namespace PTMonitor.Core.Store;

public sealed record MonitorAggregate(
    long DownSpeed,
    long UpSpeed,
    int ActiveTorrents,
    int OnlineServers,
    int FailingServers);

/// <summary>
/// Observes every enabled server with one loop per server. A slow server cannot
/// delay the others; credential and ban failures back off hard.
/// </summary>
public sealed class MonitorStore : IDisposable
{
    public const double CredentialFailureBackoff = 600;
    public const double BannedBackoff = 900;

    private readonly object _gate = new();
    private readonly SettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly Func<ServerConfig, string?, ITorrentClient> _clientFactory;
    private readonly Dictionary<Guid, ServerStatus> _statuses = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _tasks = [];
    private CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public event EventHandler? StatusesChanged;

    public MonitorStore(
        SettingsStore settings,
        ISecretStore secrets,
        Func<ServerConfig, string?, ITorrentClient>? clientFactory = null)
    {
        _settings = settings;
        _secrets = secrets;
        _clientFactory = clientFactory ?? DefaultClientFactory;
        _settings.Changed += (_, _) => Reconcile();
    }

    public IReadOnlyDictionary<Guid, ServerStatus> Statuses
    {
        get { lock (_gate) return new Dictionary<Guid, ServerStatus>(_statuses); }
    }

    public MonitorAggregate Aggregate => AggregateOf(Statuses);
    public MenuBarState MenuBarState => MenuBarStateOf(Statuses);

    public void Start() => Reconcile();

    /// <summary>Rebuilds the per-server loops from the current configuration.</summary>
    public void Reconcile()
    {
        List<CancellationTokenSource> cancelled;
        lock (_gate)
        {
            cancelled = _tasks.Values.ToList();
            _tasks.Clear();
        }
        foreach (var token in cancelled)
        {
            token.Cancel();
            token.Dispose();
        }

        var configs = _settings.Servers;
        var changed = false;
        lock (_gate)
        {
            var next = new Dictionary<Guid, ServerStatus>();
            foreach (var config in configs)
            {
                var status = config.IsEnabled
                    ? (_statuses.GetValueOrDefault(config.Id) ?? ServerStatus.Connecting.Instance)
                    : ServerStatus.Disabled.Instance;
                next[config.Id] = status;
            }
            if (!StatusDictionaryEquals(next, _statuses))
            {
                _statuses.Clear();
                foreach (var (id, status) in next) _statuses[id] = status;
                changed = true;
            }
        }
        if (changed) RaiseChanged();

        foreach (var config in configs.Where(config => config.IsEnabled))
        {
            var token = new CancellationTokenSource();
            lock (_gate) _tasks[config.Id] = token;
            _ = Task.Run(() => PollLoopAsync(config, token.Token));
        }
    }

    public async Task RefreshNowAsync()
    {
        var enabled = _settings.Servers.Where(server => server.IsEnabled).ToList();
        var results = await Task.WhenAll(enabled.Select(async config =>
            (config.Id, Status: await PollOnceAsync(config, CancellationToken.None))));
        foreach (var (id, status) in results)
        {
            if (status is not null) SetStatus(id, status);
        }
    }

    public async Task StopAsync()
    {
        List<CancellationTokenSource> tokens;
        lock (_gate)
        {
            tokens = _tasks.Values.ToList();
            _tasks.Clear();
        }
        foreach (var token in tokens)
        {
            token.Cancel();
            token.Dispose();
        }
        await Task.CompletedTask;
    }

    private async Task PollLoopAsync(ServerConfig config, CancellationToken cancellationToken)
    {
        await using var client = _clientFactory(config, _secrets.Get(config.Id));
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = _settings.PollInterval;
            try
            {
                var snapshot = await client.FetchSnapshotAsync(cancellationToken);
                SetStatus(config.Id, new ServerStatus.Online(snapshot));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ServerFailureException failure)
            {
                var status = new ServerStatus.Failed(failure.Failure);
                SetStatus(config.Id, status);
                interval = RetryDelay(status, interval);
            }
            catch (Exception exception)
            {
                var status = new ServerStatus.Failed(ServerFailure.Unreachable(exception.Message));
                SetStatus(config.Id, status);
                interval = RetryDelay(status, interval);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<ServerStatus?> PollOnceAsync(ServerConfig config, CancellationToken cancellationToken)
    {
        return await PollAsync(config, _clientFactory, _secrets, cancellationToken);
    }

    public static async Task<ServerStatus?> PollAsync(
        ServerConfig config,
        Func<ServerConfig, string?, ITorrentClient> clientFactory,
        ISecretStore secrets,
        CancellationToken cancellationToken = default)
    {
        if (!config.IsEnabled) return ServerStatus.Disabled.Instance;
        try
        {
            await using var client = clientFactory(config, secrets.Get(config.Id));
            return new ServerStatus.Online(await client.FetchSnapshotAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ServerFailureException failure)
        {
            return new ServerStatus.Failed(failure.Failure);
        }
        catch (Exception exception)
        {
            return new ServerStatus.Failed(ServerFailure.Unreachable(exception.Message));
        }
    }

    private void SetStatus(Guid id, ServerStatus status)
    {
        lock (_gate)
        {
            if (!_statuses.ContainsKey(id) || IsEquivalent(_statuses[id], status)) return;
            _statuses[id] = status;
        }
        RaiseChanged();
    }

    private void RaiseChanged() => StatusesChanged?.Invoke(this, EventArgs.Empty);

    // MARK: - Pure helpers

    /// <summary>Whether a candidate changes anything the UI shows (fetchedAt ignored).</summary>
    public static bool IsEquivalent(ServerStatus? current, ServerStatus candidate)
    {
        if (current is null) return false;
        return (current, candidate) switch
        {
            (ServerStatus.Disabled, ServerStatus.Disabled) => true,
            (ServerStatus.Connecting, ServerStatus.Connecting) => true,
            (ServerStatus.Failed left, ServerStatus.Failed right) => left.Error == right.Error,
            (ServerStatus.Online left, ServerStatus.Online right) => SnapshotEquivalent(left.Value, right.Value),
            _ => false,
        };
    }

    private static bool SnapshotEquivalent(TransferSnapshot left, TransferSnapshot right)
    {
        if (left.DownSpeed != right.DownSpeed
            || left.UpSpeed != right.UpSpeed
            || left.ActiveTorrents != right.ActiveTorrents
            || left.TotalTorrents != right.TotalTorrents
            || left.FreeSpace != right.FreeSpace
            || left.ConnectionState != right.ConnectionState
            || left.Torrents.Count != right.Torrents.Count)
            return false;

        for (var index = 0; index < left.Torrents.Count; index++)
        {
            var a = left.Torrents[index];
            var b = right.Torrents[index];
            if (a.Id != b.Id || a.State != b.State || a.Progress != b.Progress
                || a.DownSpeed != b.DownSpeed || a.UpSpeed != b.UpSpeed
                || a.EtaSeconds != b.EtaSeconds)
                return false;
        }
        return true;
    }

    public static MonitorAggregate AggregateOf(IReadOnlyDictionary<Guid, ServerStatus> statuses)
    {
        long down = 0, up = 0;
        var active = 0;
        var online = 0;
        var failing = 0;
        foreach (var status in statuses.Values)
        {
            switch (status)
            {
                case ServerStatus.Online onlineStatus:
                    down += Math.Max(0, onlineStatus.Snapshot.DownSpeed);
                    up += Math.Max(0, onlineStatus.Snapshot.UpSpeed);
                    active += Math.Max(0, onlineStatus.Snapshot.ActiveTorrents);
                    online++;
                    break;
                case ServerStatus.Failed:
                    failing++;
                    break;
            }
        }
        return new MonitorAggregate(down, up, active, online, failing);
    }

    public static MenuBarState MenuBarStateOf(IReadOnlyDictionary<Guid, ServerStatus> statuses)
    {
        var monitored = statuses.Values.Where(status => status is not ServerStatus.Disabled).ToList();
        if (monitored.Count == 0) return MenuBarState.Idle;
        if (monitored.All(status => status is ServerStatus.Failed)) return MenuBarState.Error;
        var active = monitored.Any(status =>
            status.Snapshot is { } snapshot
            && (snapshot.DownSpeed > 0 || snapshot.UpSpeed > 0 || snapshot.ActiveTorrents > 0));
        return active ? MenuBarState.Active : MenuBarState.Idle;
    }

    public static double RetryDelay(ServerStatus status, double baseInterval) => status.Failure?.Kind switch
    {
        FailureKind.Auth => Math.Max(baseInterval, CredentialFailureBackoff),
        FailureKind.Banned => Math.Max(baseInterval, BannedBackoff),
        _ => baseInterval,
    };

    public static ServerStatus? StatusFrom(Exception exception) => exception switch
    {
        OperationCanceledException => null,
        ServerFailureException failure => new ServerStatus.Failed(failure.Failure),
        _ => new ServerStatus.Failed(ServerFailure.Unreachable(exception.Message)),
    };

    private ITorrentClient DefaultClientFactory(ServerConfig config, string? password) => config.Kind switch
    {
        ServerKind.Qbittorrent => new QBittorrentClient(config, password),
        ServerKind.Transmission => new TransmissionClient(config, password),
        _ => throw new ServerFailureException(FailureKind.ProtocolViolation, "unknown server kind"),
    };

    private static bool StatusDictionaryEquals(
        IReadOnlyDictionary<Guid, ServerStatus> left,
        IReadOnlyDictionary<Guid, ServerStatus> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (id, status) in left)
        {
            if (!right.TryGetValue(id, out var other) || !IsEquivalent(other, status)) return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<CancellationTokenSource> tokens;
        lock (_gate)
        {
            tokens = _tasks.Values.ToList();
            _tasks.Clear();
        }
        foreach (var token in tokens)
        {
            token.Cancel();
            token.Dispose();
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
