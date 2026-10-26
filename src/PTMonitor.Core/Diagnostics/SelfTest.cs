using System.Text;
using System.Text.Json.Nodes;
using PTMonitor.Core.Client;
using PTMonitor.Core.Format;
using PTMonitor.Core.Model;
using PTMonitor.Core.Store;

namespace PTMonitor.Core.Diagnostics;

public sealed class SelfTestFailure(string message) : Exception(message);

public sealed record SelfTestResult(bool IsSuccess, int Passed, string? Failure);

/// <summary>Built-in pure-logic checks, runnable via <c>--selftest</c> and xUnit.</summary>
public static class SelfTest
{
    private sealed class Checks
    {
        public int Passed;
        public readonly List<string> Failures = [];

        public void Expect<T>(string name, T actual, T expected)
        {
            if (Equals(actual, expected))
            {
                Passed++;
                return;
            }
            // Collections need sequence equality; records inside compare by value.
            if (actual is System.Collections.IEnumerable actualEnumerable
                && expected is System.Collections.IEnumerable expectedEnumerable
                && actual is not string)
            {
                if (actualEnumerable.Cast<object?>().SequenceEqual(expectedEnumerable.Cast<object?>()))
                {
                    Passed++;
                    return;
                }
            }
            Failures.Add($"{name}: expected {Format(expected)}, got {Format(actual)}");
        }

        public void ExpectOptional(string name, object? actual, object? expected)
        {
            if (Equals(actual, expected)) { Passed++; return; }
            Failures.Add($"{name}: expected {expected?.ToString() ?? "nil"}, got {actual?.ToString() ?? "nil"}");
        }

        public void True(string name, bool condition)
        {
            if (condition) { Passed++; return; }
            Failures.Add(name);
        }

        public void ExpectFailure(string name, Action action)
        {
            try
            {
                action();
                Failures.Add($"{name}: expected a failure");
            }
            catch (Exception exception) when (exception is ServerFailureException or ProbeRunner.UsageError)
            {
                Passed++;
            }
        }

        public async Task ExpectFailureAsync(string name, Func<Task> action)
        {
            try
            {
                await action();
                Failures.Add($"{name}: expected a failure");
            }
            catch (ServerFailureException)
            {
                Passed++;
            }
        }

        private static string Format<T>(T? value)
        {
            if (value is null) return "nil";
            if (value is string text) return $"\"{text}\"";
            if (value is System.Collections.IEnumerable enumerable and not string)
            {
                var items = enumerable.Cast<object?>().Select(item => Format(item)).ToList();
                return "[" + string.Join(", ", items) + "]";
            }
            return value.ToString() ?? "nil";
        }
    }

    public static async Task<SelfTestResult> RunAsync()
    {
        var checks = new Checks();
        try
        {
            HostChecks(checks);
            ValidationChecks(checks);
            ProbeChecks(checks);
            ParserChecks(checks);
            PresentationChecks(checks);
            DetailChecks(checks);
            FormatChecks(checks);
            await StoreChecks(checks);
            await ClientChecks(checks);
        }
        catch (Exception exception)
        {
            return new SelfTestResult(false, checks.Passed, $"uncaught: {exception.Message}");
        }

        return checks.Failures.Count == 0
            ? new SelfTestResult(true, checks.Passed, null)
            : new SelfTestResult(false, checks.Passed, string.Join(" | ", checks.Failures));
    }

    private static void HostChecks(Checks c)
    {
        var plain = HostFieldParser.Parse("nas.local", 8080);
        c.Expect("host: bare host", plain.Host, "nas.local");
        c.Expect("host: default port", plain.Port, 8080);

        var url = HostFieldParser.Parse("https://nas.local:8443/path", 8080);
        c.Expect("host: url host", url.Host, "nas.local");
        c.Expect("host: url port", url.Port, 8443);
        c.ExpectOptional("host: url scheme tls", url.SchemeIsTls, true);

        c.ExpectFailure("host: ws scheme rejected", () => HostFieldParser.Parse("ws://nas.local:8080", 8080));

        var ipv6 = HostFieldParser.Parse("[2001:db8::1]:9091", 8080);
        c.Expect("host: ipv6", ipv6.Host, "2001:db8::1");
        c.Expect("host: ipv6 port", ipv6.Port, 9091);
    }

