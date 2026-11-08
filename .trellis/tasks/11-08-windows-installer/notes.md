# Notes — Windows installer

## Where this stands

Implemented and verified for everything a Linux box can reach (Phases 1–5 gates
+ the product gate — see the *Verification log* at the end of `implement.md`).
**Phase 6 (real-machine acceptance on Windows) has not been run at all**, so
`prd.md` acceptance criteria 1–5, 6 (the `-c`/attestation half) and 8 stay
unverified by design, not by accident.

Nothing is committed yet: the whole change set is in the working tree on
`feat/windows-installer`.

## The bug worth remembering

Both workflows verified the checksum names with
`'^PTMonitor-.*\.(zip|-setup\.exe)$'`. That demands a literal **dot** before
`-setup.exe`, so the installers — the very artifacts this task adds — never
matched, and `Verify checksums` would have failed `release` (build.yml) and
`publish` (branch-release.yml) on every tag and branch push.

It was invisible on the Linux box: the same regex was quoted in
`.trellis/spec/guides/ci-pipeline.md` (so the spec *re-taught* it), the gates in
`implement.md` only grepped for `Count -ne 4` and "the blocks are identical" —
and identical they were, identically wrong. Grepping for a count does not
exercise the pattern; a two-line accept/reject probe does.

Lesson: **when a regex decides whether a release is published, test it against
the real artifact names, both directions.** `implement.md` Gate 3 now carries
that probe.

## Deliberate decisions re-checked

- `PrivilegesRequired=lowest` + `PrivilegesRequiredOverridesAllowed=dialog` +
  `UsePreviousPrivileges=yes` is what makes "one installer, two modes, no
  re-asking on upgrade" work. `UsePreviousPrivileges` is load-bearing: without
  it a per-machine install could be duplicated as per-user.
- `ArchitecturesAllowed=x64compatible` accepts **both** x64 and Arm64 Windows
  (Inno's own example), so the `win-x64` setup *does* install on Arm64 through
  x64 emulation. `prd.md` AC5 said it was refused; that was wrong and is
  corrected there.
- `{autopf}` / `{autoprograms}` resolve per mode, which is the whole reason one
  script covers both. Verified against the Inno docs, not against a real install.
- `.iss` stays ASCII with no BOM; Inno reads a BOM-less script as the ANSI
  codepage and a stray non-ASCII byte would corrupt a message.

## Known cosmetic leftovers (not fixed)

- `README.md` still says "121 tests" somewhere; the suite runs 151. Predates
  this task, unrelated to the installer.
- `shellcheck` reports a pre-existing `SC2155` in `scripts/build.sh`
  (`export PATH="$(dirname …)"`), byte-identical in `HEAD`.
- `ConvertTo-NumericVersion` accepts 5-digit components (`99999` > 65535), so
  `1.0.99999` would pass the helper and then fail inside `iscc`. Matches design
  §3 and is unreachable from a real tag/branch version; not worth a guard.

## The first CI run (2026-11-08) — what only real execution could find

Pushed `feat/windows-installer`; `branch-release.yml` ran on `windows-latest`.
The gate job passed (build + `--selftest` 698 + 151 tests) and `build.ps1` got
all the way to `==> installer (Inno Setup)`, then failed at the version assertion:

```
Inno Setup 0.0.0.0 at C:\ProgramData\Chocolatey\bin\ISCC.exe is too old;
6.3 or newer is required
```

**The compiler was fine; the guard was reading the wrong file.** `iscc` on PATH
on that image is a **Chocolatey shim** — a ~20 KB launcher with no version
resource, so `VersionInfo.FileVersion` is `0.0.0.0` for every choco-installed
tool. The `>= 6.3` assertion therefore failed on a good Inno Setup 6.7.1, and
would have failed on every future run.

Fix in `build.ps1`: build a candidate list (`Get-Command iscc`, then
`%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe`, then `%ProgramFiles%\…`), and
resolve the **version** from the first candidate that reports a real one — the
exe itself, or the target named in `<candidate>.shim` (XML, `<path>`/`<target>`).
Only when no candidate reports a version does it warn and continue instead of
throwing, because a wrapper in front of a working compiler is legitimate and the
compile is the real check. Verified the pieces on a Windows PowerShell: a
versionless file reports `FileVersion=""` (not `0.0.0.0`), the `.shim` regex
captures the target for both element spellings, and a real version string can
carry trailing build text (`10.0.26100.9818 (WinBuild.160101.0800)`) which the
anchored `^(\d+)\.(\d+)` parse tolerates.

`build.ps1` is also the only file in this task that had never executed anywhere
— dotnet publish, zip, sums and selftest all ran green in that CI job, so only
this block was unproven.

Recorded in `.trellis/spec/guides/ci-pipeline.md`: never read `VersionInfo` off
the PATH entry alone on a runner image, because the entry is a shim.

## Follow-up: the `0.0.0.0` installer version (2026-11-08, later)

After the shim fix the installer finally compiled — but every setup exe came out
as `PTMonitor-0.0.0.0-<rid>-setup.exe` while the zip beside it carried the
correct version, and the release step then failed looking for the asset name it
had computed.

Two wrong theories were chased first (an ISPP symbol collision with the
`[Setup] AppVersion` directive, then the `/D` value being parsed as an
expression). Both were cheap to disprove with a temporary diagnostic in the
package step, which printed the truth:

```
==> diag: Version=[0.0.0.0] numeric=[1.0.0.0] rid=[win-x64]
```

The `$Version` **inside `build.ps1`** had already been overwritten by the time
the installer step ran — even though the zip name, one line earlier, was correct.

**Root cause: PowerShell variable names are case-INsensitive.** The shim
discovery loop I had just added used

```powershell
$version = [string](Get-Item $candidate).VersionInfo.FileVersion
```

which *is* the script's `[string]$Version` parameter. Reading the 0.0.0.0
"version" of the Chocolatey shim therefore rewrote the release version. The
numeric version had been computed a moment earlier, which is why it survived
and why `{#AppVersionNumeric}` was fine while `{#AppVersion}` was not.

Fix: rename the loop locals to `$candidateFileVersion` / `$shimTarget`, and
keep the note in the script so nobody reintroduces `$version`. The `.iss` was
restored to the original `AppVersion` / `AppVersionNumeric` / `Rid` define
names (the `Ptm`-prefixed rename was a wrong-theory change).

**The lesson:** in a PowerShell build script, a short lowercase local (`$version`,
`$rid`, `$zip`) can silently reassign a `param([string]$Version)`. Name loop
variables after what they hold (`$candidateFileVersion`), never after the
concept.
