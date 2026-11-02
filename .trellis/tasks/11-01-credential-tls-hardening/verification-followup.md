# Focused verification follow-up — 2026-11-02

## Status and boundary

Implemented on `bugfix/credential-tls-verification` under the user-approved
follow-up. PRD/design/plan were updated **before production edits** to adopt
fail-closed cross-origin Basic auth: never reauthenticate after a 401. No
credential-forwarding mechanism was added. `verification.md` is preserved
unchanged as the original baseline, including its historical observations.
Neither this task nor its parent is closed; no Windows-dependent AC was ticked.
No commit, push or PR was created.

Boundary: Core owns effective form parsing/credential precedence, App applies
and renders it; transport retains the existing bounded redirect loop. No
settings shape, secret persistence implementation, polling or dependencies
changed. Per-entry secret decode reporting/recovery is **unresolved**, not
implemented or declared complete here.

## Implemented

- `HostFieldParser.Inspect` retains known scheme/userinfo alongside a validation
  error. Strict `Parse` wraps it: there is still one host parser. Parser errors
  no longer interpolate the raw credential-bearing host on malformed port/IPv6.
- `ServerConfigValidator.Evaluate` returns normalized Config, runtime-only
  Password, notice metadata and Error. Non-empty explicit credential fields win.
  `Validate` remains the strict legacy wrapper. The parser/validator local
  refactor is exercised by the existing suite plus new mirrored input tables.
- The editor evaluates live host/port/user/password/TLS changes without mutating
  controls. Warning composition uses effective TLS/password even on incomplete
  forms. Valid Host LostFocus and Test/Save normalize visible host/port/TLS/user/
  password with a reentrancy guard. Consumed userinfo is removed; clearing the
  password afterward does not reimport it. Test/Save consume the same effective
  result, with the existing secret-first write order unchanged.
- Origin/adjustment notices survive normalization and password/TLS edits. New
  host input clears previous import history. Credential notices distinguish
  imports from explicit-field precedence. Port notices use `L10n.Format` with
  metadata instead of passing already-interpolated English through `L10n.S`.
  All added notice keys have Simplified Chinese translations.
- HEAD+303 preserves HEAD. `CanFollowRedirect` exposes the existing body policy
  to pure tests: same-origin body allowed; cross-origin replay blocked; rewritten
  POST 301/302/303 has no body. Cross-origin Basic stays absent, including after
  a challenge or a subsequent return hop. Cookie handling is unchanged.
- HTTPS TLS/EOF failures now include the plain-HTTP troubleshooting alternative;
  HTTP/non-TLS errors do not spuriously get that downgrade hint.
- Specs/README now agree on fail-closed auth, method/body rules, client-specific
  refused-3xx classification, preserving an older `.corrupt` copy, throwing Set
  vs swallowing deletes, and the unresolved per-entry decode limitation.

## Permanent regression coverage (network-free)

Both `SelfTest.cs` and `CredentialVerificationTests.cs` cover:

- Effective HTTP/HTTPS scheme overrides, host-derived password, explicit
  password, empty password, missing host and invalid port while warning.
- Four user/password precedence combinations, truthful notice keys, port
  parameter metadata, normalized IPv6 fields and clearing after normalization.
- Escaped userinfo with bracketed IPv6 and unbracketed IPv6 userinfo.
- POST 301/302/303 vs 307/308, HEAD+303, PUT+303/301, HEAD+302, scheme/host/port
  origin changes, auth/header/body decisions and 401 termination.
- HTTPS/HTTP TLS and EOF hint discrimination, plus ordinary connection refusal.
- Repeated Delete/DeleteAll before successful Set, first damaged backup and
  pre-existing older backup, and best-effort backup failure.

The old xUnit typed-username assertion now expects the truthful password-only
import notice. The old selftest mislabeled GET+302 check now actually tests
POST+303. No socket calls were added to either suite.

## Executed gates

Linux x64, SDK 8.0.427, no usable ICU. Commands:

```bash
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
~/.dotnet/dotnet build -c Release -t:Rebuild
~/.dotnet/dotnet build -c Release
~/.dotnet/dotnet build src/PTMonitor.Cli -c Release \
  -p:InvariantGlobalization=true --artifacts-path /tmp/credential-followup-artifacts
~/.dotnet/dotnet /tmp/credential-followup-artifacts/bin/PTMonitor.Cli/release/PTMonitor.Cli.dll --selftest
~/.dotnet/dotnet test tests/PTMonitor.Core.Tests -c Release \
  -p:InvariantGlobalization=true --artifacts-path /tmp/credential-followup-artifacts
git diff --check
```

Results on the final code:

- Shipping Release build **passed**. Clean rebuild: **4 existing warnings,
  0 errors** (CS8602 MonitorStore:306; two CA1416 SecretStore:144; AVLN3001 editor
  parameterized constructor). No new warning introduced.
- Isolated CLI **557 checks passed**.
- Isolated xUnit **116 passed, 0 failed, 0 skipped**.
- `git diff --check` passed.
- Shipping CLI runtimeconfig explicitly still has
  `System.Globalization.Invariant: false`; no source/globalization/dependency
  changes were made to accommodate Linux. Runnable CLI/testhost outputs live
  under `/tmp`. Stock non-invariant runtime runs are not claimed to work on this
  ICU-less box (the baseline records their startup failure).
- Initial implementation build caught XAML handler argument types; handlers
  were corrected to RoutedEventArgs. The initial xUnit run caught the obsolete
  combined-user/password notice assertion; it was updated to the approved
  precedence contract, then all gates were rerun.

Logs: `/tmp/credential-followup-{build,final-build,localbuild,selftest,tests}.log`.

## Actual editor, temporary headless harness

Built `/tmp/credential-followup-ui/Ui.csproj` with a reference to the actual App
project and the already cached Avalonia.Headless 11.3.2 package. This dependency
is **only in `/tmp`**, not in any repository project. A minimal Avalonia
Application hosts a real `ServerEditorWindow(null)`; it does not initialize the
AppServices singleton or touch user settings/secrets.

```bash
~/.dotnet/dotnet build /tmp/credential-followup-ui/Ui.csproj -c Release \
  -p:InvariantGlobalization=true --artifacts-path /tmp/credential-followup-ui-artifacts
~/.dotnet/dotnet /tmp/credential-followup-ui-artifacts/bin/Ui/release/Ui.dll
```

**32 checks passed**, in English and Chinese: default HTTPS, warning before
normalization, no keystroke mutation, LostFocus visible endpoint/credential
prefill, localized numeric port, persistent notices, clearing the imported
password, TLS edits, explicit precedence, IPv6 and invalid-form warning.
The harness changes real controls, drains dispatcher jobs and raises Host
LostFocus; it does **not** exercise actual Test network calls or Save disk writes.
Test/Save sharing the normalization result and secret-first abort were verified
by source inspection. It is not a Windows rendering/scaling/trust/DPAPI check.
Source and logs: `/tmp/credential-followup-ui/Program.cs`,
`/tmp/credential-followup-ui{,-build}.log`.

## Temporary loopback transport probes

A separate `/tmp/credential-followup-network/Probe.csproj` references current
Core. It used the baseline's temporary Python loopback listeners/certificate,
without changing that baseline report or its prior result logs. Synthetic
credentials only; all listeners stopped afterward.

**9 checks passed**:

- Same-origin redirect: 200, Basic and synthetic cookie retained.
- Cross-port and HTTPS→HTTP Basic challenge: final 401. Request capture has
  exactly two unauthenticated target requests, one per probe, with no retry.
- Same-origin POST 307 body replay: 200.
- Cross-origin and downgrade POST 307: returned 307, no body replay.
- Cross-origin POST 302: 200 after rewritten GET without body.
- Real HTTPS-to-plaintext connection: TLS failure includes `uncheck "Use HTTPS"`.

