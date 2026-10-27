# Share the macOS app icon with the Windows exe

## Problem

The macOS original ships `Resources/AppIcon.icns` as its app (bundle) icon.
The Windows port has no equivalent: the published `PTMonitor.exe` carries the
default .NET host icon, which the README itself lists as a limitation
("The exe uses the default .NET icon in Explorer").

## Requirements

1. The Windows exe shows the **same artwork** as the macOS app icon in
   Explorer, the taskbar, and the title bars of decorated windows
   (Preferences, Server Editor). Mechanism: `<ApplicationIcon>` in
   `PTMonitor.App.csproj` with a committed multi-size `app.ico`.
2. The `.ico` is **generated from the sibling macOS repo's
   `Resources/AppIcon.icns`** (32/64/128/256 px native PNG entries,
   PNG-compressed ICO records — no resampling). The conversion tool is
   committed as `scripts/icns-to-ico.py` (Python stdlib only) so the asset can
   be regenerated when the macOS icon changes.
3. **The tray icon is untouched.** The macOS menu bar uses per-state template
   SF Symbols, whose Windows equivalent is the runtime-drawn
   `TrayIconFactory` (idle/active/error). `AppIcon.icns` is not the menu bar
   icon on macOS and must not become the tray icon on Windows.
4. The published single-file exe (win-x64 and win-arm64) retains the icon.
5. Docs stay truthful: README limitation removed, project layout + porting
   notes mention `app.ico` and the generator; the spec's "no binary icon
   assets are committed" statements are corrected to "no binary *tray icon*
   assets; the only committed binary asset is the exe icon".

## Non-goals

- No change to the tray icon, its states, or `TrayIconFactory`.
- No per-window `Icon` overrides in code (the win32 exe icon propagates).
- No icon work in `PTMonitor.Core` (Core stays asset- and UI-free).

## Acceptance criteria

- [x] `scripts/icns-to-ico.py` regenerates `src/PTMonitor.App/app.ico` from the
      sibling `AppIcon.icns` byte-for-byte (deterministic — identical md5 on
      re-run).
- [x] `PTMonitor.App.csproj` sets `<ApplicationIcon>app.ico</ApplicationIcon>`.
- [x] A published single-file `win-x64` exe contains an `RT_GROUP_ICON`
      resource plus 4 `RT_ICON` entries (verified by parsing the PE `.rsrc`
      section); the 32px PNG payload of the .ico is found inside the exe.
- [x] Gates green after the change: `--selftest` 204 checks, 36 xUnit tests.
- [x] README + spec updated as above; no stale "default .NET icon" claims.

## Files touched

- `scripts/icns-to-ico.py` (new), `src/PTMonitor.App/app.ico` (new)
- `src/PTMonitor.App/PTMonitor.App.csproj`
- `README.md`, `.trellis/spec/frontend/*`
