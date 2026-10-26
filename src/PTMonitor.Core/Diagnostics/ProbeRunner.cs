using System.Text.Json;
using System.Text.Json.Nodes;
using PTMonitor.Core.Client;
using PTMonitor.Core.Model;

namespace PTMonitor.Core.Diagnostics;

/// <summary>Headless single-server probe: prints one snapshot as JSON.</summary>
public static class ProbeRunner
{
    public sealed record Options(
        ServerKind Kind,
        string Host,
        int Port,
        string Username = "",
        string Password = "",
        bool UseTls = false,
        bool AllowInsecureTls = false);

    public sealed class UsageError(string detail) : Exception(detail);

    public const string Usage = """
        Usage: PTMonitor.Cli --probe <qbittorrent|transmission> --host <host> [options]

          --probe <type>     Server type: qbittorrent or transmission (required)
          --host <host>      Host name, IP address, or URL (required)
          --port <port>      Server port (default: 8080 / 9091 per type)
          --user <username>  User name
          --pass <password>  Password
          --https            Use HTTPS
          --insecure         Allow a self-signed certificate

        Prints the fetched snapshot as JSON on success.
        """;

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Contains("--help") || arguments.Contains("-h"))
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        Options options;
        try
        {
            options = Parse(arguments);
        }
        catch (UsageError error)
        {
            Console.Error.WriteLine($"probe: {error.Message}\n{Usage}");
            return 2;
        }

        var config = new ServerConfig
        {
            Kind = options.Kind,
            Name = options.Kind.DisplayName(),
            Host = options.Host,
            Port = options.Port,
            UseTls = options.UseTls,
            AllowInsecureTls = options.AllowInsecureTls,
            Username = options.Username,
            IsEnabled = true,
        };

        ITorrentClient? client = null;
        try
        {
            client = options.Kind == ServerKind.Qbittorrent
                ? new QBittorrentClient(config, options.Password)
                : new TransmissionClient(config, options.Password);
            var snapshot = await client.FetchSnapshotAsync(CancellationToken.None);
            Console.Out.WriteLine(Encode(snapshot, config));
            return 0;
        }
        catch (ServerFailureException failure)
        {
            Console.Error.WriteLine($"probe: {failure.Failure.KindLabel}: {failure.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"probe: {exception.Message}");
            return 1;
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
        }
    }

    public static Options Parse(string[] arguments)
    {
        ServerKind? kind = null;
        string? host = null;
        int? port = null;
        var username = "";
        var password = "";
        var useTls = false;
        var allowInsecureTls = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            string Next(string flag)
            {
                if (++index >= arguments.Length) throw new UsageError($"{flag} requires a value");
                return arguments[index];
            }

            switch (argument)
            {
                case "--probe":
                    var raw = Next("--probe");
                    if (!ServerKindExtensions.TryParse(raw, out var parsedKind))
                        throw new UsageError($"unknown server type \"{raw}\" (expected qbittorrent or transmission)");
                    kind = parsedKind;
                    break;
                case "--host":
                    host = Next("--host");
                    break;
                case "--port":
                    var rawPort = Next("--port");
                    if (!int.TryParse(rawPort, out var parsedPort) || parsedPort is < 1 or > 65535)
                        throw new UsageError($"invalid port \"{rawPort}\" (expected 1-65535)");
                    port = parsedPort;
                    break;
                case "--user":
                    username = Next("--user");
                    break;
                case "--pass":
                    password = Next("--pass");
                    break;
                case "--https":
                    useTls = true;
                    break;
                case "--insecure":
                    allowInsecureTls = true;
                    break;
                default:
                    throw new UsageError($"unexpected argument \"{argument}\"");
            }
        }

        if (kind is null) throw new UsageError("missing --probe <qbittorrent|transmission>");
        if (string.IsNullOrWhiteSpace(host)) throw new UsageError("missing --host <host>");

        return new Options(kind.Value, host, port ?? kind.Value.DefaultPort(), username, password, useTls, allowInsecureTls);
    }

    public static string Encode(TransferSnapshot snapshot, ServerConfig config)
    {
        var root = new JsonObject
        {
            ["server"] = new JsonObject
            {
                ["kind"] = config.Kind.RawValue(),
                ["name"] = config.DisplayName,
                ["host"] = config.Host,
                ["port"] = config.Port,
                ["tls"] = config.UseTls,
            },
            ["snapshot"] = new JsonObject
            {
                ["downSpeed"] = snapshot.DownSpeed,
                ["upSpeed"] = snapshot.UpSpeed,
                ["activeTorrents"] = snapshot.ActiveTorrents,
                ["totalTorrents"] = snapshot.TotalTorrents,
                ["freeSpace"] = snapshot.FreeSpace,
                ["connectionState"] = snapshot.ConnectionState.ToString().ToLowerInvariant(),
                ["fetchedAt"] = snapshot.FetchedAt.ToUniversalTime().ToString("O"),
                ["torrents"] = new JsonArray(snapshot.Torrents.Select(torrent => (JsonNode)new JsonObject
                {
                    ["id"] = torrent.Id,
                    ["name"] = torrent.Name,
                    ["state"] = torrent.State.ToString().ToLowerInvariant(),
                    ["progress"] = torrent.Progress,
                    ["downSpeed"] = torrent.DownSpeed,
                    ["upSpeed"] = torrent.UpSpeed,
                    ["etaSeconds"] = torrent.EtaSeconds,
                }).ToArray()),
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
