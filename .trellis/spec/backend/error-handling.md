# Error Handling

> Cause-carrying failures, and the short list of places swallowing is correct.

---

## The model

Failures are data, not prose. `Model/ServerStatus.cs` defines the closed set:

```csharp
public enum FailureKind { Unreachable, Auth, Banned, ProtocolViolation, ServerError }

public sealed record ServerFailure(FailureKind Kind, string Message)
{
    public string KindLabel => Kind switch { FailureKind.Auth => "auth failed", … };
    public static ServerFailure Auth(string message) => new(FailureKind.Auth, message);
    …
}
```

`ServerFailureException` wraps a `ServerFailure` so it can travel through
`async`/`Task` and `using` blocks without losing its cause.

- The exception is a **transport** for the record. Nothing catches it to read
  `.Message` for logic — logic reads `.Failure.Kind`.
- Construct via the factory methods (`ServerFailure.Auth(...)`), not the
  positional ctor.
- Messages are lowercase, technical, English, and actionable ("host not found",
  "invalid port 70000"). The localized headline comes from
  `L10n.CauseHeadline` in the app.

---

## Throw vs. return

| Situation | Shape |
|---|---|
| Anything obtained over the network or parsed from a payload | `throw new ServerFailureException(...)` |
| Pure formatter | never throws; clamps (`ByteRateFormatter.Rate`, `TorrentSummary.ClampedProgress`) |
| Optional field absent or sentinel | `null` return, not an exception (`Json.Int64` → `long?`) |
| Persistence | swallow, keep the in-memory value (see below) |

Every layer maps the failure once and stops:

```
client/parser  → throw ServerFailureException(kind, message)
MonitorStore   → catch → ServerStatus.Failed(failure) + backoff
PanelViewModel → failure.Kind → localized headline + raw message detail
```

---

## Mapping is exclusive and ordered

```csharp
// MonitorStore.PollLoopAsync
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
catch (ServerFailureException failure) { … SetStatus(Failed(failure.Failure)); }
catch (Exception exception) { … SetStatus(Failed(ServerFailure.Unreachable(exception.Message))); }
```

- The `when (cancellationToken.IsCancellationRequested)` filter is mandatory —
  without it a timeout (`TaskCanceledException` from `HttpClient`) looks like a
  shutdown.
- The bare `Exception` arm is the last resort and always becomes `Unreachable`.
- `HttpTransport.Describe` maps socket/TLS/DNS text to a short cause before it
  reaches the record.

---

## Swallowing is allowed in exactly these places

Each one has a comment explaining why, and keeps the app running:

| Place | Why |
|---|---|
| `SettingsStore.Persist` / `Load` | settings trouble must not kill the app; memory stays authoritative |
| `DpapiSecretStore.Get` / `Load` / `PersistUnsafe` | an unreadable secret degrades to "no password" |
| `TransmissionClient.FreeSpaceIfDueAsync` | `free-space` may block on a hung mount; losing it is cosmetic |
| `PanelViewModel.SafeWebUri` | an unusable host yields `about:blank` |
| `MainPanelWindow.OpenUrl` | no browser launched is not worth a dialog |
| `SelfTest` temp-dir cleanup | `catch { }` on `Directory.Delete` |
| `ServerEditorWindow.OnTest` general catch | show it in the message line and stay open |

Write `catch (Exception)` with a comment. Do not log-and-rethrow.

---

## CLI exit codes and streams

`ProbeRunner.RunAsync` owns the contract:

| Code | Meaning |
|---|---|
| `0` | success — snapshot JSON on `stdout` |
| `1` | probe ran and failed — `probe: <kindLabel>: <message>` on `stderr` |
| `2` | usage error — message + `Usage` on `stderr` |

`SelfTest` exits `0` on all-pass, `1` on any failure. Standard output carries
machine-readable payloads; diagnostics go to standard error.

---

## Anti-patterns

- Catching `ServerFailureException` to rebuild a new one with the same data.
- A `try` around a whole method "just in case" — scope it.
- `throw new Exception(...)`; use the typed family.
- `ex.ToString()` in user-facing text; pass `ex.Message`.
- Switching on `Message` text. Switch on `Kind`.
- Swallowing inside a client (except the documented `free-space` case) — the
  caller needs the cause.
