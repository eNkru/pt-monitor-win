# Client & Parser Rules

> The `ITorrentClient` contract, the parse-once rule, and the sentinel tables.

---

## The contract

```csharp
// src/PTMonitor.Core/Client/TorrentClient.cs
public interface ITorrentClient : IAsyncDisposable
{
    Task<TransferSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken);
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
and OS-error → `FailureKind` translation (`Describe`). A client should never
construct its own `HttpClient`.

`HostFieldParser` is the single owner of `(host, port)` extraction — pasted
URLs, `host:port`, IPv6 literals, `user@host`. `ServerConfigValidator` composes
it and returns the `Notice` the editor shows. Do not re-parse hosts anywhere
else.

---

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