    private static void ValidationChecks(Checks c)
    {
        var typed = new ServerConfig
        {
            Kind = ServerKind.Transmission,
            Name = "  NAS  ",
            Host = "http://nas.local:9091",
            UseTls = true,
            Username = "  admin ",
        };
        var validated = ServerConfigValidator.Validate(typed, "");
        c.Expect("validator: host", validated.Config.Host, "nas.local");
        c.Expect("validator: port", validated.Config.Port, 9091);
        c.Expect("validator: http disables tls", validated.Config.UseTls, false);
        c.Expect("validator: trims name", validated.Config.Name, "NAS");
        c.Expect("validator: trims username", validated.Config.Username, "admin");
        c.True("validator: reports the scheme adjustment", validated.Notice is not null);

        var portFromField = ServerConfigValidator.Validate(
            new ServerConfig { Kind = ServerKind.Qbittorrent, Host = "nas.local:9091" }, "8080");
        c.Expect("validator: host port wins", portFromField.Config.Port, 9091);
        c.True("validator: port adjustment reported", portFromField.Notice is not null);

        c.ExpectFailure("validator: bad port", () =>
            ServerConfigValidator.Validate(new ServerConfig { Kind = ServerKind.Qbittorrent, Host = "nas" }, "70000"));
        c.ExpectFailure("validator: empty host", () =>
            ServerConfigValidator.Validate(new ServerConfig { Kind = ServerKind.Qbittorrent, Host = "   " }, ""));

        c.Expect("webui: qb", new ServerConfig(ServerKind.Qbittorrent, "nas.local", 8080).WebUiUri().ToString(), "http://nas.local:8080/");
        c.Expect("webui: qb tls", new ServerConfig(ServerKind.Qbittorrent, "nas.local", 8443) { UseTls = true }.WebUiUri().ToString(), "https://nas.local:8443/");
        c.Expect("webui: transmission", new ServerConfig(ServerKind.Transmission, "nas.local", 9092).WebUiUri().ToString(), "http://nas.local:9092/transmission/web/");
    }

    private static void ProbeChecks(Checks c)
    {
        var options = ProbeRunner.Parse(["--https", "--host", "nas.local", "--probe", "qbittorrent", "--port", "8080", "--insecure"]);
        c.Expect("probe: kind", options.Kind, ServerKind.Qbittorrent);
        c.Expect("probe: host", options.Host, "nas.local");
        c.Expect("probe: port", options.Port, 8080);
        c.True("probe: https", options.UseTls);
        c.True("probe: insecure", options.AllowInsecureTls);

        c.Expect("probe: default port", ProbeRunner.Parse(["--host", "nas", "--probe", "transmission"]).Port, 9091);
        c.ExpectFailure("probe: rejects unknown type", () => ProbeRunner.Parse(["--probe", "deluge", "--host", "nas"]));
        c.ExpectFailure("probe: requires host", () => ProbeRunner.Parse(["--probe", "qbittorrent"]));
        c.ExpectFailure("probe: rejects stray argument", () => ProbeRunner.Parse(["--probe", "qbittorrent", "--host", "nas", "--wat"]));

        var snapshot = new TransferSnapshot
        {
            DownSpeed = 1_258_291,
            UpSpeed = 348_160,
            ActiveTorrents = 3,
            TotalTorrents = 4,
            FreeSpace = 442_918_502_400,
            ConnectionState = ConnectionState.Connected,
            FetchedAt = DateTimeOffset.UnixEpoch,
        };
        var config = new ServerConfig(ServerKind.Qbittorrent, "nas.local", 8080) { Name = "NAS" };
        var root = JsonNode.Parse(ProbeRunner.Encode(snapshot, config))!.AsObject();
        var snapshotObject = root["snapshot"]!.AsObject();
        c.Expect("probe json: downSpeed", (long)snapshotObject["downSpeed"]!, 1_258_291L);
        c.Expect("probe json: freeSpace", (long)snapshotObject["freeSpace"]!, 442_918_502_400L);
        c.Expect("probe json: state", (string)snapshotObject["connectionState"]!, "connected");
        c.Expect("probe json: kind", (string)root["server"]!["kind"]!, "qbittorrent");

        var noFree = JsonNode.Parse(ProbeRunner.Encode(new TransferSnapshot(), config))!.AsObject();
        c.True("probe json: nil freeSpace is null", noFree["snapshot"]!["freeSpace"] is null);
    }

