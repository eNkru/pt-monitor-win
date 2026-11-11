# Implement — Windows installer (Inno Setup, dual mode)

Design: [`design.md`](./design.md) · Requirements: [`prd.md`](./prd.md)

Order matters: the `.iss` first (it defines the output name every later step
depends on), then the script that drives it, then CI, then docs. Each phase ends
with a validation gate; do not start the next phase on a red gate.

---

## Phase 0 — Baseline (no change)

- [x] `git status` clean apart from this task directory; note the current
      published asset set (2 zips + `SHA256SUMS.txt`) as the rollback shape.
- [x] Record the `iscc` version the CI image reports (it is in the package
      job's log after phase 2 lands, or on a Windows box: `iscc /?`).

## Phase 1 — The installer script

- [x] Create `scripts/installer/PTMonitor.iss`:
  - [x] header comment: what it builds, the two modes, the AppId immutability
        rule, and "ASCII only, no BOM" (Inno reads a BOM-less file as the ANSI
        codepage; a stray non-ASCII byte would corrupt a message).
  - [x] `#define Root` from `SourcePath` (`AddBackslash(SourcePath) + "..\..\"`),
        and `#ifndef` guards for `AppVersion`, `AppVersionNumeric`, `Rid` so a
        manual `iscc` run fails with a readable message instead of producing
        `PTMonitor--win--setup.exe`.
  - [x] `[Setup]` exactly as in `design.md` §2.1 — including
        `PrivilegesRequired=lowest`,
        `PrivilegesRequiredOverridesAllowed=dialog commandline`,
        `UsePreviousPrivileges=yes`, the fixed `AppId`, `{autopf}\PTMonitor`,
        `MinVersion=10.0.14393`, `OutputBaseFilename` with a `{#Rid}` suffix.
  - [x] `ArchitecturesAllowed` / `ArchitecturesInstallIn64BitMode` selected from
        `{#Rid}` (`win-x64` → `x64compatible`, `win-arm64` → `arm64`), with an
        `#else #error` for any other RID.
  - [x] `[Files]` (app exe → `{app}`, CLI exe → `{app}\cli`), `[Icons]`
        (`{autoprograms}\PTMonitor`, flat), `[Run]` (postinstall launch),
        `[Languages]` (`Default.isl` only).
- [x] Gate 1: file is ASCII (`LC_ALL=C grep -nP '[^\x00-\x7F]'` finds nothing),
      no BOM (`head -c3 | xxd`), `grep` confirms every directive from the design
      table is present. The real compile happens in phase 3's CI run.

## Phase 2 — `build.ps1`

- [x] `ConvertTo-NumericVersion` helper next to the other helpers, with the
      throw-on-non-numeric rule; used by the installer step.
- [x] `iscc` discovery (`Get-Command iscc`, then
      `${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe`), version log
      (`VersionInfo.FileVersion`), **`>= 6.3` assertion**, actionable failure
      message with the download URL.
- [x] Installer step after the zip step: `iscc /Qp` with
      `/DAppVersion`, `/DAppVersionNumeric`, `/DRid`; exit code checked; the
      built path printed.
- [x] `-SkipInstaller` switch (param block + header comment + help text), and
      skipped when `$SkipZip` is set (an installer without a zip is a debugging
      mode nobody wants; document the coupling).
- [x] `SHA256SUMS.txt` producer: name filter for `PTMonitor-*.zip` **and**
      `PTMonitor-*-setup.exe`, `Sort-Object Name`, ASCII/no BOM unchanged.
- [x] Header comment: new output line `artifacts/PTMonitor-<version>-<rid>-setup.exe`.
- [x] Gate 2: `python3` port of the numeric-version rule re-run against the
      table in this file (below) and against the PowerShell lines, side by side;
      `bash -n` is not applicable (PowerShell), so review-only + CI run.

| input | expected numeric |
|---|---|
| `1.0.0` | `1.0.0.0` |
| `1.2.3` | `1.2.3.0` |
| `1.0.0-dev.abc1234` | `1.0.0.0` |
| `1.0.0-feature-x-abc123.def4567` | `1.0.0.0` |
| `2.4` | `2.4.0.0` |
| `1.0.0.7` | → must **throw** (4 numeric components) |

## Phase 3 — CI, both lanes in one commit

- [x] `build.yml`: upload path (+`artifacts/PTMonitor-*-setup.exe`), sums
      verification (`Count -ne 4` + name pattern), attestation `subject-path`,
      release asset list, release notes.
- [x] `branch-release.yml`: the *same four blocks verbatim* (sums verification
      stays character-identical), plus the `$keep` list in the stale-asset
      sweep and the notes text.
- [x] Header comments of both workflows updated where they enumerate the assets
      ("two zips" → "two zips + two installers") — the files are documentation.
- [x] Gate 3:
      - [x] `python3 -c "import yaml,sys;[yaml.safe_load(open(f)) for f in ('.github/workflows/build.yml','.github/workflows/branch-release.yml')]"` parses.
      - [x] `grep -n "uses:" .github/workflows/*.yml` still shows 40-char SHAs
            and no `@vN`.
      - [x] identical-block diff (from `design.md` §8) is empty.
      - [x] `grep -n "Count -ne" ` shows 4 in both files.
      - [x] every artifact name mentioned in a workflow exists in the other
            workflow's set (x64 zip, arm64 zip, x64 setup, arm64 setup, sums).

## Phase 4 — `build.sh`

- [x] Accept `--skip-installer` (no-op + note) and document it in `usage()`.
- [x] After the zip step: the explicit "installers are Windows-only" line.
- [x] Sums rewrite globs zips **and** pre-existing setup exes (with
      `nullglob`/`compgen` so an empty set does not create a `*.exe` literal
      line).
- [x] `usage()` output list gains the installer line with the "Windows only"
      qualifier.
- [x] Gate 4: `bash -n scripts/build.sh`; `shellcheck` if available; simulate
      `artifacts/` with two fake zips + two fake setup exes and confirm the
      produced `SHA256SUMS.txt` has 4 sorted lines and no BOM.

## Phase 5 — Documentation

- [x] `README.md` quick start → **Installer (recommended)** / **Portable zip**;
      uninstall via Settings → Apps; `%LOCALAPPDATA%\Programs\PTMonitor` for
      per-user and `%ProgramFiles%\PTMonitor` for all-users; the mode page
      explanation; "settings stay per-user" note; "uninstall before switching
      mode"; SmartScreen caveat; `%APPDATA%\PTMonitor` kept on uninstall.
- [x] Verification section: setup exe in the `sha256sum`/`Get-FileHash` example
      and in the `gh attestation verify` line.
- [x] CI section (`build.yml` / `branch-release.yml` blocks): the asset list and
      the "two zips plus `SHA256SUMS.txt`" phrases gain the installers.
- [x] Build-from-source section: `iscc` is a Windows-only prerequisite for the
      installer step, `-SkipInstaller` is how you avoid it.
- [x] `.trellis/spec/guides/ci-pipeline.md`: installer added as a third asset
      class (naming, checksum count 4, attestation glob, built only on a Windows
      host, `.iss` is ASCII, `AppId` is immutable, `UsePreviousPrivileges` is
      load-bearing).
- [x] Gate 5: every README claim re-checked against the `.iss`/scripts
      (paths, switch names, asset names); `grep -n "no installer"` returns
      nothing.

## Phase 6 — Real-machine acceptance (Windows)
Run on Windows 10/11 x64 (and once on Arm64 if available). Tick `prd.md`
acceptance criteria 1–9 as they pass.

**Status: not run.** Every box below is Windows-only; this task was verified
only for the parts a Linux box can reach (Phases 1–5 gates + the product gate).
The list stays here as the acceptance checklist for the first Windows run.

- [ ] `.\scripts\build.ps1 -Version 1.0.0` on Windows → 4 artifacts + sums;
      `iscc` version logged; setup exe icon is the app icon.
- [ ] x64 VM, mode page → *Install for me only*: no UAC, installs to
      `%LOCALAPPDATA%\Programs\PTMonitor`, Start Menu entry works, tray app runs.
- [ ] `cd "%LOCALAPPDATA%\Programs\PTMonitor\cli"; .\PTMonitor.Cli.exe --selftest` → exit 0.
- [ ] Re-run the installer → mode page is **not** shown, in-place upgrade, one
      Installed-apps entry, new version displayed.
- [ ] x64 VM (admin) → *Install for all users*: one UAC prompt,
      `%ProgramFiles%\PTMonitor`, all-users Start Menu, entry visible to a second
      user, second user starts with empty settings (expected).
- [ ] `/VERYSILENT /CURRENTUSER` and `/VERYSILENT /ALLUSERS` both install to the
      matching directory without the mode page.
- [ ] `PTMonitor-<v>-win-arm64-setup.exe` on an Arm64 machine: installs and runs;
      the x64 setup on the same machine is accepted through emulation (documented
      behaviour) or refused if it is not — record which, and match the README.
- [ ] Uninstall both modes: files + shortcuts gone, entry gone,
      `%APPDATA%\PTMonitor` intact, reinstall keeps the server list.
- [ ] `sha256sum -c SHA256SUMS.txt` (or `Get-FileHash` comparison) for all four
      artifacts; `gh attestation verify <setup.exe>`.
- [ ] `actionlint .github/workflows/*.yml` on a machine that has it.
- [ ] Product gate unchanged: `dotnet build -c Release`,
      `PTMonitor.Cli --selftest`, `dotnet test` green.

## Phase 7 — Review gates before `task.py start`

- [ ] `prd.md`, `design.md`, this file and the jsonl manifests reviewed by the
      user; scope questions in the PRD `Decisions` table all resolved.
- [ ] No acceptance criterion depends on something out of scope (signing,
      auto-update, MSI).
- [ ] Rollback path understood: `-SkipInstaller` on the two package calls, plus
      removing the setup exes from the sums/attest/release lists, restores the
      current two-zip pipeline without touching the app.

## Rollback points

| After | Undo |
|---|---|
| Phase 1 | delete `scripts/installer/` — nothing references it yet |
| Phase 2 | `-SkipInstaller` on the CI package calls; `build.ps1` still builds zips |
| Phase 3 | revert the two workflow files to the two-zip asset lists |
| Phase 4–5 | reverting docs is independent of the pipeline |
| Phase 6 failure | the branch lane's artifacts are throwaway; nothing is released until a `v*` tag |

## Validation commands (this machine has no dotnet/pwsh/actionlint)

```bash
# YAML parse
python3 -c "import yaml;[yaml.safe_load(open(f)) for f in
  ['.github/workflows/build.yml','.github/workflows/branch-release.yml']];print('ok')"

# pinned actions
grep -n "uses:" .github/workflows/*.yml | grep -vE "@[0-9a-f]{40} # v" || echo "all pinned"

# duplicated blocks still identical
diff <(sed -n '/Verify checksums/,/verified \$name/p' .github/workflows/build.yml) \
     <(sed -n '/Verify checksums/,/verified \$name/p' .github/workflows/branch-release.yml) && echo identical

# artifact names agree across lanes
grep -o "PTMonitor-[^\"]*-\(zip\|setup\.exe\)" .github/workflows/*.yml | sort -u

# sums format, simulated
python3 - <<'PY'
import hashlib, pathlib
d = pathlib.Path('artifacts'); d.mkdir(exist_ok=True)
for n in ['PTMonitor-1.0.0-win-x64.zip','PTMonitor-1.0.0-win-arm64.zip',
          'PTMonitor-1.0.0-win-x64-setup.exe','PTMonitor-1.0.0-win-arm64-setup.exe']:
    (d/n).write_bytes(n.encode())
lines = [f"{hashlib.sha256((d/n).read_bytes()).hexdigest()}  {n}"
         for n in sorted(p.name for p in d.iterdir() if p.suffix in {'.zip','.exe'})]
assert len(lines) == 4 and all(l.split('  ')[0].islower() for l in lines), lines
print('\n'.join(lines))
PY

# ASCII / BOM / switch parity
LC_ALL=C grep -nP '[^\x00-\x7F]' scripts/installer/PTMonitor.iss || echo "ascii ok"
head -c3 scripts/installer/PTMonitor.iss | xxd | head -1
bash -n scripts/build.sh && echo "build.sh parses"
```

---

## Verification log — 2026-11-08 (Linux dev box)

Ran after the implementation was already written (the code had been sitting in
the working tree, uncommitted and unverified). No Windows host, no `pwsh`, no
`iscc`, no `actionlint` available.

| Gate | Result | Evidence |
|---|---|---|
| Gate 1 `.iss` | pass | ASCII only (`LC_ALL=C grep -nP '[^\x00-\x7F]'` no match), no BOM (`open(...,'rb').read()[:3] != EF BB BF`), all 26 `[Setup]` values from design §2.1 match directive-for-directive |
| Gate 2 `build.ps1` | pass (review + python port) | `ConvertTo-NumericVersion` ported to python: the 6-row table incl. the 4-component throw case all pass; the PowerShell is line-for-line equivalent. Not executed — no `pwsh` |
| Gate 3 workflows | pass (after a fix) | both parse; all `uses:` 40-char SHAs; `Count -ne` → 2 per file; the duplicated `run:` blocks diff clean; 5 artifact names consistent across lanes; `$keep` lists both setup exes |
| Gate 4 `build.sh` | pass | `bash -n` ok; `shellcheck` downloaded via `shellcheck-py` → only a pre-existing `SC2155`; a real run with two fake setup exes produced 4 sorted lowercase-hex lines, ASCII, no BOM; the empty-glob case writes no `*.exe` literal |
| Gate 5 docs | pass (after a fix) | every README claim re-checked against the `.iss` (`{autopf}`/`{autoprograms}`/`{userpf}`/`{commonpf}` resolution, `/CURRENTUSER`, `/ALLUSERS`, mode-page labels, switch names, asset names); `grep "no installer"` → nothing |
| Product gate | pass | `dotnet build -c Release` 0 errors · `--selftest` 698 checks · `dotnet test` 151 passed |

### Defects found and fixed in this pass

1. **Both workflows' checksum-name regex rejected every installer.**
   `'^PTMonitor-.*\.(zip|-setup\.exe)$'` demands a dot before `-setup.exe`, so
   `PTMonitor-1.0.0-win-x64-setup.exe` failed `Verify checksums` — the `release`
   (build.yml) and `publish` (branch-release.yml) jobs would have failed on
   every tag/branch push. Changed to `'^PTMonitor-.*(\.zip|-setup\.exe)$'` in
   both files (kept character-identical), and re-proved the accept/reject
   behaviour against the 4 real names plus `PTMonitor.exe`, `….zip.bak`,
   `…setup.exe.tmp`, `SHA256SUMS.txt`.
2. `.trellis/spec/guides/ci-pipeline.md` quoted the same broken regex; corrected
   so the spec does not re-teach the bug.
3. `README.md` claimed *"Nothing is written outside `%APPDATA%\PTMonitor\` in
   either case"* — false once the installer exists (install dir, Start Menu
   shortcut, uninstall key). Narrowed to "the app itself writes nothing outside
   it; the installer adds only its files, the shortcut and the Installed-apps
   entry".

### Still unverified (Windows only)

`iscc` compile of the `.iss`, any execution of `build.ps1`, the whole of Phase 6,
`actionlint`, and the runtime `iscc` version (the `>= 6.3` assertion is safe —
the runner-image readmes list Inno Setup **6.7.1** for both `windows-2022` and
`windows-2025`, but the actual `VersionInfo.FileVersion` only shows up in the
first real package-job log).
