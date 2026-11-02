# Verification — credential and TLS hardening

## Scope and verdict

Reviewed on **2026-11-02**, against merged `HEAD`
`009c6218d2795d8edebd7e02001df73030e97172`. PR #8 merged as `8503e36`.
The initial working tree and `git diff HEAD` were clean. Reviewed the PR merge
changes and current implementations, including later logging integration.
Loaded `check.jsonl`, all three referenced specs, the task PRD/design/plan,
backend/frontend indexes, client/parser, error-handling and localization rules,
and parent `11-01-security-hardening/prd.md` findings 1–7.

**Do not close the task on the strength of green suites.** The backend has
substantial verified improvements, but the editor does not implement userinfo
prefill, the plaintext warning can miss the effective plaintext credential
send, and cross-origin challenge re-authentication does not exist. Requirements,
design and specs also disagree about redirects and corrupt backups.

This review changed **only this report**. No production/test/spec edits, PRD
checkbox changes, commits, pushes, branch changes or archival. Temporary probes
and local-only invariant-globalization builds live outside the repository.

Evidence labels below:

- **Executed:** exercised the current Core assembly, suite or synthetic server.
- **Inspected:** traced source; did not exercise an Avalonia window.
- **Gap:** observed or source-proven discrepancy, not merely an unrun check.
- **Windows pending:** Windows DPAPI, certificate trust and rendered UI checks
  remain unverified. No Windows machine was used; no GUI was launched.

## Findings, ranked by severity

### 1. High — plaintext warning uses controls, not the credentials/endpoint actually sent

`src/PTMonitor.App/Views/ServerEditorWindow.axaml.cs:109–136` validates into a
separate config, but `RefreshNotices` still passes `TlsToggle.IsChecked` and
`PasswordField.Text` to `EditorNotices.Compose`. `OnTest` and `OnSave`, at
`:173–180` and `:210–216`, instead use the normalized config and host-derived
password fallback.

Reproduction by source trace plus executing those Core inputs:

1. Open Add Server (HTTPS is checked).
2. Paste `http://bob:hunter2@nas:8080/path`; leave the password box empty.
3. Test or Save: validation yields HTTP, user `bob`, password `hunter2`.
4. Warning composition receives `useTls=true`, `password=""` and omits the
   plaintext warning. Even with the checkbox manually off, a host-only password
   is omitted from warning composition. With an explicit password and a pasted
   `http://` scheme, a still-checked HTTPS control also suppresses the warning.

Only the scheme/userinfo adjustment notices appear after validation; they do
not substitute for the promised password-in-clear warning. Conversely a pasted
`https://` with an unchecked control can produce an inaccurate plaintext warning.
`OnNoticeInputChanged` calls `RefreshNotices()` without existing validator
notices, so changing the password or TLS toggle erases those notices. Host edits
have no notice-update subscription at all (`:35–36`; XAML host field at `:22`).

**Minimal follow-up:** use one effective parsed form state for Test, Save and
notices; update/normalize the visible controls at a deliberate edit boundary;
refresh notices from that state without dropping relevant validator notices.
Retain the warning for an invalid/incomplete form when the known inputs warrant
it. Add paired Core regression cases and a Windows editor checklist for scheme
and host-password overrides. Parent finding 1 remains partially open.

### 2. Medium — userinfo is consumed, but no visible prefill occurs

`HostFieldParser.cs:38–50,148–180` extracts credentials and correctly keeps an
explicit username. The editor passes them into Test/Save, but `Validate()` never
assigns `result.Config.Host`, `.Port`, `.UseTls`, `.Username`, or
`PasswordFromHost` to controls. The only `UserField.Text`/`PasswordField.Text`
assignments are initial `Load()` values (`ServerEditorWindow.axaml.cs:73–86`).
There is no host-change/lost-focus handler in the XAML either.

Thus AC9's visible host/port/user/password prefill is **not implemented**, even
after Test; the unmasked host field continues to display the pasted password.
The notice also always says both credentials came from the host even when an
explicit username/password wins (`HostFieldParser.cs:164–165`).

**Minimal follow-up:** actually prefill normalized fields while respecting
explicit credential precedence; remove userinfo from the visible host and
preserve its origin notice. Do not persist a password in `ServerConfig`.
Parent finding 5 is improved (credentials no longer discarded), not fully closed.

