# Solid tray glyph without frame

> **Revised during review** (see [Revision history](#revision-history)): the
> artwork shipped as a **fixed** mark — a bright white rounded tile with the
> arrow-over-bar knocked out of it in the dark ink — the same in the light and
> the dark theme. The requirements below are the ones the task started with;
> the acceptance criteria record what actually shipped.

## Goal

The system-tray icon (bottom-right corner of the taskbar) must read as one
bright, solid mark on a dark Windows taskbar. Today it is a hairline
rounded-square frame around a white arrow-over-bar: the frame is the same
white as the glyph, so the two merge into a busy badge and the outline is the
first thing the eye catches.

Drop the outline entirely and draw the arrow over its bar as one solid
silhouette that fills the tile. Nothing else about the icon changes: the
colour still follows the taskbar, the three states stay distinguishable, and
the icon is still drawn at runtime and packed as a real multi-size `.ico`.

## Requirements

1. **No frame, no outline, no tile.** The tray icon is one flat silhouette —
   the bold down arrow over the short bar — on a transparent background.
2. **The whole piece is one solid colour.** No second colour, no gradient, no
   stroke, no shadow. The fill follows the taskbar: near-black on a light
   taskbar, white on a dark one.
3. **The glyph fills the tile.** With the frame gone there is nothing to
   frame the mark, so the glyph is scaled about the icon centre and stays
   legible at the 16 px entry the taskbar asks for.
4. **States stay distinguishable.** Idle is the same glyph at reduced
   opacity, active is full opacity, error is red — light theme `#C42B1C`,
   dark theme `#FF99A4`. One geometry covers all three.
5. **No asset and no churn.** The icon is still drawn from vector geometry at
   runtime and packed in memory as a 16/20/24/32/48 px `.ico` with per-pixel
   alpha; one native icon per (state, theme) is created and reused.
6. **The exe icon is untouched.** `app.ico` (the taskbar window / Explorer
   icon) keeps its coloured tile — this task is about the tray glyph only.

## Constraints

- The app layer has **no automated gate** (see
  `.trellis/spec/frontend/quality-guidelines.md`); the tray icon can only be
  verified by eye on Windows. Everything that *can* be checked on this Linux
  box (build, self-test, unit tests, ICO byte layout, rendered preview PNGs)
  must be.
- The tray icon is cached per (state, theme) and must not be rebuilt per poll
  tick — the previous crash (`.trellis/tasks/11-01-tray-icon-crash`) must not
  come back.
- No new NuGet package, no XAML resource, no committed image file.

## Acceptance Criteria

- [x] The tray icon is one flat mark with no frame, no outline and no
      tile-in-the-app-icon sense: a bold down arrow over a short bar.
- [x] The glyph is scaled about the icon centre so it fills the tile, and
      stays legible at 16 px.
- [x] Idle is dimmed, active is full opacity, error is red; the states stay
      distinguishable.
- [x] The tray icon is still delivered as a multi-size `.ico` (16/20/24/32/48)
      built in memory, with straight-alpha pixels; the container parses back
      into the expected five entries.
- [x] `dotnet build -c Release`, `--selftest` and `dotnet test` pass.
- [x] The rendered glyph was reviewed as a preview PNG on this Linux box (the
      tray itself cannot run here).
- [x] `.trellis/spec/frontend/lifecycle-and-tray.md` and `README.md` describe
      the new artwork.
- [ ] Manual check on Windows (needs the user): the tray shows the bright
      tile with the dark arrow knocked out of it, on both a light and a dark
      taskbar.

## Revision history

The artwork changed twice while the task was open, both times on the user's
request after looking at the rendered previews:

1. **No frame, colour follows the taskbar** — the hairline frame was dropped
   and the glyph kept the taskbar colour (commit `5ecd251`).
2. **Colours reversed** — the glyph moved into a bright tile and took the
   dark ink; the error took the dark red (commit `9ac0834`).
3. **Fixed in every theme** — the icon no longer follows the taskbar at all:
   the same white tile with the dark arrow in the light and the dark theme.
   The theme dimension was removed from `TrayIconFactory`, from the icon
   cache and from `AppServices.UpdateTray`, and the
   `ActualThemeVariantChanged` hook with it (commit `ab6e561`).

The shipped requirement is therefore: **one fixed mark — a white rounded tile
with the arrow knocked out of it in the dark ink — identical in both themes.**

## Notes

- The user asked for the tray icon in the bottom-right corner of the taskbar,
  not the exe icon.
- Rendered previews live in `artifacts/tray-preview/` (git-ignored):
  `new/` is the shipped artwork, `A-current-*` the old look.
