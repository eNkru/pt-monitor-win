# Design — desktop widget

## 1. Shape of the change

```
src/PTMonitor.App/
  Views/DesktopWidgetWindow.axaml        # the card
  Views/DesktopWidgetWindow.axaml.cs     # gestures, lock, placement
  Services/DesktopLayer.cs               # Win32: desktop host, attach, place
  Services/AppServices.cs                # wiring: show/hide, tick, tray entry
  Services/L10n.cs                       # new strings (en + zh)
  Views/PreferencesWindow.axaml(.cs)     # the toggle
src/PTMonitor.Core/
  Store/SettingsStore.cs                 # show / position / lock
  Diagnostics/SelfTest.cs                # settings checks
tests/PTMonitor.Core.Tests/              # mirrored by the CLI selftest
```

`Core` stays string-free and platform-free: the new settings are plain
booleans/ints in `SettingsStore`; everything Win32 lives in the App layer.

## 2. The window

`DesktopWidgetWindow` is a frameless, non-activating, non-taskbar window:

| Property | Value | Why |
|---|---|---|
| `SystemDecorations` | `None` | a card, not a window |
| `ShowInTaskbar` | `False` | it is not an app window |
| `ShowActivated` | `False` | it must not steal focus |
| `Topmost` | `False` | desktop level, not always-on-top |
| `CanResize` | `False` | fixed size |
| `Background` | opaque `#F0202023` | same rule as the chip: no alpha layer |

Size: fixed logical size (`Width=248`, `Height=152`), no `SizeToContent` — the
card must not resize when the content changes.

Layout (a `Border` with `CornerRadius="14"`, `Padding="12"`, one hairline
border — the same `card` look as the dropdown panel):

```
┌──────────────────────────────────────┐
│ PT Monitor                     [🔒]  │  header: title + lock button
│ ↓ 1.2 MB/s                           │  aggregate download (statValue)
│ ↑ 340 KB/s                           │  aggregate upload (statValue)
│ ▁▂▃▅▇▅▃▂  (SpeedGraph)              │  rolling graph, 60 samples
│ 3 servers · 2 online · 12 active     │  summary line
└──────────────────────────────────────┘
```

`SpeedGraph` is reused as-is (it draws two overlaid series and needs no
assets). The card is opaque, so the graph needs no outlined text — the plain
`TextBlock` rows sit on the card, not on the graph.

## 3. Desktop layer (the interesting part)

Goal: the card belongs to the **desktop**, not to the app's z-order.

### 3.1 Primary mechanism — attach to the shell's desktop host

Windows keeps the wallpaper and the desktop icons in special top-level
windows: `Progman` and one or more `WorkerW` windows. A window that is made a
**child** of the desktop host is painted in the desktop layer:

* every application window covers it,
* it is not minimized by Win+D ("Show desktop"),
* it cannot take keyboard focus away from the foreground app.

`Services/DesktopLayer.cs` does this with `user32`/`shell32`:

```csharp
IntPtr FindDesktopHost();          // Progman -> WorkerW behind the icons
bool  Attach(IntPtr hwnd);         // style swap + SetParent + verify
void  Detach(IntPtr hwnd);         // undo, used when the widget is hidden
bool  Place(IntPtr hwnd, int x, int y, int w, int h);  // parent-relative
```

Host discovery (the canonical recipe):

1. `progman = FindWindow("Progman", null)`.
2. `defView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null)` — the icon
   view. If it is a child of `Progman`, the `WorkerW` **behind** the icons is
   found with `FindWindowEx(0, defView, "WorkerW", null)`; that is the host we
   want (the card then sits above the wallpaper and below the icons).
3. On Windows 11 builds where `SHELLDLL_DefView` is not under `Progman`, the
   `WorkerW` windows are enumerated and the one that owns the icon view wins;
   if none has it, `Progman` itself is the host.
4. No host at all → the widget stays a normal window (fallback, below).

`Attach` performs the documented dance (`SetParent` does not change the window
styles itself):

```
style = GetWindowLongPtr(hwnd, GWL_STYLE)
SetWindowLongPtr(hwnd, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD)
SetParent(hwnd, host)
```

and then **verifies** the result: the parent must be the host and the window
rect must match the requested rect. If it does not, the window is detached
again and the widget falls back to the plain always-on-top window.

`WS_EX_NOACTIVATE` is added to the window's extended style in both cases, so
clicking the card never activates it and never raises it above the app the
user is in.

