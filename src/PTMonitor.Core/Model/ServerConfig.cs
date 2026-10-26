using PTMonitor.Core.Client;
using System.Text.Json.Serialization;

namespace PTMonitor.Core.Model;

/// <summary>Which remote API a server speaks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerKind>))]
public enum ServerKind
{
    Qbittorrent,
    Transmission,
}

public static class ServerKindExtensions
{
    public static string DisplayName(this ServerKind kind) => kind switch
    {
        ServerKind.Qbittorrent => "qBittorrent",
        ServerKind.Transmission => "Transmission",
        _ => kind.ToString(),
    };

    /// <summary>Default WebUI/RPC port (qBittorrent 8080, Transmission 9091).</summary>
    public static int DefaultPort(this ServerKind kind) => kind switch
    {
        ServerKind.Qbittorrent => 8080,
        ServerKind.Transmission => 9091,
        _ => 0,
    };

    public static string RawValue(this ServerKind kind) => kind switch
    {
        ServerKind.Qbittorrent => "qbittorrent",
        ServerKind.Transmission => "transmission",
        _ => kind.ToString().ToLowerInvariant(),
    };

    public static bool TryParse(string? raw, out ServerKind kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "qbittorrent":
                kind = ServerKind.Qbittorrent;
                return true;
            case "transmission":
                kind = ServerKind.Transmission;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>
/// One server entry, exactly as persisted in <c>settings.json</c>.
/// The password is intentionally not part of this type: it lives in
/// <c>secrets.dat</c> protected with Windows DPAPI, keyed by <see cref="Id"/>.
/// </summary>
public sealed class ServerConfig : IEquatable<ServerConfig>
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ServerKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool UseTls { get; set; }
    public bool AllowInsecureTls { get; set; }
    public string Username { get; set; } = "";
    public bool IsEnabled { get; set; } = true;

    public ServerConfig()
    {
    }

    public ServerConfig(ServerKind kind, string host = "", int? port = null, string? name = null)
    {
        Kind = kind;
        Host = host;
        Port = port ?? kind.DefaultPort();
        Name = name ?? kind.DisplayName();
    }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? Kind.DisplayName() : Name;

    public string EndpointDescription => $"{Host}:{Port}";

    public Uri WebUiUri()
    {
        var baseUri = EndpointParser.BaseUri(Host, Port, UseTls);
        return Kind == ServerKind.Transmission
            ? new Uri(baseUri, "/transmission/web/")
            : baseUri;
    }

    public bool Equals(ServerConfig? other) =>
        other is not null
        && Id == other.Id
        && Kind == other.Kind
        && Name == other.Name
        && Host == other.Host
        && Port == other.Port
        && UseTls == other.UseTls
        && AllowInsecureTls == other.AllowInsecureTls
        && Username == other.Username
        && IsEnabled == other.IsEnabled;

    public override bool Equals(object? obj) => Equals(obj as ServerConfig);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id); hash.Add(Kind); hash.Add(Name); hash.Add(Host); hash.Add(Port);
        hash.Add(UseTls); hash.Add(AllowInsecureTls); hash.Add(Username); hash.Add(IsEnabled);
        return hash.ToHashCode();
    }
}
