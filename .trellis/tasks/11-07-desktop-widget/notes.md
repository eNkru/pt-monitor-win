# Notes — desktop widget

## What is verified here (Linux dev box)

- `dotnet build PTMonitor.sln -c Debug` — 0 errors.
- `dotnet test tests/PTMonitor.Core.Tests` — 151 passed.
- `dotnet run --project src/PTMonitor.Cli -- --selftest` — 698 checks passed.
- The app starts with the widget enabled, the card is created and placed, the
  fallback path is taken (there is no desktop host off Windows) and the log
  records it **once**. The process survives the run with a clean log.

## What still has to be checked on Windows

The whole desktop-layer path is Windows-only and could not be exercised here:

1. The card is **behind** an application window (not in front of it).
2. **Win+D** does not hide the card.
3. Clicking the card does not take the focus away from the app in front.
4. The card is **below the desktop icons** (the `WorkerW` behind the icon view)
   and the rounded corners show the wallpaper.
5. The **right-click menu** opens above the other windows while the card is a
   child of the desktop host (Avalonia's popup is its own top-level window, so
   it should — but it has not been seen).
6. **Dragging** the card (the manual, cursor-driven drag for a child window)
   moves it and the position survives a restart.
7. The **lock** blocks the drag and survives a restart, while the rates, the
   graph, the double-click and the menu keep working.
8. A **secondary monitor with a different scale**: the card inherits the host's
   DPI context there (documented limitation), so it looks physically smaller.

If (5) fails, the fallback is to host the menu in a small top-level window
instead of an Avalonia `ContextMenu` owned by the card.

## Known limitations carried over

- **Every settings write restarts every poll loop** (`MonitorStore.Reconcile`
  runs on `Settings.Changed`). Dragging the card or toggling the lock
  therefore interrupts the monitoring briefly. This is pre-existing behaviour
  (the floating chip's position save does the same) and was left alone here.
- The desktop host discovery follows the canonical `Progman`/`WorkerW` recipe,
  but the layout differs between Windows 10 and Windows 11 builds; the
  fallback (always-on-top) keeps the widget usable when the host is not found.
