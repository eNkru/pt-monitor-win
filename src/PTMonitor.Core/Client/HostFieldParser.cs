using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

/// <summary>The (host, port) pair a user's host field actually denotes.</summary>
public sealed record ParsedHostField(
    string Host,
    int Port,
    bool? SchemeIsTls,
    bool ExtractedPort,
    string? Username = null,
    string? Password = null);

/// <summary>Single owner for turning a typed host into (host, port).</summary>
public static class HostFieldParser
{
    public static ParsedHostField Parse(string rawHost, int defaultPort)
    {
        var host = rawHost.Trim();
        if (host.Length == 0)
            throw new ServerFailureException(FailureKind.Unreachable, "no host configured");

        bool? schemeIsTls = null;
        var schemeSeparator = host.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            var scheme = host[..schemeSeparator].ToLowerInvariant();
            if (scheme is not ("http" or "https"))
                throw new ServerFailureException(FailureKind.Unreachable,
                    $"unsupported URL scheme \"{scheme}://\" — enter a plain host name or IP address");
            schemeIsTls = scheme == "https";
            host = host[(schemeSeparator + 3)..];
        }

        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];

        // `user:pass@host` — the last `@` separates the userinfo, so an unescaped
        // `@` inside the user name still resolves to the right host.
        string? username = null;
        string? password = null;
        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = host[..at];
            host = host[(at + 1)..];
            var colon = userinfo.IndexOf(':');
            username = Uri.UnescapeDataString(colon >= 0 ? userinfo[..colon] : userinfo);
            if (colon >= 0) password = Uri.UnescapeDataString(userinfo[(colon + 1)..]);
        }

        var port = defaultPort;
        var extractedPort = false;

        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close < 0)
                throw new ServerFailureException(FailureKind.Unreachable, $"malformed IPv6 host \"{rawHost}\"");
            var address = host[1..close];
            var remainder = host[(close + 1)..];
            if (remainder.StartsWith(':'))
            {
                if (!int.TryParse(remainder[1..], out var explicitPort))
                    throw new ServerFailureException(FailureKind.Unreachable, $"invalid port in \"{rawHost}\"");
                port = explicitPort;
                extractedPort = true;
            }
            host = address;
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon >= 0 && !host[..colon].Contains(':'))
            {
                if (!int.TryParse(host[(colon + 1)..], out var explicitPort))
                    throw new ServerFailureException(FailureKind.Unreachable, $"invalid port in \"{rawHost}\"");
                port = explicitPort;
                extractedPort = true;
                host = host[..colon];
            }
        }

        if (host.Length == 0)
            throw new ServerFailureException(FailureKind.Unreachable, $"invalid host \"{rawHost}\"");
        if (port is < 1 or > 65535)
            throw new ServerFailureException(FailureKind.Unreachable, $"invalid port {port}");

        return new ParsedHostField(host, port, schemeIsTls, extractedPort, username, password);
    }
}

public static class EndpointParser
{
    public static Uri BaseUri(string rawHost, int configuredPort, bool useTls)
    {
        var parsed = HostFieldParser.Parse(rawHost, configuredPort);
        var builder = new UriBuilder
        {
            Scheme = useTls ? "https" : "http",
            Host = parsed.Host,
            Port = parsed.Port,
            Path = "/",
        };
        return builder.Uri;
    }
}

/// <summary>
/// A normalised config plus the adjustments the user should be told about.
/// <para>
/// <see cref="PasswordFromHost"/> carries the password parsed out of
/// <c>user:pass@host</c>; it is not part of <see cref="ServerConfig"/> and only
/// exists so the caller can hand it to the secret store.
/// </para>
/// </summary>
public sealed record ValidatedServerConfig(
    ServerConfig Config,
    IReadOnlyList<string> Notices,
    string? PasswordFromHost = null)
{
    /// <summary>Convenience for callers that only show the first notice.</summary>
    public string? Notice => Notices.Count == 0 ? null : Notices[0];
}

public static class ServerConfigValidator
{
    public static ValidatedServerConfig Validate(ServerConfig config, string portText)
    {
        var rawHost = config.Host.Trim();
        if (rawHost.Length == 0)
            throw new ServerFailureException(FailureKind.Unreachable, "enter a host name or IP address");

        portText = portText.Trim();
        int fallbackPort;
        if (portText.Length == 0)
        {
            fallbackPort = config.Kind.DefaultPort();
        }
        else
        {
            if (!int.TryParse(portText, out var value) || value is < 1 or > 65535)
                throw new ServerFailureException(FailureKind.Unreachable,
                    "port must be a whole number between 1 and 65535");
            fallbackPort = value;
        }

        var parsed = HostFieldParser.Parse(rawHost, fallbackPort);
        var notices = new List<string>(2);
        if (parsed.ExtractedPort && portText.Length > 0)
            notices.Add($"Used port {parsed.Port} from the host field.");

        var useTls = config.UseTls;
        if (parsed.SchemeIsTls is { } schemeIsTls)
        {
            if (schemeIsTls != useTls)
            {
                notices.Add(schemeIsTls
                    ? "Enabled HTTPS because the host starts with https://."
                    : "Disabled HTTPS because the host starts with http://.");
            }
            useTls = schemeIsTls;
        }

        if (parsed.Username is not null)
            notices.Add("Used the user name and password from the host field.");

        var normalised = new ServerConfig
        {
            Id = config.Id,
            Kind = config.Kind,
            Name = config.Name.Trim(),
            Host = parsed.Host,
            Port = parsed.Port,
            UseTls = useTls,
            AllowInsecureTls = config.AllowInsecureTls,
            Username = config.Username.Trim() is { Length: > 0 } typed ? typed : parsed.Username ?? "",
            IsEnabled = config.IsEnabled,
        };
        return new ValidatedServerConfig(normalised, notices, parsed.Password);
    }
}
