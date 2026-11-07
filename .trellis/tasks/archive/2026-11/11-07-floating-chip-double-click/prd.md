# PRD — Floating chip: double-click opens the web console, single click does nothing

## Background

The floating speed chip had three left-button behaviours sharing one gesture:
a press+release under the 4 px drag threshold opened the torrent panel, a drag
moved the chip, and a right-click opened the context menu. The panel was the
surprising one: the user treats the chip as a positional, always-visible
readout, and the panel popping up on every stray click got in the way.

## Requirements

1. A single left-click on the floating chip must do **nothing** — the chip is
   a drag handle and a launcher, not a panel toggle. The torrent panel keeps
   its existing openers (tray-icon click, `TogglePanel`).
2. Drag and move must keep working exactly as before: crossing the 4 px
   threshold hands the press to `BeginMoveDrag`, and the new position is
   persisted (`FloatingPositionX/Y`).
3. A **double left-click** must open the web UI of the first *enabled* server
   directly in the default browser — the same URL the right-click menu item
   opens (`ServerConfig.WebUiUri()`: qBittorrent at the root, Transmission at
   `/transmission/web/`), through `AppServices.OpenFirstServerWebConsole()`.
4. Double-click detection must not depend on the platform double-click
   message: the chip captures the pointer while pressed, so the gesture is
   recognised by hand (two clicks in the same spot within 400 ms and 6 px).
5. A drag must reset the click pair, and a third click after a recognised
   double-click must not open a second browser tab.
6. The chip tooltip must describe the new gesture, localized (English key +
   zh-Hans value); the now-unused `Open PT Monitor` key is removed.

## Constraints

- Touch only the floating chip path (`FloatingSpeedWindow`); the tray icon,
  panel and preferences behaviours are untouched.
- Keep the opaque-chip rules and the system hand cursor
  (`.trellis/spec/frontend/lifecycle-and-tray.md`).
- Document the change in README and the spec.

## Acceptance criteria

- [x] Single click on the chip opens nothing; drag still moves the chip and
      the position survives a restart.
- [x] Double-click opens the first enabled server's web console in the
      default browser; with no enabled server it silently does nothing
      (existing `OpenFirstServerWebConsole` guard).
- [x] A drag never counts as a click pair; a triple click opens one tab only.
- [x] Tooltip reads "Double-click to open the web console" / "双击打开网页控制台".
- [x] `dotnet build -c Release` clean, `dotnet test` 150/150,
      `PTMonitor.Cli --selftest` OK (692 checks), `scripts/build.sh --rid
      win-x64` packages successfully.

## Outcome

Delivered on `feat/floating-chip-double-click` → PR #16. One commit
(`18a7a87`) touching `FloatingSpeedWindow.axaml.cs`, `L10n.cs`, README and
`frontend/lifecycle-and-tray.md`.
