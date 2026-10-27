# Directory Structure

> Where UI code lives and the naming that goes with it.

---

## Layout

```
src/PTMonitor.App/
  Program.cs                 entry point + AppBuilder (do not reorder)
  App.axaml / App.axaml.cs   style classes, ShutdownMode, AppServices.Initialize()
  app.manifest               PerMonitorV2 DPI awareness
  app.ico                    exe icon, generated from the macOS AppIcon.icns
                             (regenerate with scripts/icns-to-ico.py)
  Services/
    AppServices.cs           composition root: stores, tray, windows, quit
    L10n.cs                  en + zh-Hans string tables
    TrayIconFactory.cs       tray icons drawn as vectors at runtime
  ViewModels/
    PanelViewModel.cs        sections, rows, expansion, aggregate, filters
  Views/
    MainPanelWindow.axaml(.cs)
    PreferencesWindow.axaml(.cs)
    ServerEditorWindow.axaml(.cs)
```

Three views, one view-model file. That is the whole UI — resist adding a folder
per feature until there is a second view-model.

---

## File pairing and naming

- A view is always `XWindow.axaml` + `XWindow.axaml.cs`, `x:Class` matching, both
  `partial`, code-behind sealed.
- `InitializeComponent()` is the first statement of the constructor, before any
  field assignment that a handler could observe.
- View-model types: `…ViewModel` (`PanelViewModel`, `TorrentRowViewModel`,
  `ServerSectionViewModel`, `DetailCellViewModel`). Row/section/cell
  view-models live beside their owner in `PanelViewModel.cs`.
- Small view-only item records that back an `ItemsControl` (for example
  `ServerRowItem`, `LanguageItem`) are declared in the owning view's code-behind.
- Services are `static` when stateless (`L10n`, `TrayIconFactory`), `sealed` and
  instance when stateful (`AppServices`).

---

## Where a change goes

| Change | Location |
|---|---|
| New user-visible string | `Services/L10n.cs`, both tables, then a call site |
| New panel row/detail cell | `Core/Model` decision → `PanelViewModel` mapping → XAML template |
| New reusable look | `App.axaml` as a class selector |
| New window | `Views/`, opened and cached by `AppServices` |
| New tray state | `TrayIconFactory` + `Monitor.MenuBarState` switch in `AppServices.UpdateTray` |
| New preference control | `PreferencesWindow.axaml` + `OnOpened`/`Reload` wiring |

---

## Anti-patterns

- Domain logic in a code-behind (`if (torrent.Ratio < 1)`) — put it in `Core`.
- A `Converters/` folder. Two conversions exist and both use Avalonia's built-in
  `StringConverters.IsNotNullOrEmpty`; prefer a view-model `bool`/`string?`.
- Windows constructed outside `AppServices` (only `ServerEditorWindow` is, by
  its owning preferences window).
- A view-model with a `Window` field, or a view with a `MonitorStore` field.
- Per-view style dictionaries; all styles live in `App.axaml`.