    private static void ParserChecks(Checks c)
    {
        var mainData = Encoding.UTF8.GetBytes("""
        {
          "rid": 1, "full_update": true,
          "server_state": { "dl_info_speed": 1258291, "up_info_speed": 348160, "connection_status": "connected", "free_space_on_disk": 442918502400 },
          "torrents": {
            "aaaa000000000000000000000000000000000000": { "state": "downloading" },
            "bbbb000000000000000000000000000000000000": { "state": "stalledUP" },
            "cccc000000000000000000000000000000000000": { "state": "uploading" },
            "dddd000000000000000000000000000000000000": { "state": "pausedDL" }
          }
        }
        """);
        var gated = QBittorrentParser.Snapshot(mainData, new WebApiVersion(2, 1, 1));
        c.Expect("qb: down", gated.DownSpeed, 1_258_291L);
        c.Expect("qb: up", gated.UpSpeed, 348_160L);
        c.Expect("qb: state", gated.ConnectionState, ConnectionState.Connected);
        c.Expect("qb: active", gated.ActiveTorrents, 3);
        c.Expect("qb: total", gated.TotalTorrents, 4);
        c.ExpectOptional("qb: free space", gated.FreeSpace, 442_918_502_400L);

        var ungated = QBittorrentParser.Snapshot(mainData, new WebApiVersion(2, 0, 2));
        c.ExpectOptional("qb: free space gated", ungated.FreeSpace, null);
        c.Expect("qb: speeds still parsed", ungated.DownSpeed, 1_258_291L);

        c.Expect("qb: version parse", QBittorrentParser.WebApiVersionFrom("2.11.2"), new WebApiVersion(2, 11, 2));
        c.True("qb: 2.0 accepted", (QBittorrentParser.WebApiVersionFrom("2.0.0") ?? WebApiVersion.V2_0_0) >= WebApiVersion.V2_0_0);

        c.Expect("qb login: Fails -> auth", QBittorrentParser.LoginFailure(200, "Fails.")?.Kind, FailureKind.Auth);
        c.True("qb login: Ok -> success", QBittorrentParser.LoginFailure(200, "Ok.") is null);
        c.True("qb login: 204 -> success", QBittorrentParser.LoginFailure(204, "") is null);
        c.Expect("qb login: 401 -> auth", QBittorrentParser.LoginFailure(401, "Unauthorized")?.Kind, FailureKind.Auth);
        c.Expect("qb login: 403 -> banned", QBittorrentParser.LoginFailure(403, "")?.Kind, FailureKind.Banned);
        c.Expect("qb login: blank 200 -> protocol", QBittorrentParser.LoginFailure(200, "")?.Kind, FailureKind.ProtocolViolation);
        c.Expect("qb endpoint: 403 -> protocol", QBittorrentParser.EndpointFailure(403, "sync/maindata")?.Kind, FailureKind.ProtocolViolation);
        c.Expect("qb endpoint: 401 -> auth", QBittorrentParser.EndpointFailure(401, "sync/maindata")?.Kind, FailureKind.Auth);
        c.True("qb endpoint: 200 -> none", QBittorrentParser.EndpointFailure(200, "sync/maindata") is null);

        (string Raw, TorrentState Expected)[] states =
        [
            ("downloading", TorrentState.Downloading), ("metaDL", TorrentState.Downloading), ("forcedDL", TorrentState.Downloading),
            ("stalledDL", TorrentState.Stalled), ("queuedDL", TorrentState.Queued), ("queuedUP", TorrentState.Queued),
            ("uploading", TorrentState.Seeding), ("forcedUP", TorrentState.Seeding), ("stalledUP", TorrentState.Seeding),
            ("pausedDL", TorrentState.Paused), ("pausedUP", TorrentState.Paused),
            ("checkingDL", TorrentState.Checking), ("checkingUP", TorrentState.Checking), ("checkingResumeData", TorrentState.Checking),
            ("allocating", TorrentState.Other), ("error", TorrentState.Error), ("missingFiles", TorrentState.Error),
            ("somethingNew", TorrentState.Other),
        ];
        foreach (var (raw, expected) in states) c.Expect($"qb state {raw}", QBittorrentParser.State(raw), expected);

        c.ExpectOptional("qb eta: sentinel", QBittorrentParser.EtaSeconds(8_640_000), null);
        c.ExpectOptional("qb eta: negative", QBittorrentParser.EtaSeconds(-1), null);
        c.ExpectOptional("qb eta: valid", QBittorrentParser.EtaSeconds(3600), 3600L);

        var torrents = new JsonObject
        {
            ["aaaa"] = new JsonObject
            {
                ["name"] = "Seeding ISO", ["state"] = "stalledUP", ["progress"] = 1.0,
                ["dlspeed"] = 0, ["upspeed"] = 20480, ["eta"] = 8640000,
                ["total_size"] = 6_600_000_000, ["uploaded"] = 9_800_000_000, ["ratio"] = 2.41,
                ["num_seeds"] = 12, ["num_leechs"] = 3, ["category"] = "Movies",
                ["tracker"] = "https://tracker.hdchina.org/announce.php?passkey=x", ["has_tracker_error"] = false,
            },
            ["bbbb"] = new JsonObject
            {
                ["name"] = "Downloading ISO", ["state"] = "downloading", ["progress"] = 0.42,
                ["dlspeed"] = 1_258_291, ["upspeed"] = 5120, ["eta"] = 1800,
                ["ratio"] = -1, ["num_complete"] = 40, ["num_incomplete"] = 7,
                ["has_tracker_error"] = true,
            },
            ["cccc"] = new JsonObject { ["state"] = "error", ["progress"] = 7.5, ["dlspeed"] = 0, ["upspeed"] = 0 },
        };
        var trackers = new JsonObject
        {
            ["https://tracker.hdchina.org/announce.php?passkey=x"] = new JsonArray("aaaa"),
            ["udp://announce.othersite.net:6969/announce"] = new JsonArray("bbbb"),
        };
        var summaries = QBittorrentParser.Summaries(torrents, trackers);
        c.Expect("qb summaries: count", summaries.Count, 3);
        var downloading = summaries.First(t => t.Name == "Downloading ISO");
        c.Expect("qb summary: state", downloading.State, TorrentState.Downloading);
        c.Expect("qb summary: eta", downloading.EtaSeconds, 1_800L);
        c.Expect("qb summary: name fallback", summaries.First(t => t.Id == "cccc").Name, "cccc");
        c.Expect("qb summary: progress clamp", summaries.First(t => t.Id == "cccc").Progress, 1.0);
        c.ExpectOptional("qb summary: ratio -1 is nil", downloading.Ratio, null);
        c.ExpectOptional("qb summary: swarm fallback", downloading.Seeders, 40);
        c.Expect("qb summary: site fallback", downloading.Site, "othersite.net");
        c.ExpectOptional("qb summary: tracker not working", downloading.TrackerStatus, TrackerStatus.NotWorking);
        c.ExpectOptional("qb summary: no tracker -> no site", summaries.First(t => t.Id == "cccc").Site, null);
        var seeding = summaries.First(t => t.Name == "Seeding ISO");
        c.ExpectOptional("qb summary: infinite eta nil", seeding.EtaSeconds, null);
        c.ExpectOptional("qb summary: site", seeding.Site, "hdchina.org");
        c.Expect("qb summary: ratio", seeding.Ratio, 2.41);
        c.Expect("qb summary: category", seeding.Category, "Movies");

        var inverted = QBittorrentParser.TrackersByHash(new JsonObject
        {
            ["https://b.example/announce"] = new JsonArray("h2", "h1"),
            ["https://a.example/announce"] = new JsonArray("h1"),
        });
        c.Expect("qb inverted: count", inverted.Count, 2);
        c.Expect("qb inverted: h2", inverted["h2"], "https://b.example/announce");
        c.Expect("qb inverted: h1 first sorted", inverted["h1"], "https://a.example/announce");

        c.Expect("tx: down", TransmissionParser.SnapshotFromSessionStats(new JsonObject
        {
            ["downloadSpeed"] = 1258291, ["uploadSpeed"] = 348160,
            ["activeTorrentCount"] = 3, ["torrentCount"] = 7,
        }).DownSpeed, 1_258_291L);

        c.ExpectFailure("tx: non-success result throws", () =>
            TransmissionParser.Snapshot(Encoding.UTF8.GetBytes("{\"result\":\"permission denied\",\"arguments\":{}}")));
        c.ExpectOptional("tx: free space", TransmissionParser.FreeSpace(new JsonObject { ["size-bytes"] = 442_918_502_400 }), 442_918_502_400L);

        (int? Raw, TorrentState Expected)[] txStates =
        [
            (0, TorrentState.Paused), (1, TorrentState.Checking), (2, TorrentState.Checking),
            (3, TorrentState.Queued), (4, TorrentState.Downloading), (5, TorrentState.Queued),
            (6, TorrentState.Seeding), (null, TorrentState.Other), (99, TorrentState.Other),
        ];
        foreach (var (raw, expected) in txStates) c.Expect($"tx state {raw?.ToString() ?? "nil"}", TransmissionParser.State(raw), expected);
        c.ExpectOptional("tx eta: -1", TransmissionParser.EtaSeconds(-1), null);
        c.ExpectOptional("tx eta: -2", TransmissionParser.EtaSeconds(-2), null);
        c.ExpectOptional("tx eta: valid", TransmissionParser.EtaSeconds(7200), 7200L);

        var txTorrents = new JsonArray
        {
            new JsonObject
            {
                ["id"] = 1, ["name"] = "Linux Distro", ["status"] = 4, ["percentDone"] = 0.5,
                ["rateDownload"] = 524288, ["rateUpload"] = 0, ["eta"] = 900, ["error"] = 0,
                ["totalSize"] = 1_400_000_000, ["downloadedEver"] = 700_000_000,
                ["uploadedEver"] = 350_000_000, ["uploadRatio"] = 0.5,
                ["peersSendingToUs"] = 5, ["peersGettingFromUs"] = 2,
                ["trackers"] = new JsonArray(new JsonObject { ["announce"] = "https://tracker.hdchina.org/announce", ["tier"] = 0 }),
            },
            new JsonObject
            {
                ["id"] = 2, ["name"] = "Broken Torrent", ["status"] = 4, ["percentDone"] = 0.1,
                ["rateDownload"] = 0, ["rateUpload"] = 0, ["eta"] = -1, ["error"] = 3,
                ["errorString"] = "unregistered torrent", ["uploadRatio"] = -1, ["trackers"] = new JsonArray(),
            },
        };
        var txSummaries = TransmissionParser.Summaries(txTorrents);
        c.Expect("tx summaries: count", txSummaries.Count, 2);
        c.Expect("tx summary: error overrides", txSummaries.First(t => t.Name == "Broken Torrent").State, TorrentState.Error);
        c.Expect("tx summary: error message", txSummaries.First(t => t.Name == "Broken Torrent").ErrorMessage, "unregistered torrent");
        c.Expect("tx summary: site", txSummaries.First(t => t.Name == "Linux Distro").Site, "hdchina.org");
        c.ExpectOptional("tx summary: eta sentinel nil", txSummaries.First(t => t.Name == "Broken Torrent").EtaSeconds, null);

        c.Expect("clamp nil", TorrentSummary.ClampedProgress(null), 0.0);
        c.Expect("clamp negative", TorrentSummary.ClampedProgress(-0.2), 0.0);
        c.Expect("clamp over", TorrentSummary.ClampedProgress(1.5), 1.0);
        c.Expect("clamp NaN", TorrentSummary.ClampedProgress(double.NaN), 0.0);
        c.Expect("clamp valid", TorrentSummary.ClampedProgress(0.42), 0.42);

        var idle = new TorrentSummary { Id = "4", Name = "Idle", State = TorrentState.Paused, Progress = 1 };
        var down = new TorrentSummary { Id = "5", Name = "Down", State = TorrentState.Downloading, Progress = 0.5, DownSpeed = 1 };
        var up = new TorrentSummary { Id = "6", Name = "Up", State = TorrentState.Seeding, Progress = 1, UpSpeed = 1 };
        var both = new TorrentSummary { Id = "7", Name = "Both", State = TorrentState.Seeding, Progress = 1, DownSpeed = 1, UpSpeed = 1 };
        var stalled = new TorrentSummary { Id = "8", Name = "Stalled", State = TorrentState.Stalled, Progress = 0.5, DownSpeed = 1 };
        (TorrentListMode Mode, bool[] Expected)[] modes =
        [
            (TorrentListMode.All, [true, true, true, true, true]),
            (TorrentListMode.Downloading, [false, true, false, true, true]),
            (TorrentListMode.Uploading, [false, false, true, true, false]),
            (TorrentListMode.Active, [false, true, true, true, true]),
        ];
        var set = new[] { idle, down, up, both, stalled };
        foreach (var (mode, expected) in modes)
            c.Expect($"list mode {mode}", set.Select(torrent => mode.Matches(torrent)).ToArray(), expected);
    }

