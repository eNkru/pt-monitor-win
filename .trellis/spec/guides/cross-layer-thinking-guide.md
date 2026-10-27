# Cross-Layer Thinking Guide

> **Purpose**: most bugs here live at a boundary, not inside a function.

---

## The boundaries in this project

```
server wire ──▶ parser ──▶ Model ──▶ MonitorStore ──▶ ViewModel ──▶ XAML
settings.json ──▶ SettingsStore ──▶ ViewModel / MonitorStore
secrets.dat ──▶ ISecretStore ──▶ client ctor
user keystroke ──▶ editor form ──▶ ServerConfigValidator ──▶ stores
L10n key ──▶ (en | zh) table ──▶ rendered label
AssemblyName ──▶ MSBuild graph ──▶ CI zip → exe
app.manifest ──▶ DPI awareness ──▶ rendered layout
```

Each arrow is an ownership hand-off. Before touching one, write down the exact
shape on both sides and who validates.

---

## Rules per boundary

| Boundary | Rule |
|---|---|
| wire → parser | one parse point; tolerances (`Json.*`) and sentinels live here |
| parser → Model | `null` for absent, no protocol strings above |
| Model → MonitorStore | compare with `IsEquivalent` before publishing |
| MonitorStore → ViewModel | copy under lock, never hold it |
| ViewModel → XAML | decisions already made; XAML only binds and lays out |
| settings → disk | atomic temp+move, nullable DTO, clamp on load |
| secret → config | secret written first, config second |
| L10n key | added to both tables in the same edit |

---

## Mistakes this repo has already made (do not repeat)

| Mistake | Real cost |
|---|---|
| Treated the qBittorrent login `403` (ban) the same as an endpoint `403` (rejected cookie) | A dropped session cookie was reported as "the server banned this IP" |
| Read `trackers` as hash → announce URL | It is announce URL → `[hash]`; the map needed inverting |
| Read `free_space_on_disk` on every WebAPI version | Absent before 2.1.1; now gated on the cached version |
| Wrote the configuration before the password | An enabled server polled with an empty password → qBittorrent ban |
| Retried at 2 s after an auth failure | 5 tries → one-hour ban; hence the 600 s/900 s ladder |
| Passed `-p:AssemblyName` on the command line | Applied to the whole graph; two projects became "PTMonitor" |
| Rendered detail lines for fields the server omitted | Fake `0` ratios and sizes; now keyed off `null` |

---

## Before implementing a cross-boundary change

- [ ] Mapped which side produces and which consumes the value
- [ ] Decided the representation on **both** sides (`long?` vs `0`)
- [ ] Decided who validates (validator for user input, parser for wire data)
- [ ] Checked whether the App needs a new `L10n` key rather than a `Core` string
- [ ] Checked what `SnapshotEquivalent` must learn about a new displayed field
- [ ] Checked whether the change belongs in a record variant instead of a new field

---

## After implementing

- [ ] Absent value renders `—`, never `0`
- [ ] Selftest covers both protocols and both sentinel values
- [ ] Both languages have the new wording
- [ ] Failure path reaches `ServerFailure.Kind`, not a message comparison
- [ ] No `Core` type gained an Avalonia dependency
- [ ] `IsEquivalent`/`SnapshotEquivalent` updated for any new visible field

---

## The two-boundary traps worth naming

**Protocol → model.** Both clients must land on one model. If you add a
protocol-specific field, add it to `Model/` normalised (for example
`TrackerStatus`), not as a `qb*`-prefixed property.

**Decision → text.** `Core` returns records (segments, detail lines, state
enums); the app maps them to words. Putting a formatted English string in
`Core` makes it untestable from the suite and untranslatable.

---

## Fixtures and packaging are boundaries too

- `SelfTest` fixtures are recorded wire payloads held in `Diagnostics/`, away
  from any UI. Changing a payload shape means changing the fixture **and** the
  parser, in one commit.
- `scripts/build.ps1` and `.github/workflows/build.yml` both encode the same
  gate order (restore → build → selftest → test → publish). Keep them in step.
- `build.ps1` publishes one RID per invocation (`-Rid`, default `win-x64`); CI
  runs the gates once and then calls it twice with `-SkipGates` (win-x64 and
  win-arm64, cross-compiled). The packaged-CLI selftest only runs when the
  RID matches the runner architecture — the pre-publish `--selftest` is the
  architecture-independent gate and must always run.
- `app.manifest` supplies `PerMonitorV2`; the panel divides screen coordinates
  by `RenderScaling`. Changing one without the other breaks positioning on
  scaled displays.
