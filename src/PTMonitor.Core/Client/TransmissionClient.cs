using System.Text;
using System.Text.Json.Nodes;
using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

public static class TransmissionParser
{
    /// <summary>Explicit field list required by the old torrent-get protocol.</summary>
    public static readonly string[] TorrentFields =
    [
        "id", "name", "status", "percentDone", "rateDownload", "rateUpload",
        "eta", "error", "errorString",
        "totalSize", "downloadedEver", "uploadedEver", "uploadRatio",
        "peersSendingToUs", "peersGettingFromUs", "trackers",
    ];

    public static TransferSnapshot Snapshot(byte[] data, DateTimeOffset? fetchedAt = null)
    {
        var root = Json.Object(data);
        if (Json.String(root["result"]) is { } result && result != "success")
            throw new ServerFailureException(FailureKind.ServerError, result);
        if (root["arguments"] is not JsonObject arguments)
            throw new ServerFailureException(FailureKind.ProtocolViolation,
                "session-stats response is missing arguments");
        return SnapshotFromSessionStats(arguments, fetchedAt);
    }

    public static TransferSnapshot SnapshotFromSessionStats(JsonObject arguments, DateTimeOffset? fetchedAt = null) =>
        new()
        {
            DownSpeed = Json.Int64(arguments["downloadSpeed"]) ?? 0,
            UpSpeed = Json.Int64(arguments["uploadSpeed"]) ?? 0,
            ActiveTorrents = (int)(Json.Int64(arguments["activeTorrentCount"]) ?? 0),
            TotalTorrents = (int)(Json.Int64(arguments["torrentCount"]) ?? 0),
            FreeSpace = null,
            ConnectionState = ConnectionState.Connected,
            FetchedAt = fetchedAt ?? DateTimeOffset.Now,
        };

    public static long? FreeSpace(JsonObject arguments) => Json.Int64(arguments["size-bytes"]);

    public static TorrentState State(int? status) => status switch
    {
        0 => TorrentState.Paused,
        1 or 2 => TorrentState.Checking,
        3 or 5 => TorrentState.Queued,
        4 => TorrentState.Downloading,
        6 => TorrentState.Seeding,
        _ => TorrentState.Other,
    };

    public static long? EtaSeconds(JsonNode? value)
    {
        var seconds = Json.Int64(value);
        return seconds is null or < 0 ? null : seconds;
    }

    public static List<TorrentSummary> Summaries(JsonArray torrents)
    {
        var result = new List<TorrentSummary>(torrents.Count);
        foreach (var node in torrents)
        {
            if (node is not JsonObject entry) continue;
            var errorCode = (int)(Json.Int64(entry["error"]) ?? 0);
            var state = errorCode != 0
                ? TorrentState.Error
                : State((int?)Json.Int64(entry["status"]));
            var id = Json.String(entry["id"]) ?? (Json.Int64(entry["id"]) ?? -1).ToString();
            result.Add(new TorrentSummary
            {
                Id = id,
                Name = Json.String(entry["name"]) ?? id,
                State = state,
                Progress = TorrentSummary.ClampedProgress(Json.Double(entry["percentDone"])),
                DownSpeed = Json.Int64(entry["rateDownload"]) ?? 0,
                UpSpeed = Json.Int64(entry["rateUpload"]) ?? 0,
                EtaSeconds = EtaSeconds(entry["eta"]),
                TotalSize = Json.Int64(entry["totalSize"]),
                Downloaded = Json.Int64(entry["downloadedEver"]),
                Uploaded = Json.Int64(entry["uploadedEver"]),
                Ratio = Ratio(entry["uploadRatio"]),
                Seeders = (int?)Json.Int64(entry["peersSendingToUs"]),
                Leechers = (int?)Json.Int64(entry["peersGettingFromUs"]),
                Site = TorrentSummary.SiteFromAnnounceUrl(FirstAnnounceUrl(entry["trackers"])),
                TrackerStatus = null,
                Category = null,
                ErrorMessage = errorCode == 0 ? null : Json.String(entry["errorString"]),
            });
        }
        return result;
    }

    private static string? FirstAnnounceUrl(JsonNode? value) =>
        value is JsonArray { Count: > 0 } array && array[0] is JsonObject first
            ? Json.String(first["announce"])
            : null;

    public static double? Ratio(JsonNode? value)
    {
        var ratio = Json.Double(value);
        return ratio is < 0 ? null : ratio;
    }
}

/// <summary>Transmission RPC client. Uses the old camelCase protocol on purpose.</summary>
public sealed class TransmissionClient : ITorrentClient
{
    private static readonly TimeSpan FreeSpaceRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly HttpTransport _transport;
    private readonly string? _basicAuthorization;
    private string? _sessionId;
    private string? _downloadDirectory;
    private long? _cachedFreeSpace;
    private DateTimeOffset? _lastFreeSpaceFetch;