    private static void PresentationChecks(Checks c)
    {
        c.Expect("row: down then eta", TorrentSummary.InfoSegments(TorrentState.Downloading, 1_258_291, 0, 1_800),
            new List<TorrentRowInfoSegment> { new TorrentRowInfoSegment.Down(1_258_291), new TorrentRowInfoSegment.Eta(1_800) });
        c.Expect("row: keeps upload", TorrentSummary.InfoSegments(TorrentState.Downloading, 1_258_291, 348_160, 1_800),
            new List<TorrentRowInfoSegment>
            {
                new TorrentRowInfoSegment.Down(1_258_291), new TorrentRowInfoSegment.Up(348_160), new TorrentRowInfoSegment.Eta(1_800),
            });
        c.Expect("row: zero speed drops", TorrentSummary.InfoSegments(TorrentState.Downloading, 0, 0, 600),
            new List<TorrentRowInfoSegment> { new TorrentRowInfoSegment.Eta(600) });
        c.Expect("row: seeding up only", TorrentSummary.InfoSegments(TorrentState.Seeding, 0, 348_160, 1_800),
            new List<TorrentRowInfoSegment> { new TorrentRowInfoSegment.Up(348_160) });
        c.Expect("row: seeding word fallback", TorrentSummary.InfoSegments(TorrentState.Seeding, 1_258_291, 0, null),
            new List<TorrentRowInfoSegment> { new TorrentRowInfoSegment.StateWord(TorrentState.Seeding) });
        c.Expect("row: negative eta dropped", TorrentSummary.InfoSegments(TorrentState.Downloading, 0, 0, -1),
            new List<TorrentRowInfoSegment> { new TorrentRowInfoSegment.StateWord(TorrentState.Downloading) });
        foreach (var state in new[] { TorrentState.Stalled, TorrentState.Queued, TorrentState.Checking, TorrentState.Paused, TorrentState.Error, TorrentState.Other })
            c.Expect($"row: {state} word only", TorrentSummary.InfoSegments(state, 1_258_291, 348_160, 60),
                new List<TorrentRowInfoSegment> { new TorrentRowInfoSegment.StateWord(state) });

        c.Expect("percent 0", TorrentSummary.PercentText(0), "0%");
        c.Expect("percent 0.624", TorrentSummary.PercentText(0.624), "62%");
        c.Expect("percent 0.995", TorrentSummary.PercentText(0.995), "100%");
        c.Expect("percent over", TorrentSummary.PercentText(1.5), "100%");
        c.Expect("percent NaN", TorrentSummary.PercentText(double.NaN), "0%");
    }

