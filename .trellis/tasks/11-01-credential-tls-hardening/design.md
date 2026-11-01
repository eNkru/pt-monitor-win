# Design — credential and TLS hardening

## Boundaries

| Concern | Home | Not in |
|---|---|---|
| Host/userinfo parsing | `Core/Client/HostFieldParser.cs` | UI |
| Plaintext warning text | `App/Views/ServerEditorWindow` (uses `L10n`) | `Core` |
| Certificate observation | `Core/Client/HttpTransport` + client ctor callback | `Core/Model` |
| Secret-store state + write propagation | `Core/Store/SecretStore.cs` | UI file IO |
| Backoff decision for unreadable secrets | `Core/Store/MonitorStore.cs` | UI |
| Redirect credential policy | `Core/Client/HttpTransport.cs` | clients, manual redirect loop |

Hard constraint: **no new field in `settings.json`**. Everything below is
runtime-only, so `persistence-guidelines.md` needs no shape migration — only a
behaviour paragraph. Trust state (a pinned thumbprint) is therefore out of scope
by construction.

## 1. `ISecretStore` gains a state and a throwing write

```csharp
public enum SecretStoreState { Ok, Absent, Unreadable }

public interface ISecretStore
{
    SecretStoreState State { get; }   // InMemorySecretStore: always Ok
    string? Get(Guid serverId);
    bool Set(string password, Guid serverId);   // throws on failed persist
    void Delete(Guid serverId);                 // still swallows
    void DeleteAll();                           // still swallows
}
```

`Set` has two outcomes and they must not be confused:

- **throws** — the write failed; nothing is stored and the caller must abort.
- **returns `true`** — a password is stored; **`false`** — the argument was empty,
  the entry was removed. `false` says nothing about success.

- `DpapiSecretStore.Load()` distinguishes `File.Exists` false (`Absent`) from a
  deserialize/IO failure (`Unreadable`); both leave `_items` empty.
- Split `PersistUnsafe` into `Persist()` (throws, used by `Set`) and
  `PersistOrIgnore()` (used by `Delete`/`DeleteAll`). `Delete` on the UI thread
  must not raise; `Set` must, because the editor's abort depends on it.
- `Set` sets `State = Ok` after a successful write.
- While `State == Unreadable`, the *first persist of any kind* preserves the
  unreadable bytes as `secrets.dat.corrupt` (`File.Move(_path, _path + ".corrupt",
  true)`, overwriting any earlier copy) before the rewrite. It happens inside the
  write path, so `Delete`/`DeleteAll` — which rewrite the file too — cannot
  destroy the only recovery copy before a `Set` ever runs. The rename is
  best-effort: if it fails, the write still proceeds — preserving the old bytes
  is not worth blocking the user's edit.

Trade-off: a throwing `Delete` would be more honest, but `PreferencesWindow`
removes the row from the UI regardless; throwing there forces a second error path
for no gain.

## 2. Corrupt store → existing backoff, not a new mechanism

Poll first; substitute the message only when the poll actually fails auth.
`PollAsync` is *not* the only path — the live loop is `PollLoopAsync`
(`MonitorStore.cs:127`), which builds its client once per reconcile and never
calls `PollAsync`. Guarding only `PollAsync` (the old design) made the loop
unaffected and falsely failed passwordless servers on **Refresh Now**, because it
returned before trying. Both paths get the same helper:

```csharp
public static ServerFailure Classify(string? password, ISecretStore secrets, ServerFailure failure) =>
    failure.Kind == FailureKind.Auth
        && password is null
        && secrets.State == SecretStoreState.Unreadable
        ? ServerFailure.Auth("stored password could not be read")
        : failure;
```

`password` is the value the client was constructed with, not a fresh `Get`: while
`Unreadable`, `Get` always returns `null`, and after a reconcile the client and
the store state are consistent again.

- `PollLoopAsync` keeps `var password = _secrets.Get(config.Id);` in a local and
  uses it for both the client and its `catch (ServerFailureException failure)` →
  `new ServerStatus.Failed(Classify(password, _secrets, failure.Failure))`.
- `PollAsync` captures the same local before `clientFactory` and applies
  `Classify` in its catch.
- `ServerFailure.Auth` already selects `CredentialFailureBackoff` (≥ 600 s) in
  `RetryDelay`, and `ServerStatus.Failed` already renders in the panel. No new
  status type, no new failure kind. `PreferencesWindow` shows one banner when
  `State == Unreadable`.

Trade-off: an unreadable entry and a genuinely wrong password are indistinguishable
downstream. Accepted — both want the same slow retry. A server that needs no
password is never pre-empted, so a corruption does not take passwordless servers
offline.

## 3. Plaintext warning

`ShowNotice(string?)` becomes `RefreshNotices(string? validatorNotice = null)`:

```csharp
var parts = new List<string>();
if (validatorNotice is not null) parts.Add(validatorNotice);
if (TlsToggle.IsChecked != true && (PasswordField.Text ?? "").Length > 0)
    parts.Add(L10n.S("The password is sent unencrypted — enable \"Use HTTPS\" unless the server is on a network you trust."));
NoticeText.Text = string.Join("\n", parts);
NoticeText.IsVisible = parts.Count > 0;
```

