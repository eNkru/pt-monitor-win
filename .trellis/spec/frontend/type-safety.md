# Type Safety

> `Nullable` is enabled solution-wide. `null` has one meaning: not reported.

---

## Baselines

`Directory.Build.props` sets:

```xml
<Nullable>enable</Nullable>
<LangVersion>latest</LangVersion>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>false</TreatWarningsAsErrors>
```

Nullable warnings are off the error list, so they are easy to ignore. Do not
ignore them; `TreatWarningsAsErrors=false` exists to keep builds unblocked, not
to license `!`.

`System.Text.Json` payload access goes through `Client/Json.cs` — never a cast
of `JsonNode` (see [client rules](../backend/client-and-parser-rules.md)).

---

## `null` semantics

| Meaning | Representation |
|---|---|
| Server did not report the field | `null` (`long?`, `double?`, `int?`, `string?`) |
| Field reported | non-null value |
| Render of a missing value | `"—"` in the view-model |
| Optional whole object | absent from the list (for example no `TorrentDetailLine`) |

Never substitute `0`, `""` or `false` for "not reported".

```csharp
// good — omitted from the row, so no fake zero appears
c.ExpectOptional("qb summary: ratio -1 is nil", downloading.Ratio, null);

// bad
Ratio = Json.Double(entry["ratio"]) ?? 0,
```

---

## Null handling idiom

```csharp
if (status.Snapshot?.FetchedAt is not { } fetched) continue;
if (peers.Seeders is { } seeders) …
```

- Use pattern matching (`is { } x`, `is null or < 0`, `is { Count: > 0 }`) over
  `!= null` plus a local.
- `?.` chains are fine when a `null` result is a valid "absent" answer.
- A default is `??` **only** when the protocol genuinely guarantees the field
  (`Json.Int64(serverState["dl_info_speed"]) ?? 0` for a required speed).

Where `!` is already accepted, keep the pattern: `AppServices.Instance = null!`
(assigned in the ctor), and code-behind element access after
`InitializeComponent()` (`RefreshButton.Content!`). Do not add `!` for
nullability suppression elsewhere.

---

## Type design

- Records for immutable/compared values: `ServerFailure`, `HttpResponse`,
  `ValidatedServerConfig`, `ParsedHostField`, `MonitorAggregate`, all
  `TorrentDetailLine`/`TorrentRowInfoSegment` variants.
- Abstract records with a private constructor for closed unions
  (`ServerStatus`), plus accessors (`Snapshot`, `Failure`) so call sites avoid
  type switches.
- `required`/`init` on view-model data that must be supplied at construction.
- Enums instead of magic strings; parse with a `TryParse` +
  `JsonStringEnumConverter<T>` pair (`ServerKind`, `TorrentState`).
- `readonly record struct` for a small compared triple
  (`WebApiVersion`, `HandshakeStep`).
- A `*Result` record when a call has more than one outcome (`SelfTestResult`,
  `ValidatedServerConfig`, `HandshakeStep`) — never a tuple of mixed meaning
  across a public boundary.

---

## Anti-patterns

- `object`/`dynamic` in a public signature.
- `var x = (string)node["field"]!;`.
- `Dictionary<string, object>` as a pseudo-model.
- `default!` for something the constructor does not set.
- A `string` state where an enum exists.
- Computed getters on a persisted type that should not vary by build
  (`ServerConfig.DisplayName`/`EndpointDescription` are written to
  `settings.json` today and ignored on load because they have no setter — think
  before adding another).
- `double` for byte counts (`long`), or `int` for rates (`long`).
- A `string` state where an enum exists.