    private static void DetailChecks(Checks c)
    {
        c.Expect("site: url", TorrentSummary.SiteFromAnnounceUrl("https://tracker.hdchina.org/announce.php?passkey=x"), "hdchina.org");
        c.Expect("site: udp", TorrentSummary.SiteFromAnnounceUrl("udp://tracker.example.net:6969/announce"), "example.net");
        c.Expect("site: announce prefix", TorrentSummary.SiteFromAnnounceUrl("announce.torrentleech.org"), "torrentleech.org");
        c.Expect("site: bare", TorrentSummary.SiteFromAnnounceUrl("hdchina.org"), "hdchina.org");
        c.Expect("site: uppercase", TorrentSummary.SiteFromAnnounceUrl("HTTPS://Tracker.HDChina.ORG/announce"), "hdchina.org");
        c.ExpectOptional("site: nil", TorrentSummary.SiteFromAnnounceUrl(null), null);
        c.ExpectOptional("site: blank", TorrentSummary.SiteFromAnnounceUrl("   "), null);

        TorrentSummary Torrent(string id, string? site) =>
            new() { Id = id, Name = id, State = TorrentState.Seeding, Progress = 1, Site = site };
        c.Expect("hoist: uniform", TorrentSummary.HoistedSite([Torrent("a", "hdchina.org"), Torrent("b", "hdchina.org")]), "hdchina.org");
        c.ExpectOptional("hoist: distinct", TorrentSummary.HoistedSite([Torrent("a", "hdchina.org"), Torrent("b", "other.org")]), null);
        c.ExpectOptional("hoist: missing", TorrentSummary.HoistedSite([Torrent("a", "hdchina.org"), Torrent("b", null)]), null);
        c.ExpectOptional("hoist: empty", TorrentSummary.HoistedSite([]), null);

        var full = new TorrentSummary
        {
            Id = "full", Name = "full", State = TorrentState.Downloading, Progress = 0.62,
            TotalSize = 6_600_000_000, Downloaded = 4_100_000_000, Uploaded = 9_800_000_000,
            Ratio = 2.41, Seeders = 12, Leechers = 3, Site = "hdchina.org",
            TrackerStatus = TrackerStatus.Working, Category = "Movies", ErrorMessage = "disk full",
        };
        c.Expect("detail: all lines", TorrentSummary.ExpandedLines(full),
            new List<TorrentDetailLine>
            {
                new TorrentDetailLine.SizeLine(6_600_000_000, 4_100_000_000, 0.62),
                new TorrentDetailLine.RatioLine(2.41, 9_800_000_000, 4_100_000_000),
                new TorrentDetailLine.PeersLine(12, 3),
                new TorrentDetailLine.TrackerLine(TrackerStatus.Working),
                new TorrentDetailLine.CategoryLine("Movies"),
                new TorrentDetailLine.ErrorLine("disk full"),
            });
        c.Expect("detail: empty", TorrentSummary.ExpandedLines(new TorrentSummary { Id = "b", Name = "b", State = TorrentState.Queued }), new List<TorrentDetailLine>());
        c.Expect("detail: seeders only", TorrentSummary.ExpandedLines(new TorrentSummary { Id = "p", Name = "p", State = TorrentState.Seeding, Seeders = 7 }),
            new List<TorrentDetailLine> { new TorrentDetailLine.PeersLine(7, null) });
    }

