# Security hardening from the 2026-11 security review

## Goal

Track and close the findings of a read-only security review of PTMonitor
(Windows). The review covered credential handling, TLS, at-rest storage, parsing
of untrusted server responses, and the CI/release chain.

This parent owns the finding set, the child map, and the cross-child acceptance
criteria. It carries no implementation work of its own.

## Background

The review found the app's posture sound in most places (DPAPI `CurrentUser`
secrets, no secret in `settings.json`, no secrets in git history, atomic writes,
no telemetry, escaped query building, asInvoker manifest) but found real defects
in the credential path and in the release chain. Two of them contradict
invariants the project already documents in
`.trellis/spec/backend/persistence-guidelines.md`.

Severity as rated in the review. Fix order follows the review's ranking
(`3 → 1 → 2 → 5 → 4`, then CI), not the finding numbers.

## Findings

| # | Finding | Severity | Child | Status |
|---|---|---|---|---|
| 1 | Credentials travel in clear by default (`TlsToggle.IsChecked = false` for new servers, `http://` in `EndpointParser`) with no warning | High | A | |
| 2 | "Allow self-signed certificate" disables *all* validation; anything accepted is invisible | High | A | |
| 3 | Failed secret write is swallowed (`PersistUnsafe`), so the documented secret-first order is not actually enforced and the editor's error branch is dead code | Med/High | A | |
| 4 | A corrupt `secrets.dat` silently drops every password, then all servers poll anonymously | Medium | A | |
| 5 | `user:pass@host` typed into the host field is parsed and discarded | Medium | A | |
| 6 | No bound on response body size (unbounded buffering) | Low/Med | A | |
| 7 | Redirects: .NET clears `Authorization` on every redirect (same-origin loses auth; another origin re-authenticates after a 401, including https→http), and a 307/308 replays the login form body | Low | A | |
| 8 | Releases unsigned, no checksum/attestation; actions pinned to mutable major tags | Medium | B | ✅ closed — SHA pins + `SHA256SUMS.txt` + build-provenance attestation |
| 9 | `contents: write` granted workflow-wide; no `pull_request` gate | Low | B | ✅ closed — workflow `contents: read`, job-scoped write, PR gate runs `gate` only |
| 10 | No log file at all; auth failures undiagnosable, redaction contract unwritten | Low | B | ✅ closed — bounded, redacted, event-only `ptmonitor.log` |

## Child map

- `11-01-credential-tls-hardening` (A, P1) — findings 1, 2, 3, 4, 5, plus 6 and 7.
- `11-01-release-chain-hardening` (B, P2) — findings 8, 9, 10.

Ordering is not structural: A and B touch disjoint files and can land in either
order or in parallel branches. Within A, the fix order above is mandatory (see
its `implement.md`).

## Requirements

1. Every finding in the table is either fixed or explicitly re-scoped, and the
   matching spec doc reflects the new reality.
2. No finding is closed by documentation alone when the code contradicting it is
   cheap to change.
3. No behaviour change for a correctly configured HTTPS server.
4. `dotnet build`, `--selftest`, and `dotnet test` stay green after every child.
5. README's "Data, settings and secrets" and security-adjacent claims stay true
   after the change.

## Cross-child acceptance criteria

- [x] All ten findings are addressed or re-scoped with a written reason.
- [x] `.trellis/spec/backend/persistence-guidelines.md` describes what the code
      actually does for secret writes, corruption, and the DPAPI fallback.
- [x] `.trellis/spec/backend/client-and-parser-rules.md` documents the TLS and
      userinfo rules.
- [x] `.trellis/spec/guides/ci-pipeline.md` documents the pinning, permissions
      and provenance rules.
- [ ] A user with a working HTTPS setup sees no change in behaviour.
- [x] Full verification (build, selftest, tests) passes on the final state.
      Verified on merged `main` (`2d04a89`): build clean, 298 selftest checks,
      85 xUnit tests.

## Non-goals

- A rewrite of the transport layer, connection pooling, or a new HTTP library.
- Certificate pinning / TOFU trust store (recorded as the natural follow-up to
  finding 2, not part of it).
- Client certificates, proxy configuration, or a credential-manager migration.
- Authenticode code signing with a purchased certificate (child B produces
  checksums and a build-provenance attestation instead).
- Anything that makes the app write outside `%APPDATA%\PTMonitor\`.

## Follow-ups deliberately left open

- CLI `--pass` is visible in shell history and the process list (`ProbeRunner`).
- `AppPaths.LogFile` is declared but unused; whoever adds logging also owns
  secret redaction.
- Neither `--selftest` nor the xUnit suite exercises the DPAPI path (both use
  `InMemorySecretStore`), which is exactly why findings 3 and 4 survived.

## Notes

- File references in the child docs are as of commit `2fd6bb3`; re-read the file
  before editing. The finding text above is the authoritative short form.
- Source of the review: this session's transcript.
