# Implement — launch path and packaging performance

Execution plan for `11-05-launch-path-performance`. Design in `design.md`,
requirements in `prd.md`.

## Before starting

- Read `design.md`'s invariant list before touching the `AppServices`
  constructor. The order carries constraints; "tidying" it breaks them.
- The App layer has **no automated gate** (`frontend/quality-guidelines.md`).
  Everything reachable from `Core` gets a selftest case **and** an xUnit
  mirror; everything else is a Windows manual check.
- Keep `scripts/build.sh` and `scripts/build.ps1` byte-for-byte equivalent in
  behaviour.

## Checklist

### Step 1 — the timeline (Core)

- [x] Add `src/PTMonitor.Core/Diagnostics/StartupTrace.cs`: `Begin`, `Mark`,
      `Flush`, `Format`, `Reset`, injectable clock. No lock (UI thread only),
      no-op when `Begin` never ran, `Flush` idempotent.
- [x] `Format` renders `phase=NN.N` pairs in fixed order plus `total=NN.N ms`;
      unknown/empty phase list renders `entry=0.0 total=0.0 ms` rather than
      throwing.
- [x] Selftest case in `src/PTMonitor.Core/Diagnostics/SelfTest.cs`: marks →
      format → assert the exact string and the `total`; assert `Flush`
      idempotence and that `Mark` without `Begin` stays silent.
- [x] xUnit mirror in `tests/PTMonitor.Core.Tests/CoreTests.cs` (same
      assertions; the spec requires both in the same commit).
- [x] Verify: `dotnet build -c Release`, `--selftest`, `dotnet test`.

### Step 2 — call sites (App)

- [x] `Program.Main`: `StartupTrace.Begin()` as the **first** statement.
- [x] `Mark("log-ready")` after `Log.Initialize`.
- [x] `Mark("avalonia")` as the first statement of
      `App.OnFrameworkInitializationCompleted` (after the dispatcher exists —
      do not touch `Dispatcher.UIThread` earlier).
- [x] `AppServices` constructor per `design.md` §2: reorder, `Mark("stores")`
      after the secret store, `Mark("glyph")` after the icon render,
      `Mark("tray-set")` after `TrayIcon.SetIcons`.
- [x] Replace the inline `SyncFloatingWindow()` / first-run `ShowPreferences()`
      with one `Dispatcher.UIThread.Post(…, DispatcherPriority.Background)`
      callback that runs them, then `Mark("windows")` and `Flush()`.
- [x] Verify by hand on Windows: one `startup: timeline …` line per launch,
      offsets non-decreasing, total equal to the last offset. *Done 2026-11-05:
      `entry=0.0 log-ready=12.2 avalonia=345.2 glyph=418.2 tray-set=418.4
      stores=423.6 windows=575.4 total=575.4 ms`.*

### Step 3 — gates

- [x] `dotnet build -c Release` → 0 errors.
- [x] `--selftest` → all checks pass.
- [x] `dotnet test` → all tests pass.
- [x] `scripts/build.sh --invariant` → ZIP + CLI produced.
- [x] Launch the published exe on Windows, leave it 30 s, confirm a clean
      `ptmonitor.log` (`frontend/quality-guidelines.md` requires this for
      startup changes). *Done 2026-11-05 on the target machine: the log holds
      one `startup: timeline` line and nothing after it.*

### Step 4 — measurement decision (needs the user)

- [x] Ask the user to run the build once on the target machine and paste the
      `startup: timeline` line. *Done — see below.*
- [x] Apply `design.md` §3's decision table: folder layout, ReadyToRun, or no
      packaging change. Change `scripts/build.sh` **and** `scripts/build.ps1`
      together, with the reason as a comment. *No packaging change — the
      measured timeline does not blame the bundle (see below).*
- [x] If the package shape changes: update the README feature bullet, the
      quick-start / build sections and the ZIP layout description; re-run
      Step 3. *N/A — the package shape is unchanged.*

### Step 5 — documentation

- [x] README `### The log`: what the timeline is and that it exists only to be
      pasted into a performance report.
- [x] README `## Troubleshooting`: the target number and what a slow phase
      means (host phase → antivirus/SmartScreen; `avalonia`/`windows` phase →
      JIT; `glyph` phase → Skia start).
