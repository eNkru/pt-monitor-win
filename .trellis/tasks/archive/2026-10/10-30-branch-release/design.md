# Design — branch-triggered prereleases

## Boundaries

| Piece | Owner |
|---|---|
| Trigger + ref filtering | new workflow `.github/workflows/branch-release.yml` |
| Version/tag derivation | same workflow, one helper function |
| Build, selftest, tests, packaging, zips | unchanged `scripts/build.ps1` + the gate commands from `build.yml` |
| Stable releases (`v*`) | unchanged `.github/workflows/build.yml` |
| Docs | `README.md` CI section, workflow header comments |

Product code is untouched.

## Why a second workflow file instead of extending `build.yml`

`build.yml` is already a three-lane file (`main` push, `v*` tag, dispatch) with a
tag-gated release step. Adding a fourth lane whose release *name* is dynamic
would push two different release semantics into one `if:`. A separate file keeps
`build.yml`'s contract intact and lets branch builds carry their own
concurrency, permissions and cleanup job.

## Trigger

```yaml
on:
  push:
    branches: ['**', '!main']
  delete:
  workflow_dispatch:
```

- `branches: ['**', '!main']` fires on the first push of a new branch and on every
  later push. Branch creation alone (`on: create`) was rejected: a branch
  created from an old commit and not yet pushed would build code nobody has
  pushed, and re-pushes would never refresh the release.
- `main` is negated so merges stay artifact-only.
- Tags are excluded by the `branches` filter, so `v*` keeps its stable path.
- `delete` is needed for R12; the job body checks
  `github.event.ref_type == 'branch'` so tag deletions are ignored.

## Tag and version shape

- `slug = lowercase(ref_name)` with `[^a-z0-9]+ → '-'`, trimmed, ≤ 33 chars,
  falling back to `branch` when empty, then `-<sha256(ref_name)[0:6]>`.
- tag `branch-build/<slug>-<refhash>`, version `1.0.0-<slug>-<refhash>.<sha7>`.
- `branch-build/` namespacing keeps these tags from ever colliding with `v1.2.3`
  stable tags, and a `/`-tag is a legal ref that `gh release` creates for us.
- Sanitising is mandatory, not cosmetic: git refs reject spaces and `~^:?*[\`,
  and `feature/Tray Panel Fix` is a realistic input.
- The digest is mandatory too: sanitising is lossy, so `feature/x` and
  `feature-x` collapse to one slug, which would otherwise mean one shared
  release, interleaved `--clobber` uploads and a cleanup that deletes the other
  branch's release. `concurrency` cannot help — different refs, different groups.
- The derivation is duplicated in the cleanup job (a `delete` event has no
  `GITHUB_SHA`/build job to inherit it from). Both copies must stay identical.

## Rolling release (R5)

1. `git tag -f "$tag" $sha` + `git push --force origin refs/tags/<tag>` **first**,
   so the ref is right on both the create and the update path.
2. `gh release view "$tag"` to probe existence. A non-`404` failure (auth,
   network, rate limit) throws instead of falling through to a create that would
   mask the cause.
3. Missing → `gh release create "$tag" <zip-x64> <zip-arm64> --target <sha>
   --prerelease --latest=false --title … --notes-file …`.
4. Present → `gh release upload "$tag" <zips> --clobber`, delete every asset that
   is not one of the two just uploaded, then
   `gh release edit --title … --notes-file … --prerelease --latest=false`.

The stale-asset sweep is not housekeeping cosiness: asset names embed the
version, the version embeds the SHA, so `--clobber` never collides with anything
and every push would strand another 2 × ~70 MB on the release (a ten-push branch
= ~1.4 GB). Deletion runs *after* the upload, so a failed upload leaves the
previous pair downloadable.

Step 1 comes first because the update API ignores `target_commitish` for a tag
that already exists, so `--target` alone cannot be trusted: the tag would stay on
the branch's first commit while the notes advertised the newest SHA, and a clone
of the tag would fetch stale code. Moving the ref before publishing also covers a
release deleted by hand while its tag survived (`gh release view` 404s, so create
would otherwise attach to the stale tag) and recreates a tag removed out-of-band.
Failure mode: a run that dies after step 1 leaves the tag on a commit whose
assets were never published — visible, and corrected by the next push.

One release per branch beats one per commit: the Releases page stays readable
and links stay stable. The trade-off is that history lives in the commit log,
not the Releases page. Re-running an **old** run rewinds the release to that
run's commit; nothing pins "newest wins".

## Ordering and failure behaviour

Build both architectures **before** touching the release, then upload both in a
single `gh release upload` call. A gate failure therefore leaves the previous
release and its assets exactly as they were (R11), and the window in which the
two assets can disagree is one upload call.

## Concurrency

```yaml
concurrency:
  group: branch-release-${{ github.event_name == 'delete' && github.event.ref || github.ref_name }}
  cancel-in-progress: true
```

Per-branch group. A newer push cancels the older run, so two runs cannot
interleave `--clobber` uploads for the same tag. Cancelling is safe because the
next run rebuilds and re-uploads everything.

The `delete` arm is load-bearing, not defensive: GitHub sets `GITHUB_REF` to the
**default branch** on `delete` events, so `github.ref_name` alone would collapse
every cleanup into `branch-release-main`. Parallel branch deletions would then
cancel each other and leave releases (and tags) behind, and a cleanup would not
share a group with the in-flight push of the branch it is deleting. `&&` binds
tighter than `||`, so the expression reads "deleted ref on delete, branch on
push".

## Credentials

`permissions: contents: write` on the workflow (release create/upload/delete and
ref movement). `GH_TOKEN: ${{ github.token }}` is scoped to the release step and
the cleanup job only, not the build steps. The tag push relies on the credentials
`actions/checkout` persists on `origin`.

## Cleanup on branch delete

Separate `cleanup` job, gated on `github.event_name == 'delete'` +
`ref_type == 'branch'`. It recomputes the slug **and digest** and runs
`gh release delete "$tag" --cleanup-tag --yes`, tolerating a 404 (branch deleted
without ever being built). Without this the rolling releases leak forever. It
runs on `ubuntu-latest`: it only needs `gh` and string handling, and `pwsh` ships
there, so the derivation stays byte-identical to the build job's.

## Trade-offs accepted

- CI minutes scale with branch pushes, including 2× ~70 MB zips. Accepted: that
  is the point of the feature.
- Each push re-downloads .NET and rebuilds both archs from scratch; no caching
  is added here (matches `build.yml`, keeps the lanes symmetric).
- A force-push to an existing branch keeps the same release (tag moves, assets
  replaced). Desired.
- PRs from forks are not published (push events in the upstream repo only).

## Rollback

Delete `.github/workflows/branch-release.yml` → branch builds stop immediately
and `build.yml` behaviour is unchanged. Existing `branch-build/*` releases stay
until deleted by hand or by branch deletion.
