# Implement — Floating desktop speed widget

## Order

1. **Settings** — add `ShowFloatingSpeeds`, `FloatingPositionX/Y`, setters,
   `SettingsFile` members, load defaults (off / null). Extend the settings
   round-trip test with the new fields.
2. **Strings** — add the two English keys + zh-Hans values in `L10n.cs`.
3. **Window** — create `Views/FloatingSpeedWindow.axaml(.cs)` with the layout,
   gesture, placement, rate setter and `ApplyLocalization`.
4. **Styles** — add `floatingChip` / `floatingRate` classes to `App.axaml`.
5. **AppServices** — cache the window, `UpdateFloatingRates`,
   `SyncFloatingWindow`, `ShowPanel` split, menu check item, hook status/settings
   updates.
6. **Preferences** — checkbox, localization, reload, toggle handler.
7. **Docs** — README feature list, Preferences table, file map, settings table.
8. **Validate** — build, self-test, tests; exercise the app.

## Validation

```bash
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release
dotnet run --project src/PTMonitor.App
```

Manual:

- [ ] Default off; enable from menu and Preferences, disable again
- [ ] Rates match the panel and become `—` offline
- [ ] Click opens the panel; drag moves without opening and survives restart
- [ ] Tooltip/labels in English and Chinese
- [ ] Quit exits cleanly

## Rollback points

- After step 1 (settings alone are inert).
- After step 5 (window + wiring; revert as a unit).
