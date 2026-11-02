# Implement — monochrome theme-adaptive tray icon

## Steps

1. **`src/PTMonitor.App/Services/IcoEncoder.cs` (new)**
   - `public static byte[] Encode(IReadOnlyList<(int Size, byte[] Bgra)> entries)`
   - Writes the ICONDIR / ICONDIRENTRY header and one 32-bit BGRA DIB per
     entry (`BITMAPINFOHEADER` + bottom-up pixels + zeroed AND mask).
   - Straight alpha in, straight alpha out — no premultiplication anywhere.
   - Defensive: throws `ArgumentException` for a non-positive size or an
     empty entry list, and for a pixel buffer whose length does not match
     `size × size × 4`.

2. **`src/PTMonitor.App/Services/TrayIconFactory.cs` (rewrite)**
   - Keep `TrayState { Idle, Active, Error }`.
   - Geometry constants (fractions of the side) for the arrow polygon and the
     bar; a `Draw` method that fills both with the state colour.
   - `Render(int size, TrayState state, bool dark)` → `RenderTargetBitmap`
     (transparent background, premultiplied output).
   - `ToBgra(RenderTargetBitmap)` → straight-alpha BGRA byte[].
   - `BuildIco(TrayState state, bool dark)` → `byte[]` (16/20/24/32/48).
   - `Icon(TrayState state, ThemeVariant variant)` → cached `WindowIcon`
     built from the ICO stream, one per (state, theme) pair.
   - Fallback: if building the `WindowIcon` from the stream fails, fall back to
     `new WindowIcon(bitmap)` so a malformed container can never leave the
     tray without an icon.

3. **`src/PTMonitor.App/Services/AppServices.cs`**
   - Track the applied `(MenuBarState, bool dark)` pair; assign
     `TrayIcon.Icon` only when it changes.
   - Subscribe to `Application.Current.ActualThemeVariantChanged` → `UpdateTray()`.
   - Keep the scoped `try`/`catch` around the assignment.

4. **`scripts/make-app-icon.py`** — docstring only: the tray is no longer the
   same drawing as the exe icon; the shared constants are gone.

5. **`.trellis/spec/frontend/lifecycle-and-tray.md`** — rewrite the
   "Tray presentation" section: new artwork, theme behaviour, ICO packaging,
   and the cache/assignment rule.

## Validation

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build PTMonitor.sln -c Release
dotnet run --project src/PTMonitor.Cli -c Release -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release
```

Plus a throwaway harness in `/tmp` (not committed) that references the App
project, calls `TrayIconFactory.BuildIco(...)` for every (state, theme) pair,
parses the ICO back, and writes each entry out as a PNG so the glyph can be
looked at on this Linux box.

## Review gates

- [ ] No `WindowIcon`/`Render` call outside `TrayIconFactory`.
- [ ] `TrayIcon.Icon` is assigned only on a (state, theme) change.
- [ ] The ICO bytes parse back into the expected five entries with the
      expected sizes and pixel lengths.
- [ ] No new package, no new binary file in the repository.
- [ ] Spec and script docstring updated in the same commit.
