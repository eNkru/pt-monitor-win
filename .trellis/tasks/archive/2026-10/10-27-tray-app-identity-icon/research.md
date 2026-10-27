# Research — macOS icon geometry + verification facts

## Source of the design

`scripts/make-icon.swift` was deleted in the macOS repo by `455b261`
("refactor: drop duplicated code and dev-only tools"). Recover it with:

```bash
git -C ../pt-monitor show 455eb93:scripts/make-icon.swift
```

Its design (commit message): "the panel's dark card with a hairline border, a
solid-teal down arrow with a dimmed up arrow (the menu bar's speed motif), and
the teal pill progress meter at 62%. Every icns size is rendered from the
vector shape, never downscaled."

### Exact constants (from the recovered file)

```
tealTop    #6FD3DE   tealBottom #3AA7B5   track teal #4FB6C3 @ α0.12
cardTop    #2E2E33   cardBottom #131316   sheen white α0.07 → α0
hairline   white α0.10, lineWidth 0.004·s
corner     0.225·s
arrow band yLow 0.34·s … yHigh 0.68·s; shaftHalf 0.043·s; headHalf 0.105·s;
           headDepth 0.105·s; down cx 0.37·s, up cx 0.63·s; up alpha 0.38
pill       w 0.52·s, h 0.032·s, cy 0.21·s, cx 0.5·s; fill = 0.62·w left-anchored
```

CoreGraphics origin is **bottom-left**; Avalonia draws **top-left**, so a CG y
becomes `s - y`. The card gradient runs cardTop→cardBottom top-to-bottom in
Avalonia; the arrow/pill gradients likewise invert.

## Committed artwork pixel facts (verified with Windows System.Drawing)

From `src/PTMonitor.App/app.ico` (built from the macOS `AppIcon.icns`):

| entry | opaque | corners α | dominant colours |
|---|---|---|---|
| 32 px | 968/1024 (95 %) | 0,0,0,0 | `303030` ×301, `484848` ×204, `181818` ×164 |
| 64 px | 3896/4096 (95 %) | 0,0,0,0 | same ramp |
| 128 px | 15624/16384 (95 %) | 0,0,0,0 | same ramp |
| 256 px | 62582/65536 (95 %) | 0,0,0,0 | same ramp |

Transparent rounded corners, opaque charcoal body — the reference band for
comparing the tray render.

## Verification harness (working recipe, scratch only)

`/tmp/iconprobe` — console project referencing `src/PTMonitor.App.csproj`, plus
`Avalonia.Headless 11.3.2`, `SkiaSharp 2.88.9`, `SkiaSharp.NativeAssets.Linux`
(must match the repo's Avalonia 11.3.2, otherwise
`TypeLoadException: Method 'CreateOffscreenRenderTarget' … no implementation`).

```csharp
AppBuilder.Configure<ProbeApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
```

Run with a RID so the native Skia lib is copied:

```bash
dotnet build -c Release -r linux-x64 --self-contained false
dotnet bin/Release/net8.0/linux-x64/IconProbe.dll
```

Measured facts:

- `UseHeadless()` alone with default options renders nothing (writes 0-byte
  PNGs); `UseHeadlessDrawing = false` + `UseSkia()` is required.
- Skia native load fails without `libfontconfig.so.1` (`DllNotFoundException`,
  and the search list shows libSkiaSharp itself found). Installed via
  `libfontconfig1` + `fonts-liberation` — no apt packages needed beyond that.
- No X11/WSLg libraries are required for this path.
- Export path: `RenderTargetBitmap.Save(stream)` is a no-op under headless; use
  `bitmap.TryGetFeature<ISkiaSharpApiLeaseFeature>()` → `SkImage` →
  `Encode(SKEncodedImageFormat.Png, 100)`.
- End-to-end proof already obtained: a `#4FB6C3` rectangle rendered headless and
  read back on Windows via `System.Drawing` returned `(79,182,195,255)` exactly.

## Do not use a hand-written PNG decoder

Two attempts in this session were wrong (Sub filter must use the same-row left
byte; IHDR body starts at PNG offset 16). `System.Drawing` on Windows is the
authoritative reader here.

## Windows-side facts

- App installs at `C:\Users\enkru\AppData\Local\PTMonitor`; runtime data at
  `%APPDATA%\PTMonitor`.
- A Windows exe launched from WSL interop dies when the launching command
  exits if it inherits WSL's pipes — use
  `Start-Process -FilePath … -WorkingDirectory …`.
- UIA `InvokePattern` on a tray button does not deliver a left click to the
  Avalonia tray icon, and overflow-area buttons report an empty
  `BoundingRectangle`. Tray presence/state is verifiable through the UIA
  tooltip text instead (e.g. `PT Monitor ↓ 0 B/s ↑ 0 B/s — 1 servers · 1 online`).
- No .NET SDK on the Windows machine — harnesses must run in WSL.
