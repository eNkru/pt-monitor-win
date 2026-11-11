# Journal - Howard.J (Part 1)

> AI development session journal
> Started: 2026-10-27

---

## 2026-10-27 — tray app-identity icon: gates on this box

This WSL box has no libicu, and `Directory.Build.props` pins
`<InvariantGlobalization>false</InvariantGlobalization>`, so the SDK emits
`"System.Globalization.Invariant": false` into `bin/**/*.runtimeconfig.json`
— which **overrides** `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. Builds work
with the env var, but `--selftest` / `dotnet test` crash on ICU unless you
first sed the emitted runtimeconfig in `bin/` to `"System.Globalization.Invariant": true`
and run `dotnet test --no-build`. Patch only the `bin/` copy, never the repo.
Pixel verification harness for tray work lives at `/tmp/iconprobe` (references
the repo's `PTMonitor.App.csproj`; do not commit it or its packages).



## Session 1: Branch prerelease pipeline
<!-- trellis-session: v=2 fp=82c2716a5b0391b8 -->

**Date**: 2026-10-30
**Task**: Branch prerelease pipeline
**Branch**: `ci/branch-release`

### Summary

Added .github/workflows/branch-release.yml: pushing any non-main branch builds both Windows archs through the existing gates and publishes one rolling prerelease per branch (tag branch-build/<slug>-<refhash>, version 1.0.0-<slug>-<refhash>.<sha7>, --prerelease --latest=false, assets swapped and tag re-pointed on each push, stale assets pruned, 404-tolerated cleanup on branch delete). build.yml comments + README CI section updated; new spec/guides/ci-pipeline.md. Live-verified on origin against the real runner (gates green, Build & Package not re-triggered, /releases/latest still 404).

### Git Commits

| Hash | Message |
|------|---------|
| `024134a` | Publish a rolling prerelease for every branch push |
| `e21929e` | chore: re-run branch lane to exercise the rolling path |
| `f785b6a` | Prune the previous commit's assets from a branch release |

### Status

[OK] **Completed**


## Session 2: Floating desktop speed widget
<!-- trellis-session: v=2 fp=5340a12d7fbe64bc -->

**Date**: 2026-10-31
**Task**: Floating desktop speed widget
**Branch**: `tray/floating-speed-widget`

### Summary

Added optional always-on-top floating speed chip: persisted off-by-default setting, drag position saved in settings.json, click opens the panel, tray-menu and Preferences toggles, L10n, README, specs and tests.

### Git Commits

| Hash | Message |
|------|---------|
| `7410e99` | Add an optional floating desktop speed widget |

### Status

[OK] **Completed**

## Session 3: Release-chain hardening
<!-- trellis-session: v=2 fp=releasechainhardening01 -->

**Date**: 2026-11-01
**Task**: Release-chain hardening (findings 8, 9, 10 of the 2026-11 security review)
**Branch**: `security/release-chain-hardening`

### Summary

Closed the last three review findings. Finding 10: a bounded, redacting, event-only
`ptmonitor.log` (`Core/Diagnostics/Log.cs`) with startup, crash, poll-transition,
secret-store, TLS-acceptance and settings-failure call sites — the crash handler
is what makes the next tray-class incident diagnosable. Finding 9: both lanes
split into `gate` / `package`|`publish` / `release`, workflow-level
`contents: read`, `pull_request` added to `build.yml` and stopping after `gate`.
Finding 8: every `uses:` pinned to a commit SHA, `SHA256SUMS.txt` from both
`build.ps1` and the release job, build-provenance attestation before publishing.
Specs (`ci-pipeline.md`, `logging-guidelines.md`) and README updated; parent PRD
findings 8-10 ticked.

The log is **always on** by decision (2026-11-01): a log enabled after the fact
cannot diagnose a crash that already happened.

### Git Commits

| Hash | Message |
|------|---------|
| `9bc5297` | Add a bounded, redacting log sink with a transition-only poll rule |
| `75988ec` | Log startup, crashes, poll transitions and store failures |
| `547a505` | Document what the log holds and never holds |
| `c54a9b1` | Pin actions, scope write access to the publishing job, attest every zip |
| `b7cc50a` | Document how to verify a download and how the lanes are gated |
| `0b32f8b` | Make the log checks prove the loop and the sink, not themselves |

### Verification

`dotnet build -c Release` clean, `--selftest` 298 checks, `dotnet test` 85 tests.
Workflows: YAML parse + all 12 `pwsh` blocks parsed with the PowerShell AST +
checksum producer/verifier dry-run under a local `pwsh`; `actionlint` unavailable
here. Two new checks were mutation-tested (removing the poll memo, no-oping
`Shutdown`) and fail as they should.

### Status

[OK] **Completed** — awaiting PR / archive.

### Outcome (2026-11-02)

Merged as `2d04a89` (PR #10). Three things that were unverifiable on the dev box
were confirmed by the first push:

- PR run `41300208646`: `gate` success, `Package, checksum` **skipped**,
  `Attest, publish release` **skipped** — the pull_request gate stops where it
  should.
- Branch run `41300195430`: rolling prerelease published with both zips **and**
  `SHA256SUMS.txt` (so the stale-asset sweep keeps the sums); `sha256sum -c` OK.
- Attestation is real SLSA v1 provenance (`predicateType
  https://slsa.dev/provenance/v1`, both zips as subjects, workflow
  `branch-release.yml` on the pushed ref), checked via the attestations API
  because the local `gh` 2.46 predates `gh attestation verify`.

