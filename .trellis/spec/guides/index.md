# Thinking Guides

> **Purpose**: the two failure modes that actually show up in this codebase.

---

## Why these exist

Almost every bug in this project's history was one of two things:

1. **The same decision implemented twice**, then one copy updated — a parser
   drift, a sentinel re-typed, a translation missing in one language.
2. **A boundary shape assumed instead of decided** — a `403` treated as one
   cause, a JSON map read in the wrong direction, a `null` rendered as `0`.

Neither is a skill problem. Both are "did not stop and look" problems.

---

## Guides

| Guide | Use when |
|---|---|
| [Code Reuse](./code-reuse-thinking-guide.md) | You are about to write a helper, a table, or a constant |
| [Cross-Layer](./cross-layer-thinking-guide.md) | A change crosses wire → model → store → UI, or settings → disk |
| [CI Pipeline](./ci-pipeline.md) | You touch `.github/workflows/`, a gate, a release, or `scripts/build.ps1` |

---

## Triggers

Open the **reuse** guide when:

- [ ] You add a second `ServerKind` branch outside `Client/`
- [ ] You type a literal that already exists (`600`, `900`, `8640000`, `100`, `8080`, `9091`, `"↓"`)
- [ ] You copy a parser, formatter or normalisation table
- [ ] You need a user-facing string (it belongs in both `L10n` tables)

Open the **cross-layer** guide when:

- [ ] You add or change a field on `TransferSnapshot`/`TorrentSummary`
- [ ] You add a sentinel or a version-gated field
- [ ] You change a `settings.json` property or enum name
- [ ] You touch `SnapshotEquivalent`, `IsEquivalent` or the backoff ladder
- [ ] You add a failure path (does it reach `ServerFailure.Kind`?)
- [ ] You change the exe name, manifest, or build/CI pipeline

Open the **CI pipeline** guide when:

- [ ] You add, remove or reorder a gate, or add a workflow/lane
- [ ] You change a trigger, tag shape, version string, artifact name or release step
- [ ] You change what `scripts/build.ps1` accepts or produces
- [ ] You add a view-model property that the UI displays

---

## The pre-change habit

> Before changing any value, search for it.

```bash
rg -n "8640000|BannedBackoff|%lld|PTMonitor\.App" src tests README.md scripts .github
```

---

## Verifying an AI review finding

Reviewers inherit the reviewer's blind spots, not the code's context.

- **"untrusted input needs validation"** — is it a local `settings.json` the user
  wrote, or wire data? They need different treatment.
- **"behaviour change"** — read the surrounding comment; the 600 s backoff and
  the `null` ratio are deliberate and selftest-asserted.
- **"test is tautological"** — delete the feature mentally; if the check still
  passes, it asserts nothing.

Budget ~35 % false positives; confirm every CRITICAL before acting.

---

**Core principle**: five minutes of "who owns this?" saves a protocol bug.
