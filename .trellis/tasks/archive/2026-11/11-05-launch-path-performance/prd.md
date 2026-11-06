# Launch path and packaging performance

Child of `11-05-performance-startup-runtime`. Owns the launch path: the
measurement, the critical path itself, and the packaging options behind it.

## Goal

From the moment the user clicks the exe to the moment the tray icon is on the
taskbar, the agent spends as little time and touches as little disk as
possible — and it can prove how long that took.

## Confirmed facts

The launch path, its anchors, and the measurements taken on 2026-11-05 are
recorded in the parent PRD (`11-05-performance-startup-runtime/prd.md`). The
short version:

- The tray icon becomes visible at `src/PTMonitor.App/Services/AppServices.cs:69`;
  everything before it is on the critical path (settings read, DPAPI store,
  one Skia render of five icon sizes).
- Poll loops start off the UI thread and never block the launch
  (`src/PTMonitor.Core/Store/MonitorStore.cs:54`).
- The floating chip window and the first-run preferences window are created on
  the same path (`AppServices.cs:87`, `:92`).
- The package is a 90 MB self-extracting single file with no ReadyToRun
  (`scripts/build.sh:112-116`, `scripts/build.ps1:53-54`).
- The host caches the native-library extraction per build, so extraction is a
  per-build cost, not a per-launch cost.
- ReadyToRun showed **no measurable** startup gain in the Linux measurement.

## Requirements

1. **Startup instrumentation.** The agent must write its own launch timeline to
   the existing log sink, with named phases and millisecond offsets:
   process entry → log sink ready → Avalonia initialised → settings and
   secrets loaded → tray glyph rendered → **tray icon set** → first optional
   window created. The line must not contain secrets, and adding it must not
   measurably slow the launch.
2. **The tray icon comes first.** No optional window may be created before the
   tray icon is on screen. The floating chip window and the first-run
   preferences window are created after the icon is set, on the dispatcher
   queue, not inline in the composition root.
3. **The measurement must be reproducible.** A user on the target machine must
   be able to run the app once, read the timeline out of
   `%APPDATA%\PTMonitor\ptmonitor.log`, and paste it back. No extra tool, no
   separate build.
4. **Publish options are evidence-driven.** ReadyToRun and the
   native-library self-extract flag are decided by the Windows measurement, not
   by habit. `scripts/build.sh` and `scripts/build.ps1` must stay in sync, and
   the decision must be recorded where the next reader will find it.
5. **No regression in what the launch produces.** The tray icon, the tray
   menu, the floating chip and the first-run preferences flow all keep working
   exactly as before; the app stays read-only and bilingual.
6. **The Linux gates stay green.** `dotnet build -c Release`, `--selftest`,
   `dotnet test`, and `scripts/build.sh --invariant` must all pass.

## Decisions

- **D1 — launch target: ≤ 1.5 s**, measured as the `total` of the startup
  timeline, on the target machine, from a warm build (a build already launched
  once). Rationale: the .NET/Avalonia floor is ≈ 0.1 s and everything above it
  is either the app's own startup work or Windows-side scanning; 1.5 s is
  reachable without changing the distribution shape or buying a certificate.
  Tightening it below ≈ 0.5 s would force the folder layout (D2).
- **D2 — the distribution shape changes only on evidence.** The measurement
  decides (rule table in `design.md` §3). The README's "a single PTMonitor.exe"
  promise is only given up if the host/extraction phase is what the timeline
  blames.
- **D3 — ReadyToRun is not adopted on habit.** The Linux measurement showed no
  gain (105 ms vs 109 ms); it is only added if a Windows A/B shows one.

## Acceptance criteria

- [x] Launching the app writes one startup timeline line to the log, with the
      phase offsets, and the offsets add up to the total. *Pinned by
      `--selftest` (648 checks) and the xUnit mirror (126 tests): one line per
      launch, cumulative non-decreasing offsets, `total` last, silence without
      `Begin`. The Windows observation is part of the manual pass below.*
- [x] The tray icon is set before the floating chip window (when enabled) or
      the preferences window (first run) is created. *Constructor order in
      `AppServices`: `TrayIcon.SetIcons` → secret store → monitor → the posted
      window callback. Confirmed on Windows: `tray-set=418.4` precedes
      `windows=575.4`.*
- [x] The timeline is visible to a user on the target machine without any
      extra tooling. *One `startup: timeline …` line in
      `%APPDATA%\PTMonitor\ptmonitor.log`.*
- [x] `scripts/build.sh` and `scripts/build.ps1` publish the same options, and
      the option choice is documented in the README and in the build scripts.
      *The flags are unchanged (D3); both scripts carry the same one-line
      reason for not adding ReadyToRun, and `README.md` documents the timeline
      and the ≤ 1500 ms target.*
- [x] `dotnet build -c Release` → 0 errors; `--selftest` → all checks pass;
      `dotnet test` → all tests pass; `scripts/build.sh --invariant` → ZIP and
      CLI produced. *Re-verified after the final change: 0 errors, 648 checks,
      126 tests, `artifacts/PTMonitor-1.0.0-win-x64.zip` + `publish/win-x64`.*
- [x] Windows manual pass: `total` ≤ 1500 ms; the floating chip still appears;
      the first-run preferences still open; the process survives 30 s with a
      clean log. *Done 2026-11-05 on the target machine: `total=575.4 ms`,
      `tray-set=418.4 ms`, one timeline line and nothing after it in the log.*
- [x] Step 4 (publish-option decision) follows the timeline. *Decided: no
      packaging change — `entry → log-ready` is 12 ms, so the bundle is not the
      bottleneck; the dominant `avalonia` phase is the framework's own start-up.
      Both scripts carry the numbers as their reason; ReadyToRun stays out
      until a Windows A/B shows a gain (D3).*

## Out of scope

- Code signing (no certificate — dropped 2026-11-03).
- The single-instance guard (separate roadmap item).
- Native AOT (Avalonia is not AOT-ready).
- Changing what the tray shows or what the panel contains.
- The poll interval and the poll payload (child `11-05-runtime-polling-performance`).
