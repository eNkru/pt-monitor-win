# Implement — floating chip: a TV-screen chip with transparent corners

## Checklist

### 1. App: the window-region helper

- [x] `src/PTMonitor.App/Services/WindowShape.cs` (new)
  - `ApplyRoundedRect(IntPtr hwnd, int width, int height, int cornerRadius)` →
    `SetWindowRgn` with `CreateRoundRectRgn`, centred in the client rect and
    drawn one pixel larger than the body on every side (a region's edge is
    aliased and must not cut the body's antialiased edge); guarded by
    `OperatingSystem.IsWindows()`; the region is deleted by us only when
    `SetWindowRgn` failed.
  - no exception escapes; a failure is logged through `Log`.

### 2. App: the round chip

- [x] `src/PTMonitor.App/Views/FloatingSpeedWindow.axaml`
  - window: `Background="Transparent"`, `TransparencyLevelHint="Transparent"`,
    `TransparencyBackgroundFallback="#FF26282D"` (the bezel colour);
  - the chip body: `Border` 68 × 52, `CornerRadius="12"`,
    `Classes="floatingChip"` (the fill and the hairline live in `App.axaml`),
    `ClipToBounds="True"`, `Margin="2"` (the window is 72 × 56);
  - the screen inside the bezel: `Border.floatingScreen`, inset 3,
    `CornerRadius="10"`, `ClipToBounds="True"`;
  - numbers mode: the rows + separator stay one vertically centred block;
  - graph mode: the plot fills the screen (clipped by it), the pinned readout
    stays in the top-left corner.
- [x] `src/PTMonitor.App/Views/FloatingSpeedWindow.axaml.cs`
  - apply the rounded-rect region on open and on a scaling change (the region
    is in device pixels; a DPI change is reported by `TopLevel.ScalingChanged`);
  - `Place()`: the size fallback becomes the window size (72 × 56);
  - the rates use `ByteRateFormatter.RateCompact` (the long form does not fit
    the circle).

### 3. App: the style class

- [x] `src/PTMonitor.App/App.axaml` — the bezel and the screen move into
  `Border.floatingChip` / `Border.floatingScreen` classes (the look is used
  once, but it belongs with the other window looks, next to `Border.card`),
  and the rates/arrows get an explicit light foreground (the body is a fixed
  dark fill, so the text must not follow the OS theme).

### 4. Docs

- [x] `README.md` — the chip is an opaque little TV screen with transparent
  corners; the "opaque, square and shadow-free" paragraph is corrected.
- [x] `.trellis/spec/frontend/component-guidelines.md` — the "Floating chip"
  window convention is corrected (transparent window, round body, region).
- [x] `.trellis/spec/frontend/lifecycle-and-tray.md` — the chip's section is
  corrected (shape, region, foreground, compact rate form).

### 4a. The shape, after the review

- [x] The first implementation was a full circle; the user rejected it
      ("I don't really like that full circle"). The chip became a rounded
      rectangle with a bezel and a screen inside it, and the window region
      followed the shape (`CreateRoundRectRgn` instead of `CreateEllipticRgn`).

### 4b. Core: the compact rate form

- [x] `src/PTMonitor.Core/Format/ByteRateFormatter.cs` — `RateCompact`
  (below 1 KB/s the ` B/s` suffix is dropped: `1023B`).
- [x] `src/PTMonitor.Core/Diagnostics/SelfTest.cs` — two mirrors.
- [x] `tests/PTMonitor.Core.Tests/CoreTests.cs` — the mirror.

### 5. Verification

- [x] `dotnet build -c Release` — green.
- [x] `dotnet run --project src/PTMonitor.Cli -c Release -- --selftest` — green (700 checks).
- [x] `dotnet test tests/PTMonitor.Core.Tests -c Release` — green (156 tests).
- [x] Manual (Windows, the user): the chip reads as a little TV screen; the
      rates and the graph are readable in both modes; drag / double-click /
      right-click still work. **Still open:** the live corner click-through and
      the bezel contrast on a real desktop.
- [ ] Manual (Linux dev build): the chip renders as a circle, no exception in
      `ptmonitor.log`.

## Rollback

The change is contained in three files (`FloatingSpeedWindow.axaml`,
`FloatingSpeedWindow.axaml.cs`, `WindowShape.cs`), one pure formatter
(`RateCompact`, additive) plus its two test mirrors, and the docs.
Merged as PR #20 (`feat/floating-chip-tv-screen`). Reverting
the three view files restores the square chip; `WindowShape` is unused
elsewhere and can go with them, and `RateCompact` is additive.
