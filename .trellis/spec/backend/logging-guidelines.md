# Logging

> There is no log file. This documents where output actually goes.

---

## Overview

The app writes nothing to disk by default. `README.md` says so, and users are
pointed at the CLI probe for diagnosis. Do not introduce a logger, a log file,
or a new package for one.

`Program.cs` enables Avalonia's trace log only:

```csharp
AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
```

`AppPaths.LogFile` (`…/ptmonitor.log`) exists but nothing writes it yet — it is
a reserved path, not a live sink.

---

## Where output goes

| Surface | Use |
|---|---|
| `stdout` | `--probe` snapshot JSON, `--selftest` pass line, CLI usage text |
| `stderr` | probe/selftest failures, usage errors, unexpected arguments |
| UI text | failure headline + detail (`L10n.CauseHeadline`, `failure.Message`) |
| Trace | Avalonia framework internals only |

The user-visible error path **is** the log: `ServerStatus.Failed` carries a
`ServerFailure` whose `Kind` picks a localized headline and whose `Message`
carries the English technical detail (`"host not found"`, `"timed out"`,
`"TLS handshake failed (enable \"allow self-signed certificate\" …)"`).

---

## Message rules

- Lowercase, no trailing period, no stack traces.
- Name the cause, then the fix when there is one:
  `"host not found"` / `"the WebUI has banned this PC's IP after too many failed
  logins — wait for the ban to expire … then check the user name and password"`.
- Prefix CLI failures with `probe: ` or `selftest: `.
- Keep `KindLabel` short — it is a display token.

---

## What never gets logged

- **Passwords**, in any form (not even a masked hint).
- Raw payload bodies. Only the parse-relevant values appear in messages, e.g.
  `could not parse WebAPI version "2.x"`.
- Auth headers, cookies, `SID` values.
- Per-poll chatter. At the default 2 s interval it would be noise.

---

## Adding an output

| Need | Do |
|---|---|
| New probe failure detail | `ServerFailure.X(...)` message — it lands on `stderr` and in the panel |
| New CLI usage line | `ProbeRunner.Usage` |
| New exit code | Only 0/1/2 are in use; add a test in `ProbeChecks` |
| Framework debugging | `LogToTrace` output, temporary |

Only reach for `Console.Write*` inside `PTMonitor.Cli`/`Diagnostics`. Nothing in
`Core.Client` or `Core.Store` prints.

---

## Anti-patterns

- `Console.WriteLine` in a store or client.
- `System.Diagnostics.Debug.WriteLine` as the only record of an important event.
- A hand-rolled `Logger` static.
- Logging the password "just to be sure".
- Logging inside `PollLoopAsync` per iteration.
