# CI & Release Pipelines

> Two Windows workflows. One contract. This file says how to change them without
> publishing an ungated, unchecksummed or unattested binary.

---

## The two lanes

| Lane | File | Trigger | Jobs | Output |
|---|---|---|---|---|
| Stable | `build.yml` | `main` push, `v*` tag, `pull_request`, dispatch | `gate` → `package` → `release` | PR: gate only · `main`/dispatch: artifacts · tag: release |
| Branch | `branch-release.yml` | any other branch push, branch delete, dispatch | `gate` → `publish`; `cleanup` | rolling prerelease per branch; delete cleanup |

```
build.yml
  gate     build + --selftest + dotnet test            every event (PRs included)
  package  build.ps1 -SkipGates x2 + SHA256SUMS.txt    event != pull_request
  release  download → verify sums → attest → release   tag pushes only
           permissions: contents: write, id-token: write, attestations: write

branch-release.yml
  gate     build + --selftest + dotnet test            event != delete
  publish  build.ps1 -SkipGates x2 + SHA256SUMS.txt,
           verify sums → attest → tag move → upsert    event != delete
           permissions: contents: write, id-token: write, attestations: write
  cleanup  release + tag delete                        branch delete events
           permissions: contents: write
```

Both run the same gates in the same order and package both architectures through
`scripts/build.ps1 -SkipGates`. A `v*` tag is the only stable-release path.

---

## Rules

1. **A new gate lands in both files in the same commit.** `build.yml` and
   `branch-release.yml` duplicate the gate commands, the two `-SkipGates` package
   calls and the artifact names on purpose — a branch release is a published
   binary, so it must pass everything `main` passes. Both headers carry the
   "keep in sync" note; if you add a third lane, copy them too.
2. **Gates run once, before packaging.** The `gate` job is the only place
   build + `--selftest` + `dotnet test` run, and `package`/`publish` `needs: gate`
   — that dependency is what makes `-SkipGates` legal. Never add a `-SkipGates`
   call to a job that does not depend on a green gate.
3. **Never build a release from a ref you did not just gate.** The release step
   is the last step of the last job and only runs after both package steps.
4. **Refs in tags are sanitised, never raw.** Branch names carry `/`, spaces and
   `$`; the slug is lowercase `[a-z0-9-]` plus a digest of the raw ref (sanitising
   is lossy: `feature/x` and `feature-x` collide without it).
5. **Anything interpolated into a pwsh string goes through `env:`.** A branch
   name containing `$`, a backtick or `"` otherwise expands or breaks the parse.
6. **`--target` is create-only.** GitHub ignores `target_commitish` for a tag
   that already exists; move rolling tags with `git tag -f` + `git push --force`.
   That push needs a checkout of the released commit with the **default**
   persisted credentials, which is why the tag move lives in the job that holds
   `contents: write` (in `branch-release.yml`, the `publish` job checks out
   `ref: ${{ github.sha }}`).
7. **Derived values used by more than one job are duplicated verbatim**, not
   re-invented (the cleanup job re-derives the slug with the same three lines;
   the checksum-verification block appears in both workflows character for
   character).
8. **`main` stays out of the branch lane** (`branches: ['**', '!main']`) and a
   `tags:` filter must never be added to it, or tag pushes would double-publish.
   Adding `pull_request` to `build.yml` does not change this: `build.yml`'s PR
   run only executes `gate`.
9. **Asset names that embed a version are never replaced by `--clobber`** — the
   name changes with every commit, so a rolling release must delete the previous
   assets explicitly or it grows by two zips + two installers per push. The one
   asset whose name is stable, `SHA256SUMS.txt`, belongs in the keep-list so the
   stale-asset sweep does not delete it.
10. **Every `uses:` is pinned to a full 40-character commit SHA** with the
    human-readable release in a trailing comment:

    ```yaml
    - uses: actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5
    ```

    A major tag is mutable — whoever controls the action can repoint it at new
    code that then runs with this repository's token. Re-resolve before bumping
    (annotated tags point at a tag object, so deref once):

    ```bash
    for spec in actions/checkout@v5 actions/setup-dotnet@v4 \
                actions/upload-artifact@v4 actions/download-artifact@v4 \
                actions/attest-build-provenance@v4; do
      repo=${spec%@*}; tag=${spec#*@}
      sha=$(gh api "repos/$repo/git/ref/tags/$tag" --jq .object.sha)
      type=$(gh api "repos/$repo/git/ref/tags/$tag" --jq .object.type)
      [ "$type" = tag ] && sha=$(gh api "repos/$repo/git/tags/$sha" --jq .object.sha)
      echo "$repo@$tag -> $sha"
    done
    ```

    No `@vN` may survive, in either file.
11. **`permissions:` is `contents: read` at workflow level.** `contents: write`
    exists only on the job that creates, edits or deletes a release or tag; an
    attesting job additionally gets `id-token: write` and `attestations: write`
    and nothing else. A job that only compiles, tests or packages must not hold
    write access — that is the whole point of the `gate`/`package` split in
    `build.yml`.

    One deliberate exception: `branch-release.yml`'s `publish` job both packages
    and publishes, because the tag move (`git tag -f` + `git push --force`) needs
    a checkout carrying that job's write token (rule 6). Splitting it further
    would mean moving the tag from a job with no workspace. A new lane should
    prefer the `build.yml` shape: package read-only, publish from a downloaded
    artifact.
