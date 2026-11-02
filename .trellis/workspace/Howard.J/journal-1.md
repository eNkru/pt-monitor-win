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
