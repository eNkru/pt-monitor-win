# Steady-state runtime performance

Child of `11-05-performance-startup-runtime`. Owns what the agent does while
it runs with nobody looking at it.

## Goal

An idle agent must cost roughly what it costs to read the numbers it displays
— no more. Today it rebuilds a window nobody can see on every poll, and it
pulls the full torrent list from the server on every poll even when the panel
that would show it is closed.

## Confirmed facts

See the parent PRD for the anchors. In short:

- `PanelViewModel` posts a full `Rebuild()` to the UI thread on every
  `StatusesChanged` (`src/PTMonitor.App/ViewModels/PanelViewModel.cs:135`),
  and the panel window is cached and only hidden
  (`src/PTMonitor.App/Services/AppServices.cs:_panel`). So the panel UI is
  rebuilt every poll — forever — after the panel has been opened once, even
  while it is hidden. `Rebuild()` clears the section collection and creates up
  to 100 row view models per server (`PanelViewModel.cs:123`, `:182`).
- `settings.Changed` triggers the same rebuild (`PanelViewModel.cs:136`).
- qBittorrent is polled with `/api/v2/sync/maindata?rid=0`
  (`src/PTMonitor.Core/Client/QBittorrentClient.cs:239`): `rid=0` is a full
  sync, so the whole torrent list and the tracker map arrive every poll.
- Transmission asks for a fixed lean field list
  (`src/PTMonitor.Core/Client/TransmissionClient.cs:10-16`) and throttles the
  free-space call (`TransmissionClient.cs:229`).
- The default poll interval is 2 s (`src/PTMonitor.Core/Store/SettingsStore.cs:14`).

## Requirements

1. **No hidden-panel rebuilds.** While the panel is closed, a poll must not
   rebuild the panel's view models. The panel must show current data the moment
   it opens, and must stay live while it is open.
2. **The same rule for settings changes.** A settings change must not rebuild a
   hidden panel either.
3. **The poll cost must match what is displayed.** The full torrent list must
   not be fetched on every poll when nothing shows it. The tray tooltip and the
   floating chip need the aggregate rates; the panel needs the list. The design
   must make the cheap path cheap and keep the panel's data fresh while it is
   open.
4. **The behaviour the user sees must not get worse.** Speeds in the tooltip
   and the chip stay as fresh as they are now, or fresher. The panel's list
   stays as fresh as it is now while the panel is open. The read-only
   guarantee and the failure/backoff behaviour are untouched.
5. **The Linux gates stay green**, and every behaviour change that can be
   expressed as a pure rule is covered by `--selftest` and the xUnit mirror
   (the spec's rule: a behaviour change updates both in the same commit).
6. **The decision on the poll interval is the user's** — the child may measure
   and propose, but the default only changes with an explicit answer.

## Decisions

- **D1 — the poll interval stays at 2 s.** The user's answer (2026-11-06):
  keep 2 seconds. The interval is not the lever: the panel-closed cost comes
  from *what* is fetched and *what* is rebuilt, not from how often. Nothing the
  user sees gets less fresh.
- **D2 — two poll scopes, not two poll loops.** `Summary` (rates, counts,
  connection state, free space — no torrent list) and `Full` (everything). The
  scope is chosen per request from one flag: the panel's visibility, pushed
  into the store by the App layer.
- **D3 — qBittorrent keeps one endpoint and sends the last `rid`.** The
  server then reports only what changed; the client merges the deltas into its
  library, so the counts stay exact and the payload follows the churn instead
  of the library size. A full re-sync every 5 minutes heals any drift.
  **Revised after the review:** the *full* scope always asks for everything
  (`rid=0`), because the panel has to show exactly what the server has and the
  server's "changed" criterion must not decide what a progress bar shows. The
  delta is used for the *summary* scope, where only the counts matter.
- **D4 — Transmission's summary poll is one request.** `session-stats` already
  carries the rates and the counts; the torrent list is only asked for while
  the panel is on screen.
- **D5 — a backoff is never interrupted.** The demand wakes a poll loop out of
  a *normal* interval only. A failing server waits out its `RetryDelay`
  (600 s for an auth failure) no matter how often the panel is opened and
  closed: qBittorrent bans an IP after five failed logins.

## Acceptance criteria

- [x] With the panel closed, a poll does not create, clear or rebuild any
      panel section or row view model. Opening the panel shows the current
      numbers at once and the fresh list within one request time; it never
      invents rows. *`PanelViewModel.IsPanelVisible` gates both triggers; the
      gate is checked on the poll thread and again on the UI thread. The store
      keeps the last full list, so the panel shows that list for a fraction of
      a second and the fresh one right after; before the first full poll there
      is nothing to show.*
- [x] With the panel open, the list still refreshes at least as often as
      before. *The demand switches the scope on and wakes the loop, so the
      first list poll lands in about one request time; the full scope asks the
      server for everything, so the panel shows exactly what the server has.*
- [x] The per-poll payload is strictly lower than today for the panel-closed
      case: no poll asks a server for the whole torrent list while nothing
      shows it (except the 5-minute re-sync, which is one request in 150), and
      the tray tooltip and the floating chip still update at the current rate
      (2 s). *qBittorrent sends the last `rid`, so the server reports only the
      changes; Transmission's summary poll is one `session-stats` request
      instead of two. The counts stay exact in both scopes, so the tray's
      Active/Idle state does not change.*
- [x] The pure parts of the new policy (which scope a poll asks for, how the
      qBittorrent library is merged, which snapshot carries which list) are
      covered by `--selftest` and by the xUnit mirror. *692 selftest checks
      (44 new) and 150 xUnit tests (24 new): `PollScopeChecks` and
      `PollScopeTests`.*
- [x] `dotnet build -c Release` → 0 errors; `--selftest` → all checks pass;
      `dotnet test` → all tests pass; `scripts/build.sh --invariant` → ZIP and
      CLI produced. *Re-verified: 0 errors, 692 checks, 150 tests, the ZIP and
      the CLI produced.*
- [x] The stub server speaks the rid protocol, and the probe against it
      returns the merged delta. *`scripts/fake-qbittorrent.mjs` answers `rid=0`
      with the full library and the last rid with a delta; the probe prints
      the full snapshot and the merged `summary` (`totalTorrents` 1 of 2, the
      delta's speed) in both WebAPI modes, and the stub's log shows the rid
      sequence without an `UNEXPECTED rid`.*
- [x] A failing server is not polled again because the panel was opened and
      closed. *The backoff wait is not interruptible; the selftest and the
      xUnit mirror assert that an auth-failing server is polled exactly once
      while the demand toggles.*
- [ ] Windows manual pass: the tooltip and the chip keep updating at the
      expected rate; the panel opens with data and stays live; the tray icon
      still switches between Idle and Active; qBittorrent's panel list matches
      the server's; the log stays clean for 30 s. **Still open — needs the
      target machine.**

## Out of scope

- Changing what the panel shows (rows, fields, sort order, the 100-row cap).
- Changing the failure and backoff ladder
  (`src/PTMonitor.Core/Store/MonitorStore.cs`).
- Any mutating call to a server.
- The launch path (owned by `11-05-launch-path-performance`).

## Open questions

- **Q3 (inherited) — answered: keep 2 s.** The default poll interval stays at
  2 seconds (D1, 2026-11-06). The idle cost is cut by fetching less and
  rebuilding less, not by polling less often.