### 3. Medium — redirect challenge re-authentication is promised but absent

`HttpTransport.cs:44–54` configures neither `Credentials` nor any equivalent
challenge handler. `SendFollowingRedirectsAsync` (`:164–205`) handles only 3xx;
a 401 returns immediately. `BuildRedirectRequest` (`:235–260`) removes Basic on
another origin and never restores it following a challenge.

**Executed:** same-origin GET redirect retained Basic and the new session
cookie, returning 200 without a 401. Cross-port HTTP and HTTPS→HTTP redirects
sent exactly one unauthenticated request to a target returning
`401 WWW-Authenticate: Basic realm="synthetic"`; both finished at **401**, with
no authenticated retry in the captured target request log.

This is fail-closed for Basic credentials, **not a demonstrated credential leak**.
It nevertheless contradicts AC10, design §7 and
`.trellis/spec/backend/client-and-parser-rules.md:135–138`. Transmission also
turns a final 401 into Auth rather than retrying (`TransmissionClient.cs:179–185`).
The qBittorrent retry re-logs in at the original base; it is not a target Basic
challenge implementation (`QBittorrentClient.cs:277–293`).

**Minimal follow-up requires a policy decision:** either explicitly approve
fail-closed cross-origin auth and correct the acceptance/spec claims, or
implement the narrowly agreed challenge behavior with bounded retry tests.
Do not silently restore arbitrary cross-origin credential disclosure to make a
check pass. PRD requirement 8's handler-only/no-manual-loop prescription is stale;
this review treats revised design §7 as the intended architecture, not a reason
to undo the manual redirect implementation.

Body behavior is independently verified and safer than the old PRD:

- Same-origin POST 307 **does** replay its body and Basic header.
- Cross-origin and HTTPS→HTTP POST 307 return 307 without reaching the target.
- Cross-origin POST 302 becomes GET with no body or Basic header.
- A redirect loop is bounded to 11 total requests (initial + 10 hops).

The design §7 table says a body is not followed on **any** origin, but code
`:187` blocks body replay only across origins. The plan/spec use the latter
interpretation. Reconcile these documents. A refused 3xx is not universally a
ProtocolViolation either: Transmission maps it to ServerError at `:196–198`.

### 4. Medium residual limitation — valid JSON with an unreadable secret remains `Ok`

**Executed:** a valid dictionary containing `plain:invalid-base64!` loads with
`State == Ok`, `Get(id) == null`, and state still `Ok` after Get. An Auth failure
retains its generic message, so the unreadable banner/relabeling never applies.
`SecretStore.cs:70–83,150–158` checks JSON readability but silently catches Decode
failures without updating state. Windows DPAPI decode failures take that same
catch path by inspection; those failures were **not** executed on Windows.

This is not a contradiction of the detailed design's JSON-load distinction or
the persistence spec's statement that unreadable entries return null. It is a
remaining limitation of broad parent finding 4 / PRD "corrupt secret file"
wording: corrupt ciphertext, invalid base64, or another Windows user's protected
values can still silently degrade to no password.

**Minimal follow-up:** explicitly narrow/record this scope, or separately agree
on per-entry decode-failure reporting and recovery semantics. Do not casually
mark an entire partially readable store unreadable without defining what happens
to its other credentials. Add a Windows wrong-profile/ciphertext-damage check.

### 5. Low — the new plain-HTTP TLS hint is guarded by the wrong scheme

`HttpTransport.cs:132–139` appends the "uncheck Use HTTPS" hint only when the
base is **HTTP**. The failing initial connection to a plain-HTTP server is made
with **HTTPS**. Executing HTTPS against the local plaintext listener yielded
only `TLS handshake failed (enable "allow self-signed certificate" if the server
uses one)`, not the intended downgrade hint.

The implementation follows the same mistaken condition in design §3; the plan
and README troubleshooting claim do not establish correct behavior.
**Minimal follow-up:** correct the effective scheme/error condition and add a
pure error-description test. Do not recommend weakening certificate validation
as the sole remedy for a non-TLS endpoint.

### 6. Low — coverage and documentation overstate what is checked

