# Implementation plan — tray icon adopts the app icon design

Branch: `tray/app-identity-icon`. Single code file + doc/spec lines.

## Checklist

1. **Port the geometry** — rewrite `src/PTMonitor.App/Services/TrayIconFactory.cs`
   with `TrayState`, shared `DrawCard` / `DrawArrow` / `DrawPill` helpers, and
   `Render(TrayState)`. Ratios and palette exactly as in `design.md`; remember
   the CG→Avalonia y-flip (`ay = s - y`).
   Validate: `dotnet build -c Release` → 0 errors, **0 warnings**.
2. **Pixel verification (harness, not committed)** — `/tmp/iconprobe` renders
   `Idle/Active/Error` bitmaps to PNG:
   ```bash
   export PATH="$HOME/.dotnet:$PATH" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
   cd /tmp/iconprobe && dotnet build -c Release -r linux-x64 --self-contained false
   dotnet bin/Release/net8.0/linux-x64/IconProbe.dll
   ```
   Then read stats back with Windows `System.Drawing` (`/tmp/probestats.ps1`):
   opaque coverage, dominant colour buckets, teal present in active, absent in
   idle, red present in error, and coverage within the `app.ico` 32 px band
   (~95 %).
   Gate: all five assertions hold. **Review gate** — report numbers before
   touching docs.
3. **Gates** —
   ```bash
   dotnet build -c Release
   dotnet run --project src/PTMonitor.Cli -c Release -- --selftest   # 204 checks
   dotnet test tests/PTMonitor.Core.Tests -c Release                 # 36 tests
   ```
4. **Docs + spec truth pass** — README tray section (three states described by
   colour/fill, not circle/triangle), porting table row for the tray, and the
   spec lines in `frontend/lifecycle-and-tray.md` +
   `frontend/component-guidelines.md` that say "idle grey circle / active green
   circle / red warning triangle" / "adding an icon file instead of drawing it".
   Grep sweep: no stale "green circle" / "warning triangle" wording anywhere.
5. **Real-Windows check** — publish, install to
   `C:\Users\enkru\AppData\Local\PTMonitor`, launch detached, confirm the tray
   tooltip still reports the state and the exe icon is unchanged
   (`RT_GROUP_ICON` present).
   Rollback point: revert `TrayIconFactory.cs` + docs if any assertion fails.
6. **Commit + PR**, then `task.py finish` / `task.py archive`.

## Do not

- Do not add `Avalonia.Headless` / `SkiaSharp` to the repo — they belong to the
  scratch harness in `/tmp` only.
- Do not load `app.ico` at runtime for the tray.
- Do not change `AppServices.UpdateTray`, `MenuBarState`, tooltip text, or the
  contract suite.
- Do not use the hand-written PNG decoder for verification; use
  `System.Drawing`.