Logs/capture: `/tmp/credential-followup-network.log`,
`/tmp/credential-followup-network/events.json`; source is in that directory.
These external probes are supplemental evidence, not permanent suite coverage.

## qBittorrent synthetic-server checks

Ran `scripts/fake-qbittorrent.mjs` on loopback port 18081 in **both WEBAPI=4.6
and WEBAPI=5.2** modes, using the isolated CLI `--probe` with synthetic credentials.
For each mode: correct password exited 0; wrong-password attempts 1–5 exited 1
with Auth; attempt 6 exited 1 with Banned. Listeners stopped afterward.
Logs: `/tmp/credential-followup-qb-{4.6,5.2}-{success,bad-N,server}.log`.

## Remaining evidence / unresolved work

- Windows DPAPI success/ACL/write failure and wrong-profile/damaged ciphertext
  checks remain pending. Valid JSON with an undecodable entry still remains Ok
  and returns null; per-entry reporting and recovery need an explicit design.
- Windows actual Test/Save, add/edit/restart, trust-store and certificate swap
  display, Preferences banner, row deletion, English/Chinese layout at 100%/150%
  scaling remain pending. Headless control assertions do not establish them.
- Normal-globalization execution remains pending on Windows/ICU-equipped host.
- No parent finding is closed solely because these Linux suites are green.

## Independent full-scope check-agent review — 2026-11-02

Reviewed every tracked diff and all three new files on
`bugfix/credential-tls-verification`, independently of the implementation report.
Loaded the task's check manifest/references, PRD/design/plan, both layer indexes
and quality guides, parser/transport/error/persistence/logging/localization/UI
contracts, and both verification reports. Traced the editor's live events,
LostFocus and shared Test/Save validation into effective form state, client
construction, redirect processing and secret-first save ordering.

### Findings and direct fixes

**Low — unchanged GET content was mistaken for a rewritten POST body.**
`HttpTransport.CanFollowRedirect` and `BuildRedirectRequest` tested whether the
next method was GET, not whether the method had changed. A same-origin GET with
content followed through 307/308 therefore silently lost its body. The original
new method table did not contain GET-with-content cases. Current qBittorrent and
Transmission calls do not send GET bodies, so this was a transport-contract edge
case, not a demonstrated outage in either client.

Fixed both decisions to use method equality: unchanged methods retain content
and cannot replay it across origins; a method rewritten to GET discards content.
Added mirrored GET 301/302/303/307/308 rows and byte-for-byte replay assertions.
The original POST/HEAD/PUT, origin, authorization and challenge rows remain.
Recorded this distinction in the backend transport spec. No credential retry,
new dependency, persistence change or unrelated refactor was introduced.

Repository files changed by this independent review:

- `src/PTMonitor.Core/Client/HttpTransport.cs`
- `src/PTMonitor.Core/Diagnostics/SelfTest.cs`
- `tests/PTMonitor.Core.Tests/CredentialVerificationTests.cs`
- `.trellis/spec/backend/client-and-parser-rules.md`
- This appended review section.

**No additional in-scope defects remain from this review.** In particular:

- Effective TLS/password drives warnings on valid and incomplete forms; explicit
  credentials win. Normalization removes consumed userinfo and does not refill
  a subsequently cleared password. Host changes invalidate prior notices;
  password/TLS changes retain normalization history. Numeric port notices and
  credential notices have English/Chinese handling.
- Test and Save share the same normalization result; save still writes the
  secret first and returns before either settings mutator on failure. No new
  password field, serialization or logging was introduced by the follow-up.
- Cross-origin Basic is removed, never restored by a return hop or a 401;
  cookies remain handler-managed. POST rewrite/body refusal and HEAD+303 agree
  with the approved policy. HTTPS TLS/EOF guidance points to the correct toggle.
- Changes to secret-backup documentation match the unchanged implementation.
  Per-entry decode recovery is still explicitly unresolved, not fixed here.

### Independently executed verification after the fix

