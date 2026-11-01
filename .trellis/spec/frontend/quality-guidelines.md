# Quality Guidelines (App)

> The App layer has no automated gate. These are the manual checks that replace one.

---

## Before you call a UI change done

```bash
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release
dotnet run --project src/PTMonitor.App
```

The first three catch `Core` regressions; the app itself barely compiles-fail.
**A change to `Program.cs` or `App.axaml.cs` has no automated gate at all** — the
suite never starts a window. If you touch startup, launch the exe and confirm a
process survives 30 seconds with a clean `ptmonitor.log`, not just that it
builds. Then exercise the change by hand:

- [ ] Tray icon appears, left-click toggles the panel, right-click opens the menu
- [ ] Panel opens bottom-right, closes on Escape, click-away and deactivation,
      and the process survives all three
- [ ] Every new/changed label reads correctly in **English and 简体中文**
- [ ] Missing data shows `—`, not `0`, and no row renders blank
- [ ] Layout holds at 100 % and 150 % display scaling, and at a narrow window
- [ ] Empty state (no servers), single-server, all-failing and offline states
      all render sensibly
- [ ] Quit from the tray menu actually exits, and monitor loops stop
- [ ] Preferences → Save round-trips through a restart

---

## Required patterns

| Area | Requirement |
|---|---|
| Bindings | `x:DataType` on every window and template |
| Text | `x:Name` + `ApplyLocalization()` for anything translated |
| Logic | Decision in `Core`, wording in the view-model |
| State | writes through `SettingsStore`, reads re-fetched per rebuild |
| Errors | `ServerFailure.Kind` → headline, `.Message` as detail |
| New string | both `L10n` tables in the same commit |
| Handler | null-safe `sender`/`DataContext` cast with early return |

---

## Forbidden patterns

- **Any Avalonia API in `Program.Main` before `BuildAvaloniaApp()` runs.** The
  platform has not installed its dispatcher yet, and `Dispatcher.UIThread`
  silently constructs one with the *default* (unsupported) implementation:
  accessing it does **not** throw, so a "does this throw?" check passes, and then
  `Dispatcher.MainLoop` throws `PlatformNotSupportedException` before the first
  frame — an instant exit on every launch with no window and no tray icon.
  Subscribe `Dispatcher.UIThread.UnhandledException` in
  `App.OnFrameworkInitializationCompleted` instead. This cost a broken release on
  2026-11-02; the comment at the top of `Program.cs` said it already.
- Domain logic in code-behind or XAML.
- A hardcoded user-visible string (English included).
- `async void` handlers without a `try`/`finally` re-enable of the button.
- Blocking on a task in a handler.
- A second `HttpClient`, `SettingsStore` or `MonitorStore` instance.
- Showing a dialog for a cosmetic failure (`OpenUrl`), or a modal error box
  where `MessageText` exists.
- A new package for UI plumbing.
- Binary assets, resource dictionaries, or a control library.
- `Thread.Sleep`, `Task.Delay` on the UI thread.
- A `Window.Show()` that is not cached in `AppServices`.

---

## Review checklist

- [ ] `x:DataType` present; no reflection bindings
- [ ] Text via `L10n`, both languages present, no clipping
- [ ] Styles use existing classes; new look used twice → class in `App.axaml`
- [ ] Row/detail decisions come from `Core`, not rebuilt locally
- [ ] `PropertyChanged` raised for every property whose state changed
- [ ] Timer started/stopped symmetrically
- [ ] Escape/close/quit behaviour unchanged
- [ ] `SystemDecorations`/`ShowInTaskbar`/transparency retained on the panel
- [ ] `—` placeholders for absent values
- [ ] No new state duplicated outside the two stores
