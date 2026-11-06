# Design — steady-state runtime performance

Technical design for `11-05-runtime-polling-performance`. Requirements live in
`prd.md`; the ordered execution steps live in `implement.md`.

## Boundaries

| Piece | Layer | Why there |
|---|---|---|
| `PollDetail` + the snapshot's detail flag | `Core/Model` | the wire contract: what a poll needs |
| The clients' two scopes | `Core/Client` | only the client knows the server's endpoints |
| The demand, the retained lists, the wake-up | `Core/Store/MonitorStore` | the store owns the poll loops |
| The visibility gate | `App/ViewModels/PanelViewModel`, `App/Views/MainPanelWindow` | only the window knows whether the panel is on screen |
| `AsyncSignal` | `Core/Store` | a wake-up that does not busy-wait |

No new package, no new settings key, no change to the poll interval.

## 0. The two facts that shape everything

1. **The panel window is cached and only hidden** (`AppServices._panel`), so its
   view models stay alive. Today every poll posts a `Rebuild()` to the UI
   thread, which clears the section collection and re-creates up to 100 row view
   models per server (`PanelViewModel.cs:135`, `:182`) — for a window nobody
   can see.
2. **The payload is not what the display needs.** The tray tooltip and the
   floating chip want the aggregate rates; only the panel wants the list.
   qBittorrent sends the whole torrent map on every poll
   (`QBittorrentClient.cs:227`), Transmission sends the whole list as well
   (`TransmissionClient.cs:148`).

So the fix has two halves that must land together: **stop rebuilding what is
not on screen**, and **stop fetching what is not displayed**. The second half
needs the panel's visibility to reach the poll loops, so the first half is also
the input of the second.

## 1. `PollDetail`

```csharp
// Core/Model/PollDetail.cs
public enum PollDetail { Summary, Full }
```

- `Summary` — the aggregate rates, the torrent counts, the connection state and
  the free space. **No torrent list.**
- `Full` — everything, including the torrent list.

`TransferSnapshot` grows one field:

```csharp
public PollDetail Detail { get; init; } = PollDetail.Full;
```

`Detail` says what the snapshot *carries*, not what it cost: a `Full` snapshot
may still be cheap if the server had nothing to report.

`ITorrentClient` changes shape:

```csharp
Task<TransferSnapshot> FetchSnapshotAsync(PollDetail detail, CancellationToken cancellationToken);
```

One method, one parameter — the client decides how to satisfy the scope. Every
implementation and every call site changes in the same commit (there are four
call sites: the poll loop, the one-shot poll, the credential probe, and the
test doubles).

## 2. The demand: the panel's visibility, pushed into the store

The store cannot see the panel, so the App layer pushes the demand:

```csharp
// Core/Store/MonitorStore.cs
public void SetListDemand(bool listNeeded);
public bool ListDemand { get; }
```

- The flag is `volatile` — written on the UI thread, read on the poll threads.
- The poll loop reads it **before every request**, so the scope follows the
  panel without a restart:

```csharp
var detail = _listDemand ? PollDetail.Full : PollDetail.Summary;
var snapshot = await client.FetchSnapshotAsync(detail, cancellationToken);
```

- Default: **no demand** (the panel is closed at start-up).
- `SetListDemand` also fires an `AsyncSignal`, which interrupts the poll
  interval. Without it, opening the panel would wait up to one interval (2 s)
  for the first `Full` poll; with it, the list arrives in about one request
  time.

```csharp
// Core/Store/AsyncSignal.cs — a sticky wake-up.
public sealed class AsyncSignal
{
    public void Signal();
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
```

