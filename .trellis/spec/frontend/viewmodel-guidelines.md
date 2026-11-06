# ViewModel Guidelines

> This is C#, not React — there are no hooks. These are the view-model rules.

---

## The rebuild-all model

`PanelViewModel` does not do per-item diffing. It clears `_sections` and
reconstructs every section and row from the current `MonitorStore` statuses, then
raises `PropertyChanged` for each top-level property:

```csharp
public void Rebuild()
{
    _sections.Clear();
    foreach (var server in servers) { … _sections.Add(new ServerSectionViewModel { … }); }
    …
    foreach (var name in new[] { nameof(Sections), nameof(DownSpeedText), … })
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

Rebuild is triggered by `Monitor.StatusesChanged`, `Settings.Changed`, mode
change, row expansion, and `OnOpened`. At the default 2 s poll and 100 rows it is
cheap; do not optimise it into grey-code diffing without a measurement.

---

## A hidden panel does not rebuild

The panel window is cached and only hidden, so its view models stay alive. A
rebuild while nobody can see the panel is invisible work: at 2 s it re-created
up to 100 row view models per server, forever.

`PanelViewModel.IsPanelVisible` is the gate:

```csharp
public bool IsPanelVisible
{
    get => _panelVisible;
    set
    {
        if (_panelVisible == value) return;
        _panelVisible = value;
        _monitor.SetListDemand(value);   // the polls fetch the list only while it is shown
        if (value) Rebuild();            // opening shows the current data at once
    }
}
```

- `false` → `StatusesChanged` and `Settings.Changed` rebuild **nothing**. The
  rows stay in the collection untouched, so re-showing them costs nothing.
- `true` → one rebuild, so the panel shows the current numbers at once. The
  torrent list it shows is the last full one the store kept; the fresh list
  follows within one request time (the demand wakes the poll loop), so the
  panel is never *stale* for long and never invents rows. Before the first
  full poll there is nothing to show — the panel says so.
- The gate is checked **twice**: when the event arrives (on the poll thread) and
  again inside the posted action (on the UI thread). A poll that lands while
  the panel is closing must not rebuild a hidden window.

`MainPanelWindow` owns the truth: the explicit `HidePanel()` call is the
signal (it sets the flag and stops the freshness timer), and
`Visual.IsVisibleProperty` is a safety net for a hide that comes from
elsewhere. `Hide()` does not raise `Closed`, so the property and the explicit
call are the only two signals.

---

## Shape rules

- Immutable display data uses `required` + `init`:

  ```csharp
  public required TorrentSummary Torrent { get; init; }
  public required string InfoText { get; init; }
  ```

- Only genuinely mutable view state is settable and raises `PropertyChanged`
  (`TorrentRowViewModel.IsExpanded`). Expansion is also projected into
  `DetailCells`, so both names are raised together.
- Derived lists are computed in the getter and guarded by the state that makes
  them meaningful:

  ```csharp
  public IReadOnlyList<DetailCellViewModel>? DetailCells =>
      IsExpanded ? BuildCells() : null;
  ```

- The view-model holds `SettingsStore`/`MonitorStore` and reads them; it never
  owns configuration values.
- The view-model never references `Window`, `Dispatcher` or a brush-producing
  service — only `Avalonia.Media.IBrush` as a value (`StateBrush`).
- The view-model may ask the store to change what the polls fetch
  (`SetListDemand`); it does not reach into the poll loops.

---

## Naming

| Member | Convention |
|---|---|
| Row/section/cell view-model type | `XViewModel` |
| Text already formatted for display | `…Text` (`InfoText`, `PercentText`, `MoreText`, `NoteText`) |
| Tooltip text | `…ToolTip` |
| `bool` for `IsVisible` | `Is…` (`IsEmptyState`, `IsExpanded`) |
| Raw value passed through to a control | no suffix (`Progress`, `StateBrush`) |

---

## Limits and defaults

`PanelViewModel.MaxVisibleTorrentsPerServer = 100`; the overflow becomes
`MoreText` via `L10n.MoreTorrents`. Empty groups get a `NoteText`
(`"No torrents."`, `"No downloading torrents."`, …). Placeholder for a missing
metric is `"—"`.

---

## Change notification

- `INotifyPropertyChanged` is implemented with an explicit
  `PropertyChanged?.Invoke(...)` — no `[ObservableProperty]`, no source
  generator, no `ReactiveUI`.
- Raise `PropertyChanged` last, after the state is already updated.
- `ObservableCollection` is used for the section list so `ItemsControl` follows
  `Clear`/`Add`.

---

## Anti-patterns

- `async void` in a view-model (the views own async handlers).
- Storing a `TorrentSummary` mutation instead of re-reading the snapshot.
- Building display strings in `Core`.
- A view-model per view file with no state (`PreferencesWindow` uses code-behind
  directly — that is accepted for simple forms).
- `PropertyChanged` with `string.Empty`/`null` instead of real names.
- Timers inside a view-model (`MainPanelWindow` owns the freshness
  `DispatcherTimer`).
