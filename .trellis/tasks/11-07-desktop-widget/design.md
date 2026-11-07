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
| `Background` | transparent | the wallpaper shows through |
| `TransparencyLevelHint` | `Transparent` | per-pixel alpha |

Size: fixed logical size (`Width=250`, `Height=150`), no `SizeToContent` — the
card must not resize when the content changes.

Layout — **no card**: no fill, no border, square corners. Every run is drawn
with `Views/OutlinedText`, which puts a hard outline around the glyphs, so the
text stays readable on the wallpaper without an opaque plate behind it:

```
 PT Monitor                   Lock position
 ↓ 1.2 MB/s
 ↑ 340 KB/s
 ▁▂▃▅▇▅▃▂  (SpeedGraph)
 3 servers · 2 online · 12 active
```

`SpeedGraph` is reused as-is (it draws two overlaid series and needs no
assets).

## 3. Desktop layer (the interesting part)

Goal: the card belongs to the **desktop**, not to the app's z-order.

### 3.1 Mechanism — pin the z-order above the desktop

Windows keeps the wallpaper and the desktop icons in special top-level
windows: `Progman` and one or more `WorkerW` windows. A window that is
**pinned directly above** one of them is painted in the desktop layer: every
application window covers it, and it is never activated by a click.

`Services/DesktopLayer.cs` does this with `user32`:

```csharp
bool PinAboveDesktop(IntPtr hwnd);   // SetWindowPos(hwnd, host, …)
bool MakeNonActivating(IntPtr hwnd); // WS_EX_NOACTIVATE
```

Host discovery (the canonical recipe):

1. `progman = FindWindow("Progman", null)`.
2. `defView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null)` — the icon
   view. If it is a child of `Progman`, that is the host.
3. On Windows 11 builds where `SHELLDLL_DefView` is not under `Progman`, the
   `WorkerW` windows are enumerated and the one that owns the icon view wins;
   if none has it, `Progman` itself is the host.
4. No host at all → the card keeps its normal z-order (see 3.2).

`WS_EX_NOACTIVATE` is added to the window's extended style, so clicking the
card never activates it and never raises it above the app the user is in.

**Why not `SetParent` into the desktop host?** The first version of this
design did that (the technique desktop widgets and wallpapers use) and it
worked — but it forced the card to stay **opaque**, because Avalonia renders a
transparent window without a redirection bitmap (DirectComposition) or with
DWM blur-behind, and neither survives being made a child of the shell's
desktop: the card would simply not be drawn. The user asked for a transparent
widget, so the z-order pin replaces the re-parenting. It needs no
re-parenting, keeps the per-pixel transparency, and still puts the card behind
every application window.

### 3.2 Fallback — the normal z-order

When the platform is not Windows, or the desktop window cannot be found, the
card keeps its normal z-order and the log records why. The pin is re-asserted
on every sample, so a shell restart heals itself.

### 3.3 Position and coordinates

The position is stored in **physical screen pixels** (like the chip's
`FloatingPositionX/Y`), and Avalonia's own `Position` moves the card — the
card is an ordinary top-level window again, so the drag is the chip's
(`BeginMoveDrag`).

**Win+D** ("Show desktop") minimizes every top-level window, so the card
restores itself when `WindowState` flips to `Minimized`: it belongs to the
desktop, not to the taskbar.

### 3.4 Explorer restarts

If the shell restarts, the desktop window changes identity. The pin is
re-asserted on every sample, so the card lands on the new desktop window
without any special handling.

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
| `%lld server · %lld online · %lld active` | `%lld 台服务器 · %lld 台在线 · %lld 个活动` |
| `%lld servers · %lld online · %lld active` | `%lld 台服务器 · %lld 台在线 · %lld 个活动` |

The summary line is **one** templated key (`L10n.WidgetSummary`), never
glued-together fragments.

## 8. Risks

| Risk | Mitigation |
|---|---|
| The desktop window cannot be found (Explorer variant) | the card keeps its normal z-order; the pin is retried on every sample |
| A transparent window cannot be a child of the desktop host | the z-order pin replaces the re-parenting (see 3.1) |
| The card steals focus | `WS_EX_NOACTIVATE` on the card |
| Win+D minimizes the card | it restores itself from `WindowState.Minimized` |
| Explorer restarts change the desktop window | the pin is re-asserted on every sample |
| The Windows path cannot be exercised on a Linux dev box | the transparency was verified on X11 (91 % of the pixels are fully transparent); the Win32 z-order pin is small, guarded and logged |
