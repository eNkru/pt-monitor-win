# Logging

> One file, one writer, events only. This documents what the app writes to disk,
> and the redaction contract that keeps secrets out of it.

---

## Overview

The app writes exactly one log: `%APPDATA%\PTMonitor\ptmonitor.log`
(`AppPaths.LogFile`), through the static `Core/Diagnostics/Log` class. There is
no package, no interface, no DI, no level configuration and no opt-out.

Why always on: a sink the user has to enable beforehand cannot diagnose a crash
that already happened, which is the case the log exists for. The cost is bounded
instead — 512 KiB plus one rotated predecessor, events only.

`Log` is a **file sink**, not a console sink: `Core` must not print, so a store
that wants to record something calls `Log.Warn(...)` rather than
`Console.WriteLine`. `Program.cs` calls `Log.Initialize(AppPaths.LogFile)` before
the Avalonia builder; until then every call is a no-op, which is what keeps the
CLI, `--selftest` and the xUnit suite free of file side effects.

```csharp
Log.Initialize(AppPaths.LogFile);   // Program.Main, before the builder
Log.Info("startup", $"...");        // version, OS, data dir, secret-store state
Log.Warn("poll", "nas: auth failed: …");
Log.Error("appdomain", "NullReferenceException: … | at Foo() | at Bar()");
Log.Shutdown();                     // after the desktop lifetime ends
```

---

## Where output goes

| Surface | Use |
|---|---|
| `stdout` | `--probe` snapshot JSON, `--selftest` pass line, CLI usage text |
| `stderr` | probe/selftest failures, usage errors, unexpected arguments |
| UI text | failure headline + detail (`L10n.CauseHeadline`, `failure.Message`) |
| `ptmonitor.log` | events only — see the list below |
| Trace | Avalonia framework internals only (`LogToTrace`) |

The user-visible error path is still the primary diagnosis: `ServerStatus.Failed`
carries a `ServerFailure` whose `Kind` picks a localized headline and whose
`Message` carries the English technical detail (`"host not found"`,
`"timed out"`, `"TLS handshake failed (enable \"allow self-signed certificate\" …)"`).
The log is the record that survives a crash.

---

## What is logged

| Call site | Event |
|---|---|
| `App/Program.cs` | one startup line: version, OS, data directory |
| `App/Services/AppServices` | startup: secret-store state; TLS certificate accepted (per server + thumbprint) |
| `App/Program.cs` | `AppDomain.CurrentDomain.UnhandledException` and `Dispatcher.UIThread.UnhandledException` — logged, and **`Handled` is never set**, so behaviour stays "terminate" |
| `MonitorStore.PollLoopAsync` | a failure **transition** only: the previous status was `Online`, or the `FailureKind` changed |
| `DpapiSecretStore` | `Unreadable` on load, the `.corrupt` copy, a failed `Persist` |
| `SettingsStore.Persist` | the atomic write failed (memory keeps the value) |
| `ServerEditorWindow.OnSave` | the secret write failed and the save was aborted |

The failure-transition rule is a pure predicate
(`MonitorStore.ShouldLogFailure`), and the loop clears its memo after every
successful poll — so a steady failure logs **once**, and recovery followed by the
same failure logs again. That is the difference between a diagnosis and 1800
lines an hour.

### Line shape

```
2026-11-01T18:59:04.123Z [warn] poll: NAS: auth failed: invalid username or password
```

UTC ISO-8601 with milliseconds, level (`info`/`warn`/`error`), area, message.
One line per event, always: newlines (stack traces) are joined with ` | `.

### Rotation

Before each append, a file at or past `maxBytes` (512 KiB by default) is moved to
`ptmonitor.log.1`, replacing any previous predecessor, and a fresh file starts.
Two files maximum, no timestamps in the rotated name — one backup is the whole
policy. Each line is written open-append-flush-close under a lock, so the file
survives a hard kill; the volume is a handful of lines per session.

### Never throws

Every write is inside `try { … } catch { /* cosmetic */ }`. An unwritable
`%APPDATA%`, a read-only folder or a full disk loses a line and nothing else —
polling and the tray keep running. `Initialize` is equally forgiving: it points
the sink at the path and creates the directory on a best-effort basis.

---

## The redaction contract

`Log.Redact` runs over **every line** at write time, so a careless call site
cannot leak. The table is the contract; the xUnit suite and `--selftest` pin it.

| Pattern | Becomes |
|---|---|
| `password=…`, `passwd=`, `pwd=`, `pass=` (form or query, `&`-delimited) | `password=***` |
| `Authorization: Basic/Bearer <token>` | `Authorization: Basic ***` |
| `Set-Cookie:` / `Cookie:` values, `SID=…` | `Set-Cookie: ***`, `SID=***` |
| `scheme://user:pass@host` userinfo | `scheme://***@host` |
| bare `user:pass@host` userinfo | `user:***@host` |

Redaction is the net, **not the design**. The rule that matters is still "do not
pass a secret to `Log`": never format a password, a header value, a cookie, a
`SID` or a raw payload body into a message. If a message needs to say *which*
credential failed, name the server, not the value.

---

## Message rules

- Lowercase, no trailing period, no stack traces in UI text (the log may carry
  one, flattened onto the same line).
- Name the cause, then the fix when there is one:
  `"host not found"` / `"the WebUI has banned this PC's IP after too many failed
  logins — wait for the ban to expire … then check the user name and password"`.
- Prefix CLI failures with `probe: ` or `selftest: `.
- Keep `KindLabel` short — it is a display token.
- Area is one lowercase word naming the subsystem: `startup`, `poll`, `secrets`,
  `settings`, `tls`, `appdomain`, `ui`.

---

## What never gets logged

- **Passwords**, in any form (not even a masked hint).
- Auth headers, cookies, `SID` values.
- Raw payload bodies. Only the parse-relevant values appear in messages, e.g.
  `could not parse WebAPI version "2.x"`.
- **Per-poll chatter** — no line for a successful poll, and no repeat line for a
  failure that has not changed. At the default 2 s interval that would be the
  whole file.
- Window/UI internals (tray redraws, panel show/hide).

---

## Adding an output

| Need | Do |
|---|---|
| A new **event** worth keeping (once per occurrence, not per poll) | `Log.Info/Warn/Error(area, message)` from the owning store, plus a `SelfTest` case and an xUnit mirror |
| A new probe failure detail | `ServerFailure.X(...)` message — it lands on `stderr` and in the panel |
| A new CLI usage line | `ProbeRunner.Usage` |
| New exit code | Only 0/1/2 are in use; add a test in `ProbeChecks` |
| Framework debugging | `LogToTrace` output, temporary |

Only reach for `Console.Write*` inside `PTMonitor.Cli`/`Diagnostics`. Nothing in
`Core.Client` or `Core.Store` prints — it calls `Log` instead.

---

## Anti-patterns

- `Console.WriteLine` in a store or client.
- `System.Diagnostics.Debug.WriteLine` as the only record of an important event.
- A second logger (a package, an interface, a `Logger` instance) — `Log` is the
  one writer, and `Core` may not print.
- A log level setting, a sink abstraction, or an opt-out toggle.
- Logging the password "just to be sure".
- Logging inside `PollLoopAsync` per iteration — success or repeated failure.
- Multi-line messages; join with ` | ` so one event stays one line.
- `catch { }` around a write **without** the "cosmetic" comment.
