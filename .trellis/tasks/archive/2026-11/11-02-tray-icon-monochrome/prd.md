# Monochrome theme-adaptive tray icon

## Goal

Replace the tray artwork (light tile + hairline ring + arrow pair + progress
pill) with one flat, monochrome glyph that follows the light/dark taskbar, so
the tray reads like a normal Windows app icon (Telegram-style) instead of a
miniature of the app icon.

The floating speed chip already shows the live rates, so the tray icon no
longer has to render a "calculation" (arrow pair + progress meter). It becomes
a static identity mark.

## Requirements

1. **One flat glyph, no furniture.** No tile, no ring, no second arrow, no
   progress pill, no gradient, no shadow. The glyph is the app's simplified
   mark: one bold down arrow over a short bar (the same silhouette the exe
   icon already uses for its sub-32 px entries).
2. **Monochrome and theme-aware.** The glyph is a single flat colour that
   follows the OS light/dark setting: near-black on a light taskbar, white on a
   dark taskbar. It must follow the taskbar, not the app's own accent colour.
3. **Static.** The glyph never encodes live rates, progress or a meter. It is
   redrawn only when the state or the theme changes — never on a poll tick.
4. **State is still visible.** Idle / active / error remain distinguishable
   without a second drawing:
   - idle → the glyph at reduced opacity,
   - active → the glyph at full opacity,
   - error → the glyph in red (light theme `#C42B1C`, dark theme `#FF99A4`).
5. **Windows-native rendering.** The tray icon must be a real multi-size icon
   (16/20/24/32/48 px) with a transparent background and smooth edges, so
   Windows picks the exact taskbar size and blends the glyph over the taskbar
   instead of hard-masking it.
6. **No new binary assets.** The icon is drawn at runtime from vector geometry
   and packaged into an in-memory ICO — nothing is added to the repository and
   nothing is loaded from disk.
7. **No icon churn.** The previous crash (`.trellis/tasks/11-01-tray-icon-crash`)
   must not come back: one native icon per (state, theme), reused, and
   `TrayIcon.Icon` assigned only when that pair actually changes.
8. **Failure stays cosmetic.** A failure while building or assigning the icon
   must never terminate the agent (the existing scoped `try`/`catch` in
   `AppServices.UpdateTray` stays).

## Constraints

- The app layer has **no automated gate** (see
  `.trellis/spec/frontend/quality-guidelines.md`); the tray icon can only be
  verified on Windows. Everything that *can* be checked on this Linux box
  (build, glyph geometry, ICO byte layout) must be.
- `app.ico` (the Explorer / taskbar window icon) is **out of scope** — it keeps
  the coloured tile. Only the tray icon changes, which means the tray artwork
  and the exe artwork stop being the same drawing.
- No new NuGet package, no XAML resource dictionary, no committed image file.

## Acceptance Criteria

- [x] The tray icon is a single monochrome glyph (arrow over bar) with a
      transparent background — no tile, ring, arrow pair or progress pill.
- [x] The glyph is near-black on a light taskbar and white on a dark taskbar,
      and it switches when the OS light/dark setting changes at runtime
      (`Application.ActualThemeVariantChanged`, raised by Avalonia's Win32
      backend on `WM_SETTINGCHANGE`/"ImmersiveColorSet").
- [x] Idle is dimmed, active is full opacity, error is red; all three share
      one geometry.
- [x] The tray icon is delivered as a multi-size ICO (16/20/24/32/48) built in
      memory at runtime, with per-pixel alpha (straight, not premultiplied).
- [x] `TrayIcon.Icon` is assigned only when the (state, theme) pair changes;
      the per-pair native icon is created once and reused.
- [x] `dotnet build -c Release`, `--selftest` and `dotnet test` pass
      (0 errors, 642 selftest checks, 121 xUnit tests); `scripts/build.sh`
      publishes the win-x64 package.
- [x] The generated glyph renders as expected (preview PNGs reviewed on this
      Linux box; the ICO bytes parse back into the expected five entries).
- [x] `.trellis/spec/frontend/lifecycle-and-tray.md`, `component-guidelines.md`,
      `directory-structure.md`, `backend/logging-guidelines.md`, `README.md`
      and the `scripts/make-app-icon.py` docstring no longer claim the tray and
      the exe icon are the same drawing.
- [ ] Manual check on Windows (needs the user; the tray cannot run on this
      Linux box): the icon shows a flat glyph on both a light and a dark
      taskbar, the edge is smooth, and the state colours are visible. The
      win-x64 build is at `publish/win-x64/PTMonitor.exe`
      (`artifacts/PTMonitor-1.0.0-win-x64.zip`).

## Notes

- The tray glyph and the exe icon are now **different drawings**: the exe icon
  keeps the coloured tile, the tray uses the flat monochrome mark. They no
  longer share constants.
- The `.ico` route is deliberate. `WindowIcon(Bitmap)` on Win32 ends in
  `CreateIconIndirect` with `fIcon = FALSE` — a 1-bit AND mask, no alpha, hard
  edges, and one bitmap downscaled to the taskbar size. A real `.ico` with
  32-bit entries keeps the alpha.
- Known follow-up: the ICO layout has no CI coverage (the App layer has no
  automated gate). It was verified here by parsing the generated bytes back
  and rendering every entry to PNG; an `InternalsVisibleTo` + test project
  would close that gap.
