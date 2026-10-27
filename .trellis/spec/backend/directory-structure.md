# Directory Structure

> How `PTMonitor.Core` is organised and where new code belongs.

---

## Project graph

```
PTMonitor.sln
  src/PTMonitor.Core   ← referenced by App, Cli, Tests. References nothing local.
  src/PTMonitor.App    → Core (+ Avalonia)
  src/PTMonitor.Cli    → Core
  tests/…Tests         → Core
```

`PTMonitor.Core.csproj` has exactly one package reference
(`System.Security.Cryptography.ProtectedData`). Adding a package here is a
review-worthy decision.

---

## Folder responsibilities

| Folder | Owns | Does not own |
|---|---|---|
| `Model/` | Normalised domain types (`ServerConfig`, `ServerStatus`, `TransferSnapshot`, `TorrentSummary`, enums) | Parsing, HTTP, persistence |
| `Client/` | `ITorrentClient`, the two clients, `HttpTransport`, `HostFieldParser`, `ServerConfigValidator`, the pure parsers, `Json` accessors | Anything UI or persisted shape |
| `Store/` | `SettingsStore`, `ISecretStore` + implementations, `MonitorStore`, `AppPaths` | Parsing, formatting |
| `Format/` | `ByteRateFormatter` (rates, byte counts, tray text ladder) | Locale-specific wording |
| `Diagnostics/` | `SelfTest`, `ProbeRunner` | Production behaviour |
| namespace `PTMonitor.Core.X` | mirrors folder `X`, file-scoped | |

`src/PTMonitor.Cli/Program.cs` is the only file in `Cli`; all real logic lives
in `Core/Diagnostics`.

---

## Where new code goes

| You are adding | Put it in |
|---|---|
| A new server kind | `Client/<Kind>Client.cs` + `Client/<Kind>Parser` + `Model/ServerKind` + `MonitorStore.DefaultClientFactory` |
| A pure presentation decision | `Model/TorrentSummary.cs` (segments/lines/sort/site) or a new `Model/` type |
| A new stored preference | `Store/SettingsStore.cs` |
| A byte/number formatter | `Format/` |
| A probe flag | `Diagnostics/ProbeRunner.Options` + `Parse` + `Usage` |
| A check | `Diagnostics/SelfTest.cs` (and the xUnit mirror) |

---

## File and type naming

- One public type per file, named after the file.
- Records for immutable shapes (`HttpResponse`, `ServerFailure`,
  `MonitorAggregate`, `SelfTestResult`, `ParsedHostField`).
- Classes for anything with state (`HttpTransport`, `MonitorStore`,
  `SettingsStore`, both clients, both stores).
- Nested records for closed variants: `ServerStatus.Disabled/Connecting/Online/Failed`,
  `TorrentDetailLine.*`, `TorrentRowInfoSegment.*`.
- Nested types that only exist to serve their parent are `private sealed`.

---

## Anti-patterns

- **`Core` referencing `Avalonia`.** Not even `Avalonia.Media.IBrush` — that is
  why brushes are chosen in `PanelViewModel`, not `TorrentSummary`.
- **A `Utils/` or `Helpers/` dumping ground.** If it is about bytes, it belongs
  in `Format/`; if about hosts, `Client/`; if about a model type, static methods
  on that type.
- **A second `Model/` type per client.** Normalise, do not parallel-model.
- **Putting parse tables in a client class.** They live in the static parser so
  `SelfTest` can call them without a network.

---

## Examples to follow

- `Client/TransmissionClient.cs` — parser static class, then client class, in
  one file because the parser exists only for that client.
- `Model/ServerStatus.cs` — abstract record with private constructor plus sealed
  nested variants, and `Snapshot`/`Failure` accessors so callers never
  type-switch.
- `Diagnostics/SelfTest.cs` — grouped `*Checks` methods, one concern each.
