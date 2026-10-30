# Implement — branch-triggered prereleases

## Checklist

1. [x] Add `.github/workflows/branch-release.yml`
   - header comment stating the contract, then `name`, `on`, `permissions`,
     `concurrency`
   - `build` job: checkout → setup .NET 8 → slug/version resolution → build →
     selftest → tests → package x64 → package arm64 (both `-SkipGates`) → upload
     workflow artifacts → upsert release
   - `cleanup` job: `delete` + `ref_type == 'branch'` only, slug recompute,
     `gh release delete --cleanup-tag --yes`, 404 tolerated
   - release step: build both zips first, re-point the tag with `git tag -f` +
     `git push --force`, then create vs upload+edit branches, `--prerelease`,
     `--latest=false`, notes written to a temp file
2. [x] Update the `build.yml` header comment: releases are no longer tag-only
3. [x] Update `README.md` § "Continuous integration & releases" + the repo-layout
   line for `.github/workflows/` to describe the branch lane
4. [x] Verify YAML parses and review the diff for secret/quoting mistakes

## Fix round after review

- [x] Append a 6-hex digest of the raw ref to the slug: `feature/x` and
      `feature-x` collided onto one tag/release, in different concurrency
      groups, so their `--clobber` uploads could interleave and either branch's
      cleanup would delete the other's release
- [x] Move the tag with `git tag -f` + `git push --force` instead of
      `gh release edit --target` (ignored by the API for an existing tag); drop
      the now-pointless flag probe
- [x] Treat only a "not found" view result as absent; throw on auth/network
- [x] Correct the header's atomicity claim and the `v*`-tag wording
- [x] Move the tag on both release paths, before publishing (a surviving stale
      tag made the create path attach to the wrong commit)
- [x] Concurrency group: `github.ref_name` is the default branch on `delete`
      events, so every cleanup shared one group and parallel deletions cancelled
      each other
- [x] `cleanup` on `ubuntu-latest` (`pwsh` ships there; needs no Windows)
- [x] Label the notes "Ref:" instead of "Branch:" (dispatch may target a tag)
- [x] README: Pre-release badge caveat, dispatch-on-`main` leak note, tag shape
- [x] Mirror "keep in sync" notes in both workflow headers

## Validation

- [x] `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/branch-release.yml'))"`
- [x] `actionlint` **1.7.7** (downloaded release binary) on both workflows — clean
- [x] Grep the workflows for `on:`, `branches:` and `prerelease` and read them
      back to confirm `main` is excluded and `v*` tags are unaffected
- [x] Confirm `.github/workflows/build.yml` still passes `-SkipGates` and its
      tag-release step is byte-identical apart from comments (`git diff -U0`: all
      changed lines are `#` lines)
- [x] Executed the real run blocks under **pwsh 7.4.6** against a stub `gh` and a
      local bare `origin`, not just read them:
  - slug step: `feature/x` → `feature-x-217d2b` vs `feature-x` → `feature-x-c791eb`
    (distinct, stable across runs); `feature/Tray Panel Fix`, `a`×60, `___` all
    produce `[a-z0-9-]{1,40}` tags with no trailing `-`
  - release step, create path: `--target`, `--prerelease`, `--latest=false`,
    both assets; missing asset fails
  - release step, rolling path: assets `--clobber`ed and the remote tag
    re-pointed from the first to the second commit (verified with
    `git rev-parse` on origin)
  - release step, view failure (401): throws `release view failed: HTTP 401`
    instead of falling into create
  - cleanup: correct tag from a `feature/x` ref, `$`/backtick ref, 404 tolerated
    (exit 0), 403 propagates (exit 1)

## Not run here

- `dotnet build` / `--selftest` / `dotnet test` — no .NET SDK or Windows runner
  in this environment; the steps are byte-copied from `build.yml`, which is green
  on `main`.

## Review gates

- Only after all boxes above: report for review, no push.

## Rollback points

- After step 1 alone the repo is still consistent (new workflow is additive).
- `build.yml` and README edits are comment/doc-only and revert independently.
