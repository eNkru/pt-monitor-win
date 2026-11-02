using System.Globalization;
using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

/// <summary>The host field's known values, even while an endpoint is incomplete.</summary>
public sealed record ParsedHostField(
    string Host,
    int Port,
    bool? SchemeIsTls,
    bool ExtractedPort,
    string? Username = null,
    string? Password = null,
    ServerFailure? Error = null);

/// <summary>Single owner for host, scheme, port and userinfo extraction.</summary>
public static class HostFieldParser
{
    public static ParsedHostField Parse(string rawHost, int defaultPort)
    {
        var parsed = Inspect(rawHost, defaultPort);
        if (parsed.Error is { } error) throw new ServerFailureException(error);
        return parsed;
    }

    /// <summary>Preserves known scheme/credentials when an unfinished endpoint is invalid.</summary>
    public static ParsedHostField Inspect(string rawHost, int defaultPort)
    {
        var host = rawHost.Trim();
        bool? schemeIsTls = null;
        string? username = null;
        string? password = null;
        var port = defaultPort;
        var extractedPort = false;
        ParsedHostField Result(string? error = null) => new(host, port, schemeIsTls,
            extractedPort, username, password, error is null ? null : ServerFailure.Unreachable(error));

        var schemeSeparator = host.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            var scheme = host[..schemeSeparator].ToLowerInvariant();
            if (scheme is not ("http" or "https"))
                return Result($"unsupported URL scheme \"{scheme}://\" — enter a plain host name or IP address");
            schemeIsTls = scheme == "https";
            host = host[(schemeSeparator + 3)..];
        }
        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];
        // The last @ separates userinfo, allowing an unescaped @ in the username.
        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = host[..at];
            host = host[(at + 1)..];
            var colon = userinfo.IndexOf(':');
            username = Uri.UnescapeDataString(colon >= 0 ? userinfo[..colon] : userinfo);
            if (colon >= 0) password = Uri.UnescapeDataString(userinfo[(colon + 1)..]);
        }

        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close < 0) return Result("malformed IPv6 host");
            var remainder = host[(close + 1)..];
            host = host[1..close];
            if (remainder.StartsWith(':'))
            {
                if (!int.TryParse(remainder[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                    return Result("invalid port in host field");
                extractedPort = true;
            }
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon >= 0 && !host[..colon].Contains(':'))
            {
                var portText = host[(colon + 1)..];
                host = host[..colon];
                if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                    return Result("invalid port in host field");
                extractedPort = true;
            }
        }
        if (host.Length == 0) return Result("no host configured");
        if (port is < 1 or > 65535) return Result($"invalid port {port}");
        return Result();
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

/// <summary>Runtime-only notice metadata; App translates the key and optional port.</summary>
public sealed record EditorNotice(string Key, int? Port = null)
{
    public string English => Port is { } port
        ? Key.Replace("%lld", port.ToString(CultureInfo.InvariantCulture)) : Key;
}

/// <summary>Shared preview/normalization result. Password never enters ServerConfig.</summary>
public sealed record EffectiveServerForm(ServerConfig Config, string Password,
    IReadOnlyList<EditorNotice> Notices, ServerFailure? Error);

/// <summary>Strict validation result; PasswordFromHost is runtime-only, never persisted in Config.</summary>
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
    public static EffectiveServerForm Evaluate(ServerConfig config, string portText, string? password)
    {
        portText = portText.Trim();
        var fallbackPort = config.Kind.DefaultPort();
        ServerFailure? error = null;
        if (portText.Length > 0 && (!int.TryParse(portText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out fallbackPort) || fallbackPort is < 1 or > 65535))
            error = ServerFailure.Unreachable("port must be a whole number between 1 and 65535");
        var parsed = HostFieldParser.Inspect(config.Host, fallbackPort);
        error ??= parsed.Error;
        if (config.Host.Trim().Length == 0)
            error = ServerFailure.Unreachable("enter a host name or IP address");

        var notices = new List<EditorNotice>();
        if (parsed.ExtractedPort && portText.Length > 0)
            notices.Add(new("Used port %lld from the host field.", parsed.Port));
        var useTls = parsed.SchemeIsTls ?? config.UseTls;
        if (useTls != config.UseTls)
            notices.Add(new(useTls ? "Enabled HTTPS because the host starts with https://."
                : "Disabled HTTPS because the host starts with http://."));

        var typedUser = config.Username.Trim();
        var importUser = typedUser.Length == 0 && !string.IsNullOrEmpty(parsed.Username);
        var importPassword = string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(parsed.Password);
        if (parsed.Username is not null)
            notices.Add(new((importUser, importPassword) switch
            {
                (true, true) => "Used the user name and password from the host field.",
                (true, false) => "Used the user name from the host field; kept the password field.",
                (false, true) => "Used the password from the host field; kept the user name field.",
                _ => "Kept the credential fields; ignored credentials in the host field.",
            }));

        var normalised = new ServerConfig
        {
            Id = config.Id,
            Kind = config.Kind,
            Name = config.Name.Trim(),
            Host = parsed.Host,
            Port = parsed.Port,
            UseTls = useTls,
            AllowInsecureTls = config.AllowInsecureTls,
            Username = importUser ? parsed.Username ?? "" : typedUser,
            IsEnabled = config.IsEnabled,
        };
        return new(normalised, importPassword ? parsed.Password ?? "" : password ?? "", notices, error);
    }

    public static ValidatedServerConfig Validate(ServerConfig config, string portText)
    {
        var form = Evaluate(config, portText, null);
        if (form.Error is { } error) throw new ServerFailureException(error);
        return new(form.Config, form.Notices.Select(notice => notice.English).ToArray(),
            form.Password.Length > 0 ? form.Password : null);
    }
}
