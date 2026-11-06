# Startup and runtime performance

Parent task. Owns the requirement set, the child-task map, the cross-child
acceptance criteria, and the final integration review. It carries no
implementation work of its own — the two children own the code changes.

## Goal

The agent must become visible quickly after a launch, and must stop doing work
that nobody can see. Two user complaints drive this:

1. **Launch latency** — after clicking the exe, the tray icon takes a while to
   appear.
2. **Running cost** — the agent keeps working in the background; the cost of
   that work should be as small as the feature allows.

The outcome is measured, not guessed: the app reports its own startup cost, and
the idle path stops doing per-poll UI work that is invisible.

## Background — confirmed facts (repository evidence, 2026-11-05)

### Launch path (process start → visible tray icon)

| Step | Anchor | Notes |
|---|---|---|
| .NET host + Avalonia platform init | `src/PTMonitor.App/Program.cs:44` | measured ≈ 105 ms on Linux (15 runs) |
| Read `settings.json` | `src/PTMonitor.App/Services/AppServices.cs:47` | one small JSON file |
| Open the DPAPI secret store | `src/PTMonitor.App/Services/AppServices.cs:48` | per-user DPAPI |
| Render the tray glyph — 5 sizes through Skia, the **first** Skia call in the process | `src/PTMonitor.App/Services/AppServices.cs:65`, `src/PTMonitor.App/Services/TrayIconFactory.cs:57` | `Sizes = [16, 20, 24, 32, 48]` |
| **Tray icon becomes visible** | `src/PTMonitor.App/Services/AppServices.cs:69` | everything above is on the critical path |
| Start the poll loops | `src/PTMonitor.App/Services/AppServices.cs:81` | `MonitorStore.Start` → `Task.Run` per server — **not** blocking (`src/PTMonitor.Core/Store/MonitorStore.cs:54`) |
| Floating chip window (only when enabled) | `src/PTMonitor.App/Services/AppServices.cs:87` | XAML load + first render |
| Preferences window (only on first run) | `src/PTMonitor.App/Services/AppServices.cs:92` | XAML load + first render |

### Packaging

- `scripts/build.sh:112-116` and `scripts/build.ps1:53-54` publish
  `PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true`,
  self-contained, **without** `PublishReadyToRun`.
