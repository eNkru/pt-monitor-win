using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

/// <summary>The (host, port) pair a user's host field actually denotes.</summary>
public sealed record ParsedHostField(string Host, int Port, bool? SchemeIsTls, bool ExtractedPort);

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
        var at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];

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

        return new ParsedHostField(host, port, schemeIsTls, extractedPort);
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

/// <summary>A normalised config plus the one adjustment the user should be told about.</summary>
public sealed record ValidatedServerConfig(ServerConfig Config, string? Notice);

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
        string? notice = null;
        if (parsed.ExtractedPort && portText.Length > 0)
            notice = $"Used port {parsed.Port} from the host field.";

        var useTls = config.UseTls;
        if (parsed.SchemeIsTls is { } schemeIsTls)
        {
            if (schemeIsTls != useTls && notice is null)
            {
                notice = schemeIsTls
                    ? "Enabled HTTPS because the host starts with https://."
                    : "Disabled HTTPS because the host starts with http://.";
            }
            useTls = schemeIsTls;
        }

        var normalised = new ServerConfig
        {
            Id = config.Id,
            Kind = config.Kind,
            Name = config.Name.Trim(),
            Host = parsed.Host,
            Port = parsed.Port,
            UseTls = useTls,
            AllowInsecureTls = config.AllowInsecureTls,
            Username = config.Username.Trim(),
            IsEnabled = config.IsEnabled,
        };
        return new ValidatedServerConfig(normalised, notice);
    }
}