Environment remains Linux x64, SDK 8.0.427, no usable ICU. All commands used
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`; only isolated temporary builds passed
`-p:InvariantGlobalization=true`:

```bash
~/.dotnet/dotnet build -c Release -t:Rebuild
~/.dotnet/dotnet build src/PTMonitor.Cli -c Release \
  -p:InvariantGlobalization=true --artifacts-path /tmp/credential-independent-artifacts
~/.dotnet/dotnet /tmp/credential-independent-artifacts/bin/PTMonitor.Cli/release/PTMonitor.Cli.dll --selftest
~/.dotnet/dotnet test tests/PTMonitor.Core.Tests -c Release \
  -p:InvariantGlobalization=true --artifacts-path /tmp/credential-independent-artifacts
git diff --check
```

- Shipping clean Release build: **passed**, four pre-existing warnings (CS8602,
  two CA1416, AVLN3001), zero errors. Source globalization configuration unchanged.
- Isolated selftest: **642 checks passed**.
- Isolated xUnit: **121 passed, zero failed/skipped**. The isolated resource-locale
  NETSDK1188 warnings remain environment-specific, as in the baseline.
- `git diff --check`: passed.
- Baseline `verification.md` SHA-256 before and after review is identical:
  `1366a185356af2d96fe86a42ad2d03add1c32d0f4a23957fa75a61e601ed9e5b`.

Logs: `/tmp/credential-independent-{build,localbuild,selftest,tests}.log`.

**Headless real editor:** first rebuilt and reran the existing 32-check harness,
then copied it to `/tmp/credential-independent-ui` and extended it without any
repository dependency changes. **40 checks passed**, across English/Chinese.
Additional checks invoke the actual shared private `Validate` boundary without
LostFocus, assert returned and visible normalized values, verify a cleared
password remains empty there, and verify a new host removes old origin notices.
This does not invoke the full async Test handler or Save disk-write handler.

```bash
~/.dotnet/dotnet build /tmp/credential-independent-ui/Ui.csproj -c Release \
  -p:InvariantGlobalization=true --artifacts-path /tmp/credential-independent-ui-artifacts
~/.dotnet/dotnet /tmp/credential-independent-ui-artifacts/bin/Ui/release/Ui.dll
```

Sources/logs: `/tmp/credential-independent-ui/{Ui.csproj,Program.cs}` and
`/tmp/credential-independent-ui{,-build}.log`.

**Loopback transport:** copied the earlier probe/listeners into
`/tmp/credential-independent-network`, using separate ports/capture files; built
with `--artifacts-path /tmp/credential-independent-network-artifacts` and the
invariant property. **12 checks passed** against current Core. Rechecked
same-origin Basic/cookie retention, cross-port and HTTPS→HTTP 401 with exactly
one unauthenticated target request per probe and no retry, POST replay/refusal,
POST→GET, and real HTTPS-to-plaintext guidance. Added GET-body same-origin replay
and cross-origin refusal; request capture confirms the synthetic GET bytes reach
only the same-origin echo. All listeners were stopped. Sources and `events.json`
are in that temporary directory; logs use `/tmp/credential-independent-network*.log`.

**qBittorrent stub:** reran `scripts/fake-qbittorrent.mjs` on loopback port 18082
with both `WEBAPI=4.6` and `WEBAPI=5.2`, using the isolated CLI. Each mode passed
correct-credential exit 0, five wrong-credential Auth failures, then Banned on
the sixth attempt. Logs: `/tmp/credential-independent-qb-{4.6,5.2}-*.log`.
All credentials were synthetic; listeners were stopped after each mode.

### Remaining blockers / limits

Windows DPAPI/ACL and actual Test/Save/restart, native certificate trust and
certificate-swap display, Preferences corruption/deletion rendering, layout at
100%/150%, and normal-globalization execution remain pending. Per-entry secret
decode reporting/recovery still requires its own agreed design. Headless Linux
control tests and loopback probes do not close those gates. No AC boxes, task or
parent closure, commits, pushes, PRs or archival were performed by this review.
