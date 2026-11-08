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