- Selftest `:676–679` labels a check "303 becomes GET" but constructs **302**
  (`HttpStatusCode.Redirect`) with an already-GET request. It cannot detect
  broken method rewriting. xUnit `CoreTests.cs:496–505` asserts only the boolean
  redirect recognition, discarding the returned method.
- Both suites exercise the named parser/notice/cap/classification helpers, but
  not effective editor state. Neither checks cross-origin 401 completion or the
  private body-block decision. Independent probes are evidence, not permanent
  regression coverage.
- The repeated-corrupt-write regression exists in xUnit (`CoreTests.cs:629–650`)
  but not a matching direct selftest scenario. The plan claims unreadable-store
  polling success/auth cases; committed selftest corruption checks at `:629–653`
  test `Classify`, not either poll path. This review exercised both paths outside
  the repository. Username+IPv6 userinfo combinations are also absent from the
  promised parser table.
- `TryGetRedirect` converts HEAD + 303 to GET (`HttpTransport.cs:226–229`),
  confirmed by executing the pure helper. The framework preserves HEAD for 303.
  Current clients use GET/POST, so this is an ancillary compatibility issue, not
  evidence of a current client outage.
- `.trellis/spec/backend/persistence-guidelines.md:109–112` still says backups
  overwrite an older copy, contrary to `SecretStore.cs:200–205` and design §1.
  The probe confirmed existing `.corrupt` bytes win, even from an older event.
- `.trellis/spec/backend/error-handling.md:79` still references `PersistUnsafe`
  and generic silent degradation. It needs synchronization with throwing Set.
- The dynamic port notice uses `L10n.S("Used port 8080 ...")` via the editor,
  while the dictionary key is `Used port %lld ...` (`L10n.cs:52`); it remains
  English in Chinese. This pre-existing localization problem is now part of the
  combined notice surface. New warning/certificate/userinfo/banner strings and
  save-error text do have Chinese entries (`:77–82,114`).

**Minimal follow-up:** add honest method/body/precedence tables in both suites,
mirror the store regressions, correct documentation (after settling redirect
policy), and fix parameterized notice localization without moving App wording
into a new Core dependency. Keep network probes outside the network-free suites.

## Acceptance matrix — all 12 PRD criteria

Paths below are relative to `src/` unless otherwise stated. No PRD boxes have
been ticked by this review.

