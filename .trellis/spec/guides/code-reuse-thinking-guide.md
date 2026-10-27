# Code Reuse Thinking Guide

> **Purpose**: stop before creating a second owner for the same decision.

---

## Why this repo is at risk

Two clients, two stores, three windows, and a check suite that asserts exact
values. Duplicated logic here does not just drift — it desynchronises the
`SelfTest` tables from the code and produces UI that disagrees with itself.

Real near-miss in the current tree: `QBittorrentParser.Ratio` and
`TransmissionParser.Ratio` are byte-identical, and `EtaSeconds` differs only in
its sentinel (`8640000` vs `< 0`). That is acceptable because they encode two
different protocols; three copies would not be.

---

## Before writing a new helper

```bash
rg -n "MethodName|keyword" src tests
```

| Question | If yes |
|---|---|
| Does a helper already own this decision? | Call it |
| Is it a decision with an existing record/enum? | Extend the record |
| Would two clients need the same shape? | Put it on the `Model/` type |
| Am I copying from another file? | Extract to `Model/` (pure) or `Format/` |
| Is it only one call site and one line? | Leave it inline |

---

## Single owners that must not be re-implemented

| Decision | Owner |
|---|---|
| Host / port extraction, pasted URLs, IPv6 | `HostFieldParser` |
| Config normalisation + the user-visible notice | `ServerConfigValidator` |
| `JsonNode` reading of any numeric/string/bool | `Client/Json.cs` |
| State name → `TorrentState` (per protocol) | `…Parser.State` |
| Login / endpoint status → `ServerFailure` | `…Parser.LoginFailure` / `EndpointFailure` |
| Progress clamping and percent text | `TorrentSummary.ClampedProgress` / `PercentText` |
| Sort order | `TorrentSummary.Sorted` |
| Row segments and detail lines | `InfoSegments` / `ExpandedLines` |
| Site derivation and hoisting | `SiteFromAnnounceUrl` / `HoistedSite` |
| Rate/byte formatting and the tray ladder | `ByteRateFormatter` |
| Backoff ladder | `MonitorStore.RetryDelay` |
| Change detection | `MonitorStore.IsEquivalent` / `SnapshotEquivalent` |
| Persistence and clamping | `SettingsStore` |
| Paths | `AppPaths` |
| Every user-facing string | `L10n` (both tables) |

`MonitorStore.DefaultClientFactory` is the only legitimate `ServerKind` switch
outside `Client/` — it selects a client and decides nothing.

---

## Real rules

**Constants once.** `CredentialFailureBackoff`, `BannedBackoff`,
`MaxVisibleTorrentsPerServer`, `DefaultPollInterval`/`Min`/`Max`, `TorrentFields`,
`HeaderField`, `Usage` are all `const`/`static readonly` in one place. The
selftest asserts the values. Never re-type `600` or `8640000` at a call site.

**Both clients, one shape.** Adding a field to `TransferSnapshot` or
`TorrentSummary` means updating both parsers and both sentinel tables, then the
`SelfTest` rows. If one parser starts producing a differently-shaped value, the
model is the thing to fix.

**Formatting once.** `Rate`, `Bytes`, `SpeedText` and the arrow constants live in
`ByteRateFormatter`. `PanelViewModel` composes `$"{ByteRateFormatter.DownArrow} …"`
rather than writing `"↓"` again; if that pattern grows past two sites, add a
helper there.

**Repeated visibility checks.** `IsVisible="{Binding X, Converter={x:Static
StringConverters.IsNotNullOrEmpty}}"` is the accepted idiom — do not invent a
converter per property.

---

## When to abstract

Abstract at the **third** occurrence, or immediately for a decision covered by
the check suite. Do not abstract: one-call helpers, one-line wrappers, or "for
symmetry" between two protocol parsers whose sentinels differ.

---

## After a batch change

```bash
rg -n "oldName|oldText|8640000" src tests README.md
dotnet run --project src/PTMonitor.Cli -- --selftest
```

Checklist:

- [ ] Every occurrence of the changed value found, including README, `L10n`
      tables and `SelfTest` fixtures
- [ ] Both `L10n` languages updated together
- [ ] No copy-pasted block that should call an owner above
- [ ] Decision added to `Core` if it was made inline in the app
- [ ] `--selftest` and xUnit still green

---

## Gotcha: `JsonNode` casts reappear easily

**Symptom**: a new field is read with `(long)node["x"]!` and works in the
selftest fixture, then throws or yields `0` against a real server that sends a
string or double.

**Prevention**: if a payload field is read in more than one place, it is
`Json.Int64`/`Double`/`String`/`Bool` or it is wrong.
