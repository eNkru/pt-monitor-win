using System.Text;
using System.Text.Json.Nodes;
using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

public readonly record struct WebApiVersion(int Major, int Minor, int Patch) : IComparable<WebApiVersion>
{
    public static readonly WebApiVersion V2_0_0 = new(2, 0, 0);
    public static readonly WebApiVersion V2_1_1 = new(2, 1, 1);

    public int CompareTo(WebApiVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(WebApiVersion left, WebApiVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(WebApiVersion left, WebApiVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(WebApiVersion left, WebApiVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(WebApiVersion left, WebApiVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public static class QBittorrentParser
{
    public static WebApiVersion? WebApiVersionFrom(string text)
    {
        var parts = text.Trim().Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return null;
        var patch = parts.Length >= 3 && int.TryParse(parts[2], out var parsed) ? parsed : 0;
        return new WebApiVersion(major, minor, patch);
    }

    public static ConnectionState ConnectionStateFrom(string? raw) => raw?.ToLowerInvariant() switch
    {
        "connected" => ConnectionState.Connected,
        "firewalled" => ConnectionState.Firewalled,
        "disconnected" => ConnectionState.Disconnected,
        _ => ConnectionState.Unknown,
    };

    /// <summary>Maps a login response to a failure, or null on success (5.1 and 5.2 shapes).</summary>
    public static ServerFailure? LoginFailure(int statusCode, string body)
    {
        var trimmed = body.Trim();
        return statusCode switch
        {
            204 => null,
            200 => trimmed switch
            {
                "Ok." => null,
                "Fails." => ServerFailure.Auth("invalid username or password"),
                _ => ServerFailure.ProtocolViolation($"unexpected login response \"{trimmed}\""),
            },
            401 => ServerFailure.Auth("invalid username or password"),
            403 => ServerFailure.Banned(
                "the WebUI has banned this PC's IP after too many failed logins — wait for the ban to expire "
                + "(or restart qBittorrent on the server), then check the user name and password"),
            405 => ServerFailure.ProtocolViolation("login endpoint rejected the request method"),
            _ => ServerFailure.ServerError($"login failed with HTTP {statusCode}"),
        };
    }

    /// <summary>A 403 outside auth/login means a rejected session, not a ban.</summary>
    public static ServerFailure? EndpointFailure(int statusCode, string context) => statusCode switch
    {
        200 => null,
        401 => ServerFailure.Auth("session rejected; credentials may be wrong"),
        403 => ServerFailure.ProtocolViolation(
            $"{context}: the WebUI rejected an authenticated request (HTTP 403) — the session cookie was not accepted"),
        404 => ServerFailure.ProtocolViolation($"{context}: endpoint not found — is this a qBittorrent WebUI?"),
        _ => ServerFailure.ServerError($"{context}: HTTP {statusCode}"),
    };

    public static TorrentState State(string? raw) => raw switch
    {
        "downloading" or "metaDL" or "forcedDL" => TorrentState.Downloading,
        "stalledDL" => TorrentState.Stalled,
        "queuedDL" or "queuedUP" => TorrentState.Queued,
        "uploading" or "forcedUP" or "stalledUP" => TorrentState.Seeding,
        "pausedDL" or "pausedUP" => TorrentState.Paused,
        "checkingDL" or "checkingUP" or "checkingResumeData" => TorrentState.Checking,
        "error" or "missingFiles" => TorrentState.Error,
        _ => TorrentState.Other,
    };

    public static long? EtaSeconds(JsonNode? value)
    {
        var seconds = Json.Int64(value);
        if (seconds is null or < 0 || seconds == 8_640_000) return null;
        return seconds;
    }

    public static List<TorrentSummary> Summaries(JsonObject torrents, JsonObject? trackers = null)
    {
        var fallbackTrackers = TrackersByHash(trackers);
        var result = new List<TorrentSummary>(torrents.Count);
        foreach (var (hash, value) in torrents)
        {
            if (value is not JsonObject entry) continue;
            var announce = Json.String(entry["tracker"]) ?? fallbackTrackers.GetValueOrDefault(hash);
            result.Add(new TorrentSummary
            {
                Id = hash,
                Name = Json.String(entry["name"]) ?? hash,
                State = State(Json.String(entry["state"])),
                Progress = TorrentSummary.ClampedProgress(Json.Double(entry["progress"])),
                DownSpeed = Json.Int64(entry["dlspeed"]) ?? 0,
                UpSpeed = Json.Int64(entry["upspeed"]) ?? 0,
                EtaSeconds = EtaSeconds(entry["eta"]),
                TotalSize = Json.Int64(entry["total_size"]),
                Downloaded = Json.Int64(entry["downloaded"]),
                Uploaded = Json.Int64(entry["uploaded"]),
                Ratio = Ratio(entry["ratio"]),
                Seeders = Count(entry["num_seeds"], entry["num_complete"]),
                Leechers = Count(entry["num_leechs"], entry["num_incomplete"]),
                Site = TorrentSummary.SiteFromAnnounceUrl(announce),
                TrackerStatus = TrackerStatusFrom(entry["has_tracker_error"], !string.IsNullOrEmpty(announce)),
                Category = Category(entry["category"]),
            });
        }
        return result;
    }

    /// <summary>Inverts announce URL → [hash] into hash → announce URL.</summary>
    public static Dictionary<string, string> TrackersByHash(JsonObject? trackers)
    {
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        if (trackers is null) return byHash;
        foreach (var url in trackers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal))
        {
            if (trackers[url] is not JsonArray hashes) continue;
            foreach (var hashNode in hashes)
            {
                if (Json.String(hashNode) is not { } hash || byHash.ContainsKey(hash)) continue;
                byHash[hash] = url;
            }
        }
        return byHash;
    }

    public static double? Ratio(JsonNode? value)
    {
        var ratio = Json.Double(value);
        return ratio is < 0 ? null : ratio;
    }

    private static int? Count(JsonNode? primary, JsonNode? fallback)
    {
        var value = Json.Int64(primary) ?? Json.Int64(fallback);
        return value is null ? null : (int)value.Value;
    }

    public static TrackerStatus? TrackerStatusFrom(JsonNode? hasError, bool hasAnnounceUrl)
    {
        if (!hasAnnounceUrl) return null;
        return Json.Bool(hasError) switch
        {
            true => TrackerStatus.NotWorking,
            false => TrackerStatus.Working,
            null => null,
        };
    }

    public static string? Category(JsonNode? value)
    {
        var category = Json.String(value);
        return string.IsNullOrEmpty(category) ? null : category;
    }

    public static TransferSnapshot Snapshot(byte[] data, WebApiVersion? webApiVersion, DateTimeOffset? fetchedAt = null)
    {
        var root = Json.Object(data);
        if (root["server_state"] is not JsonObject serverState)
            throw new ServerFailureException(FailureKind.ProtocolViolation,
                "sync/maindata response is missing server_state");

        long? freeSpace = null;
        if (webApiVersion is { } version && version >= WebApiVersion.V2_1_1)
            freeSpace = Json.Int64(serverState["free_space_on_disk"]);

        var torrents = root["torrents"] as JsonObject ?? [];
        var summaries = Summaries(torrents, root["trackers"] as JsonObject);
        var active = summaries.Count(summary =>
            summary.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Stalled);

        return new TransferSnapshot
        {
            DownSpeed = Json.Int64(serverState["dl_info_speed"]) ?? 0,
            UpSpeed = Json.Int64(serverState["up_info_speed"]) ?? 0,
            ActiveTorrents = active,
            TotalTorrents = torrents.Count,
            FreeSpace = freeSpace,
            ConnectionState = ConnectionStateFrom(Json.String(serverState["connection_status"])),
            FetchedAt = fetchedAt ?? DateTimeOffset.Now,
            Torrents = TorrentSummary.Sorted(summaries),
        };
    }
}

/// <summary>qBittorrent WebUI API v2 client.</summary>
public sealed class QBittorrentClient : ITorrentClient
{
    private readonly ServerConfig _config;
    private readonly string _password;
    private readonly HttpTransport _transport;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private WebApiVersion? _cachedWebApiVersion;
    private bool _loggedIn;

    public QBittorrentClient(ServerConfig config, string? password)
    {
        _config = config;
        _password = password ?? "";
        _transport = new HttpTransport(config, password);
    }

    public async Task<TransferSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken);
        var version = await GetWebApiVersionAsync(cancellationToken);
        var response = await AuthorizedGetAsync("/api/v2/sync/maindata",
            [new KeyValuePair<string, string>("rid", "0")], cancellationToken);
        CheckStatus(response, "sync/maindata");
        return QBittorrentParser.Snapshot(response.Body, version);
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken);
        var version = await GetWebApiVersionAsync(cancellationToken);
        var response = await AuthorizedGetAsync("/api/v2/app/version", null, cancellationToken);
        CheckStatus(response, "app/version");
        return $"qBittorrent {response.Text.Trim()} (WebAPI {version})";
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn) return;
        await LoginAsync(cancellationToken);
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (_loggedIn) return;
            var request = new HttpRequestMessage(HttpMethod.Post, _transport.Url("/api/v2/auth/login"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = _config.Username,
                    ["password"] = _password,
                }),
            };
            var response = await _transport.SendAsync(request, cancellationToken);
            if (QBittorrentParser.LoginFailure(response.StatusCode, response.Text) is { } failure)
            {
                _loggedIn = false;
                throw new ServerFailureException(failure);
            }
            _loggedIn = true;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>Retries once after a 401/403 by re-logging in; never loops.</summary>
    private async Task<HttpResponse> AuthorizedGetAsync(string path,
        IEnumerable<KeyValuePair<string, string>>? query, CancellationToken cancellationToken)
    {
        var didRetry = false;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _transport.Url(path, query));
            var response = await _transport.SendAsync(request, cancellationToken);
            if (response.StatusCode is 401 or 403 && !didRetry)
            {
                _loggedIn = false;
                await LoginAsync(cancellationToken);
                didRetry = true;
                continue;
            }
            return response;
        }
    }

    private async Task<WebApiVersion> GetWebApiVersionAsync(CancellationToken cancellationToken)
    {
        if (_cachedWebApiVersion is { } cached) return cached;
        var response = await AuthorizedGetAsync("/api/v2/app/webapiVersion", null, cancellationToken);
        CheckStatus(response, "app/webapiVersion");
        var version = QBittorrentParser.WebApiVersionFrom(response.Text)
            ?? throw new ServerFailureException(FailureKind.ProtocolViolation,
                $"could not parse WebAPI version \"{response.Text}\"");
        if (version < WebApiVersion.V2_0_0)
            throw new ServerFailureException(FailureKind.ProtocolViolation,
                $"qBittorrent WebAPI 2.0 or newer is required (server reports {version})");
        _cachedWebApiVersion = version;
        return version;
    }

    private static void CheckStatus(HttpResponse response, string context)
    {
        if (QBittorrentParser.EndpointFailure(response.StatusCode, context) is { } failure)
            throw new ServerFailureException(failure);
    }

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        _loginLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
