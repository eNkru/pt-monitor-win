# Design — release-chain hardening

Three independent deliverables, two of them in `.github/workflows/` and one in
`Core/Diagnostics`. They share no code, so they can land in any order; the only
ordering constraint is that the job split (finding 9) must exist before the
attestation (finding 8) can get its scoped permissions.

---

## 1. Pinning (finding 8, part 1)

**Decision:** pin to the commit currently behind the major tag, with the version
in a trailing comment. Not to a specific release tag — the major tag's SHA is
what the workflow runs today, so pinning it changes nothing about behaviour.

```yaml
- uses: actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5
```

Resolution procedure (run at implementation time, not copied from this doc):

```bash
for spec in actions/checkout@v5 actions/setup-dotnet@v4 \
            actions/upload-artifact@v4 actions/download-artifact@v4 \
            actions/attest-build-provenance@v4; do
  repo=${spec%@*}; tag=${spec#*@}
  sha=$(gh api "repos/$repo/git/ref/tags/$tag" --jq .object.sha)
  type=$(gh api "repos/$repo/git/ref/tags/$tag" --jq .object.type)
  # annotated tags point at a tag object: deref once
  [ "$type" = tag ] && sha=$(gh api "repos/$repo/git/tags/$sha" --jq .object.sha)
  echo "$repo@$tag -> $sha"
done
```

`actions/download-artifact` is new: the job split needs it (see §3). Everything
else keeps its current major.

---

## 2. Checksums and attestation (finding 8, parts 2–4)

### `SHA256SUMS.txt`

Format is `sha256sum`-compatible — `<64 lowercase hex><two spaces><basename>` —
so both `sha256sum -c SHA256SUMS.txt` on Linux and
`Get-FileHash`-based verification on Windows work:

```
9f2c…  PTMonitor-1.0.0-win-x64.zip
1a4b…  PTMonitor-1.0.0-win-arm64.zip
```

Two producers, deliberately:

| Where | Why |
|---|---|
| `scripts/build.ps1` (after the zip step) | a self-built binary must be comparable with a release (PRD req 4); also covers local runs with no CI |
| the release job, recomputed from the downloaded artifacts | guarantees the published sums describe the bytes actually uploaded, not a file left over in a workspace |

`build.ps1` writes `artifacts/SHA256SUMS.txt` by rewriting every
`artifacts/PTMonitor-*-<rid>.zip` it finds, sorted by name, so running it twice
for the two RIDs converges instead of appending duplicates.

### Attestation

`actions/attest-build-provenance` in the release job, after the artifacts are
downloaded and before the release is touched:

```yaml
- uses: actions/attest-build-provenance@<sha> # v4
  with:
    subject-path: artifacts/*.zip
```

Needs `id-token: write` + `attestations: write` on that job only. The repo is
public, so no plan restriction applies. Verification, documented in README:

```bash
gh attestation verify PTMonitor-1.0.0-win-x64.zip --repo eNkru/pt-monitor-win
sha256sum -c SHA256SUMS.txt
```

**Failure mode:** if attestation fails, the job fails before `gh release create`,
so an unattested release is never published. That is the same "gates before
publishing" ordering the workflows already use.

---

## 3. Job split and permissions (finding 9)

The current shape is one job that builds, packages and publishes, which forces
`contents: write` onto the compiler. Split it:

### `build.yml`

```
on: push(main, v*), pull_request, workflow_dispatch
permissions: contents: read            # workflow level

gate      : build + --selftest + dotnet test                    (all events)
package   : build.ps1 -SkipGates x2, SHA256SUMS.txt, upload     (if != pull_request)
release   : download, attest, gh release create                 (if tag)
            permissions: contents: write, id-token: write, attestations: write
```

- `gate` is the only job a `pull_request` runs. Fork PRs get `contents: read`,
  no secrets beyond the default token, and no release path.
- `package` uploads the two zips, the two exes and `SHA256SUMS.txt` as workflow
  artifacts. The zips must travel through artifacts now that `release` is a
  different job — the release step no longer has the package workspace.
- `release` re-checks the sums it downloaded (`Get-FileHash` against
  `SHA256SUMS.txt`) before attesting. A corrupted artifact round-trip fails
  loudly instead of publishing a mismatched pair.

### `branch-release.yml`

```
on: push(** !main), delete, workflow_dispatch
permissions: contents: read

gate      : build + --selftest + dotnet test
publish   : build.ps1 -SkipGates x2, SHA256SUMS.txt, tag move, release upsert,
            attest
            permissions: contents: write, id-token: write, attestations: write
cleanup   : unchanged
            permissions: contents: write   # release + tag delete
```

The tag move (`git tag -f` + `git push --force`) lives in `publish`, so that job
needs a `checkout` of the same commit (`ref: ${{ github.sha }}`) with the default
`persist-credentials: true` — the token there has `contents: write`. `cleanup`
never checks out.

**Compatibility with the existing contract** (`ci-pipeline.md` rules 1, 2, 3, 8):
gates still run once, before packaging, in both files; `-SkipGates` stays legal
because `package`/`publish` `needs: gate`; the two files still duplicate the gate
commands and artifact names, so rule 1 keeps applying.

### What does *not* change

- Trigger semantics: `main` still artifact-only, `v*` still the only stable
  release, branch lane still excludes `main` and never sees tags.
- Version derivation, slug derivation, concurrency group, stale-asset cleanup,
  `--prerelease --latest=false`.

---

## 4. Logging (finding 10)

### Module

