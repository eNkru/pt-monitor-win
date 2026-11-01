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
            // Redirects are followed by hand below: .NET's RedirectHandler clears
            // Authorization and routes the hop through a handler with credential
            // auth disabled, so a same-origin redirect would silently lose auth
            // and no handler-level Credentials setting can restore it.
            AllowAutoRedirect = false,
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
            using var response = await SendFollowingRedirectsAsync(request, cancellationToken);
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
    /// Follows a redirect chain, deciding per hop whether the credential travels.
    /// <para>
    /// .NET's own redirect handling cannot do this: it clears the Authorization
    /// header and routes the redirected request through a handler whose credential
    /// auth is disabled (<c>RedirectHandler</c>), so <c>Credentials</c> +
    /// <c>PreAuthenticate</c> re-authenticate nothing after a redirect. Verified
    /// against a loopback listener before this was written.
    /// </para>
    /// <para>
    /// Policy: a same-origin hop keeps the credential (a stock WebUI behind a
    /// path redirect is the common case). A different origin — which any
    /// https→http downgrade is — carries no Authorization header and is only
    /// re-authenticated if it asks with a 401, so a downgrade cannot leak the
    /// credential silently.
    /// </para>
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        var owned = new List<HttpRequestMessage>();
        try
        {
            for (var hop = 0; ; hop++)
            {
                var response = await _client.SendAsync(current, HttpCompletionOption.ResponseContentRead, cancellationToken);
                if (!TryGetRedirect(response, current, out var next, out var nextMethod) || hop >= MaxRedirects)
                {
                    foreach (var extra in owned) extra.Dispose();
                    return response;
                }

                var body = current.Content is null
                    ? null
                    : await current.Content.ReadAsByteArrayAsync(cancellationToken);

                // A 307/308 to another origin would replay the request body — for
                // a login that body is the password, so an https→http downgrade
                // would put it in clear. Do not follow; the caller sees the 3xx.
                if (body is not null && nextMethod != HttpMethod.Get && !SameOrigin(current.RequestUri, next))
                {
                    foreach (var extra in owned) extra.Dispose();
                    return response;
                }

                var replayed = BuildRedirectRequest(current, next, nextMethod, body);
                owned.Add(replayed);
                response.Dispose();
                current = replayed;
            }
        }
        catch
        {
            // The caller keeps ownership of its own request; dispose only ours.
            foreach (var extra in owned) extra.Dispose();
            throw;
        }
    }

    /// <summary>How many redirects are followed before the 3xx is returned.</summary>
    public const int MaxRedirects = 10;

    /// <summary>
    /// Whether <paramref name="response"/> is a redirect this transport follows.
    /// Pure so the selftest can exercise the method/body rules.
    /// </summary>
    public static bool TryGetRedirect(
        HttpResponseMessage response, HttpRequestMessage request, out Uri next, out HttpMethod nextMethod)
    {
        next = null!;
        nextMethod = request.Method;
        var code = (int)response.StatusCode;
        if (code is not (301 or 302 or 303 or 307 or 308)) return false;
        var location = response.Headers.Location;
        if (location is null || request.RequestUri is null) return false;

        next = location.IsAbsoluteUri ? location : new Uri(request.RequestUri, location);

        // 303 always becomes GET; 301/302 historically do for POST; 307/308 keep
        // both method and body.
        if (code == 303 || ((code is 301 or 302) && request.Method == HttpMethod.Post))
            nextMethod = HttpMethod.Get;
        return true;
    }

    /// <summary>
    /// Builds the request for one hop. The credential is attached only for a
    /// same-origin hop; a different origin starts without it.
    /// </summary>
    public static HttpRequestMessage BuildRedirectRequest(
        HttpRequestMessage current, Uri next, HttpMethod method, byte[]? body)
    {
        var replayed = new HttpRequestMessage(method, next);
        foreach (var header in current.Headers)
        {
            if (header.Key == "Authorization") continue;
            replayed.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var sameOrigin = SameOrigin(current.RequestUri, next);
        if (sameOrigin && current.Headers.Authorization is { } authorization)
            replayed.Headers.Authorization = authorization;

        if (body is not null && method != HttpMethod.Get)
        {
            replayed.Content = new ByteArrayContent(body);
            if (current.Content is { } original)
            {
                foreach (var header in original.Headers)
                    replayed.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return replayed;
    }

    /// <summary>
    /// Whether two URIs share scheme, host and port. An https→http hop is
    /// deliberately not the same origin, so it never inherits the credential.
    /// </summary>
    public static bool SameOrigin(Uri? from, Uri? to) =>
        from is not null && to is not null
        && Uri.Compare(from, to,
            UriComponents.Scheme | UriComponents.Host | UriComponents.Port, UriFormat.UriEscaped,
            StringComparison.OrdinalIgnoreCase) == 0;

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
