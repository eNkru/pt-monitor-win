# Multi-arch CI build on main merge

## Problem

The current `.github/workflows/build.yml` only triggers on `v*` tag pushes and
manual `workflow_dispatch`. Merges to `main` produce no build at all, and the
pipeline publishes a win-x64 exe only — ARM64 Windows machines are not served.

## Requirements

1. **Merge to `main` triggers a build.** Every push to `main` (i.e. every merged
   PR) runs the full gate + packaging pipeline.
2. **Both architectures are built** from the same `windows-latest` runner:
   - `win-x64` → self-contained single-file `PTMonitor.exe`
   - `win-arm64` → self-contained single-file `PTMonitor.exe` (cross-compile)
3. **Gates run once per build**, not once per RID: `dotnet build` →
   `--selftest` (204 checks) → `dotnet test` (36 tests). The packaged-CLI
   selftest runs only for a RID that matches the runner architecture
   (`win-arm64` binaries cannot execute on the x64 runner — skip, do not fail).
4. `scripts/build.ps1` is parameterized (`-Rid`, default `win-x64`, plus a
   `-SkipGates` switch) so CI can reuse it for both RIDs and local users keep
   the existing single-command behavior.
5. **Artifacts**: merge to `main` and manual runs upload two workflow
   artifacts (`PTMonitor-win-x64`, `PTMonitor-win-arm64`), each containing the
   exe.
6. **Releases**: pushing a `v*` tag additionally creates a GitHub Release with
   **both** zips attached (`PTMonitor-<version>-win-x64.zip`,
   `PTMonitor-<version>-win-arm64.zip`). Release notes state that ARM64 Windows
   users should pick the arm64 zip.
7. `README.md` stays truthful: Requirements (run on x64 or ARM64), Quick start
   (choose the matching zip), CI section (new trigger + both lanes), and the
   "Windows x64 only" limitation + "ARM64 publish" roadmap item are removed.

## Non-goals

- No PR-triggered builds (main merges only).
- No Linux/macOS artifacts.
- No code signing, no installer.
- No change to app code, the selftest, or the stub server.

## Acceptance criteria

- [x] `build.yml` triggers on `push` to `main`, on `v*` tags, and manually.
- [x] A merge to `main` yields both exes as workflow artifacts, no Release.
- [x] A `v*` tag yields a Release with **both** zips attached.
- [x] `scripts/build.ps1` with no arguments still does the full x64 pipeline
      (existing local workflow unchanged).
- [x] `scripts/build.ps1 -Rid win-arm64 -SkipGates` publishes the arm64 exe
      and skips the packaged selftest with a clear message instead of failing.
- [x] `dotnet publish -r win-arm64` succeeds locally: single-file
      `publish/win-arm64/PTMonitor.exe`, PE machine `0xAA64` confirmed.
- [x] README matches the implemented behavior in all four places above.

Verification on this machine (WSL, no pwsh available): YAML parsed and
step/`if`/artifact structure asserted with PyYAML; backtick continuations
checked for trailing whitespace; arm64 publish executed with the exact flags
`build.ps1` uses; gates re-run green (`--selftest` 204 checks, 36 xUnit tests).
The full `build.ps1` flow itself will first run on the CI runner.

## Files touched

- `.github/workflows/build.yml`
- `scripts/build.ps1`
- `README.md`
