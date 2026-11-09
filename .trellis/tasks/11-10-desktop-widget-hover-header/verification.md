# Verification — desktop widget hover header + status dot

Implementation run on Linux x64 (no Windows desktop available), so the
automated gates were executed and the on-screen hover/pin checks are recorded
as **Windows pending**. Nothing below is committed; the work sits in the
working tree on `feat/desktop-widget-hover-header`.

## Implemented against the plan

| Plan step | Result |
|---|---|
| §1 `L10n.WidgetStatus` replaces `WidgetSummary` | Done: `Not configured` / `%lld active` / `%lld/%lld online`; both new zh values added and the two old three-count keys removed |
| §1 `widgetDot` class + `StatusDot`/`StatusText` | Done: dot has its own outlined class, fill starts neutral and is set from code-behind; row 4 is a horizontal `StackPanel` so the line never re-flows |
| §1 `ShowStatus`, cached counters, `ApplyLocalization` re-derivation | Done: `ShowStatus` caches `_servers/_online/_active`; `ApplyStatus()` is called from both `ShowStatus` and `ApplyLocalization` |
| §1 `AppServices.UpdateDesktopWidget` | Calls `ShowStatus` |
| §2 `Header` named/classed, `Opacity=0`, `IsHitTestVisible=False` | Done; row height stays reserved and the window stays 250 × 150 |
| §2 `Grid.widgetHeader` transition | Done: one `DoubleTransition` on `Opacity`, `0:0:0.16`, `CubicEaseOut` |
| §2 exit timer + show/hide | Done: 250 ms `DispatcherTimer` grace; `PointerEntered` stops it, `PointerExited` restarts it, the tick hides (and checks `IsVisible`) |
| §2 non-animated reset on open | Done: transitions cleared locally, hidden, then `ClearValue(Animatable.TransitionsProperty)` |
| §3 README | Content, new hover bullet, Lock bullet updated; 250 × 150 behaviour row kept |
| Untouched | Size, `CardWidth`/`CardHeight`, placement/clamping, drag threshold, double-click pairing, `SavePosition()`, `EnsureDesktopPin()`, `SetLocked()`, context menu, `Core` |

## Automated gates

Environment: .NET SDK 8.0.427 under `~/.dotnet`.

```bash
~/.dotnet/dotnet build -c Release
~/.dotnet/dotnet run --project src/PTMonitor.Cli -c Release -- --selftest
~/.dotnet/dotnet test tests/PTMonitor.Core.Tests -c Release
```

| Gate | Result |
|---|---|
| `dotnet build -c Release` | **Pass** — 0 errors, 1 pre-existing `AVLN3001` warning for `ServerEditorWindow` (public-constructor warning); the earlier `CS0103` for `Animatable` was fixed by adding `using Avalonia.Animation;` |
| `PTMonitor.Cli --selftest` | **Pass — 698 checks**. The plan says 296; the suite has grown since the plan was written |
| `dotnet test tests/PTMonitor.Core.Tests -c Release` | **Pass — 151/151**, 0 failed, 0 skipped |
| `rg WidgetSummary\|ShowSummary\|SummaryText` (src) | No hits — the rename is complete |

No `Core`, CLI or test file was touched.

## Windows / manual checks still open

Run the app with the widget enabled and the desktop pin active, then walk
`design.md` "Verification" 1–8 (idle card, hover fade in, leave grace and
mid-fade reversal, drag from the hidden strip, lock hover/click/tooltip,
all four status forms, English + 简体中文, 100 % and 150 % scaling).

Status forms to confirm on screen (dot colour, both languages):

| State | Expected |
|---|---|
| no server | neutral dot, `Not configured` / `未配置` |
| all enabled online | green dot, `%lld active` / `%lld 个活动` |
| one or more offline | red dot, `%lld/%lld online` / `%lld/%lld 在线` |

## Follow-up after the first check (main session)

- **Enabled servers only.** `AppServices.UpdateDesktopWidget` now passes
  `Settings.Servers.Count(server => server.IsEnabled)` instead of the total
  count. Counting disabled servers would make `online >= servers` unreachable
  whenever anything is disabled, so the card would sit on "1/2 online" (red)
  forever with a healthy server. `design.md` records the rule.
- **README counts refreshed.** The README still claimed 296 selftest checks and
  84 tests; the suites are at 698 and 151. Both numbers updated (the gap
  predates this task, but the README is the contract this task also edits).
- Re-ran the three gates after the change: build 0 errors, selftest 698 OK,
  tests 151/151.

## Windows build produced (2026-11-10 00:14)

The App layer has no automated gate, so the exe was republished from the
working tree after the last change (`frontend/quality-guidelines.md`, "Handing
a build over" — a stale `publish/` build looks exactly like a broken change).

```bash
~/.dotnet/dotnet publish src/PTMonitor.App -c Release -r win-x64 \
    --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=1.0.0 -o publish/win-x64
~/.dotnet/dotnet publish src/PTMonitor.Cli -c Release -r win-x64 \
    --self-contained true -p:PublishSingleFile=true -p:Version=1.0.0 \
    -o publish/win-x64/cli
```

| | |
|---|---|
| exe | `publish/win-x64/PTMonitor.exe` — 94 170 095 bytes, mtime `2026-11-10 00:14` |
| sha256 | `8308dcad7688c95a3ec57d939997e0375ce70d51502908380176b4c2d2bc9190` |
| zip | `artifacts/PTMonitor-1.0.0-widget-hover-win-x64.zip` (69 972 373 bytes, `PTMonitor.exe` + `cli/`) |
| zip sha256 | `5e6cbcb6d80ddb16feb56727e82899ec98852945d59a7ee80bb2755b83003815` |

The publish is newer than the last commit (`2026-11-09 00:13`), so it contains
the change. It is **not** committed and it is **not** in `SHA256SUMS.txt` —
`artifacts/SHA256SUMS.txt` still lists the released zips, and `scripts/build.ps1`
was not used (no PowerShell on this box).

Automated gates re-run against this tree: build 0 errors (1 pre-existing
`AVLN3001` warning), selftest 698 OK, tests 151/151.

The on-screen checks (hover fade, mid-fade reversal, drag from the hidden
strip, lock click, four status forms, both languages, 100 %/150 % scaling,
behaviour with the desktop pin active) still need a Windows desktop and are
**not** claimed.

## Review fix: hit testing follows the fade-in, not the pointer

The first implementation flipped `Header.IsHitTestVisible = true` on
`PointerEntered`, i.e. 160 ms before the header was actually readable. A press
that landed in the top-right corner during the fade-in hit a nearly invisible
lock button and toggled the lock instead of starting a drag — prd.md
requirement 4 / acceptance criterion 5.

Fixed with a second timer (`_revealTimer`, `HeaderFadeInMs = 170`, slightly over
the 160 ms transition): `ShowHeader()` starts the fade with hit testing still
off, and only the tick — guarded by `_headerShown && IsVisible` — turns it on.
Leaving during the fade-in cancels the reveal, so the header never becomes
clickable after the pointer has gone. `HideHeader()` stops both timers, so no
timer outlives the state it belongs to.

Re-ran the gates: build 0 errors (4 pre-existing warnings: `CS8602`/`CA1416` in
`Core`, `AVLN3001` in `ServerEditorWindow`), selftest 698 OK.
