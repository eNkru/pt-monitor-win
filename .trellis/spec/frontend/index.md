# Avalonia Layer Guidelines

> Rules for `src/PTMonitor.App`: views, view-models, tray lifecycle, localization.

---

## Overview

`PTMonitor.App` is the only project that references Avalonia. It contains no
domain logic: it takes `Core` records and decisions and renders them. Everything
in here is expected to be thin, and the interesting decisions should already
exist in `Core`.

Namespaces follow folders (`PTMonitor.App.Views`, `.ViewModels`, `.Services`).

---

## Reference

| Guide | Description |
|---|---|
| [Directory Structure](./directory-structure.md) | Where views, view-models and services go |
| [Component Guidelines](./component-guidelines.md) | XAML structure, compiled bindings, style classes |
| [ViewModel Guidelines](./viewmodel-guidelines.md) | Rebuild-all model, `required` properties, change notification |
| [State Management](./state-management.md) | `SettingsStore` as the single source of truth |
| [Type Safety](./type-safety.md) | Nullable = "not reported", records, no `!` |
| [Lifecycle & Tray](./lifecycle-and-tray.md) | Tray-only agent, hide-not-close, quit |
| [Localization](./localization.md) | `L10n` keys, both languages, `x:Name` wiring |
| [Quality Guidelines](./quality-guidelines.md) | The App-specific build/run checks |

---

## The golden split

| Decision | Owner |
|---|---|
| Which detail lines exist, which segments a row shows, the sort order, progress clamping, site extraction | `Core/Model` (pure, string-free) |
| Which localized word/brush/format to render for that decision | `App` view-model |
| Layout, styling, visibility | XAML |

`TorrentSummary.InfoSegments` returns `TorrentRowInfoSegment` records;
`PanelViewModel.SegmentText` turns them into text. Never build a string in
`Core` and never re-derive a decision in XAML.

---

## Composition root

`AppServices` is a singleton created from `App.OnFrameworkInitializationCompleted`.
It owns the stores, the monitor, the tray icon and both windows, and exposes
`Instance`.

- `Instance` is assigned first in the constructor, because the first-run
  preferences window opens synchronously from it and reaches back.
- `Initialize()` is idempotent.
- Windows and view-models reach dependencies through `AppServices.Instance`;
  there is no DI container.
