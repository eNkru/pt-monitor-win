# Implementation plan — release-chain hardening

Three groups, in this order: logging (pure code, testable here), then the job
split, then pinning + provenance (which needs the split's scoped permissions).
Commit per step.

## Validation commands

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release --no-build -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release --no-build
python3 -c "import yaml,sys;[yaml.safe_load(open(f)) for f in sys.argv[1:]]" .github/workflows/*.yml
```

`actionlint`/`pwsh` are unavailable here (see `design.md` §5); workflow steps 4–6
gate on the YAML parse plus a line-by-line read of every changed block.

## Checklist

### Logging (finding 10)

- [ ] **1. `Log` module**
  - New `src/PTMonitor.Core/Diagnostics/Log.cs` per `design.md` §4: `Initialize`,
    `Info`/`Warn`/`Error`, `Redact`, `Shutdown`; no-op before `Initialize`;
    append-flush-close under a lock; `maxBytes` rotation to `ptmonitor.log.1`;
    every write in a `try`/`catch` with a comment.
  - Selftest `LogChecks`: redaction table (form pair, query pair, Basic/Bearer,
    `Set-Cookie`/`SID`, `scheme://u:p@host`, bare `u:p@host`), rotation past a
    tiny cap leaves `.1` + fresh file, an unwritable path does not throw, calls
    before `Initialize` are no-ops. Temp dir per case, deleted in `finally`.
  - xUnit mirror in `CoreTests.cs`, same table.
  - Gate.
- [ ] **2. Wire the call sites** (`design.md` §4 table)
  - `App/Program.cs`: `Log.Initialize(AppPaths.LogFile)` before the builder; one
    startup line; `AppDomain` + `Dispatcher.UIThread` unhandled handlers that log
    redacted and do **not** set `Handled`.
  - `MonitorStore.PollLoopAsync`: log on failure transition only — keep the last
    logged `FailureKind` per server id, log when the previous status was `Online`
    or the kind changed. Assert in a check that a repeated identical failure logs
    once.
  - `DpapiSecretStore` (`Unreadable`, `.corrupt`, `Persist` throw),
    `SettingsStore` (write failure), `AppServices` cert callback (thumbprint),
    `ServerEditorWindow.OnSave` (aborted save).
  - Gate.
- [ ] **3. Docs for the log**
  - `README.md`: `ptmonitor.log` row in "Data, settings and secrets" (contents,
    512 KiB ×2 cap, no secrets); replace "the app writes nothing by default" in
    troubleshooting.
  - `.trellis/spec/backend/logging-guidelines.md`: rewrite the "no log file"
    premise — event-only rule, rotation, the redaction table as a contract,
    "never per-poll", and the "logging never throws" rule. Update the
    `backend/index.md` row that says "There is no log file".
  - Gate.

### Least privilege (finding 9)

- [x] **4. Split `build.yml` into `gate` / `package` / `release`**
  - `permissions: contents: read` at workflow level; `release` gets
    `contents: write` (+ `id-token: write`, `attestations: write` in step 6).
  - `on: pull_request` added; `package` and `release` guarded so a PR stops after
    `gate`.
  - `package` uploads both zips, both exes and `SHA256SUMS.txt`; `release`
    downloads them, re-verifies the sums, then `gh release create` with the two
    zips + `SHA256SUMS.txt`.
  - Header comment updated (it currently describes a single-job lane).
  - Gate + YAML parse + read of the new `if:` expressions.
- [x] **5. Split `branch-release.yml` into `gate` / `publish` (+ existing
  `cleanup`)**
  - Same workflow-level read scope; `publish` carries `contents: write`;
    `cleanup` keeps `contents: write` and stays checkout-free.
  - `publish` checks out `github.sha` (default credentials) so the existing
    `git tag -f` + `git push --force` block keeps working, downloads the package
    artifacts, and leaves the version/slug derivation, concurrency group and
    stale-asset cleanup untouched.
  - Gate + YAML parse + read.
- [x] **6. Pin every action to a SHA** (both files)
  - Resolve with the loop in `design.md` §1 at implementation time; keep a
    `# vX` comment on each line. Includes the new `download-artifact`.
  - `grep -n 'uses: ' .github/workflows/*.yml` shows no `@v` reference left.
  - Gate + YAML parse.

### Provenance (finding 8)

- [x] **7. Checksums**
  - `scripts/build.ps1`: after the zip step, rewrite
    `artifacts/SHA256SUMS.txt` from every `artifacts/PTMonitor-*.zip`, sorted,
    `sha256sum` format (two spaces), lowercase hex.
  - `build.yml` `release` and `branch-release.yml` `publish`: recompute from the
    downloaded zips and fail if it disagrees with the uploaded `SHA256SUMS.txt`.
  - Gate + read of the pwsh block.
- [x] **8. Attestation**
  - `actions/attest-build-provenance` (pinned) with
    `subject-path: artifacts/*.zip`, in `release` and `publish`, after download
    and before the release is created/updated.
  - README: how to verify (`gh attestation verify`, `sha256sum -c`).
  - Gate + YAML parse.
- [x] **9. Spec + final verification**
  - `.trellis/spec/guides/ci-pipeline.md`: new rules — SHA pinning with version
    comment, permission scoping (workflow read, job write), the PR gate and why
    it does not replace the branch lane, checksum format and dual producers,
    attestation ordering (before publish), and "never publish an unattested
    binary". Keep rules 1–9 intact and extend them; update the two-lane table.
  - Parent PRD: tick the finding rows for 8/9/10 and the `ci-pipeline.md`
    cross-child criterion.
  - Full verification (build, selftest, tests, YAML parse) on the final tree.

## Review gates

- After step 1: re-read `design.md` §4 against `Log.cs` — the redaction table and
  the no-op-before-`Initialize` rule are what the call sites assume.
- After step 4: the `build.yml` job graph is the template `branch-release.yml`
  copies; fix it here before mirroring.
- After step 7: verify the checksum file by hand against the zips
  (`sha256sum -c` on Linux after a `pwsh`-free reproduction, or compare
  `Get-FileHash` output formatting) — a wrong separator silently breaks every
  downstream `-c` invocation.

## Rollback points

- Step 1–3: delete `Log.cs` + revert the call sites per area; the module is
  additive, so a partial revert leaves no-ops.
- Steps 4–5: revert both workflow files together (a half-split leaves `release`
  without artifacts).
- Steps 6–8: revert the `uses:` lines / drop the `attest` step and the
  `SHA256SUMS.txt` asset independently.