The waiters are captured under a lock, and a signal that arrives while nobody
waits is **remembered**: the next wait ends at once. A signal is never lost —
and one signal ends exactly **one** wait, so a demand change cannot cause two
poll cycles (the review's F1).

### The backoff is not interruptible

The signal ends a *normal* interval only. A failing server waits out its
`RetryDelay` (600 s for an auth failure) no matter how often the panel is
opened and closed — qBittorrent bans an IP after five failed logins, and a
demand change must never turn that into an instant retry. The **kind** decides,
not the number: `MonitorStore.HasBackoff(kind)` is true for `Auth` and
`Banned` only (the review's F2 — comparing the interval against the base
interval would break if the user's interval ceiling ever rose above 600 s).
The selftest and the xUnit mirror assert that an auth-failing server is polled
exactly once while the demand toggles.

### The retained list

A `Summary` snapshot has no list. If the store published it as-is, the panel
would lose its rows the moment the panel closed. So the store keeps the last
`Full` list per server and publishes it with the fresh summary:

```csharp
// inside SetStatus
if (status is ServerStatus.Online online)
{
    if (online.Snapshot.Detail == PollDetail.Full) _lists[id] = online.Snapshot.Torrents;
    else if (_lists.TryGetValue(id, out var list)) online.Snapshot.Torrents = list;
}
```

The published snapshot therefore always carries a list — possibly the previous
one. That is what the panel shows when it opens, and the `Full` poll that
follows within one request time replaces it.

## 3. The clients

### qBittorrent — one endpoint, two scopes

Both scopes use `/api/v2/sync/maindata`. The difference is the `rid`
parameter:

- the **full** scope sends `rid=0` → the server sends everything;
- the **summary** scope sends the **last rid the server handed out** → the
  server sends only what changed (`torrents`, `torrents_removed`,
  `trackers`, plus the current `server_state`).

The client keeps the merged library, so the counts (total, active) stay exact
without asking for the whole list. The rule that decides the rid is a pure
function, so it can be tested without a server:

```csharp
// Core/Client/QBittorrentClient.cs
public static int NextRid(
    PollDetail detail, bool libraryEmpty, int lastRid,
    DateTimeOffset now, DateTimeOffset lastFullSync, TimeSpan fullSyncInterval);
```

- `Full` → `0`. The panel shows the list, and it has to show exactly what the
  server has: a delta only reports what the server considers changed, and that
  must not decide what a progress bar shows. (This was the review's H2 — the
  delta's "changed" criterion is not verifiable from here, so the panel does
  not depend on it.)
- `Summary` → the last rid, unless the library is empty or the periodic
  re-sync is due.

A full re-sync (`rid=0`) is forced when the library is empty and every
`FullSyncInterval` (5 minutes). That bounds the damage of any drift to five
minutes, without paying for a full list every poll.

The merge is a pure function, so it can be tested without a server:

```csharp
// Core/Client/QBittorrentClient.cs
public static QbLibraryState MergeLibrary(
    QbLibraryState current,
    JsonObject? torrents,
    JsonArray? removedTorrents,
    JsonObject? trackers,
    JsonObject? serverState,
    bool fullUpdate);
```

Rules, in order:

1. `fullUpdate` → the map is the whole library → replace.
2. otherwise → copy the current library, apply the changed entries, drop the
   removed hashes.
3. **Self-heal:** a map that holds **every** torrent of the current library is
   the whole library, even when the server did not label it as one → replace.
   The test is a *superset*, not a size: a delta that is bigger than the
   library but misses a torrent is a delta, and treating it as a full set would
   lose the missing torrent (the review's F3).
4. The tracker fallback is **merged**, not replaced: a delta carries only the
   announce URLs of the torrents it mentions, and the others still need theirs
   for the site label.

`server_state` is read exactly as today (`dl_info_speed`, `up_info_speed`,
`connection_status`, `free_space_on_disk` for WebAPI 2.1.1+). The counts come
from the merged library: `TotalTorrents` = the library size, `ActiveTorrents` =
the entries in `Downloading`, `Seeding` or `Stalled` — the same rule as today.

### Transmission — one request instead of two

- `Summary` → `session-stats` only. It already carries the rates, the active
  count and the total count; the torrent list is not asked for. **One request
  per poll instead of two.**
- `Full` → `session-stats` + `torrent-get` with the fixed field list, exactly
  as today.

The free-space call stays throttled at 30 s (`TransmissionClient.FreeSpaceRefreshInterval`).

## 4. The panel's visibility gate

`PanelViewModel` grows one property:

```csharp
public bool IsPanelVisible { get; set; }
```

- `false` (default) → `StatusesChanged` and `settings.Changed` **do not
  rebuild**. No section is cleared, no row view model is created, nothing is
  posted to the UI thread.
- `true` → the demand is switched on (`_monitor.SetListDemand(true)`), and one
  rebuild runs immediately, so the panel shows the current data the moment it
  opens.
- `false` again → the demand is switched off (`SetListDemand(false)`); the
  existing rows stay in the collection, untouched — the window is hidden, so
  nobody sees them, and re-showing them costs nothing.

`MainPanelWindow` owns the truth: it sets `IsPanelVisible = true` in
`OnOpened`, and follows `Visual.IsVisibleProperty` for the hide (the panel
hides itself on `Deactivated` and on `Escape`, and `Hide()` does not raise
`Closed`).

The gate is checked **twice**: once when the event arrives (on the poll
thread) and once inside the posted action (on the UI thread). A poll that
arrives while the panel is closing must not rebuild a hidden window.

`SelectMode` and `ToggleExpanded` keep calling `Rebuild()` directly: they are
user actions inside the panel, so the panel is on screen.

## 5. What stays exactly as it is

| Thing | Why it is untouched |
|---|---|
| The 2 s poll interval | the user's decision (2026-11-06): the interval is not the lever; the payload is |
| The failure and backoff ladder (`MonitorStore.RetryDelay`) | a failing server must not be asked more often |
| The read-only guarantee | no mutating call is added |
| What the panel shows (rows, fields, sort, the 100-row cap) | the task changes *when* the panel is built, not *what* is in it |
| The tray tooltip and the chip | they read `Monitor.Aggregate`, which the `Summary` scope keeps exact |
| The tray's Active/Idle state | it is derived from the aggregate, and the counts stay exact in both scopes |

## Trade-offs

| Choice | For | Against |
|---|---|---|
| Two scopes on one client method | the store stays simple; each client picks its own cheap path | every client implementation has to honour the scope |
| qBittorrent's incremental `rid` (summary scope only) | exact counts, payload proportional to the churn | the merged library can drift if the server's delta protocol changes — bounded by the 5-minute full re-sync and the size rule |
| The full scope asks for everything | the panel shows exactly what the server has | the panel's polling costs what it always cost |
| The store retains the last list | the panel opens with data instead of an empty list | the published snapshot can carry a list that is one poll old |
| An interruptible poll interval | the panel's list arrives in ~one request time | one more moving part in the poll loop — and it must never interrupt a backoff |
| The visibility gate in the view model | the UI work stops where the UI is not | the view model now knows about window visibility |

## Review findings (2026-11-06) and what they changed

`trellis-check` reviewed the change and found three defects that mattered:

| Finding | Fix |
|---|---|
| **H1** — the demand signal interrupted the auth backoff, so opening and closing the panel five times turned a 10-minute backoff into five instant retries (and a qBittorrent IP ban) | the backoff wait is never interruptible; a selftest and an xUnit mirror assert that a failing server is polled exactly once while the demand toggles |
| **H2** — the panel's rows came from the merged delta, so a server that does not report progress-only changes would freeze the progress bars for up to 5 minutes | the **full** scope asks for everything (`rid=0`); the delta is used for the summary scope only, where only the counts matter |
| **M2** — `AsyncSignal` dropped a signal that arrived between two waits, so the wake-up the "the list arrives in one request time" promise rests on could fail | the signal is sticky: it remembers the signal and the next wait ends at once |
| **M1/M3/M4/L1–L5** — the panel's first frame, the tracker fallback, the untested rid bookkeeping, a timing-dependent assertion, `Detail` mutability, the unpruned `_lists` | each one fixed and mirrored in both suites |

A second round found four more:

| Finding | Fix |
|---|---|
| **F1** — the sticky signal delivered the wake-up **twice**: every panel open/close cost two polls, and in the full scope two whole-library downloads | the pending signals are counted, and the waiter that a signal wakes consumes it — one signal ends one wait |
| **F2** — the backoff was detected by magnitude (`interval > baseInterval`), which would break if the interval ceiling ever rose above 600 s | `MonitorStore.HasBackoff(kind)` decides by the failure kind; both suites pin it |
| **F3** — the self-heal rule ("a delta is never bigger than the library") could *lose* torrents | the self-heal is now a superset test: a map that holds every torrent of the library is the whole library |
| **F4–F8** — Transmission's scope rule had no test, the probe printed an empty list, the probe swallowed the second failure, two sentences overclaimed, the `IsVisible` handler was called "the signal" | each one fixed: a pure `NeedsTorrentList`, the probe prints the full snapshot plus the summary, the failure is reported on stderr, the wording is honest, and the spec calls the explicit `HidePanel()` the signal |

## Rollback

- The scope parameter is one enum and one parameter; reverting the clients and
  the store restores the old behaviour.
- The visibility gate is one property; removing it restores the eager rebuild.
- The incremental `rid` is one field and one merge function; setting the
  client back to `rid=0` restores the full list per poll.
