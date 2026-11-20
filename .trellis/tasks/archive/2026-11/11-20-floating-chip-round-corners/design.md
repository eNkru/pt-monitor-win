# Design — floating chip: a round chip with transparent corners

## 1. Shape of the change

```
src/PTMonitor.App/
  Views/FloatingSpeedWindow.axaml      # the round body + the layout inside it
  Views/FloatingSpeedWindow.axaml.cs   # window shaping, compact rate form
  Services/WindowShape.cs               # NEW: Win32 elliptical window region
  App.axaml                             # Border.floatingChip + explicit light text
src/PTMonitor.Core/
  Format/ByteRateFormatter.cs          # RateCompact (the chip's narrow form)
  Diagnostics/SelfTest.cs              # mirrors it
tests/PTMonitor.Core.Tests/CoreTests.cs  # mirrors it
README.md                               # the chip is a circle now
.trellis/spec/frontend/component-guidelines.md
.trellis/spec/frontend/lifecycle-and-tray.md
```

`Core` gains one pure formatter function and its two mirrors. Nothing else in
`Core` moves: no model field, no settings change.

## 2. Why the window itself has to change

A `CornerRadius` on the inner `Border` cannot round the chip, because the
*window* paints an opaque 56 × 56 square behind it. The square stays visible
in the four corners — the "sharp background behind the round corner" the user
reported.

So the window has to stop painting a background of its own:

| Property | Value | Why |
|---|---|---|
| `Background` | `Transparent` | the window paints nothing; only the round body does |
| `TransparencyLevelHint` | `Transparent` | per-pixel alpha, so the corners are really see-through |
| `TransparencyBackgroundFallback` | `#F0202023` | if a platform cannot do per-pixel alpha, the chip degrades to the old opaque look instead of a white square |
| `SizeToContent` | `WidthAndHeight` | the window is exactly the content |

The body itself stays **opaque** (`#F0202023`, unchanged) — the user was
explicit: the chip is not a translucent widget, only its corners are.

## 3. The circle

`Border` (the chip body), 60 × 60:

| Property | Value | Why |
|---|---|---|
| `CornerRadius` | `30` | half the side → a true circle |
| `Background` | `#F0202023` | the current opaque fill, unchanged |
| `BorderBrush` / `BorderThickness` | `#26FFFFFF` / `1` | the current hairline, now following the curve |
| `ClipToBounds` | `True` | Avalonia clips a `Border`'s children to its rounded rect, so the graph is cut by the circle instead of spilling over it |

**Why 60 and not 56:** a circle has less usable width than a square at the
same size. At 56 px the two stacked rows leave ~49 px at the outer rows, which
looks cramped for the widest rate; at 60 px the same content has ~54 px and
reads comfortably. The chip grows by 4 px; nothing else moves.

**Why the compact rate form:** even at 60 px, the long form `1023 B/s` does not
fit the chord of a circle at any sane badge size — the text is wider than the
curve allows at the row's top edge. The chip therefore renders
`ByteRateFormatter.RateCompact` (`1023B`, `1.2M`), whose widest form is five
characters; the panel and the tray keep the long form.

**Geometry** (circle radius `R`, centre `(R, R)`): the usable half-width at a
vertical offset `dy` from the centre is `sqrt(R² − dy²)`.

| Layout | `dy` | usable width (R = 30) |
|---|---|---|
| numbers mode, outer row edge | 13.5 | 53.7 px |
| numbers mode, centre row | 0 | 60 px |
| graph mode readout, top edge | 20 | 44.7 px |

## 4. Layout inside the circle

**Numbers mode** — the two rows and the separator become one block that is
centred in the circle (`VerticalAlignment="Center"`), instead of two rows
pinned to the outer thirds. That keeps the text optically centred and gives
the outer rows the widest chord available.

**Graph mode** — the plot keeps filling the circle and is clipped by it (a
circular gauge). The pinned rate readout moves from the **top-left corner**
(which is outside a circle and would be cut in half) to the **top centre**,
with a top margin of 10 px so its corners stay inside the curve. The glyphs
keep their hard outline, so they stay readable over the plot.

## 5. Click-through: the window region

Avalonia 11.3 on Win32 does **no per-pixel hit testing** for transparent
windows (checked against the `Avalonia.Win32` sources: there is no
`WM_NCHITTEST` → `HTTRANSPARENT` path). A transparent 64 × 64 window would
therefore swallow every click in its four corners — a dead zone over whatever
is behind the chip.

Windows has the answer for exactly this: a **window region**. `SetWindowRgn`
with an elliptical region clips the window *and* its hit area, so the four
corners belong to the window underneath again.

`Services/WindowShape.cs` (new) wraps it:

```csharp
public static bool ApplyEllipse(IntPtr hwnd, double radiusInDevicePixels);
```

- Guarded by `OperatingSystem.IsWindows()`; off Windows it returns `false`
  and nothing happens (the chip keeps working, the corners just stay dead).
- The region is the ellipse **inscribed in the window** (radius = half the
  window's side). The window is 2 px larger than the circle on every side, so
  the region's aliased edge never touches the visible circle — the round edge
  stays antialiased, and only a 2 px invisible ring around the circle stays
  clickable.
- `SetWindowRgn` takes ownership of the region on success; on failure the
  region is deleted by us.

Applied when the window is opened, when it is re-placed, and when the display
scaling changes (`TopLevel.ScalingChanged`) — the region is in device pixels,
so a DPI change would otherwise leave a stale shape behind.

## 6. Sizes

| Thing | Logical px |
|---|---|
| visible circle | 60 × 60 |
| window (circle + 2 px margin) | 64 × 64 |
| window region | ellipse inscribed in the window (r = 32) |

`FloatingSpeedWindow.Place()` clamps the window inside the work area; its
size fallback changes from 56 to 64 (the window size, not the circle size).

## 7. Risk and fallback

| Platform / renderer | Result |
|---|---|
| Windows + DirectComposition (normal) | opaque circle, transparent corners, corners click-through |
| Windows + software rendering (redirection bitmap) | Avalonia drops the alpha; the fallback brush paints the chip's own colour, the region cuts it to an aliased circle |
| Linux / macOS | the circle renders, the corners are transparent but not click-through (no region) — development only |

The chip never throws, and the app never depends on the region succeeding:
if `SetWindowRgn` fails, the chip is simply an opaque square again.
