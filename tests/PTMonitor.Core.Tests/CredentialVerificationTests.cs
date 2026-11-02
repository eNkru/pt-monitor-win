using System.Net;
using System.Net.Http.Headers;
using PTMonitor.Core.Client;
using PTMonitor.Core.Model;
using PTMonitor.Core.Store;

namespace PTMonitor.Core.Tests;

public class CredentialVerificationTests
{
    [Theory]
    [InlineData("http://bob:secret@nas:8080/path", true, "", "8080", true, false)]
    [InlineData("http://nas", true, "explicit", "8080", true, false)]
    [InlineData("https://bob:secret@nas", false, "", "8080", false, false)]
    [InlineData("http://bob:secret@", true, "", "8080", true, true)]
    [InlineData("http://bob:secret@nas:bad", true, "", "8080", true, true)]
    [InlineData("http://nas", true, "explicit", "bad", true, true)]
    [InlineData("", false, "explicit", "8080", true, true)]
    [InlineData("http://nas", true, "", "8080", false, false)]
    public void WarningUsesEffectiveInputsEvenWhenInvalid(string host, bool tls, string password,
        string port, bool warns, bool invalid)
    {
        var form = ServerConfigValidator.Evaluate(new ServerConfig(ServerKind.Qbittorrent, host)
            { UseTls = tls }, port, password);
        Assert.Equal(invalid, form.Error is not null);
        Assert.Equal(warns, EditorNotices.Compose(null, form.Config.UseTls, form.Password)
            .Contains(EditorNotices.PlaintextWarning));
    }

    [Theory]
    [InlineData("", "", "bob", "secret", "Used the user name and password from the host field.")]
    [InlineData("alice", "", "alice", "secret", "Used the password from the host field; kept the user name field.")]
    [InlineData("", "explicit", "bob", "explicit", "Used the user name from the host field; kept the password field.")]
    [InlineData("alice", "explicit", "alice", "explicit", "Kept the credential fields; ignored credentials in the host field.")]
    public void NormalizationIsVisibleSafeAndHonest(string user, string password,
        string expectedUser, string expectedPassword, string notice)
    {
        var form = ServerConfigValidator.Evaluate(new ServerConfig(ServerKind.Qbittorrent,
            "http://bob:secret@[2001:db8::1]:1234/path") { UseTls = true, Username = user }, "8080", password);
        Assert.Null(form.Error);
        Assert.Equal("2001:db8::1", form.Config.Host);
        Assert.Equal(1234, form.Config.Port);
        Assert.False(form.Config.UseTls);
        Assert.Equal(expectedUser, form.Config.Username);
        Assert.Equal(expectedPassword, form.Password);
        Assert.Contains(new EditorNotice(notice), form.Notices);
        Assert.Contains(new EditorNotice("Used port %lld from the host field.", 1234), form.Notices);
        var next = ServerConfigValidator.Evaluate(form.Config, "1234", "");
        Assert.Equal("", next.Password); // consumed host cannot refill an intentionally cleared password
        Assert.Empty(next.Notices);
    }

    [Theory]
    [InlineData("u%40x:p%3Aw@[::1]", "::1", "u@x", "p:w")]
    [InlineData("bob:secret@2001:db8::1", "2001:db8::1", "bob", "secret")]
    public void Ipv6UserinfoUsesTheSingleParser(string raw, string host, string user, string password)
    {
        var parsed = HostFieldParser.Parse(raw, 8080);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(user, parsed.Username);
        Assert.Equal(password, parsed.Password);
    }

    [Theory]
    [InlineData(301, "POST", "GET")]
    [InlineData(302, "POST", "GET")]
    [InlineData(303, "POST", "GET")]
    [InlineData(303, "HEAD", "HEAD")]
    [InlineData(303, "PUT", "GET")]
    [InlineData(301, "PUT", "PUT")]
    [InlineData(302, "HEAD", "HEAD")]
    [InlineData(307, "POST", "POST")]
    [InlineData(308, "POST", "POST")]
    [InlineData(301, "GET", "GET")]
    [InlineData(302, "GET", "GET")]
    [InlineData(303, "GET", "GET")]
    [InlineData(307, "GET", "GET")]
    [InlineData(308, "GET", "GET")]
    public async Task RedirectMethodBodyAndAuthPolicy(int status, string method, string expectedMethod)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://nas:8080/a")
            { Content = new ByteArrayContent([1, 2]) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "synthetic");
        foreach (var target in new[] { "https://nas:8080/b", "http://nas:8080/b", "https://other:8080/b", "https://nas:9091/b" })
        {
            using var response = new HttpResponseMessage((HttpStatusCode)status)
                { Headers = { Location = new Uri(target) } };
            Assert.True(HttpTransport.TryGetRedirect(response, request, out var next, out var nextMethod));
            Assert.Equal(expectedMethod, nextMethod.Method);
            var same = target == "https://nas:8080/b";
            var retainsBody = method == expectedMethod;
            Assert.Equal(same || !retainsBody, HttpTransport.CanFollowRedirect(request, next, nextMethod));
            if (!HttpTransport.CanFollowRedirect(request, next, nextMethod)) continue;
            using var hop = HttpTransport.BuildRedirectRequest(request, next, nextMethod, [1, 2]);
            Assert.Equal(same, hop.Headers.Authorization is not null);
            Assert.Equal(retainsBody, hop.Content is not null);
            if (hop.Content is { } content) Assert.Equal(new byte[] { 1, 2 }, await content.ReadAsByteArrayAsync());
            using var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=synthetic"));
            Assert.False(HttpTransport.TryGetRedirect(challenge, hop, out _, out _));
            if (!same)
            {
                using var returned = HttpTransport.BuildRedirectRequest(hop, request.RequestUri!, HttpMethod.Get, null);
                Assert.Null(returned.Headers.Authorization); // no credential restoration on a return hop
            }
        }
    }

    [Theory]
    [InlineData("https://nas", "SSL connection failed", true)]
    [InlineData("https://nas", "Received an unexpected EOF", true)]
    [InlineData("http://nas", "SSL connection failed", false)]
    [InlineData("http://nas", "Received an unexpected EOF", false)]
    [InlineData("https://nas", "connection refused", false)]
    public void PlainHttpHintBelongsToHttpsFailures(string endpoint, string error, bool expected) =>
        Assert.Equal(expected, HttpTransport.Describe(new HttpRequestException(error), new Uri(endpoint))
            .Contains("uncheck \"Use HTTPS\""));

    [Fact]
    public void FailedBackupDoesNotBlockRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pt-credential-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            File.WriteAllText(path, "damaged");
            Directory.CreateDirectory(path + ".corrupt");
            var store = new DpapiSecretStore(path);
            var id = Guid.NewGuid();
            Assert.True(store.Set("new", id));
            Assert.Equal(SecretStoreState.Ok, store.State);
            Assert.Equal("new", store.Get(id));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupSurvivesDeleteAllAndRecovery(bool existingBackup)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pt-credential-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            File.WriteAllText(path, "damaged");
            if (existingBackup) File.WriteAllText(path + ".corrupt", "older damage");
            var store = new DpapiSecretStore(path);
            store.Delete(Guid.NewGuid());
            store.DeleteAll();
            Assert.Equal(SecretStoreState.Unreadable, store.State);
            store.Set("new", Guid.NewGuid());
            Assert.Equal(SecretStoreState.Ok, store.State);
            Assert.Equal(existingBackup ? "older damage" : "damaged", File.ReadAllText(path + ".corrupt"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
