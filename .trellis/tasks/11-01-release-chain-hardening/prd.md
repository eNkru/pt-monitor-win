# Release-chain hardening

## Goal

Close findings 8, 9 and 10 of the 2026-11 security review: the published binaries
carry no proof of what built them, the workflows hand out repository write access
to every job and never gate a pull request, and there is no local log to diagnose
a failed login after the fact.

Child B of `11-01-security-hardening`. Parent owns the finding set and the
cross-child acceptance criteria.

## Findings in scope

| # | Finding | Severity | Owner |
|---|---|---|---|
| 8 | Releases unsigned, no checksum or attestation; `uses:` pinned to mutable major tags | Medium | this task |
| 9 | `contents: write` granted workflow-wide; no `pull_request` gate | Low | this task |
| 10 | No log file at all, so auth failures are undiagnosable; the redaction contract is unwritten | Low | this task |

Findings 1–7 are closed by `11-01-credential-tls-hardening` (PR #8).

## Requirements

### 8 — Provenance of a published binary

1. Every `uses:` in `.github/workflows/` is pinned to a full 40-character commit
   SHA with the human-readable version in a trailing comment. No mutable `@vN`
   tag remains, in either workflow.
2. Both release lanes publish a `SHA256SUMS.txt` next to the two zips, and the
   sums are computed from the same files that are uploaded.
3. Both release lanes attach a GitHub build-provenance attestation for both
   zips, verifiable with `gh attestation verify`.
4. A local `scripts/build.ps1` run produces the same `SHA256SUMS.txt` so a
   self-built binary can be compared with a released one.
5. README tells the user how to verify a download.

### 9 — Least-privilege workflows

6. `permissions:` is declared `contents: read` at workflow level. `contents:
   write` exists only on the job that creates or edits a release; the
   attestation jobs additionally get `id-token: write` and `attestations: write`
   and nothing else.
7. No job that compiles, tests or packages has repository write access.
8. `build.yml` gains a `pull_request` trigger that runs build + `--selftest` +
   `dotnet test` and nothing else — no packaging, no release, no artifact
   upload that needs write scope.
9. A pull request from a fork can run the gate and cannot reach a release step.
10. The two-lane contract in `.trellis/spec/guides/ci-pipeline.md` still holds:
    a new gate lands in both files in the same commit.

### 10 — A bounded, redacted log

11. The app writes one log file at `%APPDATA%\PTMonitor\ptmonitor.log`
    (the already-declared `AppPaths.LogFile`), plus at most one rotated
    predecessor, and never grows without bound.
12. Only events are logged: startup, unhandled exceptions, poll-failure
    *transitions*, secret-store failures, TLS certificate acceptance, and
    settings/secret write failures. **No per-poll chatter** — at the default 2 s
    interval that would be the whole file.
13. The log never contains a password, an auth header, a cookie/`SID`, or a raw
    payload body — enforced by a single redaction function that the tests cover,
    not by discipline at each call site.
14. A logging failure (unwritable directory, full disk) never breaks polling or
    terminates the app.
15. `README.md` and `.trellis/spec/backend/logging-guidelines.md` describe what
    the app actually writes. The current "the app writes nothing by default"
    claim is replaced, not left to rot.

## Acceptance Criteria

- [ ] `grep -n 'uses: ' .github/workflows/*.yml` shows only SHA-pinned actions,
      each with a `# vX.Y.Z` comment.
- [ ] `actionlint .github/workflows/*.yml` and a YAML parse of both files pass.
- [ ] A dry run of the release step produces `SHA256SUMS.txt` whose entries match
      `sha256sum` of the uploaded zips.
- [ ] `permissions:` blocks read `contents: read` at workflow level; `write`
      appears only under the release/publish/attestation jobs.
- [ ] `build.yml` on a `pull_request` event runs the gates and stops; the
      packaging and release jobs are skipped.
- [ ] `Log.Redact` has a table-driven test for `password=` form pairs,
      `user:pass@host` userinfo, `SID=`/cookie values and Basic-auth headers;
      both the selftest and the xUnit suite cover it.
- [ ] Log rotation is covered: writing past the cap leaves one `.1` file and a
      fresh current file.
- [ ] No call site logs inside the poll loop's success path or once per tick.
- [ ] `dotnet build -c Release`, `--selftest`, and `dotnet test` are green.
- [ ] `README.md` and `logging-guidelines.md` match the shipped behaviour.

## Non-goals

- Authenticode code signing with a purchased certificate (the parent task rules
  it out; checksums + provenance attestation are the substitute).
- Dependabot/renovate configuration — pinning is the deliverable, automated
  updates are a follow-up.
- Log levels, sinks, or a logging package. One file, one writer, no dependency.
- A log-viewing UI. The path is documented; the user opens the file.
- Redacting the CLI's `--pass` argument (already recorded as a follow-up in the
  parent PRD).

## Decisions taken

- **The log is always on** (decided 2026-11-01, user). Event-only and size-capped
  at 512 KiB ×2. Rationale: a log the user had to enable beforehand cannot
  diagnose a crash that already happened, which is exactly the scenario findings
  10 and the tray-crash task describe. The cost is that README's "writes nothing
  by default" claim is replaced with what the app actually writes — that is
  requirement 15, not a regression.
- **No log-level configuration, no opt-out toggle.** A switch adds a state the
  diagnosis path depends on, for a file capped at ~1 MiB. Revisit only if a user
  reports the file itself as a problem.

## Notes

- Finding 9's `pull_request` gate does not replace `branch-release.yml`: a
  same-repo branch push already publishes a rolling prerelease. The PR gate is
  about running the contract without publishing, and about fork PRs, which never
  reach the branch lane.
- The repo is public, so `actions/attest-build-provenance` works without a paid
  plan (verified: `gh repo view` → `"visibility":"PUBLIC"`).
- SHA pins as of planning: `checkout@fbc6f399…` (v5), `setup-dotnet@67a3573c…`
  (v4), `upload-artifact@ea165f8d…` (v4),
  `attest-build-provenance@4d101475…` (v4). Re-resolve before editing — the
  design doc says how.
