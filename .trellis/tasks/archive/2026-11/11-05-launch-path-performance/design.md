# Design — launch path and packaging performance

Technical design for `11-05-launch-path-performance`. Requirements live in
`prd.md`; the ordered execution steps live in `implement.md`.

## Boundaries

Three pieces, deliberately small:

| Piece | Layer | Why there |
|---|---|---|
| `StartupTrace` — the launch timeline | `Core/Diagnostics` | pure clock + string work, no Avalonia; `Log` already lives here and is the one sink |
| Startup-path reorder + deferred window creation | `App/Services/AppServices`, `App/Program` | only the composition root knows the launch order |
| Publish options | `scripts/build.sh`, `scripts/build.ps1`, README | packaging, not code |

No new package, no new document store, no second logger.

## 1. `StartupTrace`

```
Core/Diagnostics/StartupTrace.cs
```

Static, `internal`-free (same shape as `Log`), one purpose: hold phase marks and
emit **one** line.

```csharp
public static class StartupTrace
{
    public static void Begin();                 // Program.Main, first statement
    public static void Mark(string phase);      // one per phase, constants only
    public static void Flush();                 // exactly once, when the launch is done
    public static string Format(IReadOnlyList<StartupMark> marks, double totalMs); // pure, tested
    public static void Reset();                 // tests
    internal static Func<double> Clock { get; set; } // tests: a deterministic counter
}
```

Rules:

- `Begin()` is the **first statement** of `Program.Main`, before
  `Log.Initialize`, so `entry = 0 ms` and every offset is measured from the
  earliest point a managed app can observe.
- Phases are fixed constants, never interpolated values, so redaction has
  nothing to do: `entry`, `log-ready`, `avalonia`, `glyph`, `tray-set`, `stores`,
  `windows` — the order the launch path runs, so the offsets stay non-decreasing.
- Offsets are **cumulative** from `entry` and are rendered with one decimal
  (`1.4 ms`); `Format` also renders `total`. Ordinal-fixed order, no sorting, so
  the output is diffable between runs.
- One line, one `Log.Info("startup", …)` call in `Flush()`. That matches the
  logging spec's "events only, one line per event": one line per launch, not
  seven.
- No lock: every mark runs on the UI thread (`Program.Main` → `App` →
  `AppServices` ctor → the posted callback). Documented in the file.
- Cost: one `Stopwatch.GetTimestamp` plus one list add per mark, seven marks.
  If `Begin` never ran, `Mark`/`Flush` are no-ops — with the clock null they
  cannot produce a bogus zero line. (Keeps the CLI and the selftest free of
  side effects, exactly like `Log`.)
- `Flush()` is idempotent: it clears the marks, so a second call is a no-op.

Sample line:

```
2026-11-05T21:04:11.913Z [info] startup: timeline entry=0.0 log-ready=2.1 avalonia=118.7 glyph=158.4 tray-set=160.2 stores=163.1 windows=164.9 total=164.9 ms
```

## 2. Startup-path reorder

Target order in the `AppServices` constructor — the tray icon is set at step 7,
and nothing after step 7 can delay it:

| # | Step | Constraint it satisfies |
|---|---|---|
| 1 | `Instance = this` | the first-run preferences window re-enters through `Instance` |
| 2 | `new SettingsStore` | everything reads settings |
| 3 | `L10n.SetLanguage` | `BuildMenu()` needs the language |
| 4 | `_floatingTicker` | `SyncFloatingWindow` may run from `Settings.Changed` |
| 5 | `_trayIcon` + `TrayIconFactory.Icon(Idle, …)` ← `Mark("glyph")` | first Skia call, must precede the icon |
| 6 | `TrayIcon.SetIcons` ← `Mark("tray-set")` | **the icon is now visible** |
| 7 | `new DpapiSecretStore` ← `Mark("stores")`, log its state | DPAPI is the last thing that used to block the icon |
| 8 | `new MonitorStore`, theme hook, `StatusesChanged` hook | needs `Secrets` |
| 9 | `UpdateTray()`, `Monitor.Start()`, `_trayIcon.Clicked +=` | needs `Monitor` |
| 10 | `Dispatcher.UIThread.Post(…, Background)` → `SyncFloatingWindow`, first-run `ShowPreferences`, `Mark("windows")`, `Flush()` | optional windows leave the critical path |

