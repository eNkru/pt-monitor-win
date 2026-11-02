# Design — monochrome theme-adaptive tray icon

## 1. What changes

| File | Change |
|---|---|
| `src/PTMonitor.App/Services/TrayIconFactory.cs` | Rewritten: draws the flat glyph and packages it into an in-memory multi-size ICO. |
| `src/PTMonitor.App/Services/IcoEncoder.cs` | New: 32-bit BGRA ICO container writer (pure byte work, no Avalonia). |
| `src/PTMonitor.App/Services/AppServices.cs` | Icon is chosen by (state, theme); the theme is watched and the icon is refreshed on change. |
| `scripts/make-app-icon.py` | Docstring only: the tray is no longer the same drawing as the exe icon. |
| `.trellis/spec/frontend/lifecycle-and-tray.md` | Tray-presentation section rewritten. |

`app.ico` and `scripts/make-app-icon.py`'s artwork stay as they are.

## 2. Glyph

The glyph is drawn from a single polygon (the arrow) plus one rounded
rectangle (the bar). Every length is a fraction of the icon side, so the same
description covers 16 px … 48 px. It is the same idea as the exe icon's
sub-32 px variant (one arrow over a bar), but with its own proportions — the
two drawings no longer share constants.

```
        ┌───┐            shaft  : half-width 0.085, y 0.12 → 0.38
        │   │            head   : half-width 0.175, y 0.38 → tip 0.64
        │   │            bar    : x 0.26 → 0.74, y 0.77 → 0.89, pill ends
     ┌──┘   └──┐
     │         │          the whole glyph spans y 0.12 → 0.88 (optically
     └─────────┘          centred, so it stays centred at 16 px too)
      ───────────         the bar
```

The silhouette is the one the exe icon already uses below 32 px
(`SMALL_*` constants in `scripts/make-app-icon.py`), so the tray mark and the
app mark stay recognisably the same thing.

At 16 px the arrow head is ~5.6 px wide and the bar ~7.7 × 1.9 px — readable
without antialiasing help. Nothing thinner than 1.5 px is drawn at any size.

### Palette

| Theme | Idle | Active | Error |
|---|---|---|---|
| light taskbar | `#1F1F1F` @ 55 % | `#1F1F1F` @ 100 % | `#C42B1C` @ 100 % |
| dark taskbar | `#FFFFFF` @ 55 % | `#FFFFFF` @ 100 % | `#FF99A4` @ 100 % |

The colours are Fluent's light/dark "text" and "critical foreground" tokens.
Idle is dimmed rather than greyed: the hue stays, only the opacity drops, so
one glyph covers both states.

## 3. From vector to a native icon

Avalonia's `WindowIcon(Bitmap)` path is *not* used any more. On Win32 it
encodes the bitmap to PNG, feeds it to `IconImpl`, and Avalonia ends up in
`Win32Icon.CreateIcon` → `CreateIconIndirect` with `fIcon = FALSE`: a 32-bit
HBITMAP plus a **1-bit AND mask**. The alpha channel is dropped, so the glyph
would get hard, aliased edges and Windows would downscale a single 32 px
bitmap to the taskbar size.

Instead the icon is packaged as a real `.ico` in memory and handed to
`WindowIcon(Stream)`:

```
glyph ──► RenderTargetBitmap (per size)  ──► straight BGRA pixels
                                                     │
                                                     ▼
                                        IcoEncoder.Encode(entries)
                                                     │
                                                     ▼
                              WindowIcon(Stream) ──► Win32Icon(LoadIconFromData)
                                                     │
                                                     ▼
                              CreateIconFromResourceEx → 32-bit icon with alpha
```

`LoadIconFromData` picks the entry whose size matches the taskbar
(`GetScaledSize(16, scale)` → 16/20/24/32/48 px at 100/125/150/200/300 %),
and `CreateIconFromResourceEx` honours the 32-bit alpha channel. The shell
still receives a single-resolution `HICON` and scales it when the taskbar DPI
is not an exact match. The result is a modern icon: smooth edges, blended over
the taskbar.

### ICO layout

Standard ICO container, one 32-bit BGRA entry per size:

```
ICONDIR            reserved=0, type=1, count=5
ICONDIRENTRY[5]    16 bytes each: width, height, colourCount=0, reserved=0,
                   planes=1, bitCount=32, bytesInRes, imageOffset
  entry 0: BITMAPINFOHEADER (40 bytes) + XOR pixels (bottom-up BGRA)
           + AND mask (1 bpp, rows padded to 4 bytes, all zero)
  …
```

`biHeight` is doubled (`2 × h`) because an icon DIB carries the colour bitmap
*and* the mask; the mask is all-zero (fully opaque) because the alpha channel
does the transparency — the classic "32-bit icon" layout every modern `.ico`
uses.

The alpha written into the DIB is **straight** (un-premultiplied) alpha: the
render target is premultiplied, and copying it verbatim would darken every
antialiased edge.

Sizes: 16, 20, 24, 32, 48 — the taskbar sizes Windows asks for at 100 % …
300 % scaling.

## 4. Theme detection

`App.axaml` sets `RequestedThemeVariant="Default"`, so
`Application.Current.ActualThemeVariant` is the **OS** theme variant.
Avalonia's Win32 backend listens for `WM_SETTINGCHANGE` with
`"ImmersiveColorSet"` (and `"WindowsThemeElement"` for high contrast), updates
`PlatformColorValues`, and raises `Application.ActualThemeVariantChanged`.

So:

- `AppServices` subscribes to `Application.Current.ActualThemeVariantChanged`
  and calls `UpdateTray()`;
- `UpdateTray()` compares the wanted `(state, theme)` pair with the applied one
  and only then assigns `TrayIcon.Icon`.

No timer, no registry polling: the notification is free and only fires on a
real theme switch.

## 5. Caching and churn

`TrayIconFactory` keeps one `WindowIcon` per `(TrayState, ThemeVariant)` pair
in a dictionary, created lazily. Six pairs at most, created once per process.
`AppServices` remembers the applied pair and skips the assignment when it is
unchanged — the rule that fixed the icon churn crash
(`.trellis/tasks/11-01-tray-icon-crash`) is preserved verbatim.

## 6. Alternatives considered

- **Keep `WindowIcon(Bitmap)`** — simplest, but the 1-bit AND mask gives hard
  edges and Windows downsamples one 32 px bitmap to the taskbar size.
- **Ship a `.ico` file** — the project rule is "icons are drawn, never loaded
  from files"; a binary asset is also exactly what the task must not add.
- **Two glyphs (light + dark) chosen by the user** — a setting nobody asked
  for; the OS already knows the answer.
- **Keep the arrow pair** — two arrows plus a pill do not survive 16 px.
- **A new App test project for the encoder** — valuable, but it changes the
  build/CI scripts and the "no automated gate" statement in the spec; out of
  scope for this task. The encoder is instead verified here by parsing the
  generated bytes back and rendering the entries to PNG.
