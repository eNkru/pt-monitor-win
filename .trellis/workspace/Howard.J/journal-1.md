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