Wired to `TlsToggle.IsCheckedChanged`, `PasswordField.TextChanged`, `Validate()`
and `Load()`. It runs independently of the validator, so it appears before the
form is valid. `ServerEditorWindow.Load()`'s new-server branch sets
`TlsToggle.IsChecked = true`.

Risk: adding a server to a stock qBittorrent over HTTP 8080 now fails the first
Test Connection with "TLS handshake failed (… enable \"allow self-signed
certificate\" …)". That hint is wrong for the plain-HTTP case, so
`HttpTransport.Describe` gets a downgrade hint when the scheme is `http` and the
error is a TLS/EOF failure: "…or uncheck \"Use HTTPS\" if the server speaks plain
HTTP."

## 4. Certificate observation

Server certificates are only interesting where the user is looking, so the
observer is an optional ctor callback, not a property on `ITorrentClient`
(which would force every stub in `SelfTest`/xUnit to grow a member):

```csharp
public QBittorrentClient(ServerConfig config, string? password, Action<string>? onCertificateAccepted = null)
public TransmissionClient(ServerConfig config, string? password, Action<string>? onCertificateAccepted = null)
```

`HttpTransport` stores it and, when `AllowInsecureTls`, installs its own
`ServerCertificateCustomValidationCallback` instead of
`DangerousAcceptAnyServerCertificateValidator`:

```csharp
handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
{
    if (cert is null) return false;
    var thumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
    _observedThumbprint = thumbprint;
    _onCertificateAccepted?.Invoke(thumbprint);
    return true;                       // same accept-all semantics as today
};
```

Semantics are unchanged (still accepts anything when the toggle is on) — the
change is that the fact becomes visible. The editor appends the thumbprint to a
successful Test Connection message.

Trade-off: no expiry/CN evaluation even when the toggle is on. Deliberate; a
verify-then-accept design is the pinning follow-up, not this task.

## 5. Userinfo in the host field

```csharp
public sealed record ParsedHostField(
    string Host, int Port, bool? SchemeIsTls, bool ExtractedPort,
    string? Username, string? Password);
```

- Strip userinfo **before** the port logic (already the order today), segment on
  the first `:` inside it, `Uri.UnescapeDataString` both halves.
- `ValidatedServerConfig.Notice` becomes `IReadOnlyList<string> Notices` (port
  notice and credential notice can both apply). Only caller is
  `ServerEditorWindow.ShowNotice` → `RefreshNotices`.
- `Validate` keeps a typed user name over the parsed one:
  `Username = typed is { Length: > 0 } ? typed : parsed.Username ?? ""`.
- `ValidatedServerConfig` gains `string? PasswordFromHost`; `OnSave`/`OnTest` use
  `PasswordField.Text` when non-empty, otherwise `PasswordFromHost`.
- `EndpointParser.BaseUri` ignores userinfo, so `WebUiUri` output is unchanged.

## 6. Bounded responses

`HttpClient.MaxResponseContentBufferSize = 32 MiB` (`ResponseContentRead` is
already the completion option, so the handler enforces the cap and throws
`HttpRequestException`). A `sync/maindata` for thousands of torrents is a few MB;
32 MiB is generous and still bounded. `SendAsync`'s `HttpRequestException` catch
maps a limit breach to `FailureKind.ProtocolViolation` ("response above 32 MiB")
ahead of the generic `Unreachable` mapping.

Trade-off: relies on the framework message mentioning the buffer size. Acceptable;
the fallback is a plain `Unreachable`, which is only a worse label.

## 7. Redirect credentials

> **Revised during implementation.** The first version of this section proposed
> `handler.Credentials` + `PreAuthenticate`. A loopback probe disproved it:
> .NET's `RedirectHandler` clears `Authorization` on every hop **and** sends the
> redirected request through a second inner handler with credential auth
> disabled (`_redirectInnerHandler`), so nothing re-authenticates the hop. The
> shipped design follows redirects in the transport.

`AllowAutoRedirect = false`; `SendFollowingRedirectsAsync` walks the chain and
decides per hop:

| Hop | Credential |
|---|---|
| same origin (scheme, host, port) | `Authorization` carried over |
| different origin, including https→http | header dropped; only a 401 re-authenticates |
| any origin, redirect carries a request body | **not followed** — the 3xx is returned |

- Method rewriting matches the framework: 303 → GET, 301/302 → GET for POST,
  307/308 keep method and body. `MaxRedirects = 10`, then the 3xx is returned.
- The body rule exists because the qBittorrent login form *is* the password: a
  307/308 to another origin would replay it in clear, which is exactly the
  downgrade the finding describes.
- `UseBasicAuthorization` stays and provides the first request's header; the
  helper copies it onto same-origin hops.
- The decision points are pure (`SameOrigin`, `TryGetRedirect`,
  `BuildRedirectRequest`) so the selftest covers them without sockets, and the
  behaviour was verified end to end against two loopback listeners.

Trade-off: the engine no longer handles 100 redirect semantics for us, so the
method/body rules are ours to keep right — covered by the selftest table.

## Compatibility & rollback

- `settings.json` untouched; an existing server keeps whatever `useTls` it has, so
  the new default only affects newly added servers.
- `ISecretStore` gains `State` (`InMemorySecretStore` is the only other impl);
  `ValidatedServerConfig.Notice` survives as a convenience property so the change
  is additive.
- Each finding is its own commit → individually revertable. Rollback of the
  default alone is a one-line revert that does not touch 3, 4 or 5.
