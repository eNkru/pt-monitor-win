# Implement — desktop widget

## Checklist

### 1. Core: settings

- [ ] `src/PTMonitor.Core/Store/SettingsStore.cs`
  - fields `_showDesktopWidget`, `_desktopWidgetPositionX`, `_desktopWidgetPositionY`,
    `_desktopWidgetLocked`; public getters; setters
    `SetShowDesktopWidget(bool)`, `SetDesktopWidgetPosition(int x, int y)`,
    `SetDesktopWidgetLocked(bool)`;
  - `SettingsFile` gains the four properties; `Persist()` and `Load()` carry them
    (defaults: widget off, no position, unlocked).

### 2. Core: self-test

- [ ] `src/PTMonitor.Core/Diagnostics/SelfTest.cs` — in `StoreChecks`:
  default (off / no position / unlocked), set → reload → assert.

### 3. App: the desktop layer helper

- [ ] `src/PTMonitor.App/Services/DesktopLayer.cs` (new)
  - `FindDesktopHost()`, `Attach(hwnd)`, `Detach(hwnd)`, `Place(hwnd, x, y, w, h)`,
    `MakeNonActivating(hwnd)`, `IsWindowsHostAvailable`.
  - every P/Invoke guarded by `OperatingSystem.IsWindows()`; no exception escapes.

### 4. App: the card

- [ ] `src/PTMonitor.App/Views/DesktopWidgetWindow.axaml` (new) — the card
      described in `design.md` §2 (`x:Name` on every text element).
- [ ] `src/PTMonitor.App/Views/DesktopWidgetWindow.axaml.cs` (new)
  - `ShowRates(down, up, online)`, `ShowSummary(servers, online, active)`,
    `PushSample(down, up)`, `SetLocked(bool)`, `RefreshLocalization()`;
  - drag / double-click / lock / context menu (mirroring `FloatingSpeedWindow`);
  - placement: restore the saved position, dock bottom-right, clamp to the
    work area, save on drag;
  - desktop attach on open, detach on close.

### 5. App: wiring

- [ ] `src/PTMonitor.App/Services/AppServices.cs`
  - `_desktopWidget` field, `SyncDesktopWidget()`, `UpdateDesktopWidget()`,
    `PushDesktopSample()`, `HideDesktopWidget()`, `OnDesktopWidgetClosed()`;
  - tray menu item "Show desktop widget" (check box);
  - the 1 s ticker runs when the chip needs it **or** the widget is visible;
  - a 2 s retry timer (`_desktopAttachTimer`) that puts the card back into the
    desktop layer after a shell restart (bounded to ~60 s);
  - recreate the widget if its window was closed by an Explorer restart.

### 6. App: preferences

- [ ] `src/PTMonitor.App/Views/PreferencesWindow.axaml(.cs)` — a "Desktop widget"
      check box next to the floating-speed toggle.

### 7. Localization

- [ ] `src/PTMonitor.App/Services/L10n.cs` — the new keys, English + zh-Hans.

### 8. Docs

- [ ] `README.md` — the feature bullet, the behaviour reference and the
      settings table.

## Validation

```bash
dotnet build PTMonitor.sln -c Release
dotnet test  PTMonitor.sln
dotnet run --project src/PTMonitor.Cli -- --selftest     # the built-in suite
```

Manual (Windows): the tray toggle shows the card behind an open window, Win+D
keeps it, the drag moves it, the lock freezes it, the lock survives a restart,
double-click opens the web console, and the right-click menu hides/quits.

## Rollback

Every part is additive and behind a persisted flag that defaults to `false`:
deleting the widget code leaves the app exactly as it is, and the settings file
keeps the unknown keys harmlessly (`System.Text.Json` ignores them).