Merged tree green: build clean, 298 selftest checks, 85 xUnit tests. Main build
run `41300665595` produced the first artifact that carries **both** the tray fix
and the log sink — the deploy candidate for `11-02-tray-fix-deploy-verify`.

Left open deliberately: the parent's "a user with a working HTTPS setup sees no
change in behaviour" needs a human on Windows, and
`11-01-credential-tls-hardening`'s PRD criteria are the same class of manual UI
check, so neither was ticked by inspection.


## Session 4: Floating chip: show the aggregate rates over the speed graph
<!-- trellis-session: v=2 fp=5cd35b940c0df98d -->

**Date**: 2026-11-02
**Task**: Floating chip: show the aggregate rates over the speed graph
**Branch**: `main`

### Summary

The floating speed chip's graph mode now shows the aggregate down/up rates in the top-left corner, drawn on top of the plot. The label keeps the background transparent: a new runtime-drawn OutlinedText control paints a hard 1 px outline around the glyphs instead of an opaque plate, so the graph shows through behind the numbers. win-x64 artifacts were rebuilt and re-checksummed.

### Main Changes

- Floating chip graph mode pins the aggregate ↓/↑ rates to the top-left corner (FloatingSpeedWindow.axaml/.cs); ShowRates writes the same formatted string to both readouts
- New Views/OutlinedText.cs draws a hard outline around the glyphs (offset copies of the text, fill on top); replaces the first-pass opaque plate that hid the plot
- App.axaml gains the graphRate / graphArrow style classes; README and .trellis/spec/frontend/component-guidelines.md document the outlined-text rule

### Git Commits

| Hash | Message |
|------|---------|
| `88ab12e` | Show the aggregate rates in the corner of the floating speed graph |
| `9d7a41c` | Document the outlined-text rule for labels over live data |

### Testing

- [OK] dotnet build -c Release: 0 errors, 0 warnings
- [OK] CLI --selftest: 298 checks passed; dotnet test: 85/85 passed
- [OK] Headless Avalonia+Skia render of the real chip checked at 1x and 6x: graph mode legible over both series, numbers mode pixel-identical to before
- [OK] win-x64 self-contained publish rebuilt; artifacts/PTMonitor-1.0.0-win-x64.zip re-zipped, sha256sum -c SHA256SUMS.txt OK

### Status

[OK] **Completed**

### Next Steps

- Windows confirmation of the corner readout (needs the user on the target machine)
- The 4 open Trellis tasks (tray-icon-crash, tray-fix-deploy-verify, credential-tls-hardening, security-hardening) stay open — tray-icon-crash still needs the on-Windows event-1026 confirmation


## Session 5: Housekeeping: retire the merged security/release-chain-hardening branch
<!-- trellis-session: v=2 fp=efcf953a6abc6066 -->

**Date**: 2026-11-02
**Task**: Housekeeping: retire the merged security/release-chain-hardening branch
**Branch**: `main`

### Summary