- [x] Spec updates: `frontend/lifecycle-and-tray.md` (deferred windows, the
      timeline) and `backend/logging-guidelines.md` (the new event in the
      "What is logged" table).

## Validation commands

```bash
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release
scripts/build.sh --invariant
```

`--selftest` and `dotnet test` must be run after every `Core` edit, not once at
the end.

## Risky points

| Point | Risk | Guard |
|---|---|---|
| Constructor reorder | `OnSettingsChanged` / `SyncFloatingWindow` reaching a null field | follow `design.md` §2's invariant list; `_trayIcon` and `_floatingTicker` both precede the `Settings.Changed` subscription |
| `Dispatcher.UIThread` in `Program.Main` | `PlatformNotSupportedException` at first frame | only `StartupTrace.Begin()` (no Avalonia) runs before `BuildAvaloniaApp()` |
| Deferred first-run Preferences | a first-run user sees the icon before the window | `Background` priority only; the window still opens unconditionally when there are no servers |
| Readiness to publish | a wrong flag silently shipped | both scripts change together, CI runs `build.ps1` |
| `StartupTrace` cost | measurable slowdown of the very path it measures | seven `GetTimestamp` calls; no I/O until `Flush` |

## Rollback points

- After Step 1: delete `StartupTrace.cs` + the two test blocks.
- After Step 2: restore the constructor block and the inline window calls; the
  `Mark` calls can stay (no-ops without `Begin`).
- After Step 4: revert the flag in both scripts and the README lines.

## Definition of done

- All PRD acceptance criteria ticked, the timeline visible in a real Windows
  run, and the packaging decision recorded with its evidence.

## Review outcome (2026-11-05)

`trellis-check` reviewed the change and found one real defect, now fixed:

- **The phase order did not match the launch order.** `StartupTrace.PhaseOrder`
  listed `stores` before `glyph`, but the reordered constructor marks `stores`
  *after* `tray-set`, so the emitted offsets came out non-monotonic — the very
  thing `implement.md` Step 2 asks to verify. `PhaseOrder` is now
  `entry, log-ready, avalonia, glyph, tray-set, stores, windows`, and both the
  selftest and the xUnit mirror pin the real launch order.
- **`Clock` is internal, not public.** `design.md` §1 asked for `internal`; the
  Core project had no `InternalsVisibleTo`, so the seam would have had to be
  public — a public mutable static clock. `PTMonitor.Core.csproj` now grants
  `InternalsVisibleTo("PTMonitor.Core.Tests")` and `Clock` stays internal.
- `Flush` derives `total` from the **max** offset rather than the last one, so
  a non-monotone clock cannot understate the launch.
- `Format` renders the canonical phases first and any unexpected phase after
  them in arrival order: still diffable, still never throws.

Gates after the fixes: `dotnet build -c Release` → 0 errors · `--selftest` → 648
checks · `dotnet test` → 126 tests · `scripts/build.sh --invariant` → ZIP and
CLI produced.

## Windows measurement and the packaging decision (2026-11-05)

Timeline from the target machine (Windows 10.0.26300, build 1.0.0, warm build):

```
entry=0.0 log-ready=12.2 avalonia=345.2 glyph=418.2 tray-set=418.4 stores=423.6 windows=575.4 total=575.4 ms
```

Read against `design.md` §3:

| Evidence | Reading |
|---|---|
| `entry → log-ready` = **12 ms** | the bundle load / self-extract is not the bottleneck → **no folder-layout change** |
| `avalonia` = **345 ms** | the dominant phase, but it is the framework's own start-up (Win32 + Avalonia init), not JIT of our code |
| `tray-set` = **418 ms** | the icon is on the taskbar ~3.6× inside the 1500 ms target |
| `windows` = **575 ms** | the first optional window, *after* the icon — off the critical path by design |
| `total` = **575 ms** | 2.6× inside the target |

Decision: **no packaging change.** Both build scripts keep their flags and now
carry the numbers above as the reason, so the next reader does not re-litigate
it. ReadyToRun stays out (D3): it needs a same-machine A/B, and it was already
measured as no-gain on Linux (105 vs 109 ms). The one thing the timeline does
*not* prove is a ReadyToRun win — a Windows A/B would, and remains available as
an optional follow-up if the `avalonia` phase ever matters.
