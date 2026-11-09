# Implementation plan — hover-only widget header + status dot

Order is fixed: the status line is bottom-row work and independent, the header is
the only risky part, so it comes second and is verified by hand.

## 0. Pre-flight

- [ ] `git switch -c feat/desktop-widget-hover-header` (branch off current `main`).
- [ ] `dotnet build -c Release` is green **before** touching anything, so a later
      failure is attributable.

## 1. Status line (low risk)

- [ ] `Services/L10n.cs`: add `WidgetStatus` below `ServerCount`, replacing
      `WidgetSummary`; delete the `"%lld servers · %lld online · %lld active"`
      entry from `Zh`, add `"%lld active"` → `"%lld 个活动"` and
      `"%lld/%lld online"` → `"%lld/%lld 在线"`.
- [ ] `App.axaml`: add `views|OutlinedText.widgetDot` (FontSize 9, `#B3FFFFFF`
      fill, black outline — same shape as `widgetSummary`).
- [ ] `DesktopWidgetWindow.axaml`: row 4 becomes
      `StackPanel Orientation="Horizontal"` with `StatusDot` (`●`, class
      `widgetDot`) and `StatusText` (class `widgetSummary`).
- [ ] `DesktopWidgetWindow.axaml.cs`: `ShowSummary` → `ShowStatus`, cache the
      three counters, set `StatusText.Text` from `L10n.WidgetStatus`, set
      `StatusDot.Fill` from the branch (`#B3FFFFFF` / `#2D7D46` / `#E5484D`).
- [ ] `ApplyLocalization()` re-calls the text/colour derivation from the cached
      counters so a language change cannot leave a stale line.
- [ ] `AppServices.UpdateDesktopWidget`: call `ShowStatus`.
- [ ] Build. Confirm no `WidgetSummary` reference is left (`rg WidgetSummary`).

## 2. Hover header

- [ ] `DesktopWidgetWindow.axaml`: give the row-0 `Grid` `x:Name="Header"`,
      `Classes="widgetHeader"`, `Opacity="0"`, `IsHitTestVisible="False"`.
- [ ] `App.axaml`: `Grid.widgetHeader` style with the
      `<Setter Property="Transitions"><Transitions><DoubleTransition
      Property="Opacity" Duration="0:0:0.16" Easing="CubicEaseOut" />` setter.
- [ ] Code-behind: `_exitTimer` (`DispatcherTimer`, 250 ms, tick → hide + stop),
      `ShowHeader()` (stop timer, `IsHitTestVisible = true`, `Opacity = 1`) and
      `HideHeader()` (stop timer, `Opacity = 0`, `IsHitTestVisible = false`).
- [ ] Wire `Card.PointerEntered` / `Card.PointerExited` in the constructor.
- [ ] `OnOpened`: reset to hidden without animating — set `Header.Transitions =
      null`, `Header.Opacity = 0`, `Header.IsHitTestVisible = false`, then
      `Header.ClearValue(Animatable.TransitionsProperty)`.
- [ ] Leave `SetLocked()`, the gesture handlers, `SavePosition()`,
      `EnsureDesktopPin()` untouched; only re-check that `IsOnLockButton` still
      short-circuits the press.
- [ ] Build.

## 3. Docs

- [ ] README: the widget **Content** bullet (summary form) and a new bullet
      saying the header (name + lock) appears on hover; adjust the **Lock**
      bullet so the hover form and the menu form agree; keep the 250 × 150
      behaviour-reference row.

## 4. Gates

- [ ] `dotnet build -c Release`
- [ ] `dotnet run --project src/PTMonitor.Cli -c Release -- --selftest` → 296 OK
- [ ] `dotnet test tests/PTMonitor.Core.Tests -c Release`
- [ ] `dotnet run --project src/PTMonitor.App` and walk design.md "Verification"
      1–7 by hand on Windows; record results in `verification.md` (the App layer
      has no automated gate — `frontend/quality-guidelines.md`).
- [ ] Re-run the widget checks at 150 % scaling and in 简体中文.

## Rollback points

- After §1: the status line is self-contained; reverting only that commit leaves
  the hover work intact.
- After §2: revert the single window/style commit; `L10n.WidgetStatus` and the
  counters survive untouched but unused, so revert it together with §1 if the
  whole direction is dropped.
- The hover work never touches placement constants, so a broken fade cannot
  corrupt the saved position.

## Out of scope

- Shrinking the card or changing 250 × 150 (row is reserved instead).
- Any change to the floating chip, the tray panel, polling, or `Core`.
- New settings (no "always show header" toggle; no "hide the dot" toggle).
