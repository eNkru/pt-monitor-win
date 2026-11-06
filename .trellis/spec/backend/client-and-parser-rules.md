# Client & Parser Rules

> The `ITorrentClient` contract, the parse-once rule, and the sentinel tables.

---

## The contract

```csharp
// src/PTMonitor.Core/Client/TorrentClient.cs
public interface ITorrentClient : IAsyncDisposable
{
    Task<TransferSnapshot> FetchSnapshotAsync(PollDetail detail, CancellationToken cancellationToken);
    Task<string> TestConnectionAsync(CancellationToken cancellationToken);
}
```

That is the whole surface. Both `QBittorrentClient` and `TransmissionClient`
implement it; `MonitorStore` consumes only it. Adding a method here means
implementing it twice plus every stub (`SelfTest.StubClient`,
`MonitorStoreTests.StubClient`).

Rules:

- Every async method takes and forwards a `CancellationToken`.
- Failure is `throw new ServerFailureException(...)`, never a returned
  error object and never `null`.
- Auth state, cookies, session ids and cached versions are instance fields —
  clients are built once per poll loop and reused.
- `DisposeAsync` releases transport and locks; callers use `await using`.

---

## Two poll scopes

`PollDetail` says how much a poll needs:

| Scope | Carries | Used when |
|---|---|---|
| `Summary` | rates, torrent counts, connection state, free space — **no torrent list** | the panel is closed |
| `Full` | everything, including the torrent list | the panel is on screen |

The client picks the cheapest request that satisfies the scope. `TransferSnapshot.Detail`
says what the snapshot carries, so the store knows whether a list is there.

- **qBittorrent**: both scopes use `/api/v2/sync/maindata`; the scope decides
  what the *request* asks for:
  - `Full` → `rid=0`, the whole library. The panel shows the list, and it has
    to show exactly what the server has — a delta only reports what the server
    considers changed, and that must not decide what a progress bar shows.
  - `Summary` → the **last rid** the server handed out, so the server reports
    only what changed. The client merges the deltas into its library
    (`QBittorrentParser.MergeLibrary`), which keeps the counts exact and the
    payload proportional to the churn. A full re-sync (`rid=0`) happens when
    the library is empty and every `QBittorrentClient.FullSyncInterval`
    (5 minutes) — the safety net against drift.
  - The merge is pure and lives in the parser: a full update replaces the
    library, a delta applies the changed entries and drops the removed ones.
    A map that holds **every** torrent of the current library is the whole
    library, even when the server did not label it as one — that is the
    self-heal. The test is a *superset*, not a size: a delta that is bigger
    than the library but misses a torrent is a delta, and treating it as a
    full set would lose the missing torrent. The tracker fallback map is
    **merged**, not replaced: a delta carries only the announce URLs of the
    torrents it mentions.
- **Transmission**: `Summary` sends `session-stats` only (it already carries
  the rates and the counts), `Full` adds `torrent-get` with the fixed field
  list. The summary poll is one request instead of two. The rule is a pure
  function — `TransmissionClient.NeedsTorrentList(detail)` — so both suites
  can pin it.

The store keeps the last full list per server and publishes it with a summary
snapshot, so closing the panel never empties it.

---

## Parse exactly once, in a pure static parser

`QBittorrentParser` and `TransmissionParser` are static, network-free, and
take `byte[]` or `JsonNode`. They return `Model/` types.

```csharp
var response = await AuthorizedGetAsync("/api/v2/sync/maindata", …, cancellationToken);
CheckStatus(response, "sync/maindata");
return QBittorrentParser.Snapshot(response.Body, version);
```

- The client never reads a JSON field itself; the parser never performs I/O.
- The parser is what `SelfTest` calls with recorded wire bytes.
- Normalisation tables (`State`, `ConnectionState`, `LoginFailure`,
  `EndpointFailure`, `SessionIdHandshake.Step`) live here, not in the client
  method that happens to need them.

### Never cast `JsonNode` directly

Use the explicit accessors in `Client/Json.cs` — `Json.Int64`, `Json.Double`,
`Json.String`, `Json.Bool`, `Json.Object`. They tolerate the numeric backing
types and numeric strings that real servers emit.

```csharp
// good
DownSpeed = Json.Int64(serverState["dl_info_speed"]) ?? 0,

// bad — throws or silently misses on a string/double-backed value
DownSpeed = (long)serverState["dl_info_speed"]!,
```

Numeric parsing always passes `CultureInfo.InvariantCulture`.

---

## Sentinels — always map "not reported" to `null`

| Source | Raw | Normalised |
|---|---|---|
| qBittorrent ETA | `8640000` | `null` |
| Transmission ETA | `< 0` (`-1`, `-2`) | `null` |
| Ratio | `< 0` (`-1`) | `null` |
| `free_space_on_disk` | WebAPI `< 2.1.1` | `null` |
| Progress | anything outside `0…1`, `NaN`, `∞` | clamped by `TorrentSummary.ClampedProgress` |

Gated field example — the version check lives in the parser, not the UI:

```csharp
long? freeSpace = null;
if (webApiVersion is { } version && version >= WebApiVersion.V2_1_1)
    freeSpace = Json.Int64(serverState["free_space_on_disk"]);
```

---

## Auth and retry — exactly once, never a loop

- qBittorrent: `AuthorizedGetAsync` retries **one** re-login on 401/403, then
  returns the response for the caller to classify.
- Transmission: `RunHandshakeAsync` resends **one** request after a 409 with the
  `X-Transmission-Session-Id` from the response.
- A `403` on `auth/login` is a **ban**; a `403` on any other endpoint is a
  **rejected session** (`ProtocolViolation`). Conflating them mis-reports a
  broken cookie as a ban.
