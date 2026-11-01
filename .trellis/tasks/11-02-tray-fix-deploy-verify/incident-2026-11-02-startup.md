# Incident 2026-11-02 — instant exit on launch (`PlatformNotSupportedException`)

Recorded as part of `11-02-tray-fix-deploy-verify`, because it happened while
deploying and it changes what "verified" means for this task.

## What the user saw

`PTMonitor.exe` closed immediately on launch — no window, no tray icon, no error
dialog. Two attempts, 00:26:51 and 00:29:30 local, both from
`C:\Users\enkru\Downloads\PTMonitor.exe` (`1.0.0+9a846be`, sha256 `3FA96736…`).

## What the log said

The build that crashed was the one carrying the new log sink, so the log itself
was the diagnostic:

```
2026-11-01T11:26:51.182Z [info] startup: PTMonitor 1.0.0.0, ... data C:\Users\enkru\AppData\Roaming\PTMonitor
2026-11-01T11:26:51.557Z [info] startup: secret store: Ok
2026-11-01T11:26:51.733Z [error] appdomain: System.PlatformNotSupportedException: Operation is not supported on this platform.
  | at Avalonia.Threading.Dispatcher.MainLoop(CancellationToken cancellationToken)
  | at ...StartWithClassicDesktopLifetime(...) | at PTMonitor.App.Program.Main(String[] args)
```

This is **not** the tray crash (that one is `Win32Exception` in
`Win32Icon.CreateIcon` after ~59 min). This was a startup regression, new, and
caused by the logging work in PR #10.

## Root cause

`Program.Main` subscribed the UI-thread handler **before** the platform existed:

```csharp
AppDomain.CurrentDomain.UnhandledException += …;
Dispatcher.UIThread.UnhandledException += …;   // ← here
BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
```

`Dispatcher.UIThread` lazily constructs a `Dispatcher` bound to the **default**
dispatcher implementation, which is the "not supported on this platform" stub.
Once constructed, the Win32 platform cannot replace it, so
`Dispatcher.MainLoop` throws on the first frame.

The trap: **accessing `Dispatcher.UIThread` does not throw.** A "does this throw?"
check passes — which is exactly how it got through review. The failure only
appears later, in the run loop.

`Program.cs` already carried the warning in its header comment: *"Don't use any
Avalonia … APIs before AppMain is called: things aren't initialized yet and stuff
might break."* The change violated a rule that was written down in the file.

## Minimal repro (proves causality, not correlation)

Avalonia 11.3.2, two runs of the same tiny app, differing only in whether
`Dispatcher.UIThread` is touched first:

| Variant | Output |
|---|---|
| A — not touched | `framework init completed` → `dispatcher ran a job` → `RESULT: started and exited cleanly` |
| B — touched first | `touched Dispatcher.UIThread OK` → `framework init completed` → `RESULT: CRASH System.PlatformNotSupportedException` |

Variant B reproduces the user's log exactly, including the "touch succeeded"
step.

## Fix

`68f2839` — the subscription moved to `App.OnFrameworkInitializationCompleted`,
which runs after the platform has installed its real dispatcher and before the
loop starts. `AppDomain.CurrentDomain.UnhandledException` stays in `Main`; it is
not an Avalonia API.

## Verification

| Check | Result |
|---|---|
| Built `1.0.0+68f2839`, sha256 `E0D320AC…` | — |
| Launched from `Downloads`, alive after 20 s | **yes**, pid 20052 |
| `ptmonitor.log` after that run | two `startup` lines, **no error line** |
| `dotnet build` / `--selftest` / `dotnet test` | clean / 298 checks / 85 tests |
| First handle baseline (26 s uptime) | GDI 42, USER 49, Handles 1080 |

## Lesson recorded

Added to `.trellis/spec/frontend/quality-guidelines.md` as a forbidden pattern,
plus an explicit warning that **the App layer has no automated gate** — the suite
never starts a window, so a startup change must be verified by launching the exe
and watching it survive, not by the build going green.

The gap this exposes: `--selftest` and the xUnit suite passed on the broken
build. Nothing in the pipeline runs `Program.Main`. A startup regression is
invisible to every gate this project has.