| AC | Requirement | Evidence and result |
|---|---|---|
| 1 | Add defaults to HTTPS; saved config/endpoint HTTPS | **Inspected + Core execution:** `PTMonitor.App/Views/ServerEditorWindow.axaml.cs:64–75,210–239` sets the new-server toggle true and saves validated config; `PTMonitor.Core/Client/HostFieldParser.cs:95–105,152–178` builds HTTPS and keeps it absent a scheme override. Existing-server TLS is retained at editor `:82`. Default/open/save rendering and restart round-trip remain **Windows pending**. |
| 2 | Explicit `http://nas:8080` wins and shows Disabled HTTPS notice | **Executed:** validator scheme/port behavior passes suites and the independent pasted-URL probe. `HostFieldParser.cs:148–162`; editor `:113–114` displays notices on Test/Save. **Inspected limitation:** the checkbox is not synchronized and host edits alone do not trigger the notice. |
| 3 | Plaintext password warning appears/disappears correctly | **Partial / gap:** pure composition passes (`EditorNotices.cs:25–32`, selftest `:596–605`, xUnit `:508–524`); checkbox/password events are wired. Effective HTTP/host-password overrides suppress it incorrectly and subsequent events drop validator notices; finding 1. Visual rendering not executed. |
| 4 | Insecure successful Test shows accepted leaf SHA-256 | **Executed transport + inspected UI:** `HttpTransport.cs:55–66` callback hash equals the locally generated leaf's SHA-256; `QBittorrentClient.cs:216–220` and `TransmissionClient.cs:118–120` forward callback; editor `:169–189` appends it only after success. Pure formatting passes both suites. Actual successful client Test in the window and certificate replacement display remain **Windows pending**. Current App polling also logs accepted hashes via `PTMonitor.App/Services/AppServices.cs:49–50,99–118` (inspected). |
| 5 | Strict self-signed connection fails with toggle hint | **Executed:** strict loopback TLS failed as Unreachable with the self-signed-toggle hint and no observation callback; insecure mode succeeded. `HttpTransport.cs:55–66,284–291`. Windows trust-store/Schannel behavior remains pending. |
| 6 | Secret failure aborts save, no config change, names failure | **Executed store / inspected save:** Set failures with an existing entry, empty replacement and new entry roll back memory and retain disk; final rename failure also does so. `SecretStore.cs:86–109,171–188`; editor `:219–239` catches before either Settings mutator, reports the exception and returns. No actual editor save was fault-injected; Windows DPAPI/ACL and visible save message remain pending. |
| 7 | Corrupt file Unreadable/null; auth relabeled with long backoff | **Executed for malformed JSON:** `SecretStore.cs:148–168`; `MonitorStore.cs:127–164,187–211,268–281,332–337`. Both live-loop and PollAsync stubs returned Failed with `stored password could not be read`; RetryDelay was 600 seconds. Did not wait 600 seconds. Valid JSON with unreadable entry is a residual scope limitation (finding 4). |
| 8 | Passwordless success Online; delete preserves corrupt copy | **Executed:** both polling paths reached Online with unreadable store; Delete then DeleteAll retained original corrupt bytes; successful Set changed state to Ok. `SecretStore.cs:112–127,196–218`. Single Preferences banner wiring inspected at `PTMonitor.App/Views/PreferencesWindow.axaml.cs:91–96`; row removal calls Delete at `:138–144`. Windows visual banner/row action pending. |
| 9 | Example URL visibly prefills host/port/user/password + notice | **Gap:** Core example returns `nas`, `8080`, `bob`, `hunter2`, HTTP and all applicable notices. Editor never applies it to the visible controls (finding 2). Credential consumption is not prefill. |
| 10 | Same-origin keeps Basic; downgrade re-authenticates; spec agrees | **Partial / gap:** same-origin Basic+cookie succeeded; other-origin/downgrade target received no Basic and returned 401 with no retry. Body replay protection passed for cross-origin 307. Spec promises challenge recovery that code lacks, and design body wording conflicts; finding 3. |
| 11 | Both suites cover every touched pure function | **Partial:** named userinfo, warning, cap-error classifier, certificate formatting, origin/header helpers, and unreadable-auth classifier are exercised. Coverage misses important decision branches and some promised mirrors; mislabeled 303 test is ineffective. See finding 6. No UI/network claim should be inferred from helper-only tests. |
| 12 | Release build, selftest, xUnit pass | **Executed with environment qualification:** shipping Release build succeeds with four warnings; stock runtime CLI selftest and testhost abort for missing ICU. Isolated local invariant builds pass **298 selftest checks / 85 xUnit tests**. Shipping invariant configuration remains false. Windows/normal-globalization runs still pending here. |

## Parent findings 1–7 disposition

| Parent finding | Disposition from this review |
|---|---|
| 1 — default plaintext/no warning | HTTPS default implemented; effective-input warning has a real gap. Do not close. |
| 2 — invisible accept-any TLS | SHA-256 observation executed; success-message and live App log wiring inspected. Accept-any remains deliberate, no pinning. Windows UI/trust checks pending. |
| 3 — secret write swallowed | Throw/rollback executed, including final commit rename failure; secret-first editor abort inspected. Windows DPAPI and actual UI save fault check pending. |
| 4 — corrupt secrets silently lost | Malformed JSON handling, both poll paths, long-backoff classification and backups executed. Per-entry decode failures still silent; distinguish the scoped improvement from blanket corruption detection. |
| 5 — host credentials discarded | Parsing and consumption fixed; visible prefill missing. Do not claim AC9 satisfied. |
| 6 — unbounded responses | Executed at exactly cap and cap+1, unknown-length and gzip-expanded responses. Enforcement/classification passed on this .NET/Linux runtime. |
| 7 — redirect auth/body | Same-origin header preservation and cross-origin body refusal executed; no challenge retry. Needs explicit policy/spec reconciliation, not a claim of completion. |

## Verification commands and results

Environment: Linux x64, .NET SDK **8.0.427** under `~/.dotnet`, no usable ICU.
All .NET commands below ran with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.
`Directory.Build.props:8` explicitly sets `InvariantGlobalization=false`, which
explains why the environment variable alone did not rescue generated apps.

