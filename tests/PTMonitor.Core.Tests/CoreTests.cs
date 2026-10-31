using PTMonitor.Core.Client;
using PTMonitor.Core.Diagnostics;
using PTMonitor.Core.Format;
using PTMonitor.Core.Model;
using PTMonitor.Core.Store;

namespace PTMonitor.Core.Tests;

/// <summary>The full built-in check suite, mirrored by `PTMonitor.Cli --selftest`.</summary>
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

public class HostFieldParserTests
{
    [Theory]
    [InlineData("nas.local", 8080, "nas.local", 8080)]
    [InlineData("nas.local:9091", 8080, "nas.local", 9091)]
    [InlineData("https://nas.local:8443/path", 8080, "nas.local", 8443)]
    [InlineData("[2001:db8::1]:9091", 8080, "2001:db8::1", 9091)]
    [InlineData("user@nas.local", 8080, "user@nas.local", 8080)]
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

    private sealed class StubClient : ITorrentClient
    {
        public Task<TransferSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken) =>
            throw new ServerFailureException(FailureKind.Unreachable, "stub");
        public Task<string> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult("stub");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

            var reloaded = new SettingsStore(path);
            Assert.Single(reloaded.Servers);
            Assert.Equal(30, reloaded.PollInterval);
            Assert.Equal(TorrentListMode.Active, reloaded.TorrentListMode);
            Assert.Equal(AppLanguage.Chinese, reloaded.AppLanguage);
            Assert.True(reloaded.ShowFloatingSpeeds);
            Assert.Equal(FloatingDisplayMode.Graph, reloaded.FloatingDisplay);
            Assert.Equal(640, reloaded.FloatingPositionX);
            Assert.Equal(480, reloaded.FloatingPositionY);
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
    public void ClampsPollInterval()
    {
        var settings = new SettingsStore(TempFile());
        settings.SetPollInterval(0.2);
        Assert.Equal(1, settings.PollInterval);
        settings.SetPollInterval(999);
        Assert.Equal(60, settings.PollInterval);
    }
}
