# Lifecycle & Tray

> The tray icon is the app. Windows are disposable overlays.

---

## Startup

```csharp
// App.OnFrameworkInitializationCompleted
desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
desktop.MainWindow = null;
AppServices.Initialize();
```

- There is no main window and closing the last window must not exit.
- `AppServices`'s constructor builds the `TrayIcon`, attaches the native menu,
  starts monitoring and, when `Settings.Servers.Count == 0`, opens Preferences
  (first-run path).
- `Initialize()` is idempotent; `Instance` is assigned before anything that can
  re-enter.

---

## Show / hide

| Action | Result |
|---|---|
| Left-click tray icon | `TogglePanel()` |
| `Deactivated` on the panel | `Hide()` |
| `Escape` / `Cancel` | `Hide()` |
| Click on the floating chip | `ShowPanel()` (show + activate) |
| X / `Close()` on a window | the window closes; the process stays in the tray (`OnExplicitShutdown`). `PreferencesWindow` clears its cached reference via its `Closed` handler. The panel has no close button (borderless) — it hides on deactivate/Esc. |
| Tray menu → Quit | `Quit()` |

`TogglePanel()` answers the tray click; `ShowPanel()` is the always-show variant
the floating chip calls, so a click there never *hides* an already-open panel.

Windows are cached in `AppServices` (`_panel`, `_preferences`, `_floating`) and reused. Only
`ServerEditorWindow` is created per open, because it is a modal dialog owned by
Preferences.

What actually keeps the process alive is `ShutdownMode.OnExplicitShutdown` —
no window is ever `.Close()`d except the editor dialog; the panel and
preferences are `Hide()`n. Note: `AppServices.CanCloseWithoutQuitting` exists
but is currently **unreferenced** (dead code). Do not cite it as the reason
closing is safe, and do not build new behaviour on it without wiring it to a
`Closing` handler.

---

## Quit

```csharp
public void Quit()
{
    _quitRequested = true;
    Monitor.StopAsync();                       // fire and forget by design
    if (GetApp().ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        desktop.Shutdown();
}
```

`Quit` is the only path that ends the process. Do not call `desktop.Shutdown()`
from anywhere else.

---

## Panel positioning

`MainPanelWindow.PositionNearTray` docks to the bottom-right of the **primary**
screen's `WorkingArea`, divided by `RenderScaling`, using `EdgeGap = 12`. Height
comes from `DesiredSize` with a 360 fallback. Avalonia cannot ask the tray icon
for coordinates, so secondary taskbars are not detected (documented limitation).

When changing this, keep the `RenderScaling` division and the `double.IsNaN`
height guard.

---

## Freshness timer

`MainPanelWindow` owns one `DispatcherTimer` (1 s) calling
`ViewModel.RefreshFreshness()`. It starts in `OnOpened` and stops in `OnClosed`,
so a hidden panel does no work. Keep the stop — a leaked timer keeps the panel
alive.

---

## Floating speed chip

`FloatingSpeedWindow` is the optional always-on-top square chip
(`Topmost`, `ShowActivated=False`, `SystemDecorations=None`,
`ShowInTaskbar=False`, `SizeToContent`), created and cached by `AppServices`.
It is **opaque** (fixed `Background`, no `TransparencyLevelHint`, no
`CornerRadius`, no `BoxShadow`): a transparent/topmost window costs an alpha
composition layer every frame, which is not worth it for a 56 px widget. Keep it
that way unless the design genuinely needs transparency.

- `SyncFloatingWindow()` (called from the constructor and every
  `Settings.Changed`) shows/hides it to match `Settings.ShowFloatingSpeeds`,
  applies `Settings.FloatingDisplay`, and starts/stops the 1 s graph ticker.
- `UpdateTray()` refreshes the numeric rates on every poll. The ticker is only
  needed for graph mode, because `StatusesChanged` is suppressed when the rates
  are unchanged — without sampling the line would stop rolling.
- Display modes (`FloatingDisplayMode`): `Numbers` — `↓ rate` / `↑ rate` with
  `—` when `Aggregate.OnlineServers == 0`; `Graph` — `SpeedGraph`, a custom
  control that draws two overlaid series (download green `#35C46F`, upload
  orange `#F2994A`) over the last 60 one-second samples, scaled to the peak of
  the visible window. No assets, no chart library.
- Gesture: a press+release under the 4 px threshold opens the panel; crossing
  the threshold calls `BeginMoveDrag` instead, so a drag never clicks. The chip
  `Border` sets `Background="Transparent"` purely as a hit-test surface — a
  `Border`/`Grid` with a null background does not receive pointer events in its
  empty areas, so without it only the digits would be draggable.
- Cursor: `Services/CursorFactory.OpenHand()` draws a 32 px open-hand cursor at
  runtime — the macOS grab-hand look (white body, dark outline, fanned fingers,
  slight lean, rounded wrist, hot spot on the index fingertip) — and the window
  assigns it. Windows/Avalonia's `StandardCursorType.Hand` is the pointing
  finger, not an open hand, and there is no standard open-hand cursor to ask
  for, so it is drawn like the tray icons, with no binary asset.
- `Place()` restores `FloatingPositionX/Y` when the point still lands on a
  screen's work area, otherwise docks to the primary work area's bottom-right
  (±12 px gap), then clamps. The position is written back once per drag.
- Enabling/disabling never closes the window — it is hidden, so the rates and
  the saved position survive a toggle. The ticker is stopped while hidden.

## Tray presentation

`AppServices.UpdateTray` maps `Monitor.MenuBarState` → `TrayIconFactory` icon and
sets `ToolTipText` (`PT Monitor — N servers · M online`, plus speeds when the
toggle is on and something is online). The menu is rebuilt on any settings
change so its headers follow the language.

`TrayIconFactory` renders 32×32 with `RenderTargetBitmap` using one shared
geometry: a flat, light rounded tile with a hairline ring, a down/up arrow pair
and a pill progress meter. The states differ only in colour — idle dims both
arrows and leaves the pill an empty track, active draws the solid blue down
arrow with a 62 % pill fill (the app-icon artwork proper), error swaps blue for
red, fills the pill and outlines the tile in red. Tray icons are always
drawn, never loaded from files. (The one committed binary asset is the exe
icon `app.ico` — a different concern, see
[component guidelines](./component-guidelines.md).)

The tray artwork and the exe icon are the **same drawing** described twice:
`TrayIconFactory.cs` for 32 px and `scripts/make-app-icon.py` for the ICO's
seven sizes. Change the palette or proportions in both, or the tray and the
Explorer icon silently drift apart. The script also renders the simplified
single-arrow variant used below 32 px, which the factory never needs because
the tray is always 32 px.

---

## Anti-patterns

- `ShutdownMode.OnLastWindowClose` or a real `MainWindow`.
- `Dispose`/shutdown work inside a `Closed` handler.
- Opening a second panel instance, or re-creating Preferences per open.
- A timer that is not stopped on close, or `Task.Delay` in the UI thread.
- Blocking `.Result`/`.Wait()` in a click handler.
- Adding an icon file instead of drawing it.
