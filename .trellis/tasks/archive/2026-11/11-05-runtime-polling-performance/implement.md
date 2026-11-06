# Implement — steady-state runtime performance

Execution plan for `11-05-runtime-polling-performance`. Design in `design.md`,
requirements in `prd.md`.

## Before starting

- Read `design.md` §1–§4 before touching the clients: the scope parameter
  changes the `ITorrentClient` contract, and **every** implementation and call
  site changes in the same commit.
- The App layer has **no automated gate** (`frontend/quality-guidelines.md`).
  Everything reachable from `Core` gets a selftest case **and** an xUnit
  mirror; everything else is a Windows manual check.

## Checklist

### Step 1 — the contract (Core)

- [x] `src/PTMonitor.Core/Model/PollDetail.cs`: `Summary`, `Full`.
- [x] `TransferSnapshot.Detail` (`PollDetail`, default `Full`).
- [x] `ITorrentClient.FetchSnapshotAsync(PollDetail, CancellationToken)`.
- [x] Call sites updated together: `MonitorStore` (loop + one-shot poll),
      `ProbeRunner`, the selftest doubles, the xUnit doubles.

### Step 2 — the store (Core)

- [x] `MonitorStore.SetListDemand(bool)` / `ListDemand` (volatile), the scope
      chosen before every request.
- [x] `AsyncSignal` (`Core/Store/AsyncSignal.cs`): `Signal()`, `WaitAsync()`;
      the poll interval waits on it, so a demand change wakes the loop.
- [x] The retained list per server: a `Full` snapshot replaces it, a `Summary`
      snapshot is published with it.
- [x] The one-shot poll (`MonitorStore.PollAsync`) takes a `PollDetail`
      (default `Full`, so the existing checks keep their meaning).

### Step 3 — qBittorrent (Core)

- [x] `QBittorrentParser.MergeLibrary(...)`: replace on `fullUpdate`, apply +
      remove otherwise, and the "a delta is never bigger than the library"
      self-heal rule.
- [x] `QBittorrentClient`: `_rid`, the merged library, a forced full re-sync
      every 5 minutes and whenever the library is empty.
- [x] `Summary` → the snapshot carries the rates, the counts and the
      connection state, but no list. `Full` → the merged library as the list.
- [x] The counts come from the merged library with today's rule
      (`Downloading`, `Seeding`, `Stalled`).

### Step 4 — Transmission (Core)

- [x] `Summary` → `session-stats` only (rates, active count, total count).
- [x] `Full` → `session-stats` + `torrent-get` with the fixed field list.
- [x] The free-space call stays throttled at 30 s.

### Step 5 — the panel gate (App)

- [x] `PanelViewModel.IsPanelVisible`: `true` → demand on + one rebuild;
      `false` → demand off, no rebuild, the rows stay as they are.
- [x] `StatusesChanged` and `settings.Changed` rebuild only while the panel is
      visible — checked on the poll thread **and** inside the posted action.
- [x] `MainPanelWindow`: `OnOpened` sets the flag; `Visual.IsVisibleProperty`
      follows the hide (`Hide()` does not raise `Closed`), and the window's own
      hide paths call `HidePanel()`.
- [x] `SelectMode` / `ToggleExpanded` keep rebuilding directly.
- [x] The freshness timer stops with the panel.

### Step 6 — the checks (Core, both mirrors)

- [x] Selftest (`src/PTMonitor.Core/Diagnostics/SelfTest.cs`):
      - the merge function: full replace, delta apply, removal, the self-heal
        rule, the counts, the tracker fallback;
      - the store: the scope follows the demand (a recording client double),
        a `Summary` snapshot keeps the last list, the demand wakes the loop;
      - after the review: a backoff is not interrupted by the demand, the
        signal is not lost between two waits, the rid rule, the tracker
        fallback survives a delta.
- [x] xUnit mirror (`tests/PTMonitor.Core.Tests/CoreTests.cs`): the same
      assertions (`PollScopeTests`).
- [x] Verify after every Core edit: `dotnet build -c Release`, `--selftest`,
      `dotnet test`.

### Step 6a — the review fixes (2026-11-06)

- [x] **H1** — the backoff wait is never interruptible (the demand may not turn
      a 10-minute auth backoff into an instant retry).
- [x] **H2** — the full scope asks for everything (`rid=0`); the delta is used
      for the summary scope only, so the panel never depends on the server's
      "changed" criterion.
- [x] **M2** — `AsyncSignal` is sticky: a signal between two waits is not lost.
- [x] **M1** — the panel's first frame is honest: the last full list, then the
      fresh one; the docs say so.
