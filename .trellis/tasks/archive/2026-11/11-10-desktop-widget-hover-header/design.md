# Design — hover-only widget header + status dot

## Scope

One window (`Views/DesktopWidgetWindow`), one style block (`App.axaml`), one
localization helper (`Services/L10n.cs`), plus README. No `Core` change: the
widget already receives a plain aggregate (`ShowRates` / `ShowSummary` are called
from `AppServices.UpdateDesktopWidget`), and nothing in the polling path moves.

## Why the row stays reserved

Three layouts were considered:

| Option | Effect | Verdict |
|---|---|---|
| A. Overlay the header, shrink the card 150 → 130 | Header sits on top of the first rate line unless the content gets a top margin, which makes the `*` graph row collapse 20 px on hover | Rejected — the graph resizes on hover and `CardHeight`/placement math changes |
| B. Overlay the header, grow the window on hover | Same resize problem, plus the window would have to be repositioned every hover | Rejected |
| **C. Keep the row, fade `Opacity` only** | Nothing but the header's own pixels change | **Chosen** |

With C the window stays 250 × 150, `CardWidth`/`CardHeight`, `TargetPosition()`,
`PreparePlacement()` and the saved-position clamping are untouched, and the
card's other rows (`Auto,Auto,Auto,*,Auto`) keep their exact sizes. When the
header is hidden the top row simply reads as padding — the card has no border,
so empty space is indistinguishable from a margin.

## Hover state machine

The card (`Card`) is the only hit-test surface that matters; it already carries
`Background="Transparent"` for exactly this reason.

```
hidden ──PointerEntered──▶ showing (Opacity→1, transitions on)
   ▲                            │
   │                     PointerExited
   │                            ▼
   └──timer tick (250 ms)── pending-out (Opacity→0)
```

- `Opacacity` is driven by a `DoubleTransition` on `Opacity` (160 ms,
  `CubicEaseOut`). A `Transition` animates from the *current* value, so
  re-entering mid-fade-out reverses instead of snapping — requirement 3 comes
  for free and needs no per-direction `Transitions` swapping.
- The exit grace is a `DispatcherTimer` (250 ms). `PointerEntered` stops it;
  `PointerExited` starts/restarts it; the tick hides and stops.
- `PointerExited` on `Card` does **not** fire when the pointer moves onto a child
  (both `PointerEntered`/`PointerExited` follow `IsPointerOver`, which stays true
  for ancestors), so moving between the digits, the graph and the lock button
  never starts the timer. Only leaving the card does.

### Hit-testing

`OnPointerPressed` deliberately ignores presses that land on the lock button
(`IsOnLockButton`), so an invisible-but-hit-testable button would eat drags in
the top-right corner. The header therefore follows the fade state:

- hidden → `IsHitTestVisible = false`, so hits fall through to `Card` and a press
  in that corner drags;
- shown → `IsHitTestVisible = true`, so the lock clicks and the title area still
  drags (the press bubbles from `TitleText` to `Card`, and `IsOnLockButton` is
  false there).

`Card` keeps its own hit surface at all times, so hovering the header region
still fires `Card.PointerEntered` and reveals the header before any press can
land.

### Re-entry semantics

`OnOpened` resets the header to hidden **without** animating: clear the local
`Transitions` value, set `Opacity = 0`, `IsHitTestVisible = false`, then
`ClearValue(TransitionsProperty)` so the style's transition applies again. A
re-shown card (tray toggle) must not open with a header visible.

Nothing else changes in the gesture code: drag threshold, double-click pairing,
`SavePosition()`, `EnsureDesktopPin()`, the context menu and `SetLocked()` all
stay. `SetLocked()` keeps writing `LockText`/tooltip/menu header; only the
row's visibility changes.

## Status line

`WidgetSummary(servers, online, active)` is replaced by
`WidgetStatus(servers, online, active)` returning the text, next to the existing
formatter style, and the window gains `ShowStatus(...)`.

`servers` counts **enabled** servers only (`Settings.Servers.Count(s =>
IsEnabled)`): a disabled server is never contacted, so it can never be online
and would otherwise pin the line to `M/N online` forever.

| Condition | Dot | Text | zh |
|---|---|---|---|
| `servers <= 0` | `#B3FFFFFF` (neutral) | `Not configured` | `未配置` |
| `online >= servers` | `#2D7D46` | `"%lld active"` | `"%lld 个活动"` |
| otherwise | `#E5484D` | `"%lld/%lld online"` | `"%lld/%lld 在线"` |

- One glyph (`●`) in a new `widgetDot` class (`OutlinedText` with the existing
  black outline, `FontSize 9`, so it stays readable on a bright wallpaper);
  colour is set from code-behind because it is a decision, not a style.
- `"%lld servers · %lld online · %lld active"` is dropped from the `Zh` table —
  nothing else uses it (`"%lld servers · %lld online"` in `ServerCount` is a
  different key and stays).
- XAML names: `SummaryText` → `StatusText`, new `StatusDot`; `ShowSummary` →
  `ShowStatus`. `AppServices.UpdateDesktopWidget` passes the same three values.
- The dot is hidden (not coloured) in the `Not configured` case? **No** — it is
  always present so the line never re-flows; only its colour changes.
- Colours are the codebase's existing literals (`#2D7D46` success, `#E5484D`
  danger).

`ApplyLocalization()` re-derives the dot colour and text from the last known
counters, so a language change cannot leave a stale line; the counters are
cached in three fields written by `ShowStatus`.

## Risks

| Risk | Mitigation |
|---|---|
| `WS_EX_NOACTIVATE` window might not get hover messages | It already receives press/move for dragging; verify manually on Windows with the pin active and record it in `verification.md`. Fallback is a `DispatcherTimer` poll of `Card.IsPointerOver` |
| A `Transition` on `Grid` inside an application-wide `Style` setter | Documented Avalonia pattern (`<Setter Property="Transitions"><Transitions>…`); verify the fade on screen, not only in XAML |
| The exit timer surviving hide/close | The tick checks `IsVisible` before touching the header; the timer field is created once and stopped on enter and tick |
| Header appearing when the pointer is just *passing* over the card | The 250 ms grace means a pass shorter than that never renders it |
| `OutlinedText` + dot glyph rendering as a box on a missing font | `●` is in Inter/Segoe UI Symbol and already used in the panel rows (`TorrentRow` dots) |

## Verification

Manual, on Windows, with the widget pinned above the desktop:

1. Idle card: no title, no button, no dot-colour regression in the summary.
2. Move onto the card → header fades in; hold still → stays.
3. Leave → fades out after ~250 ms; re-enter during the fade → reverses.
4. Drag from the top strip with the header hidden → the card moves.
5. Hover the corner → lock button appears, clicks, tooltip follows.
6. Status forms: not configured / all online / one offline / all offline.
7. Both languages.
8. `dotnet build`, `dotnet test`, `PTMonitor.Cli --selftest`.