- Transmission `200` with `result != "success"` is still a failure.

---

## Transport

`HttpTransport` owns everything cross-cutting for one server: base URI, 15 s
timeout, cookie container, Basic auth header, optional self-signed TLS trust,
redirect policy, the response-size cap, and OS-error → `FailureKind`
translation (`Describe`). A client should never construct its own `HttpClient`.

`HostFieldParser` is the single owner of host/port/scheme/userinfo extraction.
`Inspect` returns known values plus an optional error for incomplete input;
`Parse` throws that error for strict consumers. `ServerConfigValidator.Evaluate`
composes it into effective form state; legacy `Validate` is its throwing wrapper.
Do not re-parse hosts in the editor.

### TLS

- New servers default to HTTPS (`ServerEditorWindow.Load`). A hand-typed
  `http://` scheme still wins and is reported as a notice.
- While HTTPS is off and a password is entered, the editor shows
  `EditorNotices.PlaintextWarning`. Its composition is a pure `Core` function
  so the selftest covers it; the `App` layer only translates and renders.
- `AllowInsecureTls` still accepts any certificate, but the transport installs
  its own validation callback so the accepted leaf's SHA-256 is recorded and
  shown on a successful Test Connection. Never go back to
  `DangerousAcceptAnyServerCertificateValidator`: it accepts the same things and
  observes nothing. There is no pinning or trust store — that is a follow-up.

### Redirects

- The transport follows redirects itself (`AllowAutoRedirect = false`) because
  .NET's `RedirectHandler` clears `Authorization` on every hop *and* sends the
  redirected request through a handler with credential auth disabled — the
  credential cannot be restored there, so a same-origin redirect would silently
  fail with a 401.
- Credential rule: a **same-origin** hop (scheme, host and port all equal) keeps
  the `Authorization` header. Any other origin — host change, port change, or an
  **https→http downgrade** — drops it and NEVER reauthenticates, even after 401.
  No handler `Credentials` are configured. Returning to the original origin later
  in the chain does not restore the dropped header.
- `CanFollowRedirect` refuses cross-origin body replay; same-origin replay is
  permitted. POST 301/302/303 becomes GET without content and may cross origins;
  POST 307/308 retains content and cannot. The caller sees a refused 3xx:
  qBittorrent classifies ProtocolViolation, Transmission ServerError.
- 303 preserves HEAD, otherwise becomes GET; 301/302 rewrite POST only;
  307/308 preserve the method. `MaxRedirects` bounds the chain.
- Body retention depends on whether the method **changed**, not whether the next
  method is GET. `BuildRedirectRequest` preserves an unchanged GET's content;
  `CanFollowRedirect` therefore blocks that content across origins too. Mirror
  GET-with-content 301/302/303/307/308 cases in both suites, asserting replayed
  bytes within the origin and refusal across it (alongside POST→GET body removal).

### Response size

`HttpClient.MaxResponseContentBufferSize` caps a buffered body at 32 MiB
(`HttpTransport.MaxResponseBytes`). A breach is a `ProtocolViolation`, not an
`Unreachable`: the server answered, it just is not a torrent client.

---

## Effective editor state contract

### Scope / trigger
Pasted URLs can override both the HTTPS control and password box. Warnings,
normalization and Test/Save must share one decision rather than drift.

### Signatures
`ServerConfigValidator.Evaluate(ServerConfig, string portText, string? password)`
returns `EffectiveServerForm(Config, Password, Notices, Error)`.
`EditorNotice(Key, Port?)` carries the English translation key and optional port.

### Contracts
Password is runtime-only. Non-empty explicit credential fields win. At Host
LostFocus and Test/Save the editor applies valid normalized fields, removing
userinfo from the visible host. Normalizing every keystroke is forbidden.
Import notices survive normalization/password/TLS changes; a new host clears
old notices. App translates parameterized notices with `L10n.Format`, not `S`.

### Validation / error matrix
- Valid endpoint: normalized host/port/TLS/user/password plus adjustment notices.
- Invalid endpoint/port: Error blocks Test/Save, but known TLS/password still
  drive `EditorNotices.Compose`; no mutation of incomplete controls.
- Consumed host, then cleared password: remains empty, no hidden fallback.

### Good / base / bad cases
Good: HTTPS checked + `http://bob:p@nas` still warns before normalization.
Base: plain `nas` uses the existing fields. Bad/incomplete: `http://bob:p@`
blocks sending but still warns. `https://...` overrides an unchecked toggle.

### Tests required
Mirror effective scheme/password, precedence, partial inputs, IPv6 userinfo,
notice metadata and repeat normalization in selftest/xUnit. Headless editor
checks can verify event wiring; Windows rendering/DPAPI checks remain separate.

### Wrong vs correct
Wrong: warning reads only `TlsToggle`/`PasswordField` while send reads parsed URL.
Correct: both use `Evaluate`; the editor only renders/applies that result with
an explicit reentrancy guard.

## Test seam

`MonitorStore` takes `Func<ServerConfig, string?, ITorrentClient>` so tests and
`SelfTest` inject stubs without a network. Keep that factory parameter when
touching `MonitorStore`.

---

## Anti-patterns

- Parsing in the client method because "it is only one field".
- `JsonNode` indexers with `!` and a cast.
- Returning `null`/empty snapshot on failure instead of throwing.
- More than one retry, or a `while (true)` without a retry flag.
- Reading a raw field in `App` — the panel must only see `Model/` types.
- Adding a package for HTTP, JSON or retry. `HttpClient` + `System.Text.Json`
  are enough.
