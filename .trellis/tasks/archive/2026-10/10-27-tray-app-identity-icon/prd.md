# Tray icon adopts the app icon design

## Problem

The Windows tray icon is a plain disc: a grey circle (idle), a green circle
(active) and a red warning triangle (error). The app's own icon — shared with
the macOS app since PR #2 — is a much richer identity: a charcoal rounded card
with a hairline border, a solid teal down arrow, a dimmed up arrow, and a teal
pill progress meter. The tray therefore looks unrelated to the app.

The macOS menu bar does *not* use the app icon (it uses template SF Symbols per
state), so this is a deliberate Windows-side divergence: the tray keeps its
three-state signalling, but drawn from the app icon's geometry instead of a
circle.

## Requirements

1. `TrayIconFactory` draws all three tray states from **one shared geometry**
   ported from the macOS generator `scripts/make-icon.swift` (recovered from
   the macOS repo at commit `455eb93`). All layout stays proportional to the
   side length, so any render size is crisp — no downscaling.
2. Per-state colour only; shape is identical across states:
   - **idle** — card + both arrows dimmed, pill track only (no fill).
     "configured, nothing transferring".
   - **active** — exactly the macOS artwork: solid teal ↓, dimmed ↑, pill
     filled 62% teal.
   - **error** — solid red ↓, dimmed red ↑, and the hairline border replaced by
     a red stroke, so the failure state reads at tray scale.
3. **No binary asset is loaded at runtime.** Tray icons remain vector-drawn
   (`RenderTargetBitmap`); the committed `app.ico` stays the exe icon only.
   This keeps the existing spec rule intact.
4. Public API unchanged: `Idle()`, `Active()`, `Error()` returning `WindowIcon`;
   `AppServices.UpdateTray` keeps its `MenuBarState` switch untouched.
5. The card matches the real panel (`App.axaml` card is `#F0202023` with a
   `#26FFFFFF` border), not an invented palette.
6. Docs/spec stay truthful: README tray section describes the new three states,
   the porting table row changes, and the spec lines that describe "grey circle
   / green circle / warning triangle" are rewritten.

## Non-goals

- No change to `MenuBarState` logic, tooltip text, or which state wins.
- No runtime loading of `app.ico` into the tray.
- No new NuGet packages.
- No `SelfTest`/xUnit changes — tray rendering is App-only and not covered by
  the contract suite.

## Acceptance criteria

- [ ] The three factory methods render from shared helpers (card, arrow, pill),
      not three independent drawings.
- [ ] Proportions match the macOS generator: corner radius 0.225·s, arrow band
      0.34·s–0.68·s, shaft half 0.043·s, head half/depth 0.105·s, arrows at
      cx 0.37·s / 0.63·s, pill 0.52·s × 0.032·s at y 0.21·s, fill 62 %.
- [ ] Palette matches the macOS source: teal `#6FD3DE`→`#3AA7B5`, teal track
      `#4FB6C3` @12 %, card `#2E2E33`→`#131316`, hairline white @10 %.
- [ ] Rendered output verified by pixel sampling (opaque coverage, dominant
      colours, pill present for active, red border for error).
- [ ] Gates green: `dotnet build` (0 warnings), `--selftest` 204 checks,
      36 xUnit tests.
- [ ] README + spec describe the new tray states accurately.
- [ ] Installed exe on Windows still shows the app icon in Explorer (unchanged).
