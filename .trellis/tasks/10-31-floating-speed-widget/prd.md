# Floating desktop speed widget

## Goal

Give the tray agent an optional small always-on-top window that shows the
aggregate download and upload rates on the desktop. It is a glanceable companion
to the tray icon, not a replacement for the dropdown panel: clicking it opens the
panel, dragging moves it, and a preference turns it on or off.

## Background

Today the only persistent surface is the notification-area icon; the aggregate
speeds are hidden behind a click or in the tooltip. Users who keep PT traffic on
a second monitor want the rates visible without hovering. macOS CodexBar has an
equivalent floating readout; this brings the same idea to the Windows agent.

## Requirements

1. **Content** — a compact chip showing aggregate `↓ <rate>` and `↑ <rate>`.
   Aggregates come from `MonitorStore.Aggregate` over enabled servers, using the
   existing `ByteRateFormatter.Rate`. No torrent list, no server count, no
   status dot.
2. **Absent data** — when no configured server is online, show `—` for both
   rates (same rule as `PanelViewModel`), never a fabricated `0`.
3. **Placement** — always on top, borderless, transparent background, no taskbar
   entry, no window chrome.
4. **Dragging** — the chip can be dragged with the left mouse button. Its
   position is persisted in `settings.json` (physical pixels) and restored on
   next launch after being clamped into a visible work area.
5. **Click** — a press-and-release without movement opens (or activates) the
   tray dropdown panel. Dragging must not open it.
6. **Toggle** — disabled by default. Enabled/disabled from a checkable tray menu
   item and a Preferences checkbox; the state persists across restarts.
7. **Live updates** — rates follow each poll and each settings change, posted to
   the UI thread. The hidden widget does no work.
8. **Lifecycle** — one cached instance owned by `AppServices`; the widget follows
   the app's quit/language behaviour and never keeps the process alive.
9. **Localization** — the tooltip and the Preferences label exist in English and
   Simplified Chinese (arrows stay untranslated).

## Non-goals

- Per-server breakdown in the widget.
- A second tray icon, start-at-login, or click-through behaviour.
- Persisting any other UI-only state (row expansion stays unpersisted).

## Acceptance Criteria

- [ ] With the setting off (default), no widget is visible and nothing changes
      from today's behaviour.
- [ ] Enabling from Preferences or the tray menu shows the chip immediately;
      disabling hides it immediately.
- [ ] The chip shows the same aggregate rates as the panel's `↓ Speed` /
      `↑ Speed` values, updating on each poll.
- [ ] With every server offline, both rates render `—`.
- [ ] Dragging the chip moves it and persists the new position; after a restart
      it reopens at that position (clamped on-screen).
- [ ] A plain click on the chip opens the panel without moving the chip; a drag
      does not open the panel.
- [ ] Escape/click-away still hides the panel and leaves the chip visible.
- [ ] The setting and position round-trip through `settings.json`, and a corrupt
      or absent file falls back to off without crashing.
- [ ] English and Chinese tooltip/preference labels both render correctly.
- [ ] `dotnet build`, the CLI self-test and the Core tests pass.

## Notes

- Keep the widget implementation in `App`; no `Core` domain logic is added beyond
  the persisted settings fields.
