# Desktop widget (desktop-level, lockable)

## Goal

A Nexus-style floating **desktop widget** for PTMonitor: a small always-present
card that lives on the Windows desktop (behind application windows), shows the
aggregate transfer state, can be dragged anywhere, and can be **locked** so it
stays exactly where it was put.

The reference is `hello-nexus/nexus` (its `nexus-overlay` desktop widget layer:
a floating card, draggable, with a per-widget `Locked` flag that blocks
drag/edit/resize until the user unlocks it). Nexus is AGPL-3.0, so only the
*behaviour* is re-implemented here — no code is copied.

## Requirements

### R1 — A desktop-level widget window

- A new frameless, **transparent** readout (`DesktopWidgetWindow`) that lives
  in the **desktop layer**: it is behind every application window, it survives
  "Show desktop" (Win+D), and it never steals keyboard focus from the app the
  user is working in.
- **No card, but a mask**: no border, square corners, and a soft translucent
  mask (50 % black) behind the content — the wallpaper still shows through it,
  the way a Nexus glass plate does — plus a hard outline on every run so the
  text stays readable on any background.
- On Windows the window is **pinned in the z-order directly above the shell's
  desktop window** (`Progman` / the `WorkerW` that owns the icons), below every
  application window, with `WS_EX_NOACTIVATE` so a click never activates it.
  It is deliberately **not** re-parented into the desktop host: Avalonia
  renders a transparent window without a redirection bitmap (DirectComposition)
  or with DWM blur-behind, and neither survives being made a child of the
  shell's desktop — the card would simply not be drawn.
- Where the desktop window cannot be found (non-Windows builds, or a shell
  restart) the card keeps its normal z-order and says so in the log.

### R2 — Content (simple first)

The card shows, from the existing `MonitorStore` aggregate only:

- the aggregate download rate and upload rate (big, monospaced),
- the rolling two-series speed graph (the existing `SpeedGraph` control, one
  sample per second, 60 samples),
- a summary line: how many servers are configured / online, and how many
  torrents are active.

No torrent list, no per-server breakdown, no charts beyond the graph. Those are
explicit follow-ups (see *Out of scope*).

### R3 — Add to / remove from the desktop

- A tray-menu entry ("Show desktop widget", a check box) toggles the widget.
- A Preferences check box does the same.
- The choice is persisted, so the widget comes back on the next launch.
- Turning it off hides the window and stops its timer; it never destroys the
  saved position.

### R4 — Move, and remember where it was

- The card is dragged with the left button; a drag past a 4 px threshold moves
  it and writes the new position back to the settings.
- The position is restored on the next launch when it still lands on a screen's
  work area; otherwise the card is docked to the bottom-right of the primary
  work area (same rule as the floating chip) and clamped inside it.

### R5 — Lock freezes the position only

- A lock control in the card header (and a "Lock position" / "Unlock position"
  item in the right-click menu) toggles the lock; the state is persisted.
- **Locked**: dragging does nothing — the card cannot be moved by accident.
  Everything else keeps working: the rates still update, the graph still rolls,
  double-click still opens the web console, the right-click menu still offers
  hide/quit, and the lock control still unlocks it.
- **Unlocked**: the card can be dragged again.

### R6 — Familiar gestures

- Double-click opens the first enabled server's web console in the default
  browser (same target and same hand-rolled double-click detection as the
  floating chip).
- Right-click opens a menu: open web console / lock (unlock) position / hide
  the widget / quit.

### R7 — Bilingual

Every new string exists in English and Simplified Chinese, applied live like
the rest of the app.

## Constraints

- **Read-only by design** — the widget only shows what the app already polls.
  It never writes to a server.
- **No new dependencies** — Avalonia + the Win32 `user32`/`shell32` calls the
  app already uses (`GetCursorPos`).
- **The chip keeps working exactly as it does.** The widget is a second,
  independent surface; both may be on screen at the same time.
- The app must still start and run on a machine where the desktop host window
  cannot be found (no crash, no invisible widget with no way back).
- Windows is the shipping platform; Linux/macOS are development platforms, so
  the desktop-layer path is Windows-only and guarded.

## Acceptance Criteria

- [ ] The tray menu and Preferences both show a "Show desktop widget" toggle,
      and the widget appears/disappears with it.
- [ ] The widget shows the aggregate ↓/↑ rates, the rolling graph and the
      summary line; all three update live while it is visible.
- [ ] On Windows the widget renders **behind** application windows and stays
      visible after Win+D.
- [ ] The widget can be dragged, and the position survives a restart.
- [ ] With the lock on, dragging the card does not move it; the rates, the
      graph, the console shortcut and the right-click menu still work.
- [ ] The lock state survives a restart.
- [ ] Double-click opens the web console of the first enabled server.
- [ ] Every new string is translated in Simplified Chinese.
- [ ] `dotnet build`, the built-in check suite (`PTMonitor.Cli --selftest`) and
      `dotnet test` all pass.

## Out of scope (follow-ups, deliberately)

- Per-server rows and top-torrent rows inside the widget (the user asked for
  the simple content first).
- A Windows 11 Widgets-board provider (WebView2 widget) — a different,
  much larger surface.
- Multi-widget instances, widget sizing/grid placement, per-widget profiles.
- Copying Nexus code or assets (AGPL-3.0).

## Notes

- "Desktop level" is the user's wording for *behind the app windows*, not
  "always on top" — the existing floating chip already is always-on-top.
- The lock deliberately freezes the **position only**; it is not a "do not
  touch" mode.
