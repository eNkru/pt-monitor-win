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
- `AppServices`'s constructor is ordered so the tray icon exists before
  anything that can block or paint: `Instance` → `Settings` → `L10n.SetLanguage`
  → the floating ticker → the tray icon and its glyph → `TrayIcon.SetIcons` →
  the secret store (DPAPI) → `MonitorStore` → the theme/`StatusesChanged` hooks
  and `Settings.Changed` → `UpdateTray()`/`Monitor.Start()` → the `Clicked`
  handler.
- The two optional windows are **posted**, not created inline: one
  `Dispatcher.UIThread.Post(…, DispatcherPriority.Background)` runs
  `SyncFloatingWindow()` and, when `Settings.Servers.Count == 0`, the first-run
  Preferences. `Background` priority lets the icon's first frame and the message
  loop win the queue, so neither window can delay the icon.
- The order carries invariants — re-read them before "tidying" it:
  `_trayIcon` must exist before `Settings.Changed += OnSettingsChanged`
  (`OnSettingsChanged` assigns `_trayIcon.Menu`), `Secrets` must exist before
  `new MonitorStore(...)`, `Monitor` before `UpdateTray()`/`StatusesChanged`/
  `Start()`, and `_floatingTicker` before anything that can call
  `SyncFloatingWindow()`.
- The launch path writes a **timeline** (`Core/Diagnostics/StartupTrace`):
  `Begin()` in `Program.Main` before `Log.Initialize`, one `Mark` per phase
  (`log-ready`, `avalonia`, `glyph`, `tray-set`, `stores`, `windows`), and one
  `Flush()` in the posted callback. One `startup: timeline …` line per launch,
  with cumulative, non-decreasing offsets and a `total`. No lock — every mark
  runs on the UI thread. The phase order mirrors the constructor order; keep the
  two in step or the offsets stop being monotonic.
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
| Right-click the floating chip | context menu: open the first server's web console / hide the chip / quit |
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
- Cursor: the **system hand cursor** (`new Cursor(StandardCursorType.Hand)`).
  The app draws no cursor of its own — `Services/CursorFactory.cs` and the
  hand-drawn open hand were removed on purpose (the user asked for the system
  cursor). Do not reintroduce a drawn cursor asset.
- Right-click opens a `ContextMenu` on the chip with three items: **open web
  console** (the first *enabled* server's `ServerConfig.WebUiUri()`, opened
  through `Services/UrlLauncher`), **hide the floating speed window**
  (`Settings.SetShowFloatingSpeeds(false)` — persisted, so the chip stays
  hidden across restarts; the tray toggle brings it back) and **quit** (`Quit()`).
  The menu is built in code-behind: the first item's header names the server
  and is disabled when there is none, which XAML cannot express. A right-click
  never reaches the click path — only the left button sets `_pressed`.
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

**Assign `TrayIcon.Icon` only when the state or the theme changes.**
`TrayIconFactory` caches one `WindowIcon` per (state, theme) pair, and
`UpdateTray` skips the assignment while both are unchanged. Reassigning the
icon on every status change builds a
new bitmap plus a new native icon per poll; at the default 2 s interval that
exhausted the native icon path after a few hours and crashed the process with an
unhandled `Win32Exception` (`AppServices.UpdateTray`, event log 1026) — see
`.trellis/tasks/11-01-tray-icon-crash`. The assignment is also wrapped in a
scoped `catch`: a cosmetic icon failure must never take the agent down.

Do not create a `WindowIcon` (or call `Render`) outside the factory. `Render` is
public for tests/artwork but the tray must use the cached accessors.

### The artwork

The tray icon is **one flat monochrome glyph** — a bold down arrow over a short
bar, in the spirit of the exe icon's sub-32 px variant but drawn separately —
inside a hairline rounded-square frame, so it reads as a square badge in the
tray. No tile, no fill, no second arrow, no progress meter: the floating chip
already shows the live rates, so the tray icon is an identity mark, not a
display.

The frame is a rounded square stroked with a hairline (1 px at 16 px, at most
2 px), one pixel inside the icon edge so it is not clipped. The corner radius
is small (10 % of the side): a hairline stroke cannot render a large radius at
16 px without the corners turning into a staircase. The glyph inside is shrunk
to keep a clear gap.

The colour follows the taskbar, not the app's accent colour:

| Theme | Idle | Active | Error |
|---|---|---|---|
| light taskbar | `#1F1F1F` @ 55 % | `#1F1F1F` @ 100 % | `#C42B1C` @ 100 % |
| dark taskbar | `#FFFFFF` @ 55 % | `#FFFFFF` @ 100 % | `#FF99A4` @ 100 % |

Idle is the same glyph at a lower opacity, so one geometry covers all three
states. Tray icons are always drawn, never loaded from files. (The one
committed binary asset is the exe icon `app.ico` — a different concern, see
[component guidelines](./component-guidelines.md).)

### Theme

The app requests the default theme variant, so
`Application.Current.ActualThemeVariant` is the OS light/dark setting, and the
taskbar follows it. Avalonia's Win32 backend reports an OS theme switch as
`WM_SETTINGCHANGE`/"ImmersiveColorSet" and raises
`Application.ActualThemeVariantChanged`; `AppServices` subscribes to it and
redraws the tray icon. There is no timer and no registry polling.

### Packaging

`TrayIconFactory` renders the glyph at 16/20/24/32/48 px and packs the results
into a real `.ico` container in memory (`Services/IcoEncoder.cs`, 32-bit BGRA
entries with straight alpha), then hands it to `WindowIcon` as a stream.

Do **not** hand Avalonia a `RenderTargetBitmap` instead. `WindowIcon(Bitmap)`
goes through `CreateIconIndirect` with `fIcon = FALSE` on Win32: the alpha
channel is thrown away and only a 1-bit AND mask survives, so the glyph gets
hard edges and Windows downscales one bitmap to the taskbar size. A real
`.ico` keeps the alpha, and Avalonia picks the entry whose size matches the
taskbar (`LoadSmallIcon` asks for 16 × the taskbar scale). The shell still
receives a single-resolution `HICON` and scales it when the taskbar DPI is not
an exact match.

### Tray vs exe icon

The tray glyph and the exe icon are **different drawings**: the exe icon
(`scripts/make-app-icon.py`, `app.ico`) keeps the coloured tile, the tray uses
the flat monochrome mark. They no longer share constants — do not assume a
change to one has to be repeated in the other.

---

## Anti-patterns

- `ShutdownMode.OnLastWindowClose` or a real `MainWindow`.
- `Dispose`/shutdown work inside a `Closed` handler.
- Opening a second panel instance, or re-creating Preferences per open.
- A timer that is not stopped on close, or `Task.Delay` in the UI thread.
- Blocking `.Result`/`.Wait()` in a click handler.
- Adding an icon file instead of drawing it.
- Drawing a cursor of your own — the system cursor is used.
- Handing the tray a `RenderTargetBitmap` instead of a `.ico` stream (that
  path drops the alpha channel).