- [x] **M3** — the tracker fallback is merged, not replaced.
- [x] **M4** — the rid rule is a pure function (`NextRid`) and is covered by
      both suites; the stub verifies the request sequence.
- [x] **L1–L5** — the index-based assertion, `Detail` back to `init`, the
      probe's second poll, `InvariantCulture`, and the pruning of `_lists`.

### Step 6b — the second review fixes (2026-11-06)

- [x] **F1** — one signal ends one wait (`AsyncSignal` counts the pending
      signals); a demand change no longer costs two polls.
- [x] **F2** — the backoff is decided by the failure kind
      (`MonitorStore.HasBackoff`), not by comparing the intervals.
- [x] **F3** — the self-heal is a superset test, not a size test: a bigger
      map that misses a torrent stays a delta.
- [x] **F4** — `TransmissionClient.NeedsTorrentList(detail)` is a pure rule
      and both suites pin it.
- [x] **F5/F6** — the probe prints the full snapshot plus the summary's
      counters, and a failing second poll is reported on stderr.
- [x] **F7/F8** — the wording is honest about the panel's first frame, and the
      spec calls the explicit `HidePanel()` the signal (the `IsVisible`
      property is the safety net).

### Step 7 — docs

- [x] `README.md`: what the panel costs while it is closed, and the two scopes.
- [x] Specs: `frontend/lifecycle-and-tray.md` (the deferred panel, the gate),
      `frontend/state-management.md` (the visibility-driven rebuild),
      `backend/client-and-parser-rules.md` (the two scopes, the rid protocol),
      `backend/concurrency-guidelines.md` (the demand, the wake-up, the
      retained list), `frontend/viewmodel-guidelines.md` (the visibility gate).

### Step 8 — Windows manual pass

- [ ] The tooltip and the chip keep updating every 2 s with the panel closed.
- [ ] The panel opens with data (not an empty list) and stays live.
- [ ] The tray icon still switches between Idle and Active (the counts are
      exact in both scopes).
- [ ] qBittorrent: the panel's list matches the server's list after a while
      (the 5-minute full re-sync is the safety net).
- [ ] Open and close the panel five times: the poll scope must fall back to
      the summary every time (the `IsVisible` property is only a safety net —
      the explicit `HidePanel()` is the signal).
- [ ] `ptmonitor.log` stays clean for 30 s.

### Step 8a — the stub server (done on Linux)

- [x] `scripts/fake-qbittorrent.mjs` speaks the rid protocol: `rid=0` is a full
      update, the last rid is a delta (one torrent changed, one removed), and
      an unknown rid gets a full update with an `UNEXPECTED rid` log line.
- [x] `PTMonitor.Cli --probe qbittorrent` against the stub: exit 0; the first
      request is `rid=0`, the second carries the rid from the first answer; the
      printed snapshot is the merged delta (`scope: summary`,
      `totalTorrents` 1 of 2, the delta's speed).
- [x] The same with `WEBAPI=5.2` — identical outcome.
- [x] Wrong credentials still report `auth failed`, not `banned`.

## Validation commands

```bash
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release
scripts/build.sh --invariant
```

`--selftest` and `dotnet test` must be run after every `Core` edit, not once at
the end.

## Risky points

| Point | Risk | Guard |
|---|---|---|
| The qBittorrent delta protocol | the merged library drifts → the counts are wrong | the 5-minute full re-sync, the "a delta is never bigger than the library" rule, and `full_update` handling |
| The counts move to the merged library | the tray's Active/Idle state changes | the same rule as today (`Downloading`/`Seeding`/`Stalled`), checked against the real server in the manual pass |
| The retained list | the panel opens with a list that is one poll old | the demand wakes the loop, so the fresh list follows within one request time |
| The visibility gate | the panel opens empty or stale | the rebuild on becoming visible, plus the `Full` poll that follows |
| The interruptible interval | a lost wake-up → the loop sleeps 2 s too long | `AsyncSignal` is sticky: a signal is remembered, never lost |
| The interruptible interval | **a backoff is cut short → an IP ban** | the backoff wait is never interruptible; both suites assert that a failing server is polled once while the demand toggles |
| The scope parameter | an implementation ignores the scope → the payload stays high | the interface forces the parameter; the selftest pins the store's choice |
| The full scope asks for everything | the panel's payload is what it always was | by design: the panel must show exactly what the server has |

## Rollback points

- After Step 1: revert the signature and the call sites.
- After Step 3: set the qBittorrent client back to `rid=0` (one line) — the
  full list per poll, everything else still works.
- After Step 5: remove `IsPanelVisible` — the eager rebuild returns.

## Definition of done

- All PRD acceptance criteria ticked, the Linux gates green, and the Windows
  manual pass reported back.
