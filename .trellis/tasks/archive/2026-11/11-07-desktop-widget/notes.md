# Notes — desktop widget

## What is verified here (Linux dev box)

- `dotnet build PTMonitor.sln -c Debug` — 0 errors.
- `dotnet test tests/PTMonitor.Core.Tests` — 151 passed.
- `dotnet run --project src/PTMonitor.Cli -- --selftest` — 698 checks passed.
- The app starts with the widget enabled, the card is created and placed, and
  the process survives the run with a clean log.
- **The look was verified on the X11 display**: the card was captured with
  `xwd` and composited over a light and a dark wallpaper. It is square, there
  is no border, and the mask is 50 % black — the wallpaper reads through it on
  both, and the white glyphs stay readable in both cases.

## What still has to be checked on Windows

The Win32 part is Windows-only and could not be exercised here:

1. The card is **behind** an application window (not in front of it).
2. **Win+D** does not hide it (it restores itself from `WindowState.Minimized`).
3. Clicking the card does not take the focus away from the app in front
   (`WS_EX_NOACTIVATE`).
4. The card is above the desktop icons and wallpaper, below every app window.
5. The **right-click menu** opens above the other windows.
6. **Dragging** the card moves it and the position survives a restart.
7. The **lock** blocks the drag and survives a restart, while the rates, the
   graph, the double-click and the menu keep working.
8. A **secondary monitor with a different scale**: the card is a normal
   top-level window again, so it scales with the monitor (this is better than
   the re-parented version, which inherited the host's DPI context).

If (5) fails, the fallback is to host the menu in a small top-level window
instead of an Avalonia `ContextMenu` owned by the card.

## Known limitations carried over

- **Every settings write restarts every poll loop** (`MonitorStore.Reconcile`
  runs on `Settings.Changed`). Dragging the card or toggling the lock
  therefore interrupts the monitoring briefly. This is pre-existing behaviour
  (the floating chip's position save does the same) and was left alone here.
- The desktop window discovery follows the canonical `Progman`/`WorkerW`
  recipe, but the layout differs between Windows 10 and Windows 11 builds; if
  the window is not found, the card keeps its normal z-order and the log says
  so.

## Why the card is not a child of the desktop host (reversed decision)

The first version re-parented the card into the shell's desktop host
(`SetParent` to `Progman`/`WorkerW`) — the technique desktop widgets and
wallpapers use. It worked, but it forced the card to stay **opaque**: Avalonia
renders a transparent window without a redirection bitmap (DirectComposition)
or with DWM blur-behind, and neither survives being made a child of the
shell's desktop, so the card would simply not be drawn.

The user asked for a **transparent** card, so the re-parenting was replaced by
a z-order pin (`SetWindowPos` with the desktop window as the window to sit
above). The card keeps the per-pixel transparency and is still behind every
application window.

## Close-out (2026-11-08)

Merged as PR #17 (`1997d24`) without a Windows box to check on. Closed out with a
final full-scope re-verification on the Linux development machine:

- `dotnet build PTMonitor.sln -c Release` — 0 errors.
- `dotnet test PTMonitor.sln` — 151 passed.
- `dotnet run --project src/PTMonitor.Cli -- --selftest` — 698 checks passed.
- All 23 `L10n.S` keys the widget uses exist in the `Zh` table, plus
  `L10n.WidgetSummary`; no key silently falls back to English.

Two stale-text defects from the transparent/mask rework were fixed in this pass
(the code was right, the prose was not):

- `README.md` *Known limitations* still claimed the widget *"inherits the
  desktop host's DPI context"* — that was true of the **reverted** `SetParent`
  design and became false once the card went back to being a top-level window.
  Replaced with the actual current limitation: placement is not multi-monitor
  aware (a saved position that no longer lands on any screen docks bottom-right
  on the primary).
- `DesktopWidgetWindow.axaml` header comment said *"40 % black plate"*; the
  mask is `#80000000` (50 %). Also dropped the word "plate" — there is no plate,
  the mask is the window background.

`.trellis/spec/frontend/lifecycle-and-tray.md` gained the matching DPI bullet
under *Desktop widget*, and an explicit "do not reintroduce `SetParent` to
'fix' placement" warning.

The Windows-only checklist above is **unverified and deferred**; it is recorded
in `prd.md` under *Deferred — Windows-only verification*.
