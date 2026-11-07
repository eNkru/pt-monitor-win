using System.Net;
using PTMonitor.Core.Client;
using PTMonitor.Core.Diagnostics;
using PTMonitor.Core.Format;
using PTMonitor.Core.Model;
using PTMonitor.Core.Store;

namespace PTMonitor.Core.Tests;

/// <summary>The full built-in check suite, mirrored by `PTMonitor.Cli --selftest`.</summary>
[Collection("log")]
public class SelfTestSuite
{
    [Fact]
    public async Task AllBuiltInChecksPass()
    {
        var result = await SelfTest.RunAsync();
        Assert.True(result.IsSuccess, result.Failure ?? "selftest failed");
        Assert.True(result.Passed > 150, $"expected a substantial suite, got {result.Passed} checks");
    }
}

/// <summary>
/// <see cref="Log"/> is process-global state, so every class that touches it
/// shares this collection: xUnit runs different collections in parallel, and a
/// second <c>Initialize</c> would steal the first one's sink.
/// </summary>
[CollectionDefinition("log", DisableParallelization = true)]
public class LogCollection;

[Collection("log")]
public class LogTests
{
    [Theory]
    [InlineData("password=hunter2", "password=***")]
    [InlineData("user=bob&pass=hunter2", "user=bob&password=***")]
    [InlineData("https://nas.local/api?password=hunter2&x=1", "https://nas.local/api?password=***&x=1")]
    [InlineData("passwd=hunter2", "password=***")]
    [InlineData("pwd=hunter2", "password=***")]
    [InlineData("Authorization: Basic aGVsbG86aHVudGVyMg==", "Authorization: Basic ***")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer ***")]
    [InlineData("Set-Cookie: SID=abc123; Path=/", "Set-Cookie: ***")]
    [InlineData("Cookie: SID=abc123", "Cookie: ***")]
    [InlineData("SID=abc123", "SID=***")]
    [InlineData("https://bob:hunter2@nas.local:8080/x", "https://***@nas.local:8080/x")]
    [InlineData("bob:hunter2@nas.local", "bob:***@nas.local")]
    [InlineData("nothing to hide", "nothing to hide")]
    public void RedactsEverySecretShape(string input, string expected) =>
        Assert.Equal(expected, Log.Redact(input));

    [Fact]
    public void RedactionIsIdempotent() =>
        Assert.Equal("password=***", Log.Redact(Log.Redact("password=hunter2")));

    [Fact]
    public void CallsBeforeInitializeAreNoOps()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "ptmonitor.log");
            Log.Shutdown();
            Log.Info("test", "before initialize");
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShutdownSilencesTheSink()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "shutdown.log");
            Log.Initialize(path);
            Log.Info("test", "while initialized");
            Log.Shutdown();
            Log.Info("test", "after shutdown");

            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            Assert.Contains("while initialized", lines[0]);
        }
        finally
        {
            Log.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TheLoopLogsASteadyFailureOnce()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "loop.log");
            Log.Initialize(path);

            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.SetPollInterval(SettingsStore.MinPollInterval);
            var config = new ServerConfig(ServerKind.Qbittorrent, "nas.invalid") { IsEnabled = true };
            settings.Add(config);

            // Counted at the client, not via StatusesChanged: an identical repeat is
            // deliberately not published (IsEquivalent), so the second failure is
            // only observable as a second poll. Wait for the THIRD poll to start:
            // the log write happens after the fetch throws, so a second-poll signal
            // could be read before the line is on disk.
            var polls = 0;
            var settled = new TaskCompletionSource();
            using var monitor = new MonitorStore(
                settings,
                new InMemorySecretStore(),
                (_, _) => new CountingClient(() =>
                {
                    if (Interlocked.Increment(ref polls) >= 3) settled.TrySetResult();
                }));

            monitor.Start();
            // Two polls at the minimum interval; the timeout only guards a hang.
            await Task.WhenAny(settled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            await monitor.StopAsync();
            Log.Shutdown();

            Assert.True(polls >= 3, $"expected three failed polls, saw {polls}");
            Assert.Single(File.ReadAllLines(path));
        }
        finally
        {
            Log.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LinesCarryUtcTimestampLevelAreaAndARedactedMessage()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "ptmonitor.log");
            Log.Initialize(path);
            Log.Info("startup", "PTMonitor 1.0.0, secrets absent");
            Log.Warn("poll", "nas: auth failed: password=hunter2");
            Log.Error("crash", "boom\n   at Foo()\r\n   at Bar()");
            Log.Shutdown();

            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[info\] startup: ", lines[0]);
            Assert.Contains("password=***", lines[1]);
            Assert.DoesNotContain("hunter2", lines[1]);
            Assert.EndsWith("boom | at Foo() | at Bar()", lines[2]);
        }
        finally
        {
            Log.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RotationLeavesOnePredecessorAndAFreshCurrentFile()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "rotate.log");
            Log.Initialize(path, 120);
            for (var index = 0; index < 12; index++) Log.Info("poll", $"event {index}");
            Log.Shutdown();

            Assert.True(File.Exists(path + Log.RotatedSuffix));
            Assert.NotEmpty(File.ReadAllLines(path + Log.RotatedSuffix));
            var current = File.ReadAllText(path);
            Assert.Contains("event 11", current);
            Assert.DoesNotContain("event 0", current);
            Assert.False(File.Exists(path + Log.RotatedSuffix + ".1"));
        }
        finally
        {
            Log.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AnUnwritablePathNeverThrows()
    {
        var directory = TempDirectory();
        try
        {
            var blocker = Path.Combine(directory, "blocker");
            File.WriteAllText(blocker, "not a directory");

            var exception = Record.Exception(() =>
            {
                Log.Initialize(Path.Combine(blocker, "ptmonitor.log"));
                Log.Info("test", "unwritable");
            });

            Assert.Null(exception);
        }
        finally
        {
            Log.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Fails every poll and reports each attempt, so a test can count ticks.</summary>
    private sealed class CountingClient(Action onPoll) : ITorrentClient
    {
        public Task<TransferSnapshot> FetchSnapshotAsync(PollDetail detail, CancellationToken cancellationToken)
        {
            onPoll();
            return Task.FromException<TransferSnapshot>(
                new ServerFailureException(ServerFailure.Unreachable("connection refused")));
        }

        public Task<string> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult("stub");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records the scope every poll asked for and returns a canned snapshot.</summary>
    private sealed class ScopeRecordingClient(
        Action<PollDetail> onPoll,
        Func<PollDetail, TransferSnapshot>? snapshot = null) : ITorrentClient
    {
        public Task<TransferSnapshot> FetchSnapshotAsync(PollDetail detail, CancellationToken cancellationToken)
        {
            onPoll(detail);
            return Task.FromResult(snapshot?.Invoke(detail) ?? new TransferSnapshot { Detail = detail });
        }

        public Task<string> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult("stub");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ptmonitor-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

/// <summary>
/// <see cref="StartupTrace"/> is process-global (the clock especially), so it
/// shares the non-parallel logging collection with <see cref="Log"/>.
/// </summary>
[Collection("log")]
public class StartupTraceTests
{
    [Fact]
    public void EmptyListRendersAZeroTimeline() =>
        Assert.Equal("entry=0.0 total=0.0 ms", StartupTrace.Format([], 0));

    [Fact]
    public void RendersInFixedCausalOrderWithOneDecimal() =>
        Assert.Equal("entry=0.0 avalonia=2.5 windows=12.5 custom=1.0 total=12.5 ms", StartupTrace.Format(
            [
                new StartupMark("entry", 0),
                new StartupMark("windows", 12.5),
                new StartupMark("avalonia", 2.5),
                new StartupMark("custom", 1),
            ], 12.5));

    [Fact]
    public void RealLaunchOrderStaysChronological() =>
        Assert.Equal("entry=0.0 glyph=158.4 tray-set=160.2 stores=163.1 total=163.1 ms", StartupTrace.Format(
            [
                new StartupMark("entry", 0),
                new StartupMark("stores", 163.1),
                new StartupMark("tray-set", 160.2),
                new StartupMark("glyph", 158.4),
            ], 163.1));

    [Fact]
    public void MarkWithoutBeginIsSilent()
    {
        var directory = Directory.CreateTempSubdirectory("ptmonitor-startup-").FullName;
        try
        {
            var path = Path.Combine(directory, "ptmonitor.log");
            Log.Initialize(path);
            StartupTrace.Reset();

            StartupTrace.Mark("avalonia");
            StartupTrace.Flush();

            Assert.False(File.Exists(path));
        }
        finally
        {
            Log.Shutdown();
            StartupTrace.Reset();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FlushWritesOneLineAndIsIdempotent()
    {
        var directory = Directory.CreateTempSubdirectory("ptmonitor-startup-").FullName;
        try
        {
            var path = Path.Combine(directory, "ptmonitor.log");
            Log.Initialize(path);
            StartupTrace.Reset();

            var elapsed = 0.0;
            StartupTrace.Clock = () => elapsed += 1.5;
            StartupTrace.Begin();
            StartupTrace.Mark("log-ready");
            StartupTrace.Mark("avalonia");
            StartupTrace.Flush();
            StartupTrace.Flush();
            StartupTrace.Mark("windows");
            Log.Shutdown();

            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            Assert.EndsWith(
                "startup: timeline entry=0.0 log-ready=1.5 avalonia=3.0 total=3.0 ms", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            Log.Shutdown();
            StartupTrace.Reset();
            Directory.Delete(directory, true);
        }
    }
}

public class HostFieldParserTests
{
    [Theory]
    [InlineData("nas.local", 8080, "nas.local", 8080)]
    [InlineData("nas.local:9091", 8080, "nas.local", 9091)]
    [InlineData("https://nas.local:8443/path", 8080, "nas.local", 8443)]
    [InlineData("[2001:db8::1]:9091", 8080, "2001:db8::1", 9091)]
    [InlineData("user@nas.local", 8080, "nas.local", 8080)]
    [InlineData("bob:hunter2@nas.local:9091", 8080, "nas.local", 9091)]
    public void ParsesExpectedShapes(string raw, int defaultPort, string expectedHost, int expectedPort)
    {
        var parsed = HostFieldParser.Parse(raw, defaultPort);
        if (raw.Contains('@'))
        {
            // userinfo is stripped
            Assert.Equal("nas.local", parsed.Host);
        }
        else
        {
            Assert.Equal(expectedHost, parsed.Host);
        }
        Assert.Equal(expectedPort, parsed.Port);
    }

    [Fact]
    public void RejectsNonHttpSchemes()
    {
        Assert.Throws<ServerFailureException>(() => HostFieldParser.Parse("ws://nas.local:8080", 8080));
    }

    [Theory]
    [InlineData("bob:hunter2@nas:8080", "bob", "hunter2")]
    [InlineData("http://bob:hunter2@nas:8080/path", "bob", "hunter2")]
    [InlineData("u%40x:p%3Aw@nas", "u@x", "p:w")]
    [InlineData("nas", null, null)]
    public void ParsesUserinfoOutOfTheHostField(string raw, string? expectedUser, string? expectedPassword)
    {
        var parsed = HostFieldParser.Parse(raw, 8080);
        Assert.Equal(expectedUser, parsed.Username);
        Assert.Equal(expectedPassword, parsed.Password);
    }

    [Fact]
    public void ValidatorPrefersATypedUserNameOverTheHostField()
    {
        var validated = ServerConfigValidator.Validate(
            new ServerConfig(ServerKind.Qbittorrent, "bob:hunter2@nas") { Username = "alice" }, "");
        Assert.Equal("alice", validated.Config.Username);
        Assert.Equal("hunter2", validated.PasswordFromHost);
        Assert.Contains("Used the password from the host field; kept the user name field.", validated.Notices);
    }
}

public class QBittorrentParserTests
{
    [Theory]
    [InlineData(204, "", null)]
    [InlineData(200, "Ok.", null)]
    [InlineData(200, "Fails.", FailureKind.Auth)]
    [InlineData(401, "Unauthorized", FailureKind.Auth)]
    [InlineData(403, "", FailureKind.Banned)]
    [InlineData(200, "", FailureKind.ProtocolViolation)]
    public void LoginFailureCoversBothServerGenerations(int status, string body, FailureKind? expected)
    {
        var failure = QBittorrentParser.LoginFailure(status, body);
        if (expected is null) Assert.Null(failure);
        else Assert.Equal(expected, failure!.Kind);
    }

    [Fact]
    public void NonLogin403IsNotABan()
    {
        var failure = QBittorrentParser.EndpointFailure(403, "sync/maindata");
        Assert.Equal(FailureKind.ProtocolViolation, failure!.Kind);
    }

    [Fact]
    public void FreeSpaceIsGatedByVersion()
    {
        var data = """{"server_state":{"dl_info_speed":1,"free_space_on_disk":100},"torrents":{}}"""u8;
        var gated = QBittorrentParser.Snapshot(data.ToArray(), new WebApiVersion(2, 1, 1));
        Assert.Equal(100, gated.FreeSpace);
        var ungated = QBittorrentParser.Snapshot(data.ToArray(), new WebApiVersion(2, 0, 0));
        Assert.Null(ungated.FreeSpace);
    }
}

public class TransmissionParserTests
{
    [Theory]
    [InlineData(0, TorrentState.Paused)]
    [InlineData(4, TorrentState.Downloading)]
    [InlineData(6, TorrentState.Seeding)]
    [InlineData(99, TorrentState.Other)]
    public void MapsStatusIntegers(int status, TorrentState expected)
    {
        Assert.Equal(expected, TransmissionParser.State(status));
    }

    [Fact]
    public void EtaSentinelsAreNil()
    {
        Assert.Null(TransmissionParser.EtaSeconds(-1));
        Assert.Null(TransmissionParser.EtaSeconds(-2));
        Assert.Equal(7200, TransmissionParser.EtaSeconds(7200));
    }
}

public class TorrentSummaryTests
{
    [Fact]
    public void SiteExtractionStripsKnownPrefixes()
    {
        Assert.Equal("hdchina.org", TorrentSummary.SiteFromAnnounceUrl("https://tracker.hdchina.org/announce.php?passkey=x"));
        Assert.Equal("example.net", TorrentSummary.SiteFromAnnounceUrl("udp://tracker.example.net:6969/announce"));
        Assert.Null(TorrentSummary.SiteFromAnnounceUrl(null));
    }

    [Fact]
    public void InfoSegmentsNeverEmpty()
    {
        var segments = TorrentSummary.InfoSegments(TorrentState.Downloading, 0, 0, null);
        Assert.Single(segments);
        Assert.IsType<TorrentRowInfoSegment.StateWord>(segments[0]);
    }

    [Fact]
    public void FixedSortPutsDownloadingFirst()
    {
        var downloading = new TorrentSummary { Id = "1", Name = "A", State = TorrentState.Downloading };
        var seeding = new TorrentSummary { Id = "2", Name = "B", State = TorrentState.Seeding };
        var paused = new TorrentSummary { Id = "3", Name = "C", State = TorrentState.Paused };
        var sorted = TorrentSummary.Sorted([paused, seeding, downloading]);
        Assert.Equal(["1", "2", "3"], sorted.Select(t => t.Id));
    }
}

public class MonitorStoreTests
{
    [Fact]
    public void CredentialFailuresBackOffHard()
    {
        var status = new ServerStatus.Failed(ServerFailure.Auth("x"));
        Assert.Equal(MonitorStore.CredentialFailureBackoff, MonitorStore.RetryDelay(status, 2));
        Assert.True(MonitorStore.CredentialFailureBackoff >= 600);
    }

    [Fact]
    public void BanBacksOffHardest()
    {
        var status = new ServerStatus.Failed(ServerFailure.Banned("x"));
        Assert.Equal(MonitorStore.BannedBackoff, MonitorStore.RetryDelay(status, 2));
        Assert.True(MonitorStore.BannedBackoff > MonitorStore.CredentialFailureBackoff);
    }

    [Fact]
    public async Task DisabledServersAreNeverContacted()
    {
        var polls = 0;
        var status = await MonitorStore.PollAsync(
            new ServerConfig(ServerKind.Qbittorrent, "nas") { IsEnabled = false },
            (_, _) => { polls++; return new StubClient(); },
            new InMemorySecretStore());
        Assert.IsType<ServerStatus.Disabled>(status);
        Assert.Equal(0, polls);
    }

    [Fact]
    public async Task ServerFailuresMapToFailedStatus()
    {
        var status = await MonitorStore.PollAsync(
            new ServerConfig(ServerKind.Qbittorrent, "nas.invalid"),
            (_, _) => new StubClient(),
            new InMemorySecretStore());
        Assert.IsType<ServerStatus.Failed>(status);
    }

    [Fact]
    public void ChangeDetectionIgnoresFetchedAt()
    {
        var old = new TransferSnapshot { DownSpeed = 5, FetchedAt = DateTimeOffset.UnixEpoch };
        var newer = new TransferSnapshot { DownSpeed = 5, FetchedAt = DateTimeOffset.Now };
        Assert.True(MonitorStore.IsEquivalent(
            new ServerStatus.Online(old), new ServerStatus.Online(newer)));
        Assert.False(MonitorStore.IsEquivalent(
            new ServerStatus.Online(old), new ServerStatus.Online(new TransferSnapshot { DownSpeed = 6 })));
    }

    [Theory]
    [InlineData(null, FailureKind.Auth, true)]
    [InlineData(FailureKind.Auth, FailureKind.Auth, false)]
    [InlineData(FailureKind.Auth, FailureKind.Unreachable, true)]
    [InlineData(FailureKind.Banned, FailureKind.Auth, true)]
    public void OnlyAFailureTransitionIsLogged(FailureKind? lastLogged, FailureKind candidate, bool expected)
    {
        var status = new ServerStatus.Failed(candidate switch
        {
            FailureKind.Auth => ServerFailure.Auth("x"),
            FailureKind.Banned => ServerFailure.Banned("x"),
            _ => ServerFailure.Unreachable("x"),
        });
        Assert.Equal(expected, MonitorStore.ShouldLogFailure(lastLogged, status));
        Assert.False(MonitorStore.ShouldLogFailure(lastLogged, new ServerStatus.Online(new TransferSnapshot())));
    }

    private sealed class StubClient : ITorrentClient
    {
        public Task<TransferSnapshot> FetchSnapshotAsync(PollDetail detail, CancellationToken cancellationToken) =>
            throw new ServerFailureException(FailureKind.Unreachable, "stub");
        public Task<string> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult("stub");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// The two poll scopes: what the loops ask for, what the qBittorrent library
/// merges into, and what the store keeps when the list is not fetched.
/// </summary>
public class PollScopeTests
{
    private const string FullUpdate = """
    {
      "rid": 7, "full_update": true,
      "server_state": { "dl_info_speed": 100, "up_info_speed": 50, "connection_status": "connected" },
      "torrents": {
        "aaa": { "state": "downloading" },
        "bbb": { "state": "uploading" }
      }
    }
    """;

    private const string Delta = """
    {
      "rid": 8,
      "server_state": { "dl_info_speed": 200, "up_info_speed": 60, "connection_status": "connected" },
      "torrents": { "aaa": { "state": "pausedDL" } },
      "torrents_removed": [ "bbb" ]
    }
    """;

    private const string BiggerThanTheLibrary = """
    {
      "rid": 9,
      "server_state": { "dl_info_speed": 200, "up_info_speed": 60, "connection_status": "connected" },
      "torrents": {
        "ccc": { "state": "downloading" },
        "ddd": { "state": "downloading" },
        "eee": { "state": "downloading" }
      }
    }
    """;

    private const string CoversTheLibrary = """
    {
      "rid": 12,
      "server_state": { "dl_info_speed": 200, "up_info_speed": 60, "connection_status": "connected" },
      "torrents": {
        "aaa": { "state": "downloading" },
        "bbb": { "state": "downloading" },
        "ccc": { "state": "downloading" }
      }
    }
    """;

    [Theory]
    [InlineData(false, PollDetail.Summary)]
    [InlineData(true, PollDetail.Full)]
    public void TheScopeFollowsTheDemand(bool demand, PollDetail expected) =>
        Assert.Equal(expected, MonitorStore.DetailFor(demand));

    [Fact]
    public void AFullUpdateReplacesTheLibrary()
    {
        var parsed = Parse(FullUpdate, PollDetail.Full, QBittorrentParser.QbLibraryState.Empty, true);
        Assert.Equal(2, parsed.Snapshot.TotalTorrents);
        Assert.Equal(2, parsed.Snapshot.ActiveTorrents);
        Assert.Equal(7, parsed.Rid);
        Assert.Equal(2, parsed.Snapshot.Torrents.Count);
    }

    [Fact]
    public void ADeltaAppliesChangesAndRemovals()
    {
        var full = Parse(FullUpdate, PollDetail.Full, QBittorrentParser.QbLibraryState.Empty, true);
        var merged = Parse(Delta, PollDetail.Full, full.State, false);
        Assert.Equal(1, merged.Snapshot.TotalTorrents);
        Assert.Equal(0, merged.Snapshot.ActiveTorrents);
        Assert.Equal(200, merged.Snapshot.DownSpeed);
        Assert.Equal(ConnectionState.Connected, merged.Snapshot.ConnectionState);
    }

    [Fact]
    public void TheSummaryCarriesTheCountsButNoList()
    {
        var full = Parse(FullUpdate, PollDetail.Full, QBittorrentParser.QbLibraryState.Empty, true);
        var summary = Parse(Delta, PollDetail.Summary, full.State, false);
        Assert.Empty(summary.Snapshot.Torrents);
        Assert.Equal(1, summary.Snapshot.TotalTorrents);
        Assert.Equal(PollDetail.Summary, summary.Snapshot.Detail);
    }

    [Fact]
    public void ABiggerMapThatMissesATorrentIsADelta()
    {
        var full = Parse(FullUpdate, PollDetail.Full, QBittorrentParser.QbLibraryState.Empty, true);
        var bigger = Parse(BiggerThanTheLibrary, PollDetail.Full, full.State, false);
        // The library keeps the torrent the map does not mention: treating the
        // map as a full set would lose it.
        Assert.Equal(5, bigger.Snapshot.TotalTorrents);
    }

    [Fact]
    public void AMapThatCoversTheLibraryIsTheWholeLibrary()
    {
        var full = Parse(FullUpdate, PollDetail.Full, QBittorrentParser.QbLibraryState.Empty, true);
        var covering = Parse(CoversTheLibrary, PollDetail.Full, full.State, false);
        Assert.Equal(3, covering.Snapshot.TotalTorrents);
    }

    [Fact]
    public async Task ThePollAsksForTheScopeThePanelNeeds()
    {
        var directory = TempDirectory();
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Add(new ServerConfig(ServerKind.Qbittorrent, "192.168.1.2", 8080) { Name = "NAS" });
            settings.SetPollInterval(1);

            var scopes = new List<PollDetail>();
            var polled = new SemaphoreSlim(0);
            using var monitor = new MonitorStore(settings, new InMemorySecretStore(),
                (config, password) => new ScopeRecordingClient(detail =>
                {
                    lock (scopes) scopes.Add(detail);
                    polled.Release();
                }));
            try
            {
                monitor.Start();
                Assert.True(await polled.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(PollDetail.Summary, scopes[0]);

                monitor.SetListDemand(true);
                Assert.True(await polled.WaitAsync(TimeSpan.FromSeconds(10)));
                // The scope is not pinned to an index: a slow machine may poll
                // once more before the demand lands.
                lock (scopes)
                {
                    Assert.Contains(PollDetail.Full, scopes.Skip(1));
                }
                Assert.True(monitor.ListDemand);
            }
            finally
            {
                await monitor.StopAsync();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ABackoffIsNotInterruptedByTheDemand()
    {
        var directory = TempDirectory();
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Add(new ServerConfig(ServerKind.Qbittorrent, "192.168.1.4", 8080) { Name = "NAS" });
            settings.SetPollInterval(1);

            var attempts = new int[1];
            var secondAttempt = new TaskCompletionSource();
            var attempted = new SemaphoreSlim(0);
            using var monitor = new MonitorStore(settings, new InMemorySecretStore(),
                (config, password) => new FailingClient(ServerFailure.Auth("wrong password"), () =>
                {
                    if (Interlocked.Increment(ref attempts[0]) >= 2) secondAttempt.TrySetResult();
                    attempted.Release();
                }));
            try
            {
                monitor.Start();
                Assert.True(await attempted.WaitAsync(TimeSpan.FromSeconds(10)));
                monitor.SetListDemand(true);
                monitor.SetListDemand(false);
                // A broken implementation would poll again at once; the wait
                // gives it the chance to show that.
                var again = await Task.WhenAny(secondAttempt.Task, Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.NotEqual(secondAttempt.Task, again);
                Assert.Equal(1, attempts[0]);
            }
            finally
            {
                await monitor.StopAsync();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ASignalIsNotLostBetweenTwoWaits()
    {
        var signal = new AsyncSignal();
        signal.Signal();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

        var waking = new AsyncSignal();
        var wait = waking.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        waking.Signal();
        var woken = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(wait, woken);
    }

    [Fact]
    public async Task OneSignalEndsOneWait()
    {
        var signal = new AsyncSignal();
        signal.Signal();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var after = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.True(after.Elapsed >= TimeSpan.FromMilliseconds(250));
    }

    [Theory]
    [InlineData(PollDetail.Summary, false)]
    [InlineData(PollDetail.Full, true)]
    public void OnlyTheFullScopeAsksForTheTorrentList(PollDetail detail, bool expected) =>
        Assert.Equal(expected, TransmissionClient.NeedsTorrentList(detail));

    [Theory]
    [InlineData(FailureKind.Auth, true)]
    [InlineData(FailureKind.Banned, true)]
    [InlineData(FailureKind.Unreachable, false)]
    [InlineData(FailureKind.ProtocolViolation, false)]
    public void OnlyTheHardFailuresBackOff(FailureKind kind, bool expected) =>
        Assert.Equal(expected, MonitorStore.HasBackoff(kind));

    [Fact]
    public void NoFailureDoesNotBackOff() => Assert.False(MonitorStore.HasBackoff(null));

    [Theory]
    [InlineData(PollDetail.Full, false, 5, 0, 0)]
    [InlineData(PollDetail.Summary, true, 5, 0, 0)]
    [InlineData(PollDetail.Summary, false, 5, 0, 5)]
    [InlineData(PollDetail.Summary, false, 5, 6, 0)]
    public void TheRidAsksForEverythingOrForTheChanges(
        PollDetail detail, bool libraryEmpty, int lastRid, int minutesSinceFullSync, int expected)
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(expected, QBittorrentClient.NextRid(
            detail, libraryEmpty, lastRid, now.AddMinutes(minutesSinceFullSync), now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void TheTrackerFallbackSurvivesADelta()
    {
        var withTracker = """
        {
          "rid": 10, "full_update": true,
          "server_state": { "dl_info_speed": 100, "up_info_speed": 50, "connection_status": "connected" },
          "trackers": { "https://tracker.example/announce": [ "aaa" ] },
          "torrents": { "aaa": { "state": "downloading" } }
        }
        """;
        var tracked = Parse(withTracker, PollDetail.Full, QBittorrentParser.QbLibraryState.Empty, true);
        Assert.Equal("https://tracker.example/announce", tracked.State.Trackers.GetValueOrDefault("aaa"));

        var withoutTrackers = """
        {
          "rid": 11,
          "server_state": { "dl_info_speed": 100, "up_info_speed": 50, "connection_status": "connected" },
          "torrents": { "aaa": { "state": "downloading" } }
        }
        """;
        var kept = Parse(withoutTrackers, PollDetail.Full, tracked.State, false);
        Assert.Equal("https://tracker.example/announce", kept.State.Trackers.GetValueOrDefault("aaa"));
    }

    [Fact]
    public async Task ASummaryPollKeepsTheList()
    {
        var directory = TempDirectory();
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            var server = new ServerConfig(ServerKind.Qbittorrent, "192.168.1.3", 8080) { Name = "NAS" };
            settings.Add(server);
            settings.SetPollInterval(1);

            var full = new TransferSnapshot
            {
                DownSpeed = 100,
                TotalTorrents = 2,
                Detail = PollDetail.Full,
                Torrents =
                [
                    new TorrentSummary { Id = "aaa", Name = "one" },
                    new TorrentSummary { Id = "bbb", Name = "two" },
                ],
            };
            var summary = new TransferSnapshot { DownSpeed = 999, TotalTorrents = 2, Detail = PollDetail.Summary };
            var published = new SemaphoreSlim(0);
            using var monitor = new MonitorStore(settings, new InMemorySecretStore(),
                (config, password) => new ScopeRecordingClient(
                    recorded => { },
                    detail => detail == PollDetail.Full ? full : summary));
            // Only the online states count: the reconcile publishes the
            // "connecting" state before the first poll.
            monitor.StatusesChanged += (_, _) =>
            {
                if (monitor.Statuses.Values.Any(state => state is ServerStatus.Online)) published.Release();
            };
            try
            {
                monitor.SetListDemand(true);
                monitor.Start();
                Assert.True(await published.WaitAsync(TimeSpan.FromSeconds(10)));

                monitor.SetListDemand(false);
                Assert.True(await published.WaitAsync(TimeSpan.FromSeconds(10)));

                var status = Assert.IsType<ServerStatus.Online>(monitor.Statuses[server.Id]);
                Assert.Equal(2, status.Value.Torrents.Count);
                Assert.Equal(PollDetail.Summary, status.Value.Detail);
                Assert.Equal(999, status.Value.DownSpeed);
            }
            finally
            {
                await monitor.StopAsync();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static QBittorrentParser.QbMainData Parse(
        string json,
        PollDetail detail,
        QBittorrentParser.QbLibraryState current,
        bool forceFull) =>
        QBittorrentParser.ParseMainData(
            System.Text.Encoding.UTF8.GetBytes(json),
            new WebApiVersion(2, 11, 2),
            detail,
            current,
            forceFull);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ptmonitor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Records the scope every poll asked for and returns a canned snapshot.</summary>
    private sealed class ScopeRecordingClient(
        Action<PollDetail> onPoll,
        Func<PollDetail, TransferSnapshot>? snapshot = null) : ITorrentClient
    {
        public Task<TransferSnapshot> FetchSnapshotAsync(PollDetail detail, CancellationToken cancellationToken)
        {
            onPoll(detail);
            return Task.FromResult(snapshot?.Invoke(detail) ?? new TransferSnapshot { Detail = detail });
        }

        public Task<string> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult("stub");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Fails every poll with a given cause, so a test can count the attempts.</summary>
    private sealed class FailingClient(ServerFailure failure, Action? onPoll = null) : ITorrentClient
    {
        public Task<TransferSnapshot> FetchSnapshotAsync(PollDetail detail, CancellationToken cancellationToken)
        {
            onPoll?.Invoke();
            return Task.FromException<TransferSnapshot>(new ServerFailureException(failure));
        }

        public Task<string> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult("stub");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class HttpTransportTests
{
    [Theory]
    [InlineData("Cannot write more bytes to the buffer than the configured maximum buffer size: 33554432.", true)]
    [InlineData("The response ended prematurely", false)]
    [InlineData("connection refused", false)]
    public void OnlyBufferLimitErrorsCountAsTooLarge(string message, bool expected)
    {
        Assert.Equal(expected, HttpTransport.IsResponseTooLarge(message));
    }

    [Fact]
    public void TheCapIsBoundedAndGenerous()
    {
        Assert.Equal(32L * 1024 * 1024, HttpTransport.MaxResponseBytes);
    }
    [Fact]
    public void RedirectsKeepTheCredentialOnlyOnTheSameOrigin()
    {
        var origin = new Uri("https://nas.local:8080/api/v2/auth/login");
        Assert.True(HttpTransport.SameOrigin(origin, new Uri("https://nas.local:8080/x")));
        Assert.False(HttpTransport.SameOrigin(origin, new Uri("http://nas.local:8080/x")));
        Assert.False(HttpTransport.SameOrigin(origin, new Uri("https://evil.example/x")));
        Assert.False(HttpTransport.SameOrigin(origin, new Uri("https://nas.local:9091/x")));
    }

    [Fact]
    public void RedirectRequestsCarryAuthorizationOnlyAcrossTheSameOrigin()
    {
        var origin = new Uri("https://nas.local:8080/api/v2/auth/login");
        var original = new HttpRequestMessage(HttpMethod.Post, origin) { Content = new ByteArrayContent([]) };
        original.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "abc");

        var same = HttpTransport.BuildRedirectRequest(original, new Uri("https://nas.local:8080/next"), HttpMethod.Post, [1]);
        var cross = HttpTransport.BuildRedirectRequest(original, new Uri("http://nas.local:8080/next"), HttpMethod.Get, null);

        Assert.NotNull(same.Headers.Authorization);
        Assert.NotNull(same.Content);
        Assert.Null(cross.Headers.Authorization);
        Assert.Null(cross.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, "GET", true)]
    [InlineData(HttpStatusCode.TemporaryRedirect, "POST", true)]
    [InlineData(HttpStatusCode.OK, "GET", false)]
    public void RedirectsAreRecognisedByStatus(HttpStatusCode status, string method, bool expected)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), new Uri("https://nas.local/a"));
        var response = new HttpResponseMessage(status) { Headers = { Location = new Uri("https://nas.local/b") } };
        Assert.Equal(expected, HttpTransport.TryGetRedirect(response, request, out _, out _));
    }
}

public class EditorNoticesTests
{
    [Fact]
    public void PlaintextWarningIsShownOnlyWithoutTlsAndWithAPassword()
    {
        Assert.Equal([EditorNotices.PlaintextWarning], EditorNotices.Compose(null, false, "hunter2"));
        Assert.Empty(EditorNotices.Compose(null, true, "hunter2"));
        Assert.Empty(EditorNotices.Compose(null, false, ""));
        Assert.Empty(EditorNotices.Compose(null, false, null));
    }

    [Fact]
    public void ValidatorNoticeComesFirstAndCoexists()
    {
        var parts = EditorNotices.Compose(["Used port 8080 from the host field."], false, "hunter2");
        Assert.Equal(["Used port 8080 from the host field.", EditorNotices.PlaintextWarning], parts);
    }
    [Fact]
    public void ThumbprintIsReportedOnlyForAcceptedInsecureCertificates()
    {
        Assert.Equal("AB12", EditorNotices.AcceptedCertificate(true, "ab12"));
        Assert.Null(EditorNotices.AcceptedCertificate(false, "ab12"));
        Assert.Null(EditorNotices.AcceptedCertificate(true, " "));
        Assert.Null(EditorNotices.AcceptedCertificate(true, null));
    }
}

public class SecretStoreTests
{
    [Fact]
    public void SetPersistsAndReportsState()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            var store = new DpapiSecretStore(path);
            var id = Guid.NewGuid();

            Assert.Equal(SecretStoreState.Absent, store.State);
            Assert.False(store.Set("", id));
            Assert.True(store.Set("hunter2", id));
            Assert.Equal("hunter2", store.Get(id));
            Assert.Equal(SecretStoreState.Ok, store.State);
            Assert.Equal("hunter2", new DpapiSecretStore(path).Get(id));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FailedWriteThrowsAndStoresNothing()
    {
        var directory = TempDirectory();
        try
        {
            var blocker = Path.Combine(directory, "blocker");
            File.WriteAllText(blocker, "not a directory");
            var store = new DpapiSecretStore(Path.Combine(blocker, "secrets.dat"));
            var id = Guid.NewGuid();

            Assert.ThrowsAny<IOException>(() => store.Set("hunter2", id));
            Assert.Equal(SecretStoreState.Absent, store.State);
            Assert.Null(store.Get(id));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DeleteNeverThrowsOnABrokenPath()
    {
        var directory = TempDirectory();
        try
        {
            var blocker = Path.Combine(directory, "blocker");
            File.WriteAllText(blocker, "not a directory");
            var store = new DpapiSecretStore(Path.Combine(blocker, "secrets.dat"));

            Assert.Null(Record.Exception(() => store.Delete(Guid.NewGuid())));
            Assert.Null(Record.Exception(store.DeleteAll));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CorruptFileIsUnreadableAndPreserved()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            File.WriteAllText(path, "{ not json");
            var store = new DpapiSecretStore(path);
            var id = Guid.NewGuid();

            Assert.Equal(SecretStoreState.Unreadable, store.State);
            Assert.Null(store.Get(id));

            store.Delete(Guid.NewGuid());
            Assert.True(File.Exists(path + ".corrupt"));
            Assert.Equal(SecretStoreState.Unreadable, store.State);

            Assert.True(store.Set("hunter2", id));
            Assert.Equal(SecretStoreState.Ok, store.State);
            Assert.Equal("hunter2", store.Get(id));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ThePreservedCopySurvivesRepeatedWritesWhileUnreadable()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            const string damaged = "{ not json";
            File.WriteAllText(path, damaged);
            var store = new DpapiSecretStore(path);

            // Two deletes: the first writes a valid file while the state is still
            // Unreadable. The second must not move that file over the copy.
            store.Delete(Guid.NewGuid());
            store.Delete(Guid.NewGuid());

            Assert.Equal(damaged, File.ReadAllText(path + ".corrupt"));
            Assert.Equal(SecretStoreState.Unreadable, store.State);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void UnreadableSecretsRelabelOnlyAuthFailures()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            File.WriteAllText(path, "{ not json");
            var store = new DpapiSecretStore(path);

            Assert.Equal("stored password could not be read",
                MonitorStore.Classify(null, store, ServerFailure.Auth("invalid username or password")).Message);
            Assert.Equal("refused",
                MonitorStore.Classify(null, store, ServerFailure.Unreachable("refused")).Message);
            Assert.Equal("x",
                MonitorStore.Classify("hunter2", store, ServerFailure.Auth("x")).Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ptmonitor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

public class ByteRateFormatterTests
{
    [Theory]
    [InlineData(999, "999 B/s")]
    [InlineData(1024, "1.0K")]
    [InlineData(1_258_291, "1.2M")]
    [InlineData(348_160, "340K")]
    public void Rates(long value, string expected) => Assert.Equal(expected, ByteRateFormatter.Rate(value));

    [Fact]
    public void ByteCounts() => Assert.Equal("412 GB", ByteRateFormatter.Bytes(442_918_502_400));

    [Fact]
    public void SpeedText()
    {
        Assert.Equal("↓ 1.2M ↑ 340K", ByteRateFormatter.SpeedText(1_258_291, 348_160));
        Assert.Equal("↓1.2M ↑340K", ByteRateFormatter.SpeedText(1_258_291, 348_160, MenuBarTextForm.Compact));
    }
}

public class SettingsStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "ptmonitor-tests-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void RoundTripsThroughTheFile()
    {
        var path = TempFile();
        try
        {
            var settings = new SettingsStore(path);
            settings.Add(new ServerConfig(ServerKind.Qbittorrent, "192.168.1.2", 8080) { Name = "NAS" });
            settings.SetPollInterval(30);
            settings.SetTorrentListMode(TorrentListMode.Active);
            settings.SetAppLanguage(AppLanguage.Chinese);
            settings.SetShowFloatingSpeeds(true);
            settings.SetFloatingDisplayMode(FloatingDisplayMode.Graph);
            settings.SetFloatingPosition(640, 480);
            settings.SetShowDesktopWidget(true);
            settings.SetDesktopWidgetPosition(120, 80);
            settings.SetDesktopWidgetLocked(true);

            var reloaded = new SettingsStore(path);
            Assert.Single(reloaded.Servers);
            Assert.Equal(30, reloaded.PollInterval);
            Assert.Equal(TorrentListMode.Active, reloaded.TorrentListMode);
            Assert.Equal(AppLanguage.Chinese, reloaded.AppLanguage);
            Assert.True(reloaded.ShowFloatingSpeeds);
            Assert.Equal(FloatingDisplayMode.Graph, reloaded.FloatingDisplay);
            Assert.Equal(640, reloaded.FloatingPositionX);
            Assert.Equal(480, reloaded.FloatingPositionY);
            Assert.True(reloaded.ShowDesktopWidget);
            Assert.Equal(120, reloaded.DesktopWidgetPositionX);
            Assert.Equal(80, reloaded.DesktopWidgetPositionY);
            Assert.True(reloaded.DesktopWidgetLocked);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FloatingSpeedsAreOffWithoutAFile()
    {
        var settings = new SettingsStore(TempFile());
        Assert.False(settings.ShowFloatingSpeeds);
        Assert.Equal(FloatingDisplayMode.Numbers, settings.FloatingDisplay);
        Assert.Null(settings.FloatingPositionX);
        Assert.Null(settings.FloatingPositionY);
    }

    [Fact]
    public void DesktopWidgetIsOffWithoutAFile()
    {
        var settings = new SettingsStore(TempFile());
        Assert.False(settings.ShowDesktopWidget);
        Assert.Null(settings.DesktopWidgetPositionX);
        Assert.Null(settings.DesktopWidgetPositionY);
        Assert.False(settings.DesktopWidgetLocked);
    }

    [Fact]
    public void ClampsPollInterval()
    {
        var settings = new SettingsStore(TempFile());
        settings.SetPollInterval(0.2);
        Assert.Equal(1, settings.PollInterval);
        settings.SetPollInterval(999);
        Assert.Equal(60, settings.PollInterval);
    }
}
