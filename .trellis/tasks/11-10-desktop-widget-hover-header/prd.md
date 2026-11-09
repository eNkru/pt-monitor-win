# Desktop widget: hover-only header and a simpler status line

## Goal

The desktop widget carries two permanent pieces of chrome that are only needed
sometimes: the product name and the lock button. They occupy the top row at all
times, so a card that is meant to read as a bare glass plate always looks like a
labelled app window. Fade the header in only while the pointer is over the card
and out again when it leaves, and cut the bottom line down to the one number that
matters.

## Background

`DesktopWidgetWindow` (`src/PTMonitor.App/Views/DesktopWidgetWindow.axaml`) is a
250 × 150 translucent, non-activating card pinned directly above the desktop.
Row 0 is a `Grid` holding `TitleText` ("PT Monitor") and `LockButton`; row 4 is
`SummaryText`, fed from `L10n.WidgetSummary` as
`"%lld servers · %lld online · %lld active"`.

Both pieces are real controls, not decoration:

- the header names the product (a first-time user has no other label on the
  card) and hosts the lock, which is the discoverable form of *Lock position*;
- the summary is the only place the card says a server is *not* reachable — the
  rates render `—` for an offline aggregate but never say why.

Hiding the header must therefore not hide the capability: the lock stays in the
right-click menu (`_lockItem`) and the header still appears on hover.

## Requirements

1. The header (product name + lock control) is **invisible when the pointer is
   not over the card**, and fades in while it is, fades out after it leaves.
2. The header row keeps its **reserved height**: no other element moves and the
   window stays 250 × 150, so the existing placement, clamping and
   `CardWidth`/`CardHeight` math is untouched.
3. The fade is a real animation (~160 ms `Opacity` transition) that **reverses
   from the current value** instead of snapping when the pointer re-enters
   mid-fade, and the fade-out waits a short grace (~250 ms) so grazing the card
   does not flash the header.
4. An invisible header must **not swallow drags**. Presses land on the card for
   dragging unless the header is actually shown.
5. Lock state stays reachable and visible: context menu, tooltip, and the button
   itself on hover. Locking nothing new is introduced; unlocking still works the
   same way.
6. The bottom line is simplified to a status dot plus one number:
   - no server configured → neutral dot, `Not configured`;
   - every enabled server online → green dot, `%lld active`;
   - any server not online → red dot, `%lld/%lld online`.
   The old three-count form (`N servers · M online · K active`) is removed,
   including its localization key, because nothing else uses it.
7. Both languages cover the two new strings; `ApplyLocalization()` keeps working
   (the dot and the text are re-derived on a language change, not baked once).
8. No change to polling, the graph, the drag/double-click/context-menu gestures,
   the desktop pin, or the tray and Preferences toggles.
9. README's widget description and the behaviour reference stay true.

## Constraints

- `PTMonitor.Core` stays string-free; the branch that picks *which* form is
  shown follows the existing precedent of `L10n.WidgetSummary` (App layer) and
  the strings live in `L10n` with an English key plus a zh-Hans value.
- The heading text is intentionally **not** localized (`TitleText.Text` is
  assigned the literal "PT Monitor").
- The card must stay legible on a bright wallpaper: outlined text classes only,
  no new fill behind the header.
- `ShowActivated=False` / `WS_EX_NOACTIVATE` stay as they are: hover must work
  without the card ever taking focus.

## Acceptance Criteria

- [ ] Pointer off the card: header fully transparent within ~1 s, no visible
      text or button, and no layout shift when it appears.
- [ ] Pointer on the card: header fades in over ~160 ms; moving within the card
      keeps it; leaving fades it out after the grace period.
- [ ] Re-entering during the fade-out reverses the fade from its current opacity
      (no jump to 0 or 1).
- [ ] A left-press anywhere on the card — including the header strip, whether
      the header is visible or not — still drags the unlocked card; the lock
      button still clicks, and only when it is visible.
- [ ] Presses on the invisible header corner start a drag instead of a lock
      toggle.
- [ ] Status line renders exactly the three forms in requirement 6, dot colour
      included, in English and in Chinese.
- [ ] With no servers configured the dot does not promise an online state.
- [ ] `dotnet build` and `dotnet test` are green; `PTMonitor.Cli --selftest`
      still reports 296 passing checks.
- [ ] The card renders and hovers correctly with the desktop pin active (manual
      check on Windows, recorded in `verification.md`).
