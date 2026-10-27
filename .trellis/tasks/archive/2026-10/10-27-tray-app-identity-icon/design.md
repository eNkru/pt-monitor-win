# Design — tray icon adopts the app icon design

## Boundary

Everything lives in `src/PTMonitor.App/Services/TrayIconFactory.cs`. Nothing in
`Core`, no `SelfTest`/xUnit change (tray rendering is not part of the contract
suite), no new NuGet package in the repo, no runtime asset loading.

`AppServices.UpdateTray` keeps its `MenuBarState → Idle/Active/Error` switch;
the factory's public API stays `WindowIcon`-returning so the tray wiring is
untouched.

## Shape of the refactor

```csharp
public enum TrayState { Idle, Active, Error }

public static class TrayIconFactory
{
    public static WindowIcon Idle()   => Render(TrayState.Idle);
    public static WindowIcon Active() => Render(TrayState.Active);
    public static WindowIcon Error()  => Render(TrayState.Error);

    public static RenderTargetBitmap Render(TrayState state);   // exposed for verification
}
```

`Render` composes three shared helpers — `DrawCard`, `DrawArrow`, `DrawPill` —
so the three states differ only in colour/fill parameters, never in geometry.
`Render` is public (not private) because the verification harness needs the
bitmap; the `WindowIcon` wrappers stay the API the app uses.

## Geometry port (CG bottom-left → Avalonia top-left)

`make-icon.swift` uses a CoreGraphics context whose origin is bottom-left;
Avalonia draws top-left, so every y becomes `s - y`. Ratios, unchanged:

| Element | macOS (CG) | Avalonia (y down) |
|---|---|---|
| card | rounded rect, corner `0.225·s` | same |
| card gradient | `cardTop` at y=s → `cardBottom` at y=0 | `cardTop` at y=0 → `cardBottom` at y=s |
| sheen | white 7 %→0, from y=s to y=0.42·s | from y=0 to y=0.58·s |
| hairline | white α0.10, width `0.004·s` | same |
| arrow band | y `0.34·s` … `0.68·s` | y `0.32·s` … `0.66·s` |
| down arrow | cx `0.37·s`, tip at yLow | cx `0.37·s`, tip at `0.66·s` |
| up arrow | cx `0.63·s`, tip at yHigh | cx `0.63·s`, tip at `0.32·s` |
| shaft/head | half-widths `0.043·s`, `0.105·s`, head depth `0.105·s` | same |
| pill | `0.52·s × 0.032·s` at y `0.21·s`, cx `0.5·s` | y `0.79·s` |
| pill fill | left-anchored 62 % of track | same |

Palette (verbatim from the macOS source): teal top `#6FD3DE`, teal bottom
`#3AA7B5`, track teal `#4FB6C3` at α0.12, card `#2E2E33` →
`#131316`, sheen white α0.07, hairline white α0.10. Up arrow alpha 0.38.

Avalonia needs `ImmutableSolidColorBrush` for solid fills; gradients use
`LinearGradientBrush` (drawing contexts accept `IBrush`; if the render path
rejects a mutable brush, call `ToImmutable()`).

## State matrix

| State | ↓ arrow | ↑ arrow | pill | border |
|---|---|---|---|---|
| idle | teal gradient @ α0.38 | teal @ α0.38 | track only, no fill | hairline white α0.10 |
| active | teal gradient, solid | teal @ α0.38 | track + 62 % teal fill | hairline white α0.10 |
| error | red gradient (`#E5484D`→`#C33C39`), solid | red @ α0.38 | track + 100 % red fill | red α0.55, width `0.05·s` |

Two deliberate deviations from the macOS artwork, both for tray legibility:

- **error border is thicker** than the hairline (`0.05·s` ≈ 1.6 px at 32 logical
  px instead of 0.13 px). At tray scale the macOS hairline is invisible, so the
  failure state needs a stroke that actually renders.
- **idle dims the down arrow** (macOS always draws it solid) — otherwise idle and
  active would be indistinguishable at 16 px, and the whole point of the tray
  icon is the state.

Red is the existing error colour in `App.axaml` (`#E5484D`), so the palette stays
consistent with the panel.

## Verification (no repo dependency added)

Scratch harness at `/tmp/iconprobe` (not committed, references `PTMonitor.App`,
adds `Avalonia.Headless` + `SkiaSharp` + `SkiaSharp.NativeAssets.Linux`):

```bash
dotnet build -c Release -r linux-x64 --self-contained false
dotnet bin/Release/net8.0/linux-x64/IconProbe.dll      # writes /tmp/iconprobe/{idle,active,error}.png
```

It needs `libfontconfig.so.1` (installed) — measured root cause of the earlier
`DllNotFoundException`.

Pixel stats are read back with Windows `System.Drawing` (authoritative decoder;
a hand-written PNG decoder in this session was wrong twice, so it is not used).
Assertions per state:

- opaque coverage ≈ 95 % (matches the committed `app.ico` 32 px entry: 968/1024)
- dominant colours are the charcoal ramp (`#303030`, `#484848`, `#181818`)
- active: teal-family pixels present (`#4FB6C3`/`#6FD3DE`/`#3AA7B5`)
- idle: no saturated teal pixels (arrows alpha 0.38 only)
- error: red-family pixels present, teal absent

Cross-check the active render against the committed `app.ico` 32 px entry: same
coverage band and same dominant colour buckets.

## Compatibility & rollback

- `app.ico` and `<ApplicationIcon>` untouched → Explorer/taskbar icon unchanged.
- Tooltip text and `MenuBarState` logic unchanged.
- Rollback = revert one file (`TrayIconFactory.cs`) plus the doc/spec lines.
