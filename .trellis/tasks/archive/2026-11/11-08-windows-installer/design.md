# Design — Windows installer (Inno Setup, dual per-user / per-machine)

## 1. Shape of the change

```
scripts/installer/PTMonitor.iss     NEW  the installer script (ASCII, no BOM)
scripts/build.ps1                   + numeric version, iscc, [Files]/sums, -SkipInstaller
scripts/build.sh                    + sums glob for setup exes, "Windows-only" note
.github/workflows/build.yml         sums count 4, upload/release/attest globs, notes
.github/workflows/branch-release.yml  same blocks + stale-asset keep list
README.md                           install / uninstall / verify / CI sections
.trellis/spec/guides/ci-pipeline.md third asset class + installer rules
```

No product code changes. No new NuGet package. No new GitHub action (the runner
image already ships Inno Setup 6.7.1 — verified on the `windows-2022` and
`windows-2025` image readmes).

## 2. The installer script

`scripts/installer/PTMonitor.iss`, one script, two architectures, two install
modes. Everything variable arrives as a `/D` define from `build.ps1`
(`AppVersion`, `AppVersionNumeric`, `Rid`); paths are derived from
`SourcePath` so the script works from any working directory.

### 2.1 `[Setup]` — the load-bearing directives

| Directive | Value | Why |
|---|---|---|
| `AppId` | `{{41E97050-0E4C-4F6E-B0F2-00318AD6F145}` | **never change it**: Inno matches upgrades and the uninstall key on it. One entry per install mode. |
| `AppName` | `PTMonitor` | matches the assembly name and the exe |
| `AppVersion` | `{#AppVersion}` | the exact release string, incl. `-dev.<sha7>` / branch suffix — visible in Installed apps |
| `VersionInfoVersion` | `{#AppVersionNumeric}` | must be `x.y.z.w`: `1.0.0` → `1.0.0.0` |
| `AppVerName` | `PTMonitor {#AppVersion}` | not shown in modern wizard, kept consistent |
| `AppPublisher` | `eNkru` | repo owner; no invented company |
| `AppPublisherURL` / `AppSupportURL` / `AppUpdatesURL` | `https://github.com/eNkru/pt-monitor-win` | the only real support channel |
| `DefaultDirName` | `{autopf}\PTMonitor` | resolves to `%ProgramFiles%` per-machine, `%LOCALAPPDATA%\Programs` per-user |
| `DefaultGroupName` | `PTMonitor` | |
| `DisableProgramGroupPage` | `yes` | one flat Start Menu entry, no one-item folder |
| `PrivilegesRequired` | `lowest` | default is per-user, no UAC — preserves today's "no admin rights" contract |
| `PrivilegesRequiredOverridesAllowed` | `dialog commandline` | the **mode page** (*Install for me only* / *Install for all users*), plus `/CURRENTUSER` / `/ALLUSERS` for silent runs |
| `UsePreviousPrivileges` | `yes` | on upgrade, reuse the detected mode and do not ask → no second entry, no per-user copy of a per-machine install |
| `ArchitecturesAllowed` | `x64compatible` \| `arm64` | per `Rid` |
| `ArchitecturesInstallIn64BitMode` | same token | 64-bit install mode, so `{autopf}` is the real `Program Files` |
| `MinVersion` | `10.0.14393` | Win10 1607, the documented floor |
| `OutputDir` | `{#Root}\artifacts` | next to the zips |
| `OutputBaseFilename` | `PTMonitor-{#AppVersion}-{#Rid}-setup` | `PTMonitor-<version>-<rid>-setup.exe`, same shape as the zip |
| `SetupIconFile` | `{#Root}src\PTMonitor.App\app.ico` | the app icon, already in the repo |
| `UninstallDisplayIcon` | `{app}\PTMonitor.exe` | icon in Installed apps |
| `Compression` / `SolidCompression` | `lzma2/max` / `yes` | the payload is a ~70 MB self-extracting exe |
| `CloseApplications` / `RestartApplications` | `yes` / `no` | an upgrade while the tray app runs; do not silently relaunch |
| `WizardStyle` | `modern` | |
| `LicenseFile` / `InfoBeforeFile` | (none) | the README is the documentation |