    private static void FormatChecks(Checks c)
    {
        c.Expect("format: bytes", ByteRateFormatter.Rate(999), "999 B/s");
        c.Expect("format: k", ByteRateFormatter.Rate(1024), "1.0K");
        c.Expect("format: m", ByteRateFormatter.Rate(1_258_291), "1.2M");
        c.Expect("format: hundreds", ByteRateFormatter.Rate(348_160), "340K");
        c.Expect("format: bytes count", ByteRateFormatter.Bytes(442_918_502_400), "412 GB");
        c.Expect("format: full", ByteRateFormatter.SpeedText(1_258_291, 348_160), "↓ 1.2M ↑ 340K");
        c.Expect("format: compact", ByteRateFormatter.SpeedText(1_258_291, 348_160, MenuBarTextForm.Compact), "↓1.2M ↑340K");
        var text = ByteRateFormatter.DegradedSpeedText(1_258_291, 348_160, 200, candidate => candidate.Length * 6);
        c.Expect("format: full fits", text, "↓ 1.2M ↑ 340K");
        c.ExpectOptional("format: degrades to icon", ByteRateFormatter.DegradedSpeedText(1_258_291, 348_160, 1, _ => 100), null);
    }

    private static async Task StoreChecks(Checks c)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ptmonitor-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            c.Expect("settings: empty", settings.Servers.Count, 0);
            c.Expect("settings: default interval", settings.PollInterval, 2.0);
            c.True("settings: default speeds", settings.ShowSpeedsInMenuBar);
            c.Expect("settings: default mode", settings.TorrentListMode, TorrentListMode.All);

