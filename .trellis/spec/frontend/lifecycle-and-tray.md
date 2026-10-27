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
| X / `Close()` on a window | the window closes; the process stays in the tray (`OnExplicitShutdown`). `PreferencesWindow` clears its cached reference via its `Closed` handler. The panel has no close button (borderless) — it hides on deactivate/Esc. |
| Tray menu → Quit | `Quit()` |

Windows are cached in `AppServices` (`_panel`, `_preferences`) and reused. Only
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

## Tray presentation

`AppServices.UpdateTray` maps `Monitor.MenuBarState` → `TrayIconFactory` icon and
sets `ToolTipText` (`PT Monitor — N servers · M online`, plus speeds when the
toggle is on and something is online). The menu is rebuilt on any settings
change so its headers follow the language.

`TrayIconFactory` renders 32×32 with `RenderTargetBitmap` — idle grey
circle, active green circle, red warning triangle. Tray icons are always
drawn, never loaded from files. (The one committed binary asset is the exe
icon `app.ico` — a different concern, see
[component guidelines](./component-guidelines.md).)

---

## Anti-patterns

- `ShutdownMode.OnLastWindowClose` or a real `MainWindow`.
- `Dispose`/shutdown work inside a `Closed` handler.
- Opening a second panel instance, or re-creating Preferences per open.
- A timer that is not stopped on close, or `Task.Delay` in the UI thread.
- Blocking `.Result`/`.Wait()` in a click handler.
- Adding an icon file instead of drawing it.