    public TransmissionClient(ServerConfig config, string? password)
    {
        _transport = new HttpTransport(config, password);
        var passwordValue = password ?? "";
        if (config.Username.Length > 0 || passwordValue.Length > 0)
        {
            _basicAuthorization = "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.Username}:{passwordValue}"));
        }
    }

    public async Task<TransferSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken)
    {
        var stats = await RpcAsync("session-stats", null, cancellationToken);
        var snapshot = TransmissionParser.SnapshotFromSessionStats(stats);

        var torrentArguments = await RpcAsync("torrent-get",
            new JsonObject { ["fields"] = new JsonArray(TransmissionParser.TorrentFields.Select(f => JsonValue.Create(f)).ToArray()) },
            cancellationToken);
        if (torrentArguments["torrents"] is JsonArray list)
            snapshot.Torrents = TorrentSummary.Sorted(TransmissionParser.Summaries(list));

        if (await FreeSpaceIfDueAsync(cancellationToken) is { } freeSpace)
            snapshot.FreeSpace = freeSpace;
        return snapshot;
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var session = await RpcAsync("session-get", null, cancellationToken);
        var version = Json.String(session["version"]) ?? "unknown";
        return $"Transmission {version}";
    }

    private async Task<JsonObject> RpcAsync(string method, JsonObject? arguments, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["method"] = method,
            ["arguments"] = arguments ?? [],
            ["tag"] = 1,
        };
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var url = _transport.Url("/transmission/rpc");

        var (response, sessionId) = await RunHandshakeAsync(async currentSessionId =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (_basicAuthorization is not null)
                request.Headers.Authorization =
                    System.Net.Http.Headers.AuthenticationHeaderValue.Parse(_basicAuthorization);
            if (currentSessionId is not null)
                request.Headers.TryAddWithoutValidation(SessionIdHandshake.HeaderField, currentSessionId);
            return await _transport.SendAsync(request, cancellationToken);
        }, cancellationToken);
        _sessionId = sessionId;

        switch (response.StatusCode)
        {
            case 200:
                break;
            case 401:
                throw new ServerFailureException(FailureKind.Auth, "Transmission authentication failed");
            case 403:
                throw new ServerFailureException(FailureKind.Banned,
                    "this IP is not allowed by rpc-whitelist, or it is temporarily locked out after failed auth attempts");
            case 409:
                throw new ServerFailureException(FailureKind.ServerError,
                    "Transmission kept rejecting the RPC session id");
            case 421:
                throw new ServerFailureException(FailureKind.ServerError,
                    "host not in rpc-host-whitelist — add it to rpc-host-whitelist or connect by IP address");
            default:
                throw new ServerFailureException(FailureKind.ServerError,
                    $"Transmission RPC returned HTTP {response.StatusCode}");
        }

        var root = Json.Object(response.Body);
        if (Json.String(root["result"]) is { } result && result != "success")
            throw new ServerFailureException(FailureKind.ServerError, result);
        if (root["arguments"] is not JsonObject argumentsObject)
            throw new ServerFailureException(FailureKind.ProtocolViolation, $"{method}: response is missing arguments");
        return argumentsObject;
    }

    private async Task<(HttpResponse Response, string? SessionId)> RunHandshakeAsync(
        Func<string?, Task<HttpResponse>> perform, CancellationToken cancellationToken)
    {
        var sessionId = _sessionId;
        var hasRetried = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await perform(sessionId);
            var decision = SessionIdHandshake.Step(
                response.StatusCode, response.Header(SessionIdHandshake.HeaderField), hasRetried);
            if (decision.ShouldRetry)
            {
                sessionId = decision.SessionId;
                hasRetried = true;
                continue;
            }
            return (response, decision.SessionId ?? sessionId);
        }
    }

    private async Task<long?> FreeSpaceIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (_lastFreeSpaceFetch is { } last && now - last < FreeSpaceRefreshInterval)
            return _cachedFreeSpace;

        try
        {
            if (_downloadDirectory is null)
            {
                var session = await RpcAsync("session-get", null, cancellationToken);
                _downloadDirectory = Json.String(session["download-dir"]);
            }
            if (_downloadDirectory is null)
            {
                _lastFreeSpaceFetch = now;
                _cachedFreeSpace = null;
                return null;
            }
            var arguments = await RpcAsync("free-space",
                new JsonObject { ["path"] = _downloadDirectory }, cancellationToken);
            _cachedFreeSpace = TransmissionParser.FreeSpace(arguments);
            _lastFreeSpaceFetch = now;
            return _cachedFreeSpace;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _lastFreeSpaceFetch = now;
            _cachedFreeSpace = null;
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }
}

public readonly record struct HandshakeStep(bool ShouldRetry, string? SessionId);

public static class SessionIdHandshake
{
    public const string HeaderField = "X-Transmission-Session-Id";

    public static HandshakeStep Step(int statusCode, string? sessionIdHeader, bool hasRetried)
    {
        var header = string.IsNullOrEmpty(sessionIdHeader) ? null : sessionIdHeader;
        if (statusCode != 409 || hasRetried || header is null)
            return new HandshakeStep(false, header);
        return new HandshakeStep(true, header);
    }
}