```bash
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
~/.dotnet/dotnet build -c Release
~/.dotnet/dotnet run --project src/PTMonitor.Cli -c Release --no-build -- --selftest
~/.dotnet/dotnet test tests/PTMonitor.Core.Tests -c Release --no-build
```

Results:

- Release build: exit 0, **4 warnings / 0 errors**. CS8602 at
  `MonitorStore.cs:306`; two CA1416 warnings at `SecretStore.cs:144`; AVLN3001
  for `ServerEditorWindow.axaml` lacking a public parameterless constructor.
  These are warnings on the reviewed tree, not all newly introduced by PR #8.
- Stock CLI selftest: exit 134, runtime abort for missing ICU, before checks.
- Stock xUnit: exit 1, testhost aborted for missing ICU, not a test assertion.

Local-only reruns isolated **all outputs under `/tmp`**:

```bash
~/.dotnet/dotnet build src/PTMonitor.Cli -c Release \
  -p:InvariantGlobalization=true --artifacts-path /tmp/credential-review-artifacts
~/.dotnet/dotnet /tmp/credential-review-artifacts/bin/PTMonitor.Cli/release/PTMonitor.Cli.dll --selftest
~/.dotnet/dotnet test tests/PTMonitor.Core.Tests -c Release \
  -p:InvariantGlobalization=true --artifacts-path /tmp/credential-review-artifacts
```

- Selftest: exit 0, **298 checks passed**.
- xUnit: exit 0, **85 passed, 0 failed, 0 skipped**.
- Isolated build also emits environment-related NETSDK1188 package-resource
  locale warnings under invariant globalization. No globalization setting was
  baked into shipping outputs or changed in source.
- No separate lint/typecheck gate is defined by the loaded project guidelines;
  the Release compiler and the two suites were used.

Logs: `/tmp/credential-review-build.log`, `credential-review-selftest.log`,
`credential-review-tests.log`, `credential-review-localbuild.log`,
`credential-review-selftest-local.log`, `credential-review-tests-local.log`.

### Independent loopback / store probes

Harness source: `/tmp/credential-review-harness/{Review.csproj,Program.cs,servers.py}`.
It references the current Core project, uses only synthetic credentials, temp
secret/settings files and local HTTP/TLS listeners. Generated self-signed cert
and key stay in that temporary directory. Server request captures are in
`events.json`; results are in `results.log`. Listeners were stopped afterward.

Build/run shape (ports discovered dynamically from `ports.json`):

```bash
openssl req -x509 -newkey rsa:2048 -nodes \
  -keyout /tmp/credential-review-harness/key.pem \
  -out /tmp/credential-review-harness/cert.pem -days 1 -subj /CN=localhost
~/.dotnet/dotnet build /tmp/credential-review-harness/Review.csproj -c Release \
  --artifacts-path /tmp/credential-review-probe-artifacts
python3 /tmp/credential-review-harness/servers.py \
  /tmp/credential-review-harness/cert.pem /tmp/credential-review-harness/key.pem &
# Run while the listeners are alive; stop that Python process afterward.
~/.dotnet/dotnet /tmp/credential-review-probe-artifacts/bin/Review/release/Review.dll
```

Final harness execution exited 0. Initial harness launch-path and PEM-loading
mistakes were corrected **only in `/tmp`** before the final run; they were not
product failures. The harness prints observations rather than hiding unexpected
results behind a success count. Observed results:

| Probe | Result |
|---|---|
| Same-origin 302, Basic + Set-Cookie | 200; redirected GET kept Basic and cookie. |
| Cross-port HTTP 302 → Basic challenge | 401; target saw no Basic, no retry. |
| HTTPS→HTTP 302 → Basic challenge | 401; target saw no Basic, no retry. |
| Same-origin POST 307 | 200; body and Basic replayed. |
| Cross-origin POST 307 | 307; target not contacted. |
| HTTPS→HTTP POST 307 | 307; target not contacted. |
| Cross-origin POST 302 | GET at target, no body, no Basic; 200. |
| Strict self-signed TLS | Unreachable with self-signed hint; no accepted hash. |
| Insecure self-signed TLS | 200; observed SHA-256 exactly matched leaf hash. |
| HTTPS against plaintext server | TLS failure; missing intended uncheck-HTTPS hint. |
| Body 32 MiB + 1 with Content-Length | ProtocolViolation with configured 32 MiB message. |
| Body 32 MiB + 1, close-delimited/unknown length | Same ProtocolViolation. |
| Gzip expanding to 32 MiB + 1 | Same ProtocolViolation after decompression. |
| Body exactly 32 MiB | 200, 33,554,432 bytes. |
| Redirect loop | 302 returned after initial request + 10 hops. |
| Set replacement/empty/new entry with blocked `.tmp` | Each throws; previous entry and disk preserved, state Ok. |
| Set final rename denied after temp write | Throws; previous in-memory entry and original disk preserved; temp was written. |
| Delete / DeleteAll on broken write path | Neither throws. |
| Malformed JSON | Unreadable; Get null. |
| Unreadable + successful PollAsync/live loop | Both Online. |
| Unreadable + Auth PollAsync/live loop | Both Failed with stored-password message; RetryDelay 600. |
| Repeated Delete/DeleteAll before Set | Original damaged backup survives; state remains Unreadable. |
| Successful Set after corruption | State Ok, new credential readable, backup unchanged. |
| Pre-existing `.corrupt` | Older copy retained, not overwritten. |
| Backup destination is a directory | Best-effort backup failure does not prevent successful Set. |
| Valid JSON, invalid base64 entry | State Ok, Get null; no unreadable-auth relabeling. |
| Pasted example URL | Correct normalized values; warning inputs wired as in editor omit plaintext warning. |
| HEAD + 303 pure redirect decision | GET (framework compatibility discrepancy). |

The temporary harness is not a committed regression suite and `/tmp` artifacts
are ephemeral; this report preserves the observations needed to reproduce them.

### qBittorrent stub — both response shapes

Ran `node scripts/fake-qbittorrent.mjs 18080` with `WEBAPI=4.6`, then `WEBAPI=5.2`,
using the isolated CLI above and `--probe qbittorrent --host 127.0.0.1 --port
18080 --user admin --pass adminadmin`.

For **each** shape:

- Correct credentials: exit 0, snapshot returned (cookie handling works).
- Six separate wrong-password probes: attempts 1–5 exit 1 with `auth failed:
  invalid username or password`; attempt 6 exit 1 with `banned`.
- The stub was terminated after each mode. The shell wrapper's final 143 came
  from waiting for the intentionally terminated server, not from a client probe.

Per-probe logs: `/tmp/credential-review-qb-{4.6,5.2}-{success,bad-N}.log` and
matching `-server.log` files. No real torrent server was contacted.

## Remaining Windows/manual gate and minimal next step

Before closing any GUI/Windows acceptance claims:

1. Exercise Add/Edit default HTTPS, explicit HTTP/HTTPS overrides, pasted
   credentials, explicit credentials taking precedence, live notices and notice
   coexistence in **English and 简体中文**, at 100% and 150% scaling.
2. Inject a secret-write failure during real Save for both add and edit. Confirm
   the settings file is byte-for-byte unchanged and the error stays visible.
   Exercise Windows DPAPI under the intended user and unreadable/wrong-profile
   values; no Linux fallback test proves this.
3. Exercise malformed secrets, auth/no-auth polling, Preferences banner, repeated
   deletion and first recovery. Confirm `.corrupt` content, including the
   best-effort failure and pre-existing-backup policy.
4. Test a normal trusted HTTPS server with unchanged settings, strict rejection
   of a self-signed certificate, insecure successful Test with visible SHA-256,
   and a changed certificate. Confirm existing HTTPS installations do not regress.
5. Re-run normal-globalization gates on Windows (or an ICU-equipped machine),
   and retain the runtime-specific cap/redirect results as supplemental evidence.

Suggested next work is a **small, explicitly approved follow-up**, not a broad
rewrite: fix editor effective-state/prefill/notices first; decide and align
redirect policy; fix the TLS hint; add the missing paired regression cases;
then reconcile docs and finish Windows verification. Separately decide whether
per-entry secret decode failures are in scope. The current review does not
approve those changes or mark the task complete.
