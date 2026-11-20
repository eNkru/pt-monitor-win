# Design — floating chip: a TV-screen chip with transparent corners

## 1. Shape of the change

```
src/PTMonitor.App/
  Views/FloatingSpeedWindow.axaml      # the round body + the layout inside it
  Views/FloatingSpeedWindow.axaml.cs   # window shaping, compact rate form
  Services/WindowShape.cs               # NEW: Win32 rounded-rect window region
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
| `TransparencyBackgroundFallback` | `#FF26282D` | the bezel colour: if a platform cannot do per-pixel alpha, the chip degrades to the old opaque look instead of a white square |
| `SizeToContent` | `WidthAndHeight` | the window is exactly the content |

The body itself stays **opaque** — the user was explicit: the chip is not a
translucent widget, only its corners are.

## 3. The screen

Two nested `Border`s, the way a little TV is built — a frame with a screen in
it:

| Border | Size | `CornerRadius` | `Background` | Why |
|---|---|---|---|---|
| `floatingChip` (the body) | 68 × 52 | 12 | `#FF26282D` | the bezel; the hairline `#33FFFFFF` follows the curve |
| `floatingScreen` (inset 3) | 62 × 46 | 10 | `#FF15171B` | the darker screen inside the frame |

Both carry `ClipToBounds="True"`: Avalonia clips a `Border`'s children to its
rounded rect, so the graph is cut by the screen instead of spilling over the
bezel.

**Why a rounded rectangle and not a circle:** the chip is a readout, not a
badge. The first attempt was a full circle and the user rejected it ("I don't
really like that full circle") — the TV screen is the shape they asked for.
It also keeps the old chip's stacked rate layout and its top-left graph
readout, because a rounded shape has a real top-left corner.

**Why the compact rate form:** the long form `1023 B/s` does not fit a small
screen. The chip therefore renders `ByteRateFormatter.RateCompact` (`1023B`,
`1.2M`); the panel and the tray keep the long form.

## 4. Layout inside the circle

**Numbers mode** — the two rows and the separator stay one block centred on
the screen, the way the old chip showed them.

**Graph mode** — the plot fills the screen and is clipped by it. The pinned
rate readout stays in the **top-left corner**, where the old chip had it (the
oldest samples are the low ones there). The glyphs keep their hard outline,
so they stay readable over the plot.

## 5. Click-through: the window region

Avalonia 11.3 on Win32 does **no per-pixel hit testing** for transparent
windows (checked against the `Avalonia.Win32` sources: there is no
`WM_NCHITTEST` → `HTTRANSPARENT` path). A transparent 64 × 64 window would
therefore swallow every click in its four corners — a dead zone over whatever
is behind the chip.

Windows has the answer for exactly this: a **window region**. `SetWindowRgn`
with a rounded-rectangle region clips the window *and* its hit area, so the
four corners belong to the window underneath again.

`Services/WindowShape.cs` (new) wraps it:

```csharp
public static bool ApplyRoundedRect(IntPtr hwnd, int width, int height, int cornerRadius);
```

- Guarded by `OperatingSystem.IsWindows()`; off Windows it returns `false`
  and nothing happens (the chip keeps working, the corners just stay dead).
- The region is the body's shape **one pixel larger on every side**: a
  region's edge is aliased, so it must stay outside the body's antialiased
  edge instead of cutting it. The window is 4 px larger than the body, so the
  window's own corners fall outside the region and are click-through.
- `SetWindowRgn` takes ownership of the region on success; on failure the
  region is deleted by us.

Applied when the window is opened and when the display scaling changes
(`TopLevel.ScalingChanged`) — the region is in device pixels, so a DPI change
would otherwise leave a stale shape behind.

## 6. Sizes

| Thing | Logical px |
|---|---|
| body (bezel) | 68 × 52, corner radius 12 |
| screen (inset 3) | 62 × 46, corner radius 10 |
| window (body + 2 px margin) | 72 × 56 |
| window region | the body's shape, 1 px larger on every side |

`FloatingSpeedWindow.Place()` clamps the window inside the work area; its
size fallback becomes the window size (72 × 56).

## 7. Risk and fallback

| Platform / renderer | Result |
|---|---|
| Windows + DirectComposition (normal) | opaque screen, transparent corners, corners click-through |
| Windows + software rendering (redirection bitmap) | Avalonia drops the alpha; the fallback brush paints the bezel colour, the region cuts it to an aliased rounded rectangle |
| Linux / macOS | the screen renders, the corners are transparent but not click-through (no region) — development only |

The chip never throws, and the app never depends on the region succeeding:
if `SetWindowRgn` fails, the chip is simply an opaque rectangle again.
