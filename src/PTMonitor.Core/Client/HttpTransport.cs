using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

public sealed record HttpResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Per-server HTTP wrapper: timeouts, cookies, Basic auth, optional self-signed
/// TLS trust, and OS-error to <see cref="ServerFailure"/> mapping.
/// </summary>
public sealed class HttpTransport : IDisposable
{
    /// <summary>Upper bound on a buffered response body.</summary>
    public const long MaxResponseBytes = 32L * 1024 * 1024;

    /// <summary>The message shown when a response is refused for its size.</summary>
    public const string TooLargeMessage = "response above 32 MiB; is this a torrent client API?";

    public Uri BaseUri { get; }

    private readonly HttpClient _client;
    private readonly string? _basicAuthorization;
    private readonly Action<string>? _onCertificateAccepted;
    private bool _disposed;

    /// <summary>The last leaf-certificate thumbprint accepted in insecure mode, if any.</summary>
    public string? ObservedThumbprint { get; private set; }

    public HttpTransport(ServerConfig config, string? password, Action<string>? onCertificateAccepted = null)
    {
        BaseUri = EndpointParser.BaseUri(config.Host, config.Port, config.UseTls);
        _onCertificateAccepted = onCertificateAccepted;

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (config.AllowInsecureTls)
        {
            // Same accept-anything semantics as DangerousAcceptAnyServerCertificateValidator,
            // but the accepted leaf certificate is recorded so a swap is visible.
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                ObservedThumbprint = thumbprint;
                _onCertificateAccepted?.Invoke(thumbprint);
                return true;
            };
        }

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            // A sync/maindata for thousands of torrents is a few MB; anything
            // past this is not a snapshot and must not be buffered.
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PTMonitor/1.0");

        var passwordValue = password ?? "";
        if (config.Username.Length > 0 || passwordValue.Length > 0)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{passwordValue}"));
            _basicAuthorization = "Basic " + token;
        }
    }

    /// <summary>Builds an absolute URL under the base (e.g. <c>/api/v2/auth/login</c>).</summary>
    public Uri Url(string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        var builder = new UriBuilder(BaseUri)
        {
            Path = path,
            Query = query is null
                ? ""
                : string.Join("&", query.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    public void UseBasicAuthorization(HttpRequestMessage request)
    {
        if (_basicAuthorization is null) return;
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(_basicAuthorization);
    }

    public async Task<HttpResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
            foreach (var header in response.Content.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
            return new HttpResponse((int)response.StatusCode, headers, body);
        }
        catch (ServerFailureException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServerFailureException(FailureKind.Unreachable, "timed out");
        }
        catch (HttpRequestException exception)
        {
            if (IsResponseTooLarge(exception.Message))
                throw new ServerFailureException(FailureKind.ProtocolViolation, TooLargeMessage);

            var message = Describe(exception);
            // With HTTPS on by default, a plain-HTTP server fails here with a TLS
            // error whose stock hint ("allow self-signed certificate") is wrong.
            // Point at the toggle that actually fixes it.
            if (BaseUri.Scheme == Uri.UriSchemeHttp
                && message.StartsWith("TLS handshake failed", StringComparison.Ordinal))
                message += "; or uncheck \"Use HTTPS\" if the server speaks plain HTTP";
            throw new ServerFailureException(FailureKind.Unreachable, message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ServerFailureException(FailureKind.Unreachable, exception.Message);
        }
    }

    /// <summary>
    /// Whether a framework error is the buffer cap being hit. The wording comes
    /// from HttpClient ("Cannot write more bytes to the buffer than the configured
    /// maximum buffer size"), so match it loosely and only in our own cap's terms.
    /// </summary>
    public static bool IsResponseTooLarge(string message) =>
        message.Contains("maximum buffer size", StringComparison.OrdinalIgnoreCase)
        || message.Contains("MaxResponseContentBufferSize", StringComparison.OrdinalIgnoreCase);

    private static string Describe(HttpRequestException exception)
    {
        var text = exception.Message.ToLowerInvariant();
        if (text.Contains("name or service") || text.Contains("host") && text.Contains("not known"))
            return "host not found";
        if (text.Contains("refused")) return "connection refused";
        if (text.Contains("timed out") || text.Contains("timeout")) return "timed out";
        if (text.Contains("certificate") || text.Contains("ssl") || text.Contains("tls"))
            return "TLS handshake failed (enable \"allow self-signed certificate\" if the server uses one)";
        return exception.Message;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
