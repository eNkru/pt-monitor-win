# Windows installer (Inno Setup, per-user)

## Goal

Ship a **real Windows installer** for PTMonitor, next to the portable zips that
already exist, so a user can install the app "properly": an entry in Start Menu
and in **Settings → Apps → Installed apps**, a working uninstaller, and an
upgrade that replaces the previous version instead of leaving a second copy.

The portable zip stays. It is the no-install path, the thing CI already
publishes, and what a user runs from a USB stick. The installer is an
*additional* artifact, not a replacement.

## Confirmed facts (from the repository)

| Fact | Evidence |
|---|---|
| There is no installer today; the build produces a self-contained single-file exe, a CLI, and a zip of both | `scripts/build.ps1`, `scripts/build.sh` |
| The README says this out loud and documents "uninstall = quit and delete the exe + `%APPDATA%\PTMonitor`" | `README.md:135-136` |
| Identical publish options in both build scripts; gates are duplicated across the two workflows on purpose | `scripts/build.ps1`, `scripts/build.sh`, `.github/workflows/build.yml`, `branch-release.yml`, `.trellis/spec/guides/ci-pipeline.md` |
| Published assets today: `PTMonitor-<version>-win-x64.zip`, `…-win-arm64.zip`, `SHA256SUMS.txt` + build-provenance attestation; no code signing | both workflows |
| `SHA256SUMS.txt` is rewritten from `artifacts/*.zip` (sorted, lowercase hex, ASCII, no BOM); CI asserts **exactly 2** lines and re-verifies the bytes | `scripts/build.ps1`, both workflows |
| App identity: assembly name `PTMonitor`, icon `src/PTMonitor.App/app.ico`, published as `publish/<rid>/PTMonitor.exe` + `publish/<rid>/cli/PTMonitor.Cli.exe` | `PTMonitor.App.csproj`, `build.ps1` |
| User data lives only in `%APPDATA%\PTMonitor` (`settings.json`, DPAPI `secrets.dat`); no registry writes, nothing machine-wide | `README.md`, `.trellis/spec/backend/persistence-guidelines.md` |
| Version strings in use: `1.0.0` (tag), `1.0.0-dev.<sha7>` (main), `1.0.0-<slug>-<hash>.<sha7>` (branch) — all **non-numeric** for a Windows file version | `build.yml`, `branch-release.yml` |
| GitHub-hosted `windows-2022` / `windows-2025` images already ship **Inno Setup 6.7.1** (`iscc` on PATH) — no new action, no new pinned SHA, no `choco install` | actions/runner-images image readme (checked 2026-11-08) |
| Inno Setup supports x64 **and** Arm64 targets and non-administrative (per-user) installation | jrsoftware.org product page + `ArchitecturesAllowed` docs |
| The app is a tray agent with no autostart mechanism of its own | `src/PTMonitor.App/Services/` (no Run-key / Startup-folder code) |

## Requirements

### R1 — One installer per architecture, install mode chosen at run time

- Inno Setup 6 (`scripts/installer/PTMonitor.iss`), one installer per
  architecture, produced from `publish/<rid>/`.
- **Dual mode, chosen by the user in the wizard** — not two separate setup.exe
  files:
  - `PrivilegesRequired=lowest` — the **default is per-user, no UAC**;
  - `PrivilegesRequiredOverridesAllowed=dialog commandline` — Setup shows the
    "Select Setup Install Mode" page (*Install for me only* / *Install for all
    users*), and `/ALLUSERS` / `/CURRENTUSER` do the same for silent installs;
  - `{autopf}\PTMonitor` as `DefaultDirName` and `{autoprograms}` for the
    shortcut, so one script resolves to the right place per mode:

    | Mode | Dir | Start Menu | Elevation |
    |---|---|---|---|
    | per-user (default) | `%LOCALAPPDATA%\Programs\PTMonitor` | `%APPDATA%\Microsoft\Windows\Start Menu\Programs` | none |
    | per-machine | `%ProgramFiles%\PTMonitor` | `%ProgramData%\Microsoft\Windows\Start Menu\Programs` | UAC once, at install |

