# CI & Release Pipelines

> Two Windows workflows. One contract. This file says how to change them without
> publishing an ungated binary.

---

## The two lanes

| Lane | File | Trigger | Output |
|---|---|---|---|
| Stable | `build.yml` | `main` push, `v*` tag, dispatch | artifacts; release on tag only |
| Branch | `branch-release.yml` | any other branch push, branch delete, dispatch | rolling prerelease per branch; delete cleanup |

Both run the same gates in the same order and package both architectures through
`scripts/build.ps1 -SkipGates`. A `v*` tag is the only stable-release path.

---

## Rules

1. **A new gate lands in both files in the same commit.** `build.yml` and
   `branch-release.yml` duplicate the gate commands, the two `-SkipGates` package
   calls and the artifact names on purpose — a branch release is a published
   binary, so it must pass everything `main` passes. Both headers carry the
   "keep in sync" note; if you add a third lane, copy them too.
2. **Gates run once, before packaging.** `-SkipGates` is only legal after
   build + `--selftest` + `dotnet test` succeeded in the same job.
3. **Never build a release from a ref you did not just gate.** The release step
   is the last step and only runs after both package steps.
4. **Refs in tags are sanitised, never raw.** Branch names carry `/`, spaces and
   `$`; the slug is lowercase `[a-z0-9-]` plus a digest of the raw ref (sanitising
   is lossy: `feature/x` and `feature-x` collide without it).
5. **Anything interpolated into a pwsh string goes through `env:`.** A branch
   name containing `$`, a backtick or `"` otherwise expands or breaks the parse.
6. **`--target` is create-only.** GitHub ignores `target_commitish` for a tag
   that already exists; move rolling tags with `git tag -f` + `git push --force`.
7. **Derived values used by more than one job are duplicated verbatim**, not
   re-invented (the cleanup job re-derives the slug with the same three lines).
8. **`main` stays out of the branch lane** (`branches: ['**', '!main']`) and a
   `tags:` filter must never be added to it, or tag pushes would double-publish.

---

## Changing a workflow

```bash
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/<file>.yml'))"
actionlint .github/workflows/*.yml
```

Then diff the changed run blocks against the other workflow. actionlint does not
know pwsh, so a green lint says nothing about quoting — read those blocks.

## Local dry run

The release and cleanup steps run under pwsh on Linux (invariant globalization +
a stub `gh` + a local bare `origin`), so tag-moving and quoting mistakes are
catchable without pushing:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 PATH=<stub-bin>:$PATH pwsh -File step.ps1
```

`dotnet build`/`--selftest`/`test` still need a .NET SDK; they cannot be faked.
