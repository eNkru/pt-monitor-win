using System.Net;
using System.Net.Http.Headers;
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
    public Uri BaseUri { get; }

    private readonly HttpClient _client;
    private readonly string? _basicAuthorization;
    private bool _disposed;

    public HttpTransport(ServerConfig config, string? password)
    {
        BaseUri = EndpointParser.BaseUri(config.Host, config.Port, config.UseTls);

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (config.AllowInsecureTls)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
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
            throw new ServerFailureException(FailureKind.Unreachable, Describe(exception));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ServerFailureException(FailureKind.Unreachable, exception.Message);
        }
    }

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