Deleted the remote branch security/release-chain-hardening. Its work was already merged (PR #10) and its task archived, so the branch only kept alive a rolling branch prerelease built 14 commits behind main. The delete event fired the Branch prerelease cleanup job, which removed that prerelease and its tag. The release/1.0.0 branch and its Latest release were deliberately left in place: they are the entry the README points users to, so they must not be dropped before a stable v* release exists.

### Main Changes

- Deleted remote branch security/release-chain-hardening (was fully merged into main; PR #10)
- Branch prerelease cleanup job removed PTMonitor 1.0.0-security-release-chain-hardening-8e21b5.a057bcc and tag branch-build/security-release-chain-hardening-8e21b5
- Kept release/1.0.0 and its Latest, non-prerelease release — it is the download the README documents

### Git Commits

(No commits - planning session)

### Testing

- [OK] Verified after the delete: remote branches are main + release/1.0.0; the only remaining release is latest=true, prerelease=false; the stale tag is gone

### Status

[OK] **Completed**

### Next Steps

- Cut a stable v1.0.0 tag from main (build.yml's v* lane) so the Releases page has a real stable entry, then release/1.0.0 can go too


## Session 6: Close the security review and the tray deploy task
<!-- trellis-session: v=2 fp=7af89efdba95c608 -->

**Date**: 2026-11-02
**Task**: Close the security review and the tray deploy task
**Branch**: `main`

### Summary

Archived the tray deploy task by user decision (no soak; the fix is merged and has not recurred) and closed the credential/TLS task and its parent after re-verifying all twelve acceptance criteria on merged main (0 errors, 642 selftest checks, 121 xUnit tests). Recorded what each closure claims and what it does not: rendered-Windows observations, DPAPI, and per-entry corrupt-secret recovery remain open and are documented as such rather than ticked.

### Main Changes

- Archived 11-02-tray-fix-deploy-verify with its soak criteria explicitly not run and a recurrence triage path in the PRD
- Re-verified credential/TLS ACs on main; added a closure table to verification-followup.md and ticked the PRD
- Finalized the parent finding table: 7 closed, finding 4 partial, standing follow-ups named

### Git Commits

(No commits - planning session)

### Testing

- [OK] Release rebuild 0 errors; selftest 642 checks; xUnit 121 passed; parent archived with --skip-branch-validation because it never had its own branch

### Status

[OK] **Completed**

### Next Steps

- Windows observations (DPAPI, trust, rendered UI) and a design for per-entry secret recovery remain the only substantive gaps


## Session 7: Monochrome, theme-aware tray icon
<!-- trellis-session: v=2 fp=tray-icon-monochrome -->

**Date**: 2026-11-02
**Task**: 11-02-tray-icon-monochrome
**Branch**: `main`

### Summary

Replaced the tray artwork (light tile + ring + arrow pair + progress pill) with one flat monochrome glyph — a bold down arrow over a short bar — that follows the OS light/dark taskbar. The floating chip already shows the rates, so the tray is an identity mark, not a meter. Idle = 55 % opacity, active = full, error = red; one geometry for all three.

The icon is now packaged as a real multi-size `.ico` (16/20/24/32/48) built in memory, because Avalonia's `WindowIcon(Bitmap)` path on Win32 ends in `CreateIconIndirect` with `fIcon = FALSE`: a 1-bit AND mask, no alpha, hard edges, and one bitmap downscaled to the taskbar size. The theme comes from `Application.ActualThemeVariant` (the app requests the default variant, so it is the OS setting) and the icon is redrawn on `ActualThemeVariantChanged`, which Avalonia's Win32 backend raises on `WM_SETTINGCHANGE`/"ImmersiveColorSet".

### Main Changes

- `Services/TrayIconFactory.cs` rewritten: glyph drawing, straight-alpha BGRA extraction, per-(state, theme) icon cache
- `Services/IcoEncoder.cs` added: `.ico` container writer (32-bit BGRA entries, `biHeight = 2×h`, empty AND mask)
- `Services/AppServices.cs`: icon keyed by (state, theme), theme-change subscription, warn-level logging on failure
- Specs + README + `scripts/make-app-icon.py` docstring: the tray and the exe icon are different drawings now
- `scripts/make-app-icon.py` still reproduces `app.ico` byte-for-byte (verified with `cmp`)

### Git Commits

- (see git log for this session's commit)

### Testing

- [OK] Release build 0 errors; selftest 642 checks; xUnit 121 passed; `scripts/build.sh` published the win-x64 package
- [OK] Tray glyph rendered through the shipped code path on this Linux box (Skia headless) and previewed as PNG for all six state/theme pairs
- [OK] Generated `.ico` parsed back by an independent parser: 5 entries, correct offsets, `biHeight = 2×h`, empty AND mask, straight alpha
- [ ] Windows only: the icon on a real light and dark taskbar (the tray cannot run here)

### Status

[OK] **Completed** (Windows spot-check left to the user)

### Next Steps

- The ICO layout has no CI coverage — the App layer still has no automated gate; `InternalsVisibleTo` + a test project would close that


## Session 8: Square frame around the tray glyph
<!-- trellis-session: v=2 fp=tray-icon-square-frame -->

**Date**: 2026-11-02
**Task**: none (small tweak on top of 11-02-tray-icon-monochrome)
**Branch**: `main`

### Summary

User liked the monochrome glyph but wanted the tray icon to read as a square. Added a hairline square frame around the arrow: plain square (sharp corners — a 0.4 px radius at 16 px only produced ragged corner pixels), 1 px at 16 px and at most 2 px, one pixel inside the icon edge so it is not clipped. The glyph was pulled in vertically (shaft top 0.12 → 0.19, bar 0.77/0.89 → 0.71/0.82) to keep a clear gap inside the frame.

Tried and rejected on the way: a rounded-square frame (corner AA made a stair pattern at 16 px) and a fractional margin (the 1 px stroke landed between pixels and washed out).

No Trellis task for this one — it is one drawing routine plus three doc lines.

### Main Changes

- `Services/TrayIconFactory.cs`: square frame (pen, hairline) + the shrunk glyph inside it
- `lifecycle-and-tray.md` / `README.md`: artwork description now mentions the frame

### Git Commits

- (see git log for this session's commit)

### Testing

- [OK] Release build 0 errors; selftest 642 checks; xUnit 121 passed
- [OK] Glyph rendered through the shipped code path (Skia headless) and previewed at 16/32 px for all six state/theme pairs; generated `.ico` parsed back, 5 entries intact
- [ ] Windows only: the framed glyph on a real taskbar

### Status

[OK] **Completed**

### Next Steps

- Windows spot-check of the framed icon (the user has the win-x64 build)


## Session 9: Rounded corners on the tray frame
<!-- trellis-session: v=2 fp=tray-icon-rounded-frame -->

**Date**: 2026-11-02
**Task**: none (tweak on top of the tray-icon work)
**Branch**: `main`

### Summary

User asked for "very minor round corner" on the tray border, with a screenshot of a rounded-square badge as the shape reference. The frame now has a 10 % corner radius.

The bug behind the earlier ragged corners: the path radius was computed as `radius - inset`, but `inset` is the *margin* (position), not the stroke — the stroke is centred on the path, so it adds `stroke/2` outside and takes `stroke/2` inside. The correct path radius is `wanted visible radius - stroke/2`. With that fixed, a 2 px radius at 16 px renders as a clean rounded corner instead of a sub-pixel staircase.

Radius chosen by eye: 0.10 of the side (1.6 px visible at 16 px, 3.2 px at 32 px). 0.14 was visibly rounder at 32 px but chopped at 16 px.

Still an outline, not the filled white plate in the screenshot — the user said "border"; the filled plate was offered as a one-line change.

### Main Changes

- `Services/TrayIconFactory.cs`: frame radius 0.10, path radius corrected to `radius - stroke/2`
- `lifecycle-and-tray.md` / `README.md`: artwork text mentions the rounded frame

### Git Commits

- (see git log for this session's commit)

### Testing

- [OK] Release build 0 errors; selftest 642 checks; xUnit 121 passed
- [OK] Rendered through the shipped code path; 16 px and 32 px previews for all six state/theme pairs; `.ico` parsed back, 5 entries intact
- [ ] Windows only: the rounded frame on a real taskbar

### Status

[OK] **Completed**


## Session 10: Code signing — investigated, declined
<!-- trellis-session: v=2 fp=signing-probe -->

**Date**: 2026-11-02
**Task**: none (dropped by user decision)
**Branch**: `main`

### Summary

User asked how to remove the "Publisher: Unknown publisher" warning on the
unsigned release. Probed the whole path on this box and then dropped it after
the cost became clear. Nothing in the repo changed.

What the probe established (no residue left behind — the probe cert was
removed from `Cert:\CurrentUser\My`, the Root store was never written to, the
temp files are gone):

- `New-SelfSignedCertificate -Type CodeSigningCert` + `Set-AuthenticodeSignature`
  works from WSL through `powershell.exe` interop; the exe signs and gets a
  DigiCert timestamp. No Windows SDK / `signtool.exe` needed.
- `osslsigncode` is not installed here and `sudo` needs a password, so the
  Linux build script cannot sign on its own.
- A self-signed cert only removes the warning on machines that import its
  public part into the trusted-root store. Third parties still see "Unknown
  publisher", so it does not solve the published-download case.

Real fix for everyone: a CA-issued code-signing certificate (OV/EV), Azure
Trusted Signing, or SignPath Foundation for an accepted open-source project.
EV gets SmartScreen reputation immediately; OV takes days/weeks.

The README already states the unsigned situation accurately (the SmartScreen
troubleshooting entry and the "Signing needs a code-signing certificate"
note), so no doc change was needed.

### Main Changes

- None. The idea was dropped before it was implemented.

### Git Commits

- (none)

### Testing

- [OK] Probe only: signing + timestamping verified via `Get-AuthenticodeSignature`; trust-anchor step deliberately not left installed

### Status

[OK] **Dropped by user decision**

### Next Steps

- If the warning ever matters for distribution: buy an OV/EV cert or use Azure
  Trusted Signing, then wire optional signing into `scripts/build.sh` and
  `scripts/build.ps1` (one env var for the PFX) — the scripts do not exist yet.

## Session 11: Floating widget right-click menu and system cursor
<!-- trellis-session: v=2 fp=widget-context-menu -->

**Date**: 2026-11-03
**Task**: `11-03-floating-widget-context-menu` (archived)
**Branch**: `main`

### Summary

Two tweaks to the floating speed chip, both asked for together.

The hand-drawn open-hand cursor is gone; the chip uses the system hand cursor
(`new Cursor(StandardCursorType.Hand)`). The drawn one existed because
Avalonia's `Hand` is a pointing finger rather than the macOS open hand — the
user wanted the system look, so `Services/CursorFactory.cs` was deleted
outright (its only consumer was the chip; a second cursor style nobody uses is
dead weight).

Right-clicking the chip now opens a context menu with three items: open the
first enabled server's web console in the default browser, hide the chip
(persisted, so the tray toggle is the way back), and quit the agent.

Details worth keeping:

- The menu is built in code-behind, not XAML. The first header names the
  server (`Open qBittorrent web UI`) and the item is disabled when nothing is
  configured — neither is expressible in XAML, so `Control.ContextMenu` +
  `ItemsSource` it is. The click handlers stay one-liners into `AppServices`.
- The URL is `ServerConfig.WebUiUri()` (qBittorrent at the root, Transmission
  under `/transmission/web/`, https when TLS is on) — the model already owned
  it; the tray/panel never re-derive it. Opening moved into
  `Services/UrlLauncher` so the panel row button and the menu share one path.
- A right-click cannot disturb the click/drag gesture: Avalonia's
  `Control.OnPointerReleased` raises `ContextRequested` only when the initial
  press was the right button, and the chip's `OnPointerPressed` returns early
  for anything but the left one.
- Two new `L10n` keys in both tables (`Hide floating speed window`,
  `Open web console`); the latter is the disabled-state header only.

Known Windows caveat, left documented rather than guessed at: the chip window
is `ShowActivated=False` and Avalonia hosts the menu in a non-activating
popup (`MA_NOACTIVATE`), so a click on *another application* may leave the
menu standing; clicking the chip dismisses it. If that grates, the fix is a
borderless window like `MainPanelWindow` (closes on `Deactivated`) instead of
a `ContextMenu`. Also noted: Avalonia 11.3.2 has no `TrayIcon` right-click
event at all (`Clicked` is left-button only, the Win32 backend shows the
native menu on right button), so "right-click the tray icon" was not an
option without a global hook.

### Main Changes

- `Views/FloatingSpeedWindow.axaml.cs`: system hand cursor, `BuildMenu()`,
  `RefreshMenu()` from `ApplyLocalization`
- `Services/AppServices.cs`: `OpenFirstServerWebConsole()`,
  `HideFloatingSpeedWindow()`
- `Services/UrlLauncher.cs` (new): one owner for "open in the default
  browser"; `MainPanelWindow.OnOpenWebUi` now calls it
- `Services/CursorFactory.cs` (deleted), `Services/L10n.cs` (2 keys)
- Specs (`lifecycle-and-tray`, `component-guidelines`, `directory-structure`)
  and `README.md` updated

### Git Commits

- `0d6ebcd` feat(widget): right-click menu and system hand cursor

### Testing

- [OK] `dotnet build -c Release` 0 errors
- [OK] `--selftest` 642 checks; `dotnet test` 121 passed
- [OK] `scripts/build.sh --invariant`: win-x64 publish + ZIP
- [ ] Windows only: menu opens, browser target, hide, quit, cursor shape

### Status

[OK] **Complete — Windows pass pending**

### Next Steps

- User to run the five-step manual pass in the archived PRD and report back;
  if the leave-open caveat bites, swap the `ContextMenu` for a borderless
  window.


## Session 12: Launch timeline and tray-icon-first startup
<!-- trellis-session: v=2 fp=f19504dab95515a8 -->

**Date**: 2026-11-06
**Task**: Launch timeline and tray-icon-first startup
**Branch**: `main`

### Summary

The agent now measures its own launch and puts the tray icon in front of the startup path; the packaging decision waits for the Windows timeline.

### Main Changes

- New Core/Diagnostics/StartupTrace: one launch timeline per start, written as a single 'startup: timeline' line through the existing log sink
- AppServices constructor reordered so the tray icon is set before the secret store, the monitor and the optional windows; the floating chip and first-run Preferences are posted at Background priority
- Phase order pinned to the constructor order in the selftest and the xUnit mirror, so the offsets cannot go non-monotonic
- PTMonitor.Core grants InternalsVisibleTo to the test project, keeping the StartupTrace clock seam internal
- Specs (lifecycle-and-tray, logging-guidelines, frontend index) and README updated; both build scripts record why ReadyToRun is not enabled

### Git Commits

| Hash | Message |
|------|---------|
| `f7d1390` | feat(startup): launch timeline and tray-icon-first startup |
| `845b480` | chore(task): plan the startup and runtime performance work |

### Testing

- [OK] [OK] dotnet build -c Release: 0 errors
- [OK] [OK] --selftest: 648 checks passed
- [OK] [OK] dotnet test: 126 passed
- [OK] [OK] scripts/build.sh --invariant: win-x64 ZIP + CLI produced
- [OK] [ ] Windows only: launch timeline, tray icon, floating chip, first-run preferences, 30 s clean log

### Status

[OK] **Completed**

### Next Steps

- User runs the published build on the target machine and pastes the 'startup: timeline' line; the packaging decision (folder layout / ReadyToRun / no change) follows from it
- Windows manual pass: total <= 1500 ms, the chip appears, first-run preferences opens, the process survives 30 s
- Child task 11-05-runtime-polling-performance stays in planning: the hidden-panel rebuild and the per-poll payload


## Session 13: Close the launch-path performance child
<!-- trellis-session: v=2 fp=6c1f4a0d92be7711 -->

**Date**: 2026-11-06
**Task**: Launch path and packaging performance (11-05-launch-path-performance)
**Branch**: `main`

### Summary

The Windows timeline came back from the target machine, so the packaging
decision could be made on evidence instead of habit. Result: no packaging
change, and the child task is finished.

### Main Changes

- Recorded the measured timeline in the task artifacts, the README and the
  lifecycle spec: tray icon visible at 418 ms, total 575 ms, against the
  1500 ms target; entry->log-ready is 12 ms, so the self-extracting bundle is
  not the bottleneck
- Packaging decision: no change. Both build scripts keep their flags and now
  carry the measurement as their reason; ReadyToRun stays out until a Windows
  A/B of the same build shows a gain (D3)
- The README's timeline example is the real Windows line, and the troubleshooting
  row now names the healthy offsets

### Git Commits

| Hash | Message |
|------|---------|
| `dd62b3d` | docs(startup): record the Windows launch timeline and the packaging decision |

### Testing

- [OK] dotnet build -c Release: 0 errors
- [OK] --selftest: 648 checks passed
- [OK] dotnet test: 126 passed
- [OK] scripts/build.sh --invariant: win-x64 ZIP + CLI produced
- [OK] Windows: total 575 ms <= 1500 ms, tray icon at 418 ms, clean log

### Status

[OK] **Completed**

### Next Steps

- Child `11-05-runtime-polling-performance` is still in planning: the
  hidden-panel rebuild and the per-poll payload (needs design.md + implement.md)
- Optional: a Windows ReadyToRun A/B, only if the avalonia phase ever matters


## Session 14: The idle agent stops working for nobody
<!-- trellis-session: v=2 fp=9d3f1c7ab42e056 -->

**Date**: 2026-11-06
**Task**: Steady-state runtime performance (11-05-runtime-polling-performance)
**Branch**: `main`

### Summary

The second child of the performance parent: the running agent no longer
rebuilds a panel nobody can see, and no longer pulls the whole torrent list
from every server on every poll. Two review rounds found four real defects;
all of them are fixed.

### Main Changes

- Core/Model/PollDetail: `Summary` (rates, counts, connection state, free
  space) and `Full` (with the torrent list). `ITorrentClient` takes the scope.
- MonitorStore: one volatile demand flag, a pure `DetailFor` rule, an
  `AsyncSignal` that interrupts a normal poll interval, and the last full list
  per server, so a summary poll never empties the panel.
- qBittorrent: the summary scope sends the last `rid` (the server reports only
  the changes), the full scope asks for everything (the panel shows exactly
  what the server has), the merge is a pure function with a superset-based
  self-heal, and a full re-sync every 5 minutes repairs any drift.
- Transmission: the summary poll is one `session-stats` request instead of two.
- App: `PanelViewModel.IsPanelVisible` gates both rebuild triggers and drives
  the demand; the panel's freshness timer stops with the panel.
- scripts/fake-qbittorrent.mjs speaks the rid protocol, so the probe against
  the stub proves the delta path end to end.

### Review findings and fixes

- **H1** — the demand wake-up interrupted the 600 s auth backoff: opening and
  closing the panel five times would have earned a qBittorrent IP ban. The
  backoff wait is never interruptible now, and both suites assert that a
  failing server is polled once while the demand toggles.
- **H2** — the panel's rows came from the merged delta, so a server that does
  not report progress-only changes would have frozen the progress bars. The
  full scope asks for everything; the delta is used for the summary scope only.
- **F1** — the sticky signal delivered the wake-up twice (two whole-library
  downloads per panel open). One signal now ends exactly one wait.
- **F2** — the backoff was detected by magnitude; it is decided by the failure
  kind now (`MonitorStore.HasBackoff`).
- **F3** — the self-heal rule could lose a torrent; it is a superset test now.
- Plus the smaller ones: the tracker fallback is merged, Transmission's scope
  rule is a pure function, the probe prints the full snapshot and the summary,
  and the wording in the PRD, the specs and the README no longer overclaims.

### Git Commits

| Hash | Message |
|------|---------|
| `f0b5709` | feat(runtime): poll only what is on screen and rebuild only a visible panel |

### Testing

- [OK] dotnet build -c Release: 0 errors (one pre-existing CS8602)
- [OK] --selftest: 692 checks passed (44 new)
- [OK] dotnet test: 150 passed (24 new)
- [OK] scripts/build.sh --invariant: win-x64 ZIP + CLI produced
- [OK] qBittorrent stub: rid=0 full, then the delta; the merged summary
      (`totalTorrents` 1 of 2, the delta's speed); WebAPI 4.6 and 5.2 alike
- [ ] Windows only: the tooltip and chip keep updating, the panel opens with
      data and stays live, the tray icon still switches Idle/Active, qBittorrent's
      list matches the server's, the log stays clean for 30 s

### Status

[OK] **Completed — Windows pass pending**

### Next Steps

- User runs the published build on the target machine and reports the manual
  pass (including: open and close the panel five times, the scope must fall
  back to the summary every time)
- Parent task 11-05-performance-startup-runtime is then 2/2 and can be archived


## Session 15: Solid tray glyph without a frame
<!-- trellis-session: v=2 fp=e45f413e99359d3b -->

**Date**: 2026-11-06
**Task**: Solid tray glyph without a frame
**Branch**: `feat/tray-icon-solid-glyph`

### Summary

The user runs Windows in dark mode and asked to invert the system-tray icon (bottom-right corner): drop the outline, make the whole piece bright. Read it as 'no frame, one solid fill' -- a literal colour swap would vanish on a dark taskbar. Removed the hairline rounded-square frame from TrayIconFactory, scaled the arrow-over-bar glyph 1.25x about the icon centre so it fills the tile, and updated the tray spec and README. Verified on this box: build clean, 692 selftest checks, 150 tests, in-memory .ico still parses into five straight-alpha entries, and the glyph was rendered to preview PNGs (artifacts/tray-preview/) and reviewed. The Windows manual check (solid bright arrow-over-bar on a dark taskbar) is still open.

### Main Changes

- src/PTMonitor.App/Services/TrayIconFactory.cs: frame constants and draw call removed; GlyphScale = 1.25 scales the arrow and bar about the icon centre
- .trellis/spec/frontend/lifecycle-and-tray.md: artwork section no longer describes a framed badge
- README.md: the tray-icon feature bullet

### Git Commits

| Hash | Message |
|------|---------|
| `5ecd251` | feat(tray): draw the tray glyph as one solid piece without a frame |

### Testing

- [OK] dotnet build -c Release: 0 errors
- [OK] --selftest: 692 checks passed
- [OK] dotnet test: 150 passed
- [OK] scripts/build.sh --invariant: win-x64 ZIP + CLI produced
- [OK] rendered preview PNGs at 16/20/24/32/48 px for both themes and all three states

### Status

[OK] **Completed**

### Next Steps

- Windows manual check: the tray shows one solid bright arrow-over-bar on a dark taskbar, no leftover outline


## Session 16: One fixed tray mark in every theme
<!-- trellis-session: v=2 fp=835a0453df80d534 -->

**Date**: 2026-11-06
**Task**: One fixed tray mark in every theme
**Branch**: `feat/tray-icon-solid-glyph`

### Summary

Follow-up to 11-06: the user picked the 'bright tile, dark arrow' artwork and asked for it regardless of the theme colour. The theme dimension is gone from the whole tray factory: Icon(TrayState), BuildIco(TrayState), Render(size, TrayState), a cache keyed by state alone, and a fixed palette (tile #FFFFFF, ink #1F1F1F, error #C42B1C). AppServices lost _trayDark and CurrentTheme(), the ActualThemeVariantChanged subscription (an OS theme switch cannot change the icon any more) and UpdateTray now assigns the icon on a state change only -- the 11-01 crash-prevention rule still holds. Stale 'the tray follows the taskbar' claims fixed in the spec, the README (feature bullet and the macOS-porting table) and the make-app-icon.py docstring. Two trellis-check passes: the second found only stale docs, all fixed.

### Main Changes

- src/PTMonitor.App/Services/TrayIconFactory.cs: no theme parameter; fixed palette; the tile is always drawn
- src/PTMonitor.App/Services/AppServices.cs: _trayDark/CurrentTheme removed, theme hook removed, assign on a state change
- .trellis/spec/frontend/lifecycle-and-tray.md: artwork, colour table, theme section, constructor order
- README.md and scripts/make-app-icon.py: the artwork description

### Git Commits

| Hash | Message |
|------|---------|
| `ab6e561` | feat(tray): use the same tray icon in every theme |

### Testing

- [OK] dotnet build -c Release: 0 errors
- [OK] --selftest: 692 checks passed
- [OK] dotnet test: 150 passed
- [OK] in-memory .ico: five 32-bit entries (16/20/24/32/48), straight alpha
- [OK] rendered previews identical in both themes (artifacts/tray-preview/final/)

### Status

[OK] **Completed**

### Next Steps

- Windows manual check: the bright tile with the dark arrow reads on both a light and a dark taskbar


## Session 17: Stale build handed over as the artwork fix
<!-- trellis-session: v=2 fp=944ae1557109034d -->

**Date**: 2026-11-06
**Task**: Stale build handed over as the artwork fix
**Branch**: `feat/tray-icon-solid-glyph`

### Summary

The user reported still seeing the old tray icon. The code was right, the handover was not: publish/win-x64/PTMonitor.exe had been published 10 seconds BEFORE the commit that changed the artwork (14:48:12 vs commit 5ecd251 at 14:48:22), so the exe they copied still drew the framed icon. Republished with scripts/build.sh (new sha256 31158d24..., DrawTile present and GlyphDark gone in the bundle), gave the checksum, the quit-first step and the hash check. Codified the lesson: frontend/quality-guidelines.md now has a 'Handing a build over' section (stat the exe against the last commit, quote the sha256), and the README troubleshooting table has a 'tray icon still looks like the old one' row.

### Main Changes

- .trellis/spec/frontend/quality-guidelines.md: new 'Handing a build over' section with the timestamp/sha256 checks
- README.md: troubleshooting row for a stale exe or a cached tray entry
- .trellis/tasks/archive/2026-11/11-06-tray-icon-solid-glyph/task.json: incident note

### Git Commits

(No commits - planning session)

### Testing

- [OK] scripts/build.sh: full pipeline, win-x64 ZIP republished at 16:36
- [OK] dotnet build -c Release: 0 errors
- [OK] --selftest: 692 checks passed
- [OK] dotnet test: 150 passed

### Status

[OK] **Completed**

### Next Steps

- User confirms the icon on Windows with the new exe (sha256 31158d24...)


## Session 18: Desktop widget (desktop level, lockable)
<!-- trellis-session: v=2 fp=7c1d2f0a9b4e6d33 -->

**Date**: 2026-11-07
**Task**: Desktop widget (desktop-level, lockable)
**Branch**: `feat/desktop-widget`

### Summary

The user asked for a copy of the Nexus desktop widget: a card that lives on the
desktop and can be locked in a position. Nexus (`hello-nexus/nexus`, AGPL-3.0)
was only read for the *behaviour* — a floating card, draggable, with a
per-widget `Locked` flag that blocks drag/edit/resize. No code was copied.

Scope decided with the user: simple content first (aggregate rates + the
existing rolling graph + a summary line), a **true desktop-level** widget
(behind the app windows, not always-on-top), and a lock that freezes the
**position only**.

What was built: `Services/DesktopLayer.cs` (Win32 desktop layer) and
`Views/DesktopWidgetWindow` (the card), plus the settings, the tray/Preferences
toggles and the localization. The card is made a **child of the shell's desktop
host** (`Progman`/`WorkerW`) — behind every app window, survives Win+D, cannot
steal the focus. Without a host it degrades to an always-on-top window and the
log says why; a 2 s retry re-attaches after a shell restart.

Two constraints shaped the design: a **layered (transparent) window cannot be a
child of the desktop host**, so the card stays opaque and its rounded corners
are cut out with `SetWindowRgn`; and a **child window is not moved by the
window manager**, so the drag follows the cursor itself
(`GetCursorPos` + `SetWindowPos` in host coordinates).

### Main Changes

- `src/PTMonitor.App/Services/DesktopLayer.cs` (new): the desktop host
  discovery, the `SetParent` attach with the `WS_POPUP`↔`WS_CHILD` style swap,
  the verified host-relative placement, `WS_EX_NOACTIVATE`, `SetWindowRgn`.
- `src/PTMonitor.App/Views/DesktopWidgetWindow.axaml(.cs)` (new): the card.
- `src/PTMonitor.App/Services/AppServices.cs`: the widget wiring, the shared
  1 s sample ticker (`SyncTicker`), the 2 s attach-retry timer.
- `src/PTMonitor.Core/Store/SettingsStore.cs`: `ShowDesktopWidget`,
  `DesktopWidgetPositionX/Y`, `DesktopWidgetLocked`.
- Specs: a "Desktop widget" section in `frontend/lifecycle-and-tray.md`, the
  settings table in `frontend/state-management.md`, the DTO and the mutator
  list in `backend/persistence-guidelines.md`.

### Git Commits

| Hash | Message |
|------|---------|
| `b7f5c09` | feat(widget): a desktop-level card with a position lock |

### Testing

- [OK] dotnet build -c Debug: 0 errors
- [OK] --selftest: 698 checks passed
- [OK] dotnet test: 151 passed
- [OK] app run with the widget enabled: the card is created and placed, the
      fallback is logged once, the process survives with a clean log
- [ ] **Windows only** (cannot run on this box): behind the app windows,
      Win+D, the focus behaviour, the menu over the desktop layer, the drag
      of a child window, the lock across a restart — the checklist is in
      `.trellis/tasks/11-07-desktop-widget/notes.md`

### Status

[OK] **Implemented, PR #17 open**

### Next Steps

- Check the widget on Windows, then merge PR #17 and archive the task
- Follow-up (not in this task): per-server rows / top torrents inside the card

### Update (same day, later): transparent and square

The user looked at the first build and asked for two changes: **no rounded
corner** and **transparent**. That reversed the desktop-layer mechanism.

The first version re-parented the card into the shell's desktop host
(`SetParent` to `Progman`/`WorkerW`). That worked, but it forced the card to
stay **opaque**: Avalonia renders a transparent window without a redirection
bitmap (DirectComposition) or with DWM blur-behind, and neither survives being
made a child of the shell's desktop — the card would simply not be drawn.

So the card is now pinned in the **z-order** directly above the desktop window
(`SetWindowPos(hwnd, host, …)`), below every application window, with
`WS_EX_NOACTIVATE` so a click never activates it. The pin is re-asserted on
every sample, so a shell restart heals itself. The card is an ordinary
top-level window again: the drag is the chip's `BeginMoveDrag`, and Win+D is
undone by restoring it from `WindowState.Minimized`.

The card has no card: no fill, no border, square corners, and every run is
drawn with `OutlinedText` (a hard outline around the glyphs) so it stays
readable on the wallpaper.

**Verified on the X11 display**: the card was captured with `xwd`, converted
to a PNG and inspected — 91 % of its pixels are fully transparent (1 % opaque:
the glyph cores; 8 % partial: the outlines and the anti-aliased graph).

The lesson worth keeping: *a transparent Avalonia window and a desktop-host
child window are mutually exclusive on Windows.* Choose one.

### Update 2 (same day): a soft mask behind the readout

Fully transparent was too bare — the text sat straight on the wallpaper. The
card now has a **mask**: 50 % black per-pixel alpha, no border, square
corners, so the wallpaper still reads through it the way a Nexus glass plate
does. The hard outlines stayed (and got thinner) for bright wallpapers,
where the mask alone is not enough contrast.

Verified on the X11 display again: the card was captured with `xwd` and
composited over a light and over a dark wallpaper — the mask reads through
on both and the text stays legible.

The mask is the *window background* (`Background="#80000000"`), not a plate
inside it: with `TransparencyLevelHint="Transparent"` the window is per-pixel
alpha, so the background is composited over the desktop. A plate inside the
window would have needed rounded corners to look right, and the user does not
want those.

### Close-out (2026-11-08): merged and archived

PR #17 merged into `main` as `1997d24`. The task stayed `in_progress` only
because the checkboxes were never flipped — the code had already shipped two
commits earlier.

Re-verified before archiving (Linux box, `$HOME/.dotnet`):

- [OK] `dotnet build PTMonitor.sln -c Release` — 0 errors
- [OK] `dotnet test PTMonitor.sln` — 151 passed
- [OK] `PTMonitor.Cli --selftest` — 698 checks passed
- [OK] every `L10n.S` key the widget uses exists in `Zh` (23 keys + `WidgetSummary`)

Two pieces of prose had gone stale during the transparent → mask rework, and
both were fixed here (the code was right, the text was not):

- `README.md` *Known limitations* still described the **reverted** `SetParent`
  design ("the widget inherits the desktop host's DPI context"). Replaced with
  the real current limitation (placement is not multi-monitor aware; a saved
  position off every screen docks bottom-right on the primary).
- `DesktopWidgetWindow.axaml` said "40 % black plate" for a `#80000000` mask —
  now "50 % black mask", and no longer calls it a plate.

`.trellis/spec/frontend/lifecycle-and-tray.md` carries the DPI bullet now, plus
"do not reintroduce `SetParent` to fix placement".

**Windows-only items are deferred, not verified**: behind app windows, Win+D,
`WS_EX_NOACTIVATE` focus, a hand drag, a shell restart, a mixed-DPI monitor.
They are listed in `prd.md` → *Deferred — Windows-only verification* and
`notes.md`, so they are not quietly forgotten.

### Status

[OK] **Merged (PR #17) and archived.** Windows-only behaviour remains
unverified by design of the environment, recorded as a deferred checklist.


## Session 19: Close-out sweep — windows-installer and desktop-widget-hover-header
<!-- trellis-session: v=2 fp=39c42f3548d0bfc8 -->

**Date**: 2026-11-10
**Task**: Housekeeping: close the two stale `in_progress` tasks
**Branch**: `main`

### Summary

Two tasks were still marked `in_progress` even though their PRs had already
been merged. `11-10-desktop-widget-hover-header` shipped as **PR #19** (merge
commit `07b1f48`, merged 2026-11-09) and `11-08-windows-installer` as **PR #18**
(merge commit `d67854b`, merged 2026-11-08); both `task.json` files still had
`completedAt`, `commit` and `pr_url` set to `null`. This entry bits them
straight: set `status: completed`, the real `completedAt`/`commit`/`pr_url`, and
`git mv`-ed both task directories into `.trellis/tasks/archive/2026-11/`.

No source or spec bytes changed — this is task-metadata and journal only.

### Main Changes

- `11-10-desktop-widget-hover-header`: completed 2026-11-09, commit `07b1f48`,
  PR #19; archived to `archive/2026-11/`.
- `11-08-windows-installer`: completed 2026-11-08, commit `d67854b`, PR #18;
  archived to `archive/2026-11/`.
- `status` set to `completed` (the repo-wide convention — 19 prior tasks use it,
  not `done`) rather than a literal `done`.

### Git Commits

| Hash | Message |
|------|---------|
| _this_ | chore(task): close out and archive the merged widget and installer tasks |

### Testing

- [OK] metadata + journal only; no code touched, no build/test run
- [OK] `git status` clean of source changes; only the archived task dirs (renames) and the journal

### Status

[OK] **Completed** — zero active tasks remain; any new work starts from a fresh task.

### Next Steps

- Note the `createdAt` clock quirk on both tasks (dates trail the merge date by
  up to a day); left untouched, worth fixing at the source (task-creation clock
  vs. git/PR timestamps).
