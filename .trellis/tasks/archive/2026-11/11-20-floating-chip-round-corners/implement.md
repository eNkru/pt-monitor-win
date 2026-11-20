# Implement — floating chip: a round chip with transparent corners

## Checklist

### 1. App: the window-region helper

- [x] `src/PTMonitor.App/Services/WindowShape.cs` (new)
  - `ApplyCircle(IntPtr hwnd, double diameterInDevicePixels)` → `SetWindowRgn`
    with `CreateEllipticRgn`, centred in the client rect; guarded by
    `OperatingSystem.IsWindows()`; the region is deleted by us only when
    `SetWindowRgn` failed.
  - no exception escapes; a failure is logged through `Log`.

### 2. App: the round chip

- [x] `src/PTMonitor.App/Views/FloatingSpeedWindow.axaml`
  - window: `Background="Transparent"`, `TransparencyLevelHint="Transparent"`,
    `TransparencyBackgroundFallback="#FF202023"` (opaque body colour);
  - the chip body: `Border` 60 × 60, `CornerRadius="30"`,
    `Classes="floatingChip"` (the fill and the hairline live in `App.axaml`),
    `ClipToBounds="True"`, `Margin="2"` (the window is 64 × 64);
  - numbers mode: the rows + separator become one vertically centred block;
  - graph mode: the plot keeps filling the circle (clipped by it), the pinned
    readout moves to the top centre.
- [x] `src/PTMonitor.App/Views/FloatingSpeedWindow.axaml.cs`
  - apply the elliptical region on open and on a scaling change (the region is
    in device pixels; a DPI change is reported by `TopLevel.ScalingChanged`);
  - `Place()`: the size fallback becomes the window size (64);
  - the rates use `ByteRateFormatter.RateCompact` (the long form does not fit
    the circle).

### 3. App: the style class

- [x] `src/PTMonitor.App/App.axaml` — the chip's fill/border move into a
  `Border.floatingChip` class (the look is used once, but it belongs with the
  other window looks, next to `Border.card`), and the rates/arrows get an
  explicit light foreground (the body is a fixed dark fill, so the text must
  not follow the OS theme).

### 4. Docs

- [x] `README.md` — the chip is a round, opaque circle with transparent
  corners; the "opaque, square and shadow-free" paragraph is corrected.
- [x] `.trellis/spec/frontend/component-guidelines.md` — the "Floating chip"
  window convention is corrected (transparent window, round body, region).
- [x] `.trellis/spec/frontend/lifecycle-and-tray.md` — the chip's section is
  corrected (shape, region, foreground, compact rate form).

### 4b. Core: the compact rate form

- [x] `src/PTMonitor.Core/Format/ByteRateFormatter.cs` — `RateCompact`
  (below 1 KB/s the ` B/s` suffix is dropped: `1023B`).
- [x] `src/PTMonitor.Core/Diagnostics/SelfTest.cs` — two mirrors.
- [x] `tests/PTMonitor.Core.Tests/CoreTests.cs` — the mirror.

### 5. Verification

- [x] `dotnet build -c Release` — green.
- [x] `dotnet run --project src/PTMonitor.Cli -c Release -- --selftest` — green (700 checks).
- [x] `dotnet test tests/PTMonitor.Core.Tests -c Release` — green (156 tests).
- [ ] Manual (Windows, **the user's to do**): the chip is a circle, the corners
      show the desktop through them, a click in a corner reaches the window
      underneath, the rates and the graph are readable in both modes, drag /
      double-click / right-click still work.
- [ ] Manual (Linux dev build): the chip renders as a circle, no exception in
      `ptmonitor.log`.

## Rollback

The change is contained in three files (`FloatingSpeedWindow.axaml`,
`FloatingSpeedWindow.axaml.cs`, `WindowShape.cs`), one pure formatter
(`RateCompact`, additive) plus its two test mirrors, and the docs. Reverting
the three view files restores the square chip; `WindowShape` is unused
elsewhere and can go with them, and `RateCompact` is additive.
