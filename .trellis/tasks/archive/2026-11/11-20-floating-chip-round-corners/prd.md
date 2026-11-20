# Floating chip: a TV-screen chip with transparent corners

## Goal

Turn the 56 × 56 floating speed chip into a little **TV screen**: a rounded
rectangle with a bezel and a darker screen inside it. The **body stays
opaque** — the user was explicit about that: "I don't want the entire widget
transparent" — but the four corners outside the rounded shape must become
**transparent**, so the screen shows the desktop through it instead of the
sharp square window background that a plain `CornerRadius` would leave behind.

The shape was iterated on the way there: a full circle first (rejected: "I
don't really like that full circle"), then the rounded TV screen.

## Background

`FloatingSpeedWindow` (`src/PTMonitor.App/Views/FloatingSpeedWindow.axaml`) is
a 56 × 56 **opaque square** that stays on top of other windows. The window
itself carries the fill (`Background="#F0202023"`); the 56 × 56 `Border` inside
it only adds the 1 px hairline (`#26FFFFFF`) and acts as the pointer surface.

Two consequences of that layout:

- The chip is a square with a 1 px border, while every other floating surface
  in the app (the dropdown panel, the desktop widget) is round or soft. A
  `CornerRadius` on the `Border` alone cannot fix the shape: the window's own
  square background still shows through the rounded corners, which is exactly
  the "sharp background behind the round corner" the user reported.
- Avalonia 11.3 on Win32 does **no per-pixel hit testing** for transparent
  windows (verified against the `Avalonia.Win32` sources: no `WM_NCHITTEST` →
  `HTTRANSPARENT` path). The moment the window becomes per-pixel transparent,
  the whole 56 × 56 rectangle keeps swallowing clicks, and the transparent
  corners would become dead zones over whatever is behind the chip.

## Requirements

1. The chip is a **rounded rectangle** — a little TV screen (68 × 52, corner
   radius 12) with a bezel and a darker screen inside it. The body keeps its
   opaque fill and 1 px hairline; only the shape changes. The chip stays
   compact: a screen, not a badge.
2. The area **outside** the circle is **fully transparent**: the desktop (or
   any window behind the chip) shows through it, with a smooth, antialiased
   edge. No square background is visible anywhere around the round body.
3. The body is **not** made translucent. The chip keeps reading as a solid
   badge; only the corners are see-through.
4. The transparent corners must **not capture mouse input**: a click in a
   corner belongs to the window underneath the chip, and the pointer cursor
   there comes from that window as well.
5. The visible circle must never be **clipped** by the platform's window
   shaping, so its antialiased edge stays smooth at every display scaling.
6. Both display modes keep working inside the circle:
   - **Numbers** — the `↓` / `↑` pair stays centred on the screen, in the old
     chip's stacked layout. The chip uses a **compact rate form** (`1023B`, not
     `1023 B/s`): the long form does not fit a small screen. The panel and the
     tray keep the long form.
   - **Graph** — the rolling plot fills the screen and is **clipped by the
     rounded shape**, and the pinned rate readout stays in the **top-left
     corner** (a rounded shape has a real corner, unlike a circle).
7. The chip's gestures are unchanged: drag to move (position still saved and
   restored), double-click opens the web console, right-click opens the menu.
8. The chip keeps its footprint: the saved position still means the same place
   on screen, and the chip still reads as a small 68 × 52 screen.
9. The chip still works off Windows (the app also runs on Linux/macOS for
   development): the shaping degrades to "square chip" rather than breaking.
10. README and `.trellis/spec/frontend/component-guidelines.md` stop claiming
    the chip is an opaque square with no rounded corners.

## Constraints

- `PTMonitor.Core` gains exactly one thing: `ByteRateFormatter.RateCompact`
  (the chip's narrow form), mirrored in `SelfTest` and the xUnit suite in the
  same commit. No model field, no settings change.
- No new package, no image asset: the circle is geometry.
- The chip stays cheap: a 60 px always-on-top window, one alpha-blended
  surface, nothing per sample.
- Text stays ≥ 9.5 px (the accessibility floor in the frontend spec).

## Out of scope

- The **desktop widget** (the big one) keeps its square-cornered mask.
- A drop shadow: it would need a larger window and would put a wide
  invisible dead zone around the screen, which contradicts requirement 4.
- Changing the long rate form used by the panel and the tray.

## Acceptance Criteria

- [ ] The chip renders as a 68 × 52 rounded screen with an opaque body and a
      bezel; the corners around it show the desktop through them.
- [ ] The circle's edge is smooth (antialiased) and complete — no flat cut, no
      square background behind the curve.
- [ ] A click in a transparent corner reaches the window underneath the chip;
      the cursor there is the underlying window's cursor.
- [ ] Numbers mode: `↓` / `↑` are centred inside the circle and not clipped
      for any rate the formatter produces (the chip's compact form is the
      widest one, `1023B`).
- [ ] Graph mode: the plot is clipped by the circle, the pinned rates sit at
      the top centre and stay readable.
- [ ] Drag, double-click and the right-click menu behave exactly as before,
      and the position still survives a restart.
- [ ] The chip still renders on a non-Windows build (no crash, no exception
      in `ptmonitor.log`).
- [ ] `dotnet build -c Release`, the CLI selftest and the xUnit suite are
      green.
- [ ] README and the frontend component spec describe the chip as it is now.

## Notes

- The "sharp background behind the round corner" the user saw is the
  *window's* background, not the `Border`'s: the fix is a transparent window
  plus an opaque round body, not a rounded `Border` on an opaque window.
- The corners only become click-through if the window itself is shaped
  (Windows: a rounded-rectangle window region). Avalonia does not do that for
  us.
