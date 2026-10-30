# Auto-release on new branch push

## Goal

Pushing a non-`main` branch produces an installable, automatically published
prerelease build, so a branch can be tried on a real Windows machine without a
local toolchain. Tag-driven stable releases and artifact-only `main` builds keep
their current behaviour.

## Requirements

### Trigger

- R1. A push to any branch **other than `main`** triggers the branch-release
  pipeline (branch creation included, since a new branch's first push is a push).
- R2. A push to `main` does **not** produce a branch release (stays artifact-only
  in `build.yml`).
- R3. Pushing a `v*` tag keeps its current behaviour: stable GitHub Release from
  `build.yml`, untouched by the new pipeline.
- R4. Manual `workflow_dispatch` can run the branch pipeline for a chosen ref.

### Release shape

- R5. One **rolling** prerelease per branch: repeated pushes to the same branch
  update that release and replace its assets instead of creating a new release
  per commit. Nothing from the previous commit is left behind — the release holds
  two zips, not two per push.
- R6. The release is marked `prerelease` and never becomes the repository's
  "Latest" release.
- R7. Version string is `1.0.0-<slug>-<refhash>.<sha7>`; the tag is
  `branch-build/<slug>-<refhash>`. Slugs are lowercase, `[a-z0-9-]`-only, ≤ 33
  chars, followed by a 6-hex digest of the raw ref, so the tag is a legal git ref
  and two branches that sanitise alike keep separate releases.
- R8. Both Windows architectures (`win-x64`, `win-arm64`) are built and attached.
- R9. Release notes state the branch, the short SHA and the head commit subject,
  plus a short "download the zip for your architecture" line.

### Gates

- R10. A branch release is only created when the standard gates pass:
  `dotnet build -c Release`, `--selftest`, unit tests.
- R11. A failing gate leaves the previous release untouched.

### Hygiene

- R12. Deleting a branch deletes its `branch-build/<slug>-<refhash>` release and
  tag.
- R13. Concurrent pushes to the same branch cancel the older run, so assets
  cannot interleave from two runs.

### Docs

- R14. `README.md` CI section and the workflow header comments describe the new
  lane; no doc claims that releases only come from `v*` tags.

## Non-goals

- No version bumping/tag mutation on `main`.
- No signing, installer, or changelog generation for branch builds.
- No per-PR (`pull_request`) pipeline.

## Acceptance Criteria

- [ ] A push to a feature branch runs build + selftest + tests for both archs and
      ends with a prerelease whose name/tag carry the sanitised branch slug.
- [ ] A second push to the same branch updates the same release: still one
      release, exactly two zips from the new commit, new short SHA in the notes.
- [ ] A push to `main` creates no branch release.
- [ ] `v*` tag still produces a non-prerelease release via `build.yml`.
- [ ] Deleting the branch removes its release and its `branch-build/<slug>-<refhash>`
      tag.
- [ ] A branch name needing sanitising (`feature/Tray Panel Fix`) yields a valid
      tag and version with no spaces or uppercase.
- [ ] `feature/x` and `feature-x` land on two different releases, and deleting
      one branch leaves the other's release intact.
- [ ] A second push moves the tag to the new commit (`git fetch --tags` shows the
      head SHA), not just the assets.
- [ ] Existing `build.yml` gates and packaging are unchanged apart from comments.

## Notes

- Scope is CI only; no product code is touched.
- `VERSION` semantics stay local to the pipeline — no repo file is rewritten by CI.