- `UsePreviousPrivileges=yes` (Inno's default — keep it explicit): on an upgrade
  Setup detects the existing install, **reuses its mode and does not ask again**,
  so a per-machine install is never silently duplicated as a per-user one (and
  vice versa). The mode page appears only on a first-time install.
- A stable `AppId` GUID so a newer build upgrades in place (one entry, not two).
  Inno writes the uninstall key to HKLM for all-users and HKCU for per-user and
  the uninstaller removes the matching one.
- `ArchitecturesAllowed` / `ArchitecturesInstallIn64BitMode` set per RID:
  `x64compatible` for win-x64 (runs on Arm64 Windows through x64 emulation,
  which is what the x64 portable exe does today), `arm64` for win-arm64. This
  also keeps 64-bit install mode on, so `{autopf}` is the real `C:\Program Files`.
- Installs `PTMonitor.exe` and, in a `cli\` subfolder, `PTMonitor.Cli.exe`
  (the headless selftest/probe binary is part of the product, keep it).

### R2 — Start Menu entry and uninstall entry

- Start Menu shortcut named **PTMonitor** under `{autoprograms}` (all-users
  folder for a per-machine install, the current user's folder otherwise).
- Entry in Settings → Apps → Installed apps with the product name, publisher,
  display icon and version; a per-machine entry is visible to every user, a
  per-user entry only to the user who installed it — that asymmetry is expected
  and documented, not a bug.
- Uninstall removes the installed files and shortcuts and leaves no orphan entry.
- No desktop shortcut by default; no `Run` key.
- Per-user settings stay per-user in **both** modes: `%APPDATA%\PTMonitor` is
  not shared by a per-machine install, so "all users" means "installed for all
  users, each with their own server list" — documented so it is not a surprise.

### R3 — Upgrade and uninstall behaviour

- Installing a newer version over an older one in the **same** mode replaces the
  files and leaves exactly one Installed-apps entry (verified by installing two
  versions in a row, once per mode).
- Switching mode on an existing install is not offered: `UsePreviousPrivileges`
  reuses the detected mode. Changing mode therefore means uninstall then install,
  which the README states in one line.
- Uninstall removes the install dir + shortcuts **and keeps user data**
  (`%APPDATA%\PTMonitor`) by default, because it holds the user's server list
  and their DPAPI secrets; the README documents how to delete it manually.
  A "delete my settings too" checkbox is *not* part of MVP.

### R4 — Version information in the installer

- Displayed version = the exact release string (`1.0.0`, `1.0.0-dev.<sha7>`,
  `1.0.0-<slug>-<hash>.<sha7>`).
- Windows file version (`VersionInfoVersion`, `VersionInfoProductVersion`) must be
  numeric `x.y.z.w`; the build derives it from the display version (numeric
  prefix, pad to four components, prerelease dropped) and passes both to `iscc`.
- `AppVersion` in the uninstall entry equals the display version, so a dev build
  is identifiable in Installed apps.

### R5 — Build wiring (both scripts, one contract)

- `scripts/build.ps1` produces
  `artifacts/PTMonitor-<version>-<rid>-setup.exe` after the zip step, by invoking
  `iscc` with `/DAppVersion=… /DAppVersionNumeric=… /DRid=…`.
- New switch `-SkipInstaller` for the lean path; **default is to build it** when
  `iscc` is available on PATH. If the installer is requested and `iscc` is
  missing, the script fails with an actionable message — never silently skips.
- Version-string → numeric-version conversion lives in one place and is used by
  both the installer step and any future packaging.
- `scripts/build.sh` cannot run Inno Setup (Linux/WSL, no `iscc`): it keeps
  producing zips and prints an explicit "installers are built on Windows only
  (CI or a Windows box)" line instead of pretending. Its `SHA256SUMS.txt`
  rewrite must still list pre-existing `*-setup.exe` files so a Linux run does
  not silently drop checksums that a Windows run wrote.

### R6 — Checksums and provenance cover the installer too

- `SHA256SUMS.txt` lists **every published artifact** (2 zips + 2 setup exes),
  sorted by name, same `sha256sum` format. Both producers (scripts and the CI
  verification blocks) are updated together; the "exactly 2 lines" assertions
  become "exactly 4".
- The build-provenance attestation's `subject-path` covers the setup exes as
  well as the zips.
- Release uploads and release notes in **both** workflows list the installers;
  the branch lane's stale-asset sweep keeps them in the uploaded set and must
  not delete `SHA256SUMS.txt`.

### R7 — CI stays gate-first and pinned

- No new GitHub action, no `choco install`, no unpinned `uses:` — the runner
  image's `iscc` is used, and the packaging step asserts `iscc` is present and
  reports its version in the log (a runner image that drops it must fail loudly).
- Both lanes keep identical gate/package/asset blocks; changes land in
  `build.yml` and `branch-release.yml` in the same commit, per
  `.trellis/spec/guides/ci-pipeline.md`.
- Installers are produced only after the gate passed (inside the existing
  `-SkipGates` package jobs), never from an ungated checkout.

### R8 — Documentation

- README install section rewritten: **Installer (recommended)** vs
  **Portable zip**; uninstall via Installed apps; where files land
  (`%LOCALAPPDATA%\Programs\PTMonitor`); note that SmartScreen will warn because
  the binaries are not code-signed, and that this is unchanged from the zip.
- Download verification section shows the setup exe in the checksum example and
  keeps `gh attestation verify` working for it.
- `.trellis/spec/guides/ci-pipeline.md` gains the installer as a third asset
  class (naming, checksum count, attestation glob, Windows-only build rule).

## Decisions

| # | Decision | Status |
|---|---|---|
| D1 | Inno Setup `setup.exe` (over Velopack / WiX MSI) | **Accepted** (user) |
| D2 | **Dual mode in one installer**: mode page (`PrivilegesRequiredOverridesAllowed=dialog`), per-user pre-selected and no UAC by default, per-machine available for users who want Program Files; `UsePreviousPrivileges=yes` | **Accepted** (user request) |
| D3 | Keep the zip artifact unchanged and add the installer | Accepted (implemented) |
| D4 | Uninstall keeps `%APPDATA%\PTMonitor` (settings + secrets), documented | Accepted (implemented) |
| D5 | Optional desktop shortcut and "start with Windows" task are **out of MVP**; Start Menu shortcut only | Accepted (implemented) |
| D6 | One `SHA256SUMS.txt` for all four artifacts (not a second installer-only file) | Accepted (implemented) |
| D7 | zh-CN installer localization out of MVP (needs a vendored unofficial `.isl`) | Accepted (implemented) |
| D8 | No code signing (unchanged); SmartScreen warning documented | Accepted (implemented) |

## Out of scope

- Code signing / EV certificate and the SmartScreen reputation work it implies.
- Auto-update (`Velopack`-style delta updates, in-app "new version available").
- MSI packaging, MSIX, Store submission.
- A *separate* per-machine installer (dual mode is one binary) and the
  "install for all users but share one settings store" idea — settings stay
  per-user by design.
- Removing `%APPDATA%\PTMonitor` from the uninstaller.
- File associations, protocol handlers, scheduled tasks, services.

## Acceptance criteria

Status as of 2026-11-08: **6 of 9 cannot be run on the Linux dev box** (they
need Windows VMs). The ones that can → marked *verified*. Nothing here is
ticked off unverified.

1. On a clean Windows 10/11 x64 VM, `PTMonitor-<v>-win-x64-setup.exe` opens the
   **mode page**; with *Install for me only* it installs with **no UAC prompt**
   into `%LOCALAPPDATA%\Programs\PTMonitor`;
   with *Install for all users* it prompts UAC once and installs into
   `%ProgramFiles%\PTMonitor`. In both cases the app appears in the Start Menu
   and in Settings → Apps → Installed apps with the right version.
2. Launching from the Start Menu starts the tray app; the installed
   `cli\PTMonitor.Cli.exe --selftest` exits 0 (both modes).
3. Installing a second, newer build over it leaves one Installed-apps entry and
   the newer version, with no duplicated files — **and does not ask for the mode
   again** (the detected mode is reused).
3b. A `/VERYSILENT /ALLUSERS` and a `/VERYSILENT /CURRENTUSER` install both
   succeed without the mode page and land in the matching directory.
4. Uninstall (from Installed apps) removes files + shortcuts and leaves no
   entry; `%APPDATA%\PTMonitor` survives; a subsequent reinstall sees the old
   server list.
5. Running the `win-x64` installer on an Arm64 Windows machine is **accepted**
   through x64 emulation (`ArchitecturesAllowed=x64compatible`), and the
   `win-arm64` installer installs natively there. On an Arm64 machine that
   cannot emulate x64 the setup is refused with Inno's architecture message.
   *(Corrected 2026-11-08: this criterion previously said the x64 setup is
   refused on Arm64, which contradicted design §2.1, the `.iss` and the README.
   The implementation follows the design; the Windows run records which
   behaviour the machine shows.)*
6. `artifacts/SHA256SUMS.txt` lists exactly the four published artifacts and
   `sha256sum -c` passes for each; `gh attestation verify <setup.exe>` succeeds.
   *(The format half is verified on Linux; `-c` against real binaries and the
   attestation need the CI run + a Windows box.)*
7. `dotnet build -c Release`, `PTMonitor.Cli --selftest`, `dotnet test` remain
   green (no product-code change is expected, but the gate is the contract).
   *(verified 2026-11-08: 0 errors, 698 checks, 151 tests)*
8. `actionlint` is clean on both workflows and the two lanes still differ only
   where they already did. *(not run — `actionlint` is not installable on the
   Linux dev box; the YAML-parse and pinned-SHA gates stand in for it there.)*
9. A Linux `./scripts/build.sh` run leaves a `SHA256SUMS.txt` that does not drop
   setup-exe lines written by an earlier Windows run, and explains why it built
   no installer. *(verified 2026-11-08 with two fake setup exes: 4 sorted
   lowercase-hex lines, no BOM, and the Windows-only notice printed)*