New `src/PTMonitor.Core/Diagnostics/Log.cs` — no package, no interface, no
DI. `Core` may not print (`logging-guidelines.md`), so this is a file sink, not
a console sink.

```csharp
public static class Log
{
    public static void Initialize(string path, long maxBytes = 512 * 1024);
    public static void Info(string area, string message);
    public static void Warn(string area, string message);
    public static void Error(string area, string message);
    public static string Redact(string text);      // pure, testable
    public static void Shutdown();
}
```

- **Before `Initialize`, every call is a no-op** — that keeps `Core` tests and
  the selftest free of file side effects and lets the CLI stay silent.
- **Format:** `2026-11-01T18:59:04.123Z [warn] poll: …` — UTC ISO-8601, level,
  area, message. One line per event, never multi-line (stack traces are joined
  with ` | `).
- **Rotation:** before appending, if the file exceeds `maxBytes`, move it to
  `ptmonitor.log.1` (overwriting the previous `.1`) and start a fresh file. Two
  files maximum, ~1 MiB ceiling. No timestamps in rotated names — one backup is
  the whole policy.
- **Write path:** open-append-flush-close per line under a `lock`. Volume is
  events only (a handful per session), so the syscall cost is irrelevant and the
  file survives a hard kill.
- **Never throws:** every write is inside `try { … } catch { /* cosmetic */ }`
  with a comment, matching the tray-icon precedent. An unwritable
  `%APPDATA%` must not break polling.

### Redaction

One function, applied to every message at write time, so a careless call site
cannot leak:

| Pattern | Becomes |
|---|---|
| `password=…`, `passwd=`, `pwd=`, `pass=` (form or query, `&`-delimited) | `password=***` |
| `Authorization: Basic/Bearer <token>` | `Authorization: Basic ***` |
| `Set-Cookie:` / `Cookie:` values, `SID=…` | `…=***` |
| `scheme://user:pass@host` userinfo | `scheme://***@host` |
| bare `user:pass@host` userinfo | `user:***@host` |

Documented in `logging-guidelines.md` as the contract, with the note that the
primary rule is still "do not pass secrets to `Log`" — redaction is the net, not
the design.

### Call sites — event only

| Where | Event |
|---|---|
| `App/Program.cs` | `Log.Initialize(AppPaths.LogFile)`; one line: version, OS, data dir, secret-store state |
| `App/Program.cs` | `AppDomain.CurrentDomain.UnhandledException` and `Dispatcher.UIThread.UnhandledException` handlers: log the exception **redacted**, and do **not** set `Handled` — behaviour stays "terminate", only the record is new |
| `MonitorStore.PollLoopAsync` | failure **transition** only: log when the previous state was `Online` or the `FailureKind` changed. A steady failure logs once, not 1800×/hour |
| `MonitorStore.Classify` path | the "stored password could not be read" and `Unreadable` cases (already user-visible; now also on disk) |
| `DpapiSecretStore` | `Unreadable` detection, `.corrupt` preservation, `Persist` throw |
| `SettingsStore` | atomic-write failure |
| `App/Services/AppServices` (cert callback) | self-signed certificate accepted, with the SHA-256 thumbprint |
| `ServerEditorWindow.OnSave` | secret write failure that aborted the save |

Explicitly **not** logged: per-poll success, per-poll failure repeats, passwords,
payload bodies, `SID`.

The unhandled-exception handler is the piece that makes the next tray-class crash
diagnosable instead of silent — finding 10 and the tray task are the same
incident from two sides.

### Privacy / README

`README.md` "Data, settings and secrets" gains a `ptmonitor.log` row (contents,
cap, that it holds no secrets), and the troubleshooting line "the app writes
nothing by default" is replaced with the real behaviour plus the CLI probe.

---

## 5. Validation

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release --no-build -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release --no-build
python3 -c "import yaml,sys;[yaml.safe_load(open(f)) for f in sys.argv[1:]]" .github/workflows/*.yml
```

`actionlint` and `pwsh` are **not installed on this box** (checked), so:

- YAML is validated by parse only; the CI lint step is the real gate.
- Every changed pwsh block is reviewed by reading, and the *logic* of the
  release/cleanup steps is left untouched where possible so the review surface
  stays small. New pwsh is limited to the checksum block, which is three lines
  of `Get-FileHash` + `-f` formatting.
- The one behavioural risk this leaves is a YAML indentation or `if:`
  expression mistake, which fails the next push, not a user.

The app cannot be run on Linux, so log behaviour is verified through the
selftest and xUnit (redaction table, rotation, unwritable path, no-op before
`Initialize`), plus one manual read of the call sites against this document.

---

## 6. Rollback

| Change | Rollback |
|---|---|
| Pinning | revert the `uses:` lines only — no other edit depends on them |
| Checksums + attestation | drop the `attest` step and the `SHA256SUMS.txt` asset; release step otherwise unchanged |
| Job split | revert the two workflow files together — a half-split leaves `release` without artifacts |
| Logging | `Log.Initialize` removal makes every call a no-op; the call sites can stay. Fully reverting means deleting `Log.cs` + call sites per area, independent of CI |

---

## 7. Decisions taken (not open)

- **Log is always on** (decided 2026-11-01): a log enabled after the
  fact cannot diagnose a crash that already happened. Size-capped at 512 KiB ×2
  and event-only, so the cost is bounded and the privacy claim stays honest once
  README says what it writes.
- **No log level configuration.** `Info`/`Warn`/`Error` exist for line shape, not
  for filtering; there is nothing to configure.
- **`AppPaths` stays in `SecretStore.cs`.** Moving it is unrelated churn and
  would touch every file that reads a path.