            var server = new ServerConfig(ServerKind.Qbittorrent, "192.168.1.2", 8080) { Name = "NAS" };
            settings.Add(server);
            c.Expect("settings: add", settings.Servers.Count, 1);
            server.Name = "NAS 2";
            server.Port = 8081;
            settings.Update(server);
            c.Expect("settings: update", settings.Server(server.Id)?.Port, 8081);
            settings.SetEnabled(false, server.Id);
            c.True("settings: disable", settings.Server(server.Id)?.IsEnabled == false);
            settings.SetPollInterval(0.2);
            c.Expect("settings: clamp low", settings.PollInterval, 1.0);
            settings.SetPollInterval(999);
            c.Expect("settings: clamp high", settings.PollInterval, 60.0);
            settings.SetShowSpeedsInMenuBar(false);
            settings.SetTorrentListMode(TorrentListMode.Uploading);
            settings.SetAppLanguage(AppLanguage.Chinese);

            var reloaded = new SettingsStore(settings.FilePath);
            c.Expect("settings: reload count", reloaded.Servers.Count, 1);
            c.Expect("settings: reload interval", reloaded.PollInterval, 60.0);
            c.True("settings: reload speeds", !reloaded.ShowSpeedsInMenuBar);
            c.Expect("settings: reload mode", reloaded.TorrentListMode, TorrentListMode.Uploading);
            c.Expect("settings: reload language", reloaded.AppLanguage, AppLanguage.Chinese);
            reloaded.Delete(server.Id);
            c.Expect("settings: delete", reloaded.Servers.Count, 0);

            var slow = new TransferSnapshot { DownSpeed = 10, UpSpeed = 5, ActiveTorrents = 1, TotalTorrents = 2, ConnectionState = ConnectionState.Connected };
            var slowNewer = new TransferSnapshot { DownSpeed = 10, UpSpeed = 5, ActiveTorrents = 1, TotalTorrents = 2, ConnectionState = ConnectionState.Connected, FetchedAt = DateTimeOffset.Now.AddMinutes(5) };
            c.True("change: fetchedAt ignored", MonitorStore.IsEquivalent(new ServerStatus.Online(slow), new ServerStatus.Online(slowNewer)));
            c.True("change: speed difference publishes", !MonitorStore.IsEquivalent(new ServerStatus.Online(slow), new ServerStatus.Online(new TransferSnapshot { DownSpeed = 11 })));
            c.True("change: auth vs unreachable", !MonitorStore.IsEquivalent(
                new ServerStatus.Failed(ServerFailure.Auth("x")), new ServerStatus.Failed(ServerFailure.Unreachable("x"))));

            c.Expect("backoff: success interval", MonitorStore.RetryDelay(new ServerStatus.Online(slow), 2), 2.0);
            c.Expect("backoff: auth", MonitorStore.RetryDelay(new ServerStatus.Failed(ServerFailure.Auth("x")), 2), MonitorStore.CredentialFailureBackoff);
            c.Expect("backoff: banned", MonitorStore.RetryDelay(new ServerStatus.Failed(ServerFailure.Banned("x")), 2), MonitorStore.BannedBackoff);
            c.True("backoff: banned > auth", MonitorStore.BannedBackoff > MonitorStore.CredentialFailureBackoff);
            c.True("backoff: auth is minutes", MonitorStore.CredentialFailureBackoff >= 600);

            var ids = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var statuses = new Dictionary<Guid, ServerStatus>
            {
                [ids.Item1] = new ServerStatus.Online(slow),
                [ids.Item2] = new ServerStatus.Online(new TransferSnapshot { DownSpeed = 5, UpSpeed = 7, ActiveTorrents = 2 }),
                [ids.Item3] = new ServerStatus.Failed(ServerFailure.Auth("x")),
            };
            var aggregate = MonitorStore.AggregateOf(statuses);
            c.Expect("aggregate: down", aggregate.DownSpeed, 15L);
            c.Expect("aggregate: up", aggregate.UpSpeed, 12L);
            c.Expect("aggregate: active", aggregate.ActiveTorrents, 3);
            c.Expect("aggregate: online", aggregate.OnlineServers, 2);