`UsePreviousPrivileges` only works when `AppId` is a literal (no constants) and
`PrivilegesRequiredOverridesAllowed` allows dialog — both hold here.

### 2.2 `[Files]` — exes only

```
Source: "{#Root}publish\{#Rid}\PTMonitor.exe";      DestDir: "{app}";       Flags: ignoreversion
Source: "{#Root}publish\{#Rid}\cli\PTMonitor.Cli.exe"; DestDir: "{app}\cli"; Flags: ignoreversion
```

PDBs and `.core` artifacts stay out of the installer (the portable zip keeps
them). `ignoreversion` because the single-file bundle is version-stamped by the
build, not by the installer.

### 2.3 `[Icons]`, `[Run]`

```
[Icons]
Name: "{autoprograms}\PTMonitor"; Filename: "{app}\PTMonitor.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PTMonitor.exe"; Description: "Launch PTMonitor"; Flags: nowait postinstall skipifsilent
```

`{autoprograms}` follows the mode: all-users Start Menu for per-machine, the
current user's folder otherwise. No desktop icon, no `{autostartup}` entry, no
`[Registry]` section — the app writes no registry state today and the installer
does not start doing it for the app.

### 2.4 `[Languages]` and `[Code]`

`Languages` = Inno's built-in `Default.isl` (English) only. zh-CN needs
`ChineseSimplified.isl`, which ships only in the unofficial ISL repo → out of
MVP (D7); the section is the single place to add it later.

No `[Code]` section in MVP. Rejected alternatives: a `CurUninstallStepChanged`
prompt to delete `%APPDATA%\PTMonitor` (D4 keeps the data silently and the
README says how), and an `InitializeSetup` "previous install in the other mode"
check (`UsePreviousPrivileges` already prevents the duplicate).

## 3. Version → numeric version

Windows file versions are 16-bit-per-component `a.b.c.d`; the release strings
are not (`1.0.0-dev.abc1234`, `1.0.0-feature-x-abc123.abc1234`).

Rule, implemented once in `build.ps1` (`ConvertTo-NumericVersion`):

1. cut at the first `-` or `+` → `1.0.0`;
2. split on `.`, require every part to be 1–5 digits, at most 3 parts → else
   **throw** (a silently wrong file version is worse than a failed build);
3. pad to three parts and append `0` → `1.0.0.0`.

`AppVersion` (display) keeps the full string, so `1.0.0-dev.<sha7>` is
identifiable in Installed apps while Explorer shows a sane file version.

## 4. `build.ps1` — the installer step

Order: gates → publish app → publish CLI → packaged selftest → zip → **installer**
→ `SHA256SUMS.txt`.

- `iscc` discovery: `Get-Command iscc` first, then
  `${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe`. Log
  `(Get-Item $iscc).VersionInfo.FileVersion`, require **>= 6.3** (`arm64`
  architecture token + modern wizard), and fail with the install URL otherwise.
  Missing `iscc` is never a silent skip.
- Invocation: `& $iscc /Qp "/DAppVersion=$Version" "/DAppVersionNumeric=$numeric" "/DRid=$Rid" scripts\installer\PTMonitor.iss`,
  exit code checked like every other step.
- `-SkipInstaller` switch for the lean path; the header comment lists the new
  output line.
- `SHA256SUMS.txt` producer changes from `Get-ChildItem -Filter "PTMonitor-*.zip"`
  to a name filter that accepts both `PTMonitor-*.zip` and
  `PTMonitor-*-setup.exe`, still `Sort-Object Name`, ASCII, no BOM. Ordering is
  stable (`-` < `.`), so setup exes sort before zips — irrelevant as long as it
  is deterministic.

## 5. `build.sh` — honest about the host

Inno Setup is Windows-only (running `iscc` under Wine is not a dependency this
project wants). `build.sh` therefore:

- keeps producing `publish/<rid>` + the zip;
- prints one explicit line — "installers are built on Windows only (CI or a
  Windows box); skipped here" — instead of failing or pretending;
- globs **zips and pre-existing setup exes** when rewriting
  `SHA256SUMS.txt`, so a developer who built on Windows and then ran the Linux
  script does not lose two checksum lines.
- `--skip-installer` is accepted for command-line parity with `build.ps1` and is
  a no-op with the same note, so a shared CI recipe cannot break.

## 6. Workflows — the same edit in both lanes

Per `.trellis/spec/guides/ci-pipeline.md`, the duplicate blocks stay duplicated:

| Block | Change |
|---|---|
| package/publish artifact upload path | add `artifacts/PTMonitor-*-setup.exe` to the `PTMonitor-release` artifact (otherwise the release job cannot upload what it never downloaded) |
| `Verify checksums` | `$sums.Count -ne 4` (message says "4 artifacts: 2 zips + 2 installers"), each name must match `^PTMonitor-.*\.(zip|-setup\.exe)$` and exist — the block stays character-identical in both files |
| `Attest build provenance` | `subject-path` becomes two lines: `artifacts/*.zip` + `artifacts/*-setup.exe` |
| release assets | `$x64setup` / `$arm64setup` added to `gh release create` (build.yml) and to create/upload (branch-release.yml) |
| release notes | "Download the installer for your architecture, or the zip if you prefer no install" instead of "unzip and run" |
| branch stale-asset sweep | `$keep` grows to `$x64, $x64setup, $arm64, $arm64setup, $sums`; `SHA256SUMS.txt` stays in the keep-list |

No new `uses:`, no `choco install`, no new pinned SHA. The gate/package split is
untouched: installers are built inside the existing `-SkipGates` package jobs,
i.e. only after a green gate.

Rollback: the installer is additive. If it misbehaves, `-SkipInstaller` in the
package steps (one line each) returns the pipeline to today's two-zip shape
while the `.iss` sits unused.

## 7. Risks and how they are handled

| Risk | Handling |
|---|---|
| Runner image drops/updates Inno Setup | `build.ps1` asserts presence **and** `>= 6.3`; a mismatch fails the package job loudly instead of shipping a broken installer |
| Per-machine install leaves a stale entry after a mode switch | `UsePreviousPrivileges=yes` reuses the mode; README says "uninstall before switching" |
| Users expect shared settings from an all-users install | documented: settings are per-user (`%APPDATA%`), per-machine only changes where the exe lives |
| SmartScreen warning looks like malware to a first-time user | README states the exe is not code-signed and shows the checksum / attestation verification; signing stays out of scope |
| Checksum count assertion forgets the new artifacts | acceptance criterion 6 + the identical block in both workflows + `sha256sum -c` run in the implement checklist |
| Single-file self-extraction leaves `%TEMP%\.net\PTMonitor` | unchanged behaviour of the portable exe today; noted in the README, not cleaned by the uninstaller |

## 8. Verification strategy (no Windows box in this session)

| Check | Where |
|---|---|
| Both workflows parse, `uses:` still 40-char pinned, no `@vN` | `python3 -c "yaml.safe_load(...)"` + `grep`, locally |
| The duplicated blocks are still byte-identical | `diff <(sed -n '/Verify checksums/,/^$/p' build.yml) <(same for branch-release.yml)` — the spec's own rule |
| Sums format/naming | simulate the artifact set in `artifacts/`, run the PowerShell glob equivalent in `python3`, confirm 4 sorted lines and the `sha256sum` shape |
| Numeric-version mapping | table-driven cases exercised against a ported `python3` implementation of the same rule; the PowerShell version is reviewed line-by-line |
| `.iss` integrity | `iscc /?`-level syntax cannot be checked on Linux — CI's package job is the first real compile |
| Install / upgrade / uninstall, both modes, both arches | manual VM checklist in `implement.md`; a branch push exercises the CI path end-to-end before merge |
