# State Management

> `SettingsStore` and `MonitorStore` are the only state owners.

---

## Where each kind of state lives

| State | Owner | Read via |
|---|---|---|
| Servers, poll interval, list mode, language, tray toggle, floating-chip toggle/display mode/position | `SettingsStore` (`AppServices.Settings`) | `Settings.Servers`, `Settings.PollInterval`, … |
| Live per-server status | `MonitorStore` (`AppServices.Monitor`) | `Monitor.Statuses` (copy), `Monitor.Aggregate` |
| Passwords | `ISecretStore` (`AppServices.Secrets`) | `Secrets.Get(id)` |
| Expanded row, freshness text, section list | `PanelViewModel` | view-model properties |
| Form contents, `_isTesting`, `_loadingUi` | the window's code-behind | fields |
| Which tray icon/window is shown | `AppServices` | private fields |

No state is duplicated across these. A view-model never caches a server list it
read from settings — it re-reads on each `Rebuild`.

---

## There is no global mutable UI state

Nothing static except the two singletons that must be singleton-shaped:
`AppServices.Instance` and the `L10n` language static. `AppServices` is created
once in `App.OnFrameworkInitializationCompleted` and is not re-creatable.

---

## All writes go through a store

```csharp
AppServices.Instance.Settings.SetPollInterval((double)value);
AppServices.Instance.Settings.SetEnabled(!row.Server.IsEnabled, row.Server.Id);
AppServices.Instance.Settings.SetAppLanguage(item.Language);
```

- Never mutate a `ServerConfig` in place and assume it persisted —
  `SettingsStore.Update`/`SetEnabled` is what writes and notifies. (`SelfTest`
  does mutate before `Update`; the app copy flow uses fresh instances from
  `ServerConfigValidator.Validate`.)
- Deleting a server also deletes its secret, in that order of concern:

  ```csharp
  AppServices.Instance.Settings.Delete(row.Server.Id);
  AppServices.Instance.Secrets.Delete(row.Server.Id);
  ```

- `Settings.Changed` drives everything downstream: `MonitorStore.Reconcile`,
  `PanelViewModel.Rebuild`, `AppServices.OnSettingsChanged` (language + menu
  rebuild + tray refresh).

---

## Read-through, not cached snapshots

```csharp
var statuses = _monitor.Statuses;              // fresh copy
var servers = _settings.Servers.Where(s => s.IsEnabled).ToList();
```

`MonitorStore.Statuses` returns a new dictionary under the lock, so iterating
while a poll completes is safe. Do not hold the result across a rebuild.

---

## Derived state is recomputed, never stored

Tray icon, tooltip, aggregates, empty state, section notes and durations all
come from `Monitor.Aggregate` / `Monitor.MenuBarState` / `Snapshot` at
`UpdateTray`/`Rebuild` time. There are no cached counters to keep in sync.

---

## Anti-patterns

- A view-model writing `settings.Servers` directly.
- A `static` mutable field in a service.
- Copying a status snapshot into a field and reading it later across a poll.
- Storing the password on `ServerConfig` or in the editor beyond the save call.
- Any second source of truth for the poll interval or list mode.
- Persisting UI-only state (expanded row, transient window state). The one
  deliberate exception is the floating speed chip's position
  (`FloatingPositionX/Y`), which the user expects to survive a restart; the
  main panel's position is still not persisted.