            c.Expect("menu: all failing", MonitorStore.MenuBarStateOf(new Dictionary<Guid, ServerStatus> { [ids.Item1] = new ServerStatus.Failed(ServerFailure.Auth("x")) }), MenuBarState.Error);
            c.Expect("menu: active", MonitorStore.MenuBarStateOf(statuses), MenuBarState.Active);
            c.Expect("menu: idle", MonitorStore.MenuBarStateOf(new Dictionary<Guid, ServerStatus> { [ids.Item1] = new ServerStatus.Online(new TransferSnapshot()) }), MenuBarState.Idle);
            c.Expect("menu: empty", MonitorStore.MenuBarStateOf(new Dictionary<Guid, ServerStatus>()), MenuBarState.Idle);

            var secrets = new InMemorySecretStore();
            var stubConfig = new ServerConfig(ServerKind.Qbittorrent, "example.invalid") { IsEnabled = true };
            var successFactory = (ServerConfig _, string? _) => new StubClient(snapshot: slow);
            var online = await MonitorStore.PollAsync(stubConfig, successFactory, secrets);
            c.True("poll: stub online", online is ServerStatus.Online);
            var authFactory = (ServerConfig _, string? _) => new StubClient(error: ServerFailure.Auth("invalid username or password"));
            var failed = await MonitorStore.PollAsync(stubConfig, authFactory, secrets);
            c.True("poll: failure mapped", failed is ServerStatus.Failed { Failure.Kind: FailureKind.Auth });
            var counter = 0;
            var disabled = await MonitorStore.PollAsync(
                new ServerConfig(ServerKind.Transmission, "nas") { IsEnabled = false },
                (ServerConfig _, string? _) => { counter++; return new StubClient(snapshot: slow); }, secrets);
            c.True("poll: disabled", disabled is ServerStatus.Disabled);
            c.Expect("poll: disabled no client", counter, 0);

            var retryCounter = 0;
            var outcome = await RunHandshakeAsync(null, _ =>
            {
                retryCounter++;
                return Task.FromResult(retryCounter == 1
                    ? new HttpResponse(409, new Dictionary<string, string> { [SessionIdHandshake.HeaderField] = "sess-abc" }, [])
                    : new HttpResponse(200, new Dictionary<string, string>(), []));
            });
            c.Expect("409: retried once", retryCounter, 2);
            c.Expect("409: id stored", outcome.SessionId, "sess-abc");
            c.True("409: first retries", SessionIdHandshake.Step(409, "abc", false).ShouldRetry);
            c.True("409: second does not", !SessionIdHandshake.Step(409, "abc", true).ShouldRetry);
            c.True("409: 200 does not", !SessionIdHandshake.Step(200, null, false).ShouldRetry);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static async Task ClientChecks(Checks c)
    {
        var secrets = new InMemorySecretStore();
        var config = new ServerConfig(ServerKind.Qbittorrent, "example.invalid") { IsEnabled = true };
        var failure = await MonitorStore.PollAsync(config, (_, _) => new StubClient(error: ServerFailure.Unreachable("x")), secrets);
        c.True("client: unreachable mapped", failure is ServerStatus.Failed { Failure.Kind: FailureKind.Unreachable });

        var server = new ServerConfig(ServerKind.Qbittorrent, "nas.local");
        c.ExpectOptional("webui url: invalid host", TryWebUi(new ServerConfig(ServerKind.Qbittorrent, "   ")), null);
        c.Expect("webui url: transmission path", server.Kind == ServerKind.Qbittorrent ? "ok" : "no", "ok");
    }

    private static string? TryWebUi(ServerConfig config)
    {
        try { return config.WebUiUri().ToString(); }
        catch { return null; }
    }

    private static async Task<(HttpResponse Response, string? SessionId)> RunHandshakeAsync(
        string? initial, Func<string?, Task<HttpResponse>> perform)
    {
        var sessionId = initial;
        var hasRetried = false;
        while (true)
        {
            var response = await perform(sessionId);
            var decision = SessionIdHandshake.Step(response.StatusCode, response.Header(SessionIdHandshake.HeaderField), hasRetried);
            if (decision.ShouldRetry)
            {
                sessionId = decision.SessionId;
                hasRetried = true;
                continue;
            }
            return (response, decision.SessionId ?? sessionId);
        }
    }

    private sealed class StubClient : ITorrentClient
    {
        private readonly TransferSnapshot? _snapshot;
        private readonly ServerFailure? _error;

        public StubClient(TransferSnapshot? snapshot = null, ServerFailure? error = null)
        {
            _snapshot = snapshot;
            _error = error;
        }

        public Task<TransferSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken) =>
            _error is not null
                ? Task.FromException<TransferSnapshot>(new ServerFailureException(_error))
                : Task.FromResult(_snapshot ?? new TransferSnapshot());

        public Task<string> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult("stub");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