### 3.2 Fallback — a normal always-on-top window

When the platform is not Windows, or the host cannot be found, or the attach
verification fails: the card behaves like the floating chip
(`Topmost=True`, positioned by Avalonia). The user still gets a usable widget;
the log records why the desktop layer was not used.

### 3.3 Position and coordinates

The position is stored in **physical screen pixels** (like the chip's
`FloatingPositionX/Y`). Once the window is a child of the desktop host, the
coordinates are relative to the host's client area, so `ScreenToClient` is used
to convert. The host covers the whole virtual desktop, so the conversion is a
constant offset in practice.

Because the card is a child window, Avalonia's `Position` property is not used
after the attach — the placement is done with `SetWindowPos`.

**DPI caveat (documented, accepted):** a child window inherits its parent's
DPI context, so on a secondary monitor with a different scale the card is
rendered at the host's scale and looks physically smaller there. The default
placement is the primary monitor's bottom-right corner.

### 3.4 Explorer restarts

If the shell restarts, the host window dies and the child window with it. The
widget window's `Closed` handler tells `AppServices`, which recreates the
window (the setting is still on) and re-attaches it to the new host.

## 4. Data flow

```
MonitorStore.Aggregate ──► AppServices.UpdateDesktopWidget()  (on every poll)
                        └─► widget.ShowRates(down, up, online)
                        └─► widget.ShowSummary(servers, online, active)

1 s DispatcherTimer ────► widget.PushSample(down, up) ──► SpeedGraph
```

The ticker is shared with the floating chip: it runs when the chip is in graph
mode **or** the widget is visible, and each sink decides whether it needs a
sample. The graph needs the timer even when the rates do not change, because
`StatusesChanged` is suppressed for an unchanged rate.

Only the **aggregate** is used, so the widget does not need the torrent list:
`MonitorStore.ListDemand` stays driven by the panel alone.

## 5. Settings

`SettingsStore` gains three fields (camelCase in `settings.json`):

| Field | Type | Default | Meaning |
|---|---|---|---|
| `showDesktopWidget` | bool | `false` | the widget is on the desktop |
| `desktopWidgetPositionX` | int? | `null` | physical screen X |
| `desktopWidgetPositionY` | int? | `null` | physical screen Y |
| `desktopWidgetLocked` | bool | `false` | the position is frozen |

The setters follow the store's rules: change → `PersistAndNotify()` →
`Settings.Changed` → `AppServices.OnSettingsChanged` → the widget is
re-synced. No second source of truth.

## 6. Gestures

Mirroring `FloatingSpeedWindow`:

- left press → capture the pointer on the card,
- move past the 4 px threshold → `BeginMoveDrag` (the window manager owns the
  move) and save the position,
- release without a drag → a click; two clicks in the same spot within 400 ms
  and 6 px → open the web console,
- **the lock is checked before the drag starts**: a locked card ignores the
  drag entirely (the press is not even captured),
- right click → the context menu (built in code, like the chip's, because the
  first item's header names the server and the lock item's header follows the
  state).

The lock button in the header is a real `Button` (not part of the drag
surface): it toggles the lock and stops the event from reaching the drag
handlers (`e.Handled = true`).

## 7. Localization

New keys (English key = the English text, Chinese value in `L10n.Zh`):

| Key | zh-Hans |
|---|---|
| `Show desktop widget` | `显示桌面挂件` |
| `Hide desktop widget` | `隐藏桌面挂件` |
| `Lock position` | `锁定位置` |
| `Unlock position` | `解锁位置` |
| `Desktop widget` | `桌面挂件` |
| `%lld active` | `%lld 个活动` |

The summary line reuses the existing helpers: `L10n.ServerCount(total,
online)` ("3 servers · 2 online") and `L10n.ActiveTorrents(active, total)`.

## 8. Risks

| Risk | Mitigation |
|---|---|
| The desktop host cannot be found (Explorer variant) | fallback to an always-on-top window; the widget is never lost |
| `SetParent` leaves the card in a wrong rect | verify after attach, detach + fall back |
| Avalonia fights the child-window placement | placement is done through `SetWindowPos` only; Avalonia's `Position` is not used after the attach |
| The card steals focus | `WS_EX_NOACTIVATE` on the card |
| Explorer restarts kill the card | `Closed` → recreate + re-attach |
| The Windows path cannot be exercised on a Linux dev box | the fallback path is the one under test locally; the Windows path is small, guarded and logged |