- The published exe is ~90 MB (`publish/win-x64/PTMonitor.exe`).
- The single-file host extracts the native libraries **once per bundle id** and
  caches them (`%TEMP%\.net\PTMonitor\<hash>\` on Windows; observed on Linux
  during the 2026-11-05 measurement). Repeated launches of the same build reuse
  the directory — so extraction is a **per-build** cost, not a per-launch cost.
- ReadyToRun measured on Linux: app startup 109 ms → 105 ms, CLI startup
  40 ms → 42 ms. **No measurable gain** on the startup path that was measured.
- The exe is unsigned, so SmartScreen shows its "unknown publisher" warning on
  a downloaded copy.

### Steady state (the running agent)

- Default poll interval is **2 s** (`src/PTMonitor.Core/Store/SettingsStore.cs:14`).
- `PanelViewModel` subscribes to `monitor.StatusesChanged` and posts a full
  `Rebuild()` on the UI thread (`src/PTMonitor.App/ViewModels/PanelViewModel.cs:135`).
  The panel window is cached and only hidden, never disposed
  (`src/PTMonitor.App/Services/AppServices.cs:_panel`), so **after the panel has
  been opened once it rebuilds its whole UI on every poll, forever, even while
  invisible**: `_sections.Clear()` plus up to
  `MaxVisibleTorrentsPerServer = 100` new row view models per server
  (`src/PTMonitor.App/ViewModels/PanelViewModel.cs:123`, `:182`).
- qBittorrent is polled with `/api/v2/sync/maindata?rid=0`
  (`src/PTMonitor.Core/Client/QBittorrentClient.cs:239`) — `rid=0` is a **full**
  sync: the whole torrent list and the tracker map come back on every poll.
- Transmission already asks for a fixed, lean field list
  (`src/PTMonitor.Core/Client/TransmissionClient.cs:10-16`) and throttles the
  free-space call (`src/PTMonitor.Core/Client/TransmissionClient.cs:229`).
- Torrent ordering is ordinal (`src/PTMonitor.Core/Model/TorrentSummary.cs:82`)
  and the Chinese UI is a hand-written table
  (`src/PTMonitor.App/Services/L10n.cs`), so the ICU data files are not needed
  for either.

## Requirements

1. **Measure before optimising.** The app must be able to report how long its
   own startup took, so the launch work is guided by numbers from the target
   machine rather than by guesses. The measurement is written to the existing
   log sink (`src/PTMonitor.Core/Diagnostics/Log.cs`, millisecond stamps).
2. **Nothing invisible may be built on the launch path.** Window creation that
   is not needed for the tray icon to appear must not delay the tray icon.
3. **No work for a hidden panel.** The agent must not rebuild the panel UI on
   polls while the panel is closed.
4. **Bounded poll cost.** The per-poll cost must not grow with the size of the
   torrent library beyond what the panel actually shows.
5. **No behaviour regressions.** The app stays read-only, keeps its bilingual
   UI, keeps the secrets in the DPAPI store, and keeps every existing
   self-test and xUnit check green.
6. **Windows is the only place the launch claim can be proven.** Every claim
   about launch latency must be verifiable on the target machine; the Linux
   build gates (build, selftest, xUnit, publish) must stay green here.

## Child task map

| Child | Owns |
|---|---|
| `11-05-launch-path-performance` | Startup instrumentation, the launch path itself, publish options, and the distribution-shape decision. |
| `11-05-runtime-polling-performance` | The hidden-panel rebuild, the per-poll payload, and the poll interval. |

The children are independent: neither needs the other's code to be verifiable.
Child A's startup instrumentation is the measuring instrument both children use.

## Cross-child acceptance criteria

- [x] The published Windows package reports its own startup cost in
      `%APPDATA%\PTMonitor\ptmonitor.log`, split into named phases. *One
      `startup: timeline entry=0.0 log-ready=12.2 avalonia=345.2 glyph=418.2
      tray-set=418.4 stores=423.6 windows=575.4 total=575.4 ms` line per
      launch, measured on the target machine.*
- [x] `dotnet build -c Release` finishes with 0 errors, `--selftest` passes,
      and `dotnet test` passes. *692 selftest checks, 150 xUnit tests.*
- [x] `scripts/build.sh --invariant` still produces `PTMonitor-<version>-win-x64.zip`
      and the CLI, and `scripts/build.ps1` stays in sync with it. *Both scripts
      carry the same publish options and the same one-line reason; the flags
      are unchanged (the measured timeline does not blame the bundle).*
- [x] The panel shows the same data as before while it is open, and the agent
      does no panel-rebuild work while it is closed. *The panel's visibility
      drives one flag in the store: no rebuild and no torrent list while it is
      hidden. The Windows observation of the open panel is part of the manual
      pass below.*
- [x] The README's feature/behaviour descriptions still match the shipped
      behaviour (poll interval, packaging shape, bilingual UI). *The README
      documents the two poll scopes, the 2 s interval and the packaging
      decision.*

## Manual verification still open (Windows)

Both children are code-complete and gated on Linux. The target machine has to
confirm what a Linux box cannot:

- the launch timeline stays within the 1500 ms target and the tray icon shows
  up first (done for the launch child);
- the tooltip and the floating chip keep updating every 2 s with the panel
  closed;
- the panel opens with data and stays live; opening and closing it five times
  leaves the poll scope on the summary;
- the tray icon still switches between Idle and Active;
- qBittorrent's panel list matches the server's list;
- the log stays clean for 30 s.

## Out of scope

- **Code signing.** It needs a CA-issued certificate (OV/EV) or Azure Trusted
  Signing; dropped on 2026-11-03 because no certificate exists. The SmartScreen
  warning therefore stays.
- **The single-instance guard.** A separate roadmap item
  (`README.md` → "Plausible next steps"); it changes behaviour, not cost.
- **Native AOT.** Avalonia is not AOT-ready.
- **Changing the poll interval default** without an explicit user decision
  (that decision belongs to child B).
- **Any new mutating call to a server.** The app stays read-only.

## Open questions

- ~~**Q3 — the poll interval (still open).**~~ **Answered 2026-11-06: keep
  2 s.** The default poll interval is unchanged. The idle cost was cut by
  fetching less and rebuilding less, not by polling less often — the tooltip
  and the chip stay as fresh as they were. See
  `11-05-runtime-polling-performance/prd.md` (D1).

## Resolved decisions

- **Q1 — launch target: ≤ 1.5 s**, measured as the `total` of the startup
  timeline on the target machine, warm build. Below ≈ 0.5 s would force the
  folder layout. Owned and detailed in
  `11-05-launch-path-performance/prd.md` (D1).
- **Q2 — the distribution shape changes only on measured evidence**, by the
  decision table in `11-05-launch-path-performance/design.md` §3. The single-exe
  promise is only given up if the host/extraction phase is what the timeline
  blames.
