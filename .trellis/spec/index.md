# PTMonitor Engineering Spec

> Coding conventions for this repository, written from the code that exists.

---

## What this project is

A Windows system-tray agent that polls qBittorrent WebUI and Transmission RPC
read-only and renders a tray panel. .NET 8 + Avalonia 11. It is a port of a
macOS menu-bar app, so the domain model, parser tables, sort order and backoff
ladder are intentionally identical to the original.

Read `README.md` first for behaviour — this directory covers **how to write
code here**, not what the app does.

---

## Layer map

| Spec directory | Project(s) | Contents |
|---|---|---|
| [`backend/`](./backend/index.md) | `src/PTMonitor.Core`, `src/PTMonitor.Cli`, `tests/` | Domain model, API clients, stores, formatting, diagnostics. No UI types. |
| [`frontend/`](./frontend/index.md) | `src/PTMonitor.App` | Avalonia views, view-models, tray icon, localization, app lifecycle. |
| [`guides/`](./guides/index.md) | all | Cross-cutting thinking guides (reuse, cross-layer data flow, CI/release pipeline). |

`backend`/`frontend` are Trellis layer names, not a web split. "Backend" =
everything that must stay UI-free and headlessly testable.

---

## Non-negotiables

1. **Dependency direction is one-way**: `App`/`Cli`/`Tests` → `Core`. `Core`
   never references Avalonia, `PTMonitor.App`, or any window type.
2. **Read-only.** No mutating API call is ever sent to a server.
3. **Passwords never reach `settings.json`.** They go to the DPAPI secret store
   keyed by server id.
4. **`null` means "the server did not report it"** — never a fabricated `0`.
5. **Presentation decisions are pure and string-free in `Core`**; `App` turns
   them into localized text.
6. **Behaviour changes update `SelfTest` and the xUnit mirror in the same
   commit.** They are the contract.
7. **Text is never hardcoded in a view.** Every user-facing string goes through
   `L10n` in both languages.

---

## Shape of a change

| Change | Where it goes |
|---|---|
| New remote API / new server kind | `Core/Client`, a new `ITorrentClient`, a pure parser, normalised `Model` |
| New field on a snapshot or torrent | `Core/Model` + both parsers + sentinel handling + selftest + xUnit |
| New persisted preference | `Core/Store/SettingsStore` (default, clamp, nullable DTO) + Preferences UI + selftest |
| New user-visible string | `App/Services/L10n.cs` in **both** tables + a call site |
| New visual state | `Core` decision (segment/line record) + `App` mapping to text/brush + XAML |
| New check | `Core/Diagnostics/SelfTest.cs` **and** `tests/PTMonitor.Core.Tests/CoreTests.cs` |

---

## Verification before you call it done

```bash
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release
```

All three must be green. Workflow changes also get `actionlint` plus a diff
against the other lane — see [`guides/ci-pipeline.md`](./guides/ci-pipeline.md).
`dotnet build` alone is not verification: the app compiles fine with a broken
parser table.
