# Quality Guidelines

> The check suite is the contract. This file says how to extend it and what
> never to do.

---

## The verification contract

Two gates, always run together:

```bash
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release -- --selftest   # exit 0 required
dotnet test tests/PTMonitor.Core.Tests -c Release                 # must be green
```

- `SelfTest.RunAsync` (in `Core/Diagnostics/SelfTest.cs`) is the behaviour
  specification for parsers, validation, formatting, sorting, segments, detail
  lines, backoff, aggregation and stores.
- `tests/…/CoreTests.cs` mirrors it and also runs the whole suite
  (`SelfTestSuite.AllBuiltInChecksPass`, plus `Passed > 150`). The two can never
  drift.
- **A behaviour change without both updates is incomplete, even if it compiles.**

Assertions use the `Checks` helper (`Expect`, `ExpectOptional`, `True`,
`ExpectFailure`, `ExpectFailureAsync`). `Expect` handles record/collection
equality; `nil` is rendered as `nil` in failures.

---

## Required patterns

| Pattern | Why |
|---|---|
| `Checks.Expect*` with a descriptive `"area: case"` name | Failure output is the only diagnostic |
| xUnit `[Theory]`/`[InlineData]` for tables, `[Fact]` for single cases | Cheapest way to keep both gates honest |
| Pure static method for every decision | Testable without a network or a window |
| `ITorrentClient` stub for store tests | No sockets in the suite |
| Temp directory per store test, deleted in `finally` | No cross-run state |
| `DateTimeOffset.UnixEpoch` in fixtures | Deterministic |
| `CultureInfo.InvariantCulture` for all numeric text | Decimal-comma locales |
| `double.IsNaN`/`IsInfinity` guards before clamping | Real payloads carry them |

---

## Forbidden patterns

- **Network in the suite.** No test may touch a real server; use a stub or the
  node stub script.
- **Skipping the mirror.** A `SelfTest` case with no xUnit counterpart, or the
  reverse *as the only* coverage.
- **`Thread.Sleep` in a check.** Await properly.
- **Tautological checks.** Delete the feature in your head — if the check still
  passes, it asserts nothing.
- **Asserting localized text from `Core`.** `SelfTest` may assert decisions and
  `L10n`-free formatting only; translations live in `App`.
- **Culture-sensitive formatting** (`ToString()` on numbers, `.ToString("N0")`).
- **`catch { }` without a comment**.
- **New NuGet packages in `Core`** without a reason in the PR. There is exactly
  one today.
- **`TreatWarningsAsErrors` is off** — so do not leave warnings. Read them.
- **Changing `Directory.Build.props`** (`Nullable`, `LangVersion`,
  `Deterministic`) to make code compile.
- **Renaming a persisted JSON property or enum member** (see
  [persistence](./persistence-guidelines.md)).

---

## Real-server verification

When touching a client, also run the qBittorrent stub in **both** shape modes —
it reproduces the 403 ban path, the missing-cookie path and the 5.2 status-code
switch:

```bash
node scripts/fake-qbittorrent.mjs 18080
dotnet run --project src/PTMonitor.Cli -- --probe qbittorrent \
    --host 127.0.0.1 --port 18080 --user admin --pass adminadmin

WEBAPI=5.2 node scripts/fake-qbittorrent.mjs 18080   # identical outcome required
```

Wrong credentials must report `auth failed` for the first 5 attempts and only
then `banned`.

---

## Review checklist

- [ ] `dotnet build`, `--selftest` and `dotnet test` all green locally
- [ ] New behaviour has a `SelfTest` case **and** an xUnit mirror
- [ ] New fields are normalised in the parser, `null` for "not reported"
- [ ] New error paths use `ServerFailureException` with an actionable message
- [ ] Cancellation token threaded through every new async method
- [ ] Snapshot fields used by the UI added to `SnapshotEquivalent`
- [ ] Persisted shape changes are backward compatible
- [ ] No `Console.Write*` outside `Cli`/`Diagnostics`
- [ ] No new package without justification
- [ ] `Core` still has zero Avalonia references
- [ ] Formatting/number output passes `InvariantCulture`