12. **The `pull_request` gate runs the contract and stops.** `build.yml` on a
    `pull_request` event runs `gate` only: `package` is guarded with
    `github.event_name != 'pull_request'` and `release` additionally with
    `startsWith(github.ref, 'refs/tags/v')` plus `needs: package`, so a PR (a
    fork PR especially, whose token is read-only) can never reach a release step.
    It does **not** replace `branch-release.yml`: a same-repo branch push still
    publishes a rolling prerelease, and only a fork PR is invisible to that lane.
13. **`SHA256SUMS.txt` is `sha256sum`-compatible**: `<64 lowercase hex><two
    spaces><basename>`, one line per published artifact (two zips + two
    installers, four lines), sorted by name. Two producers, both required:

    | Producer | Why |
    |---|---|
    | `scripts/build.ps1` / `scripts/build.sh` (after the zip step) | a self-built binary must be comparable with a released one; also covers local runs with no CI. Rewrites the file from every `artifacts/PTMonitor-*.zip` and `artifacts/PTMonitor-*-setup.exe` it can see, so running it once per `-Rid` converges instead of appending. `build.sh` never builds an installer, so its rewrite must keep setup-exe lines an earlier Windows run wrote |
    | the `release`/`publish` job | re-derives the sums from the bytes that job is about to ship, so a format slip or a workspace leftover cannot reach the release. In `build.yml` the artifacts arrive through the `PTMonitor-release` artifact, so the round-trip is a real re-verification; in `branch-release.yml` they are the files just built in the same job, so the block catches format slips and a stale extra file rather than a corrupted transfer |

    The workflow block re-derives every line with `Get-FileHash` and fails on a
    count mismatch (anything but 4), a malformed line, a name outside
    `^PTMonitor-.*(\.zip|-setup\.exe)$`, uppercase hex, a missing artifact or a
    hash disagreement. Two spaces, not one: `sha256sum -c` splits on exactly two
    and a wrong separator silently breaks every downstream check.
14. **Attestation happens before publishing.** `actions/attest-build-provenance`
    with `subject-path` covering `artifacts/*.zip` **and** `artifacts/*-setup.exe`
    (two glob lines) runs after the artifacts are in place (downloaded in
    `release`, freshly built in `publish`) and before the release is created or
    updated — so a failed attestation fails the job while the previous release
    is still intact. **Never publish an unattested binary**, and never reorder
    the attest step after `gh release create`/`gh release upload`.
15. **The installer is a third asset class.** `PTMonitor-<version>-<rid>-setup.exe`
    is published next to the zips and obeys the same rules:

    - built by `scripts/build.ps1` after the zip step, inside the gated package
      job, with the `iscc` the Windows runner image ships — no new action, no
      `choco install`. The script asserts `iscc >= 6.3` and logs its version, so
      a runner image that drops or downgrades Inno Setup fails the package job
      loudly instead of shipping a broken installer. **`iscc` on PATH on the
      runner is a Chocolatey shim, and a shim has no version resource of its
      own** (`FileVersion` reads `0.0.0.0`), so the script resolves the version
      through the shim's `.shim` file (or the `Program Files` install
      directory) before asserting. Never read `VersionInfo` off the PATH entry
      alone: that guards a number the wrapper does not carry and fails the
      package job on a perfectly good compiler;
    - **Windows only.** `scripts/build.sh` never builds one: it prints
      "installers are built on Windows only (CI or a Windows box)" and still
      lists pre-existing `*-setup.exe` files when it rewrites `SHA256SUMS.txt`,
      so a Linux run cannot drop checksums it is unable to rebuild;
    - in `SHA256SUMS.txt` (four lines), in the attestation `subject-path`, in the
      `PTMonitor-release` artifact upload path, in both release asset lists and
      in the branch lane's `$keep` set — all of those move together, exactly
      like the zips;
    - `scripts/installer/PTMonitor.iss` is **ASCII with no BOM** (Inno reads a
      BOM-less script as the ANSI code page, so a stray non-ASCII byte corrupts
      a message) and its `AppId` GUID is **immutable**: Inno matches upgrades and
      the uninstall key on it, so changing it installs side by side instead of
      upgrading;
    - `UsePreviousPrivileges=yes` is load-bearing, together with
      `PrivilegesRequiredOverridesAllowed=dialog`: an upgrade reuses the detected
      per-user/per-machine mode and never asks again, which is what keeps one
      Installed-apps entry instead of a per-user copy of a per-machine install;
    - rollback is `-SkipInstaller` on the two package calls plus removing the
      setup exes from the sums/attest/release lists — the pipeline returns to
      the two-zip shape without touching product code.

---

## Changing a workflow

```bash
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/<file>.yml'))"
actionlint .github/workflows/*.yml
```

Then diff the changed run blocks against the other workflow. actionlint does not
know pwsh, so a green lint says nothing about quoting — read those blocks. It
also does not know which SHA a `uses:` should have; check that against the
resolution loop in rule 10.

## Local dry run

The release and cleanup steps run under pwsh on Linux (invariant globalization +
a stub `gh` + a local bare `origin`), so tag-moving and quoting mistakes are
catchable without pushing:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 PATH=<stub-bin>:$PATH pwsh -File step.ps1
```

`dotnet build`/`--selftest`/`test` still need a .NET SDK; they cannot be faked.
The checksum step is the exception — it needs no network and no stub, so extract
it, point it at a directory with two dummy zips, two dummy `-setup.exe` files
and a `SHA256SUMS.txt`, and run it under `pwsh`.