Preserved invariants, spelled out because the order looks arbitrary otherwise:

- `_trayIcon` exists before `Settings.Changed += OnSettingsChanged`
  (`OnSettingsChanged` assigns `_trayIcon.Menu`).
- `Secrets` exists before `new MonitorStore(...)` (the store takes it).
- `Monitor` exists before `UpdateTray()` (it reads `MenuBarState`/
  `Aggregate`) and before `StatusesChanged`/`Start()`.
- `UpdateTray()` keeps running once during construction with no statuses —
  identical to today's behaviour, so the tooltip and the idle icon are correct
  before the first poll.
- The theme-variant subscription moves next to the icon it affects, still before
  any window exists.

`DispatcherPriority.Background` (not `Default`) so the icon's first frame and
the message loop win the queue. Deferring also means `Settings.Servers.Count`
is read at post time, not construction time — acceptable, nothing mutates
settings between the two.

## 3. Windows measurement, then packaging

The instrumentation is the instrument for the packaging decision, so packaging
lands **after** the user reports a timeline:

| Timeline evidence | Decision |
|---|---|
| `entry → log-ready` (bundle load / extraction) dominates | switch the package to the folder layout: `-p:IncludeNativeLibrariesForSelfExtract=false` in both scripts, keep `PublishSingleFile` off is **not** required — the flag alone stops the self-extract; DLLs sit beside the exe. README's "single exe" promise and the ZIP layout are updated with it. |
| `avalonia` or JIT-heavy phases dominate, with a same-machine A/B confirming it | `-p:PublishReadyToRun=true` in both scripts |
| neither dominates, total already under target | ship no packaging change; record the numbers and the reasoning as a comment in both scripts |

Both scripts change together, always (`scripts/build.sh`, `scripts/build.ps1` —
`build.ps1` is the CI path). The chosen options get a one-line reason next to
them, so the next reader does not re-litigate it.

## Compatibility / migration

- **Log format**: `startup` gains one line per launch. Existing `startup` lines
  (version/OS, secret-store state) stay — the timeline is additive.
- **README**: a target number and the "how to read the timeline" note belong in
  `### The log` / `## Troubleshooting`; the packaging shape (if it changes)
  belongs in the Features bullet and the build/quick-start sections, plus the
  "unblock the ZIP" note for the still-unsigned SmartScreen path.
- **Spec**: `frontend/lifecycle-and-tray.md` stops being true about
  "the constructor … opens Preferences" — it must describe the deferred
  windows and the timeline. `backend/logging-guidelines.md` gets the new event
  in its "What is logged" table.
- **Users**: no settings file, secret or API change. The poll interval is child
  B's business.

## Trade-offs

| Choice | For | Against |
|---|---|---|
| Timeline into the existing log | no new tool, survives a kill, already redacted | one more line per launch in a file the troubleshooting section tells users to read |
| One line at the end, not seven | matches "events only"; cheap to scan | a launch that dies early logs nothing |
| Reordering the constructor | the only lever that helps on *every* machine | the order carries invariants that must be re-read before touching it |
| Deferring window creation | removes real render work from the path | first-run Preferences appears a frame later |
| `Background` priority | the icon wins | the chip can trail the icon by a frame |

## Rollback

- The instrumentation is additive: reverting the `Mark`/`Flush` call sites
  leaves `StartupTrace` unused and harmless; reverting the file too is a clean
  delete.
- The reorder is a single block in one constructor — re-read the invariant list
  above when reverting, do not "tidy" it back blindly.
- Publish options are one token per script plus one README line each; revert
  them together.
