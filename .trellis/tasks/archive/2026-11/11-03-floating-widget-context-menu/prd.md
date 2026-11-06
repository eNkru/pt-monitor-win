# Floating widget context menu and system hand cursor

## Goal

Right-clicking the floating speed widget opens a context menu with three
actions — **open the first server's web console**, **hide the widget**, and
**quit the app**. The hand-drawn open-hand cursor on the widget is replaced by
the system hand cursor.

## Requirements

1. **Right-click opens a context menu on the floating widget.** The menu has
   exactly three actions, in this order:
   1. **Open web console** — opens the first configured server's web console in
      the default browser. The URL is the one the model already owns:
      `ServerConfig.WebUiUri()` → qBittorrent `http://host:port/` (`https://`
      when the server uses TLS), Transmission
      `http://host:port/transmission/web/`. "First server" = the first
      **enabled** entry of `Settings.Servers` (the same set the panel shows).
      The item's header carries the server name (`Open qBittorrent web UI`).
      With no server configured the item is disabled.
   2. **Hide the floating speed window** — hides the widget and persists the
      choice (`Settings.ShowFloatingSpeeds = false`), so it stays hidden across
      restarts. The tray menu toggle ("Show floating speed window") brings it
      back.
   3. **Quit PT Monitor** — quits the agent completely (same path as the tray
      menu's Quit and the panel's Quit button).
2. **Left-click keeps its behaviour.** A press+release under the drag
   threshold still opens the panel; a drag still moves the chip. The
   right-click must not trigger the click path.
3. **System hand cursor.** The widget uses the platform hand cursor
   (`StandardCursorType.Hand`) instead of the hand-drawn bitmap. The
   now-unused `Services/CursorFactory.cs` is deleted — the app has exactly one
   cursor style left, so there is no reason to draw one.
4. **Localized.** Every menu header goes through `L10n` in both tables
   (English + Simplified Chinese). The "Open web console" header follows the
   active language and the server name.
5. **The menu follows the current state.** Headers are refreshed when the
   widget opens and when the language or the server list changes (the widget
   is already refreshed on every `Settings.Changed`).
6. **Failures stay cosmetic.** A browser that refuses to start, or a server
   whose host cannot be turned into a URL, must not crash the agent — the menu
   item simply does nothing.

## Constraints

- The App layer has no automated gate (`.trellis/spec/frontend/quality-guidelines.md`);
  the widget menu and the cursor can only be verified on Windows. Everything
  that *can* be checked on this Linux box (build, selftest, xUnit) must be.
- No new NuGet package, no image asset, no binary cursor file.
- The URL is **not** re-derived: `ServerConfig.WebUiUri()` stays the single
  owner of the web-console URL.
- The tray icon behaviour is untouched (left click toggles the panel, right
  click opens the native menu).

## Acceptance Criteria

- [x] Right-clicking the floating widget opens a context menu with exactly
      three items: open web console / hide the widget / quit the app
      (built from `Control.ContextMenu` + `ContextRequested`, which Avalonia's
      `Control.OnPointerReleased` raises only for the right button, so the
      left-click path is untouched).
- [x] "Open web console" opens the **first enabled** server's web console in
      the default browser — `http://host:port/` for qBittorrent,
      `http://host:port/transmission/web/` for Transmission, `https://` when
      the server uses TLS. With no server configured the item is disabled.
- [x] "Hide the floating speed window" hides the widget and persists the
      setting; the tray menu toggle shows it again.
- [x] "Quit PT Monitor" calls the same `Quit()` as the tray menu and the panel
      footer (stops the monitor, `desktop.Shutdown()`).
- [x] Left-click still opens the panel and dragging still moves the widget;
      the drag/click handler returns early for any non-left button.
- [x] The widget shows the system hand cursor; `Services/CursorFactory.cs` is
      gone and nothing references it (`rg CursorFactory` only hits the spec).
- [x] All three headers are localized (English + Chinese, `L10n.S` /
      `OpenInWebUi`) and are refreshed by `ApplyLocalization` on every open and
      language change.
- [x] `dotnet build -c Release`, `--selftest` (642 checks) and `dotnet test`
      (121 tests) pass; `scripts/build.sh --invariant` publishes the win-x64
      package and ZIP.
- [x] `.trellis/spec/frontend/lifecycle-and-tray.md`,
      `directory-structure.md` and `component-guidelines.md` no longer describe
      the drawn cursor, and describe the widget's context menu instead.
- [ ] Manual check on Windows (needs the user): the menu opens on right
      click, the browser opens the right console, hide/quit work.

## Verification

Run here, all green: `dotnet build -c Release` (0 errors), `--selftest`
(642/642), `dotnet test` (121/121), `scripts/build.sh --invariant`
(win-x64 publish + ZIP). Nothing in this change is Core-testable, so the
interaction itself needs the Windows pass below.

Manual pass to do on Windows (the box here has no ICU and no taskbar):

1. Right-click the chip → menu with *Open qBittorrent web UI* / *Hide floating
   speed window* / *Quit PT Monitor*.
2. Pick the first item → the qBittorrent web UI opens in the default browser.
3. Right-click again → *Hide* → the chip disappears; the tray toggle
   (*Show floating speed window*) brings it back.
4. Right-click → *Quit* → the process exits and the tray icon goes away.
5. Left-click still opens the panel; dragging still moves the chip.

Known limitation to watch in step 1: the chip window is `ShowActivated=False`
and Avalonia hosts the menu in a non-activating popup (`MA_NOACTIVATE`). The
mouse works, but a click on **another application** may leave the menu open
instead of dismissing it — clicking the chip dismisses it. If that is
annoying, replace the `ContextMenu` with a borderless window like
`MainPanelWindow` (closes on `Deactivated`).

## Notes

- Avalonia's `StandardCursorType.Hand` is the pointing hand, not the macOS
  open hand. The drawn cursor existed to get that open-hand look; the user
  asked for the system cursor instead, so the drawing goes away.
- The menu is built in code-behind: its first header is dynamic (the server
  name) and its enabled state depends on the configuration, which XAML cannot
  express. The three actions themselves are plain click handlers.
- Right-clicking the **tray icon** is a different thing and stays as it is
  (native menu). Avalonia 11.3.2 exposes no right-click event on `TrayIcon`,
  so it could not be intercepted anyway.
