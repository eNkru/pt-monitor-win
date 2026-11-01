# Fix tray-icon churn that crashes the app

## Goal

Stop `PTMonitor.exe` from terminating after hours of uptime with an unhandled
`Win32Exception` raised while updating the tray icon.

## Evidence

Windows Application log, `.NET Runtime` event 1026, five identical crashes
(2026-10-30 15:44 and 20:17, 2026-11-01 10:34, 16:54 and 18:59 UTC):

```
Exception Info: System.ComponentModel.Win32Exception (0x80004005): The operation completed successfully.
   at Avalonia.Win32.Interop.Win32Icon.CreateIcon(Bitmap bitmap, PixelPoint hotSpot)
   at Avalonia.Win32.Interop.Win32Icon..ctor(Win32Icon original, PixelSize size)
   at Avalonia.Win32.TrayIconImpl.UpdateIcon(Boolean remove)
   at Avalonia.Controls.TrayIcon.OnPropertyChanged(...)
   at PTMonitor.App.Services.AppServices.UpdateTray()
```

## Cause

`UpdateTray` ran on every `StatusesChanged` and assigned `_trayIcon.Icon`
unconditionally. Each assignment:

1. built a new `RenderTargetBitmap` and `WindowIcon` (`TrayIconFactory`), never
   disposed; and
2. made Avalonia wrap it in a new native `HICON`
   (`TrayIconImpl.UpdateIcon` → `Win32Icon..ctor(original, size)`).

At the default 2 s poll this is ~1,800 icon rebuilds per hour. After a few hours
the native `CreateIcon` call failed, and — because the app installs no unhandled
exception handler — the exception escaped the UI dispatcher and terminated the
process. The tray icon simply vanished.

## Requirements

1. The three tray icons are rendered once per process and reused; no bitmap is
   allocated on a poll tick.
2. `TrayIcon.Icon` is assigned only when `Monitor.MenuBarState` actually
   changes.
3. A failure while updating the icon must not terminate the agent; the tray
   keeps its previous icon and monitoring continues.
4. No log file is introduced (see `backend/logging-guidelines.md`).

## Acceptance Criteria

- [x] `TrayIconFactory.Idle/Active/Error` return stable cached instances.
- [x] `UpdateTray` compares against the last rendered state and skips the
      assignment when unchanged.
- [x] The assignment is scoped in a `try`/`catch` with a comment saying why the
      failure is swallowed.
- [x] `dotnet build -c Release`, `--selftest`, and `dotnet test` pass.
- [ ] Confirm on Windows that a long session no longer logs event 1026 (needs
      the user; the UI cannot run on this Linux box).

## Notes

- Avalonia never disposes the `WindowIcon` handed to `TrayIcon` (verified in
  `Avalonia.Win32/TrayIconImpl.cs`, 11.3.2: it disposes only its own internal
  `Win32Icon` and takes new ones via `LoadSmallIcon`). Caching caller-side
  instances is therefore safe.
- State oscillation (active ↔ idle) can still assign, but the cached bitmaps
  mean no per-tick allocation, and the interval is bounded by the poll rate.
