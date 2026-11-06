using PTMonitor.Core.Client;
using PTMonitor.Core.Diagnostics;
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
    private readonly Dictionary<Guid, List<TorrentSummary>> _lists = [];
    private readonly AsyncSignal _demandSignal = new();
    private CancellationTokenSource _lifetime = new();
    private volatile bool _listDemand;
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

    /// <summary>
    /// Whether the poll loops must fetch the torrent list. The App layer sets
    /// it from the panel's visibility: while the panel is closed nothing shows
    /// the list, so the loops ask for the summary only.
    /// </summary>
    public bool ListDemand => _listDemand;

    /// <summary>
    /// Switches the poll scope and wakes the loops, so a change is in effect
    /// on the next request instead of after the current interval.
    /// </summary>
    public void SetListDemand(bool listNeeded)
    {
        if (_listDemand == listNeeded) return;
        _listDemand = listNeeded;
        _demandSignal.Signal();
    }

    /// <summary>The scope a poll asks for: the list only when something shows it.</summary>
    public static PollDetail DetailFor(bool listDemand) =>
        listDemand ? PollDetail.Full : PollDetail.Summary;

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
            // A deleted server must not keep its torrent list in memory.
            foreach (var id in _lists.Keys.ToList())
            {
                if (!next.ContainsKey(id)) _lists.Remove(id);
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
        var detail = DetailFor(_listDemand);
        var results = await Task.WhenAll(enabled.Select(async config =>
            (config.Id, Status: await PollOnceAsync(config, detail, CancellationToken.None))));
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
        var password = _secrets.Get(config.Id);
        await using var client = _clientFactory(config, password);
        // Failure-transition memo: a steady failure is logged once, not once per
        // tick (at the default 2 s interval that would be the whole log file).
        // A successful poll clears it, so a recovery-then-failure logs again.
        FailureKind? loggedFailure = null;
        void Report(ServerStatus status, string? note = null)
        {
            if (status.Failure is not { } failure || !ShouldLogFailure(loggedFailure, status)) return;
            loggedFailure = failure.Kind;
            Log.Warn("poll", $"{config.Name}: {failure.KindLabel}: {failure.Message}{note}");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var baseInterval = _settings.PollInterval;
            var interval = baseInterval;
            var backoff = false;
            try
            {
                // The scope follows the panel: closed -> the summary only,
                // open -> the list as well.
                var detail = DetailFor(_listDemand);
                var snapshot = await client.FetchSnapshotAsync(detail, cancellationToken);
                loggedFailure = null;
                SetStatus(config.Id, new ServerStatus.Online(snapshot));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ServerFailureException failure)
            {
                var classified = Classify(password, _secrets, failure.Failure);
                var status = new ServerStatus.Failed(classified);
                SetStatus(config.Id, status);
                Report(status, IsUnreadableSecretAuth(password, _secrets, failure.Failure)
                    ? " (the secret store is unreadable; re-enter the password)"
                    : null);
                interval = RetryDelay(status, interval);
                backoff = HasBackoff(status.Failure?.Kind);
            }
            catch (Exception exception)
            {
                var status = new ServerStatus.Failed(ServerFailure.Unreachable(exception.Message));
                SetStatus(config.Id, status);
                Report(status);
                interval = RetryDelay(status, interval);
                backoff = HasBackoff(status.Failure?.Kind);
            }

            try
            {
                if (backoff)
                {
                    // A backoff is never interrupted. A demand change must not
                    // turn a 10-minute auth backoff into an instant retry --
                    // qBittorrent bans an IP after five failed logins.
                    await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                }
                else
                {
                    // A demand change interrupts the wait: opening the panel must
                    // not wait out the whole interval.
                    await _demandSignal.WaitAsync(TimeSpan.FromSeconds(interval), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<ServerStatus?> PollOnceAsync(ServerConfig config, PollDetail detail, CancellationToken cancellationToken)
    {
        return await PollAsync(config, _clientFactory, _secrets, detail, cancellationToken);
    }

    public static async Task<ServerStatus?> PollAsync(
        ServerConfig config,
        Func<ServerConfig, string?, ITorrentClient> clientFactory,
        ISecretStore secrets,
        PollDetail detail = PollDetail.Full,
        CancellationToken cancellationToken = default)
    {
        if (!config.IsEnabled) return ServerStatus.Disabled.Instance;
        var password = secrets.Get(config.Id);
        try
        {
            await using var client = clientFactory(config, password);
            return new ServerStatus.Online(await client.FetchSnapshotAsync(detail, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ServerFailureException failure)
        {
            return new ServerStatus.Failed(Classify(password, secrets, failure.Failure));
        }
        catch (Exception exception)
        {
            return new ServerStatus.Failed(ServerFailure.Unreachable(exception.Message));
        }
    }

    private void SetStatus(Guid id, ServerStatus status)
    {
        if (status is ServerStatus.Online online) AttachList(id, online);
        lock (_gate)
        {
            if (!_statuses.ContainsKey(id) || IsEquivalent(_statuses[id], status)) return;
            _statuses[id] = status;
        }
        RaiseChanged();
    }

    /// <summary>
    /// Keeps the torrent list alive across a summary poll. A summary snapshot
    /// carries no list; publishing it as it is would empty the panel the moment
    /// the panel closed. The last full list stays with the server instead.
    /// </summary>
    private void AttachList(Guid id, ServerStatus.Online status)
    {
        var snapshot = status.Value;
        lock (_gate)
        {
            if (snapshot.Detail == PollDetail.Full)
            {
                _lists[id] = snapshot.Torrents;
                return;
            }
            if (_lists.TryGetValue(id, out var list)) snapshot.Torrents = list;
        }
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

    /// <summary>
    /// Relabels an auth failure caused by an unreadable secret file, so the panel
    /// says what happened instead of blaming the credentials. A server that
    /// polls successfully is never pre-empted: it takes no password, or keeps
    /// failing for a reason of its own.
    /// </summary>
    public static ServerFailure Classify(string? password, ISecretStore secrets, ServerFailure failure) =>
        IsUnreadableSecretAuth(password, secrets, failure)
            ? ServerFailure.Auth("stored password could not be read")
            : failure;

    /// <summary>
    /// Whether an auth failure is really "the secret file cannot be read". The
    /// same predicate drives <see cref="Classify"/> and the log note, so the two
    /// can never disagree about what happened.
    /// </summary>
    public static bool IsUnreadableSecretAuth(string? password, ISecretStore secrets, ServerFailure failure) =>
        failure.Kind == FailureKind.Auth
        && password is null
        && secrets.State == SecretStoreState.Unreadable;

    /// <summary>
    /// Failure-transition rule: log only when this kind has not already been
    /// logged since the server was last online. The loop passes <c>null</c> after
    /// every success, so recovery-then-failure is logged again.
    /// </summary>
    public static bool ShouldLogFailure(FailureKind? lastLogged, ServerStatus candidate) =>
        candidate.Failure is { } failure && failure.Kind != lastLogged;

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

    /// <summary>
    /// Whether a failure kind backs off hard. A backoff wait is never
    /// interrupted — qBittorrent bans an IP after five failed logins, so the
    /// panel must not turn a 10-minute backoff into an instant retry.
    /// </summary>
    public static bool HasBackoff(FailureKind? kind) =>
        kind is FailureKind.Auth or FailureKind.Banned;

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
