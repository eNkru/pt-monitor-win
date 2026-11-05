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

## Acceptance criteria

- [ ] With the panel closed, a poll does not create, clear or rebuild any
      panel section or row view model. Opening the panel shows the current
      data (no stale list, no empty list).
- [ ] With the panel open, the list still refreshes at least as often as
      before.
- [ ] The per-poll request volume and payload are strictly lower than today
      for the panel-closed case, and the tray tooltip and the floating chip
      still update at the current rate.
- [ ] The pure parts of the new policy (which refresh a snapshot needs, how
      often the panel list is refreshed) are covered by `--selftest` and by the
      xUnit mirror.
- [ ] `dotnet build -c Release` → 0 errors; `--selftest` → all checks pass;
      `dotnet test` → all tests pass; `scripts/build.sh --invariant` → ZIP and
      CLI produced.
- [ ] Windows manual pass: the tooltip and the chip keep updating at the
      expected rate; the panel opens with data and stays live.

## Out of scope

- Changing what the panel shows (rows, fields, sort order, the 100-row cap).
- Changing the failure and backoff ladder
  (`src/PTMonitor.Core/Store/MonitorStore.cs`).
- Any mutating call to a server.
- The launch path (owned by `11-05-launch-path-performance`).

## Open questions

- **Q3 (inherited)** — the default poll interval: keep 2 s, or raise it? Raising
  it to 5 s cuts the idle network and CPU cost by 60 % at the price of slower
  tooltip updates. The child measures the actual cost first and then asks.
