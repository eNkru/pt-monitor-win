# Concurrency & Polling

> One loop per enabled server, one lock per store, one publish per real change.

---

## The model

`MonitorStore` is the only concurrent component. Shape:

```csharp
private readonly object _gate = new();
private readonly Dictionary<Guid, ServerStatus> _statuses = [];
private readonly Dictionary<Guid, CancellationTokenSource> _tasks = [];
public event EventHandler? StatusesChanged;
```

```
Reconcile():
  cancel + dispose every existing CTS (outside the lock)
  rebuild the status dictionary (inside the lock), raise Changed if it differs
  foreach enabled server → new CTS, store it, Task.Run(PollLoopAsync)

PollLoopAsync:
  await using client = factory(config, secret(config.Id))
  loop:
    interval = settings.PollInterval
    try fetch → Online
    catch → Failed + RetryDelay
    delay(interval), exit on cancellation
```

Rules that must survive any edit:

1. **Never one shared loop.** A hanging server must not stall the others.
2. **Never poll a disabled server.** The factory is not even invoked
   (`PollAsync` returns `Disabled` before calling it) — the selftest asserts the
   factory call count.
3. **A slow or blocking call takes a token**, and `OperationCanceledException`
   is filtered on `cancellationToken.IsCancellationRequested` so a timeout is
   not mistaken for a shutdown.
4. **`Reconcile()` after any configuration change**, including password-only
   edits.
5. **Cancel and dispose every CTS** on `StopAsync`, `Dispose`, and at the top of
   `Reconcile`.

---

## Locking

- One `_gate` per store (`MonitorStore`, `SettingsStore`, `DpapiSecretStore`).
- Getters that hand a collection outward return a copy:
  `get { lock (_gate) return new Dictionary<Guid, ServerStatus>(_statuses); }`.
- Never `await` inside `lock`. Take a snapshot under the lock, then act.
- Never raise an event under the lock — `RaiseChanged` is called after.
- `MonitorStore.PollAsync` is `static` and takes its dependencies so it can run
  without instance state.

---

## Change detection

`IsEquivalent` decides whether a new status is worth publishing, and it
**ignores `FetchedAt`** so identical polls do not churn the UI. It compares:

- `Failed` by the whole `ServerFailure` record (kind **and** message);
- `Online` via `SnapshotEquivalent`: speeds, counts, free space, connection
  state, torrent count, then per-torrent id/state/progress/speeds/ETA.

Add a new displayed snapshot field to `SnapshotEquivalent`, or a change to it
will go unnoticed until something else moves.

---

## Backoff

```csharp
public const double CredentialFailureBackoff = 600; // 10 min
public const double BannedBackoff = 900;           // 15 min

public static double RetryDelay(ServerStatus status, double baseInterval) =>
    status.Failure?.Kind switch
    {
        FailureKind.Auth => Math.Max(baseInterval, CredentialFailureBackoff),
        FailureKind.Banned => Math.Max(baseInterval, BannedBackoff),
        _ => baseInterval,
    };
```

`Math.Max` — backoff **never shortens** a longer user interval. Why it exists:
qBittorrent bans an IP after 5 failed logins with no time limit, so a 2 s retry
turns one typo into a one-hour ban. The ladder is banned > auth ≥ 600 s.

`RefreshNowAsync` bypasses the delay (and the loop) for enabled servers, so
"Refresh Now" is an immediate retry.

---

## Aggregation stays pure

`AggregateOf`, `MenuBarStateOf`, `RetryDelay`, `StatusFrom` are static and take
a dictionary. `AggregateOf` clamps negatives with `Math.Max(0, …)` — a broken
server cannot make the tray total go backwards. `MenuBarStateOf` maps
all-failing → `Error`, any traffic → `Active`, else `Idle`.

---

## Anti-patterns

- `Task.Run` per fetch with no cancellation, or a `Timer` that overlaps itself.
- Calling `Task.Delay` without the token.
- `lock` around `await`, `I/O`, or an event invocation.
- Publishing `Online` without `IsEquivalent` (guaranteed UI churn at 2 s).
- Comparing snapshots including `FetchedAt`.
- Capturing the password string anywhere longer-lived than a loop's client. The
  secret is read when `PollLoopAsync` constructs its client
  (`_clientFactory(config, _secrets.Get(config.Id))`); since every settings
  change rebuilds the loops, an updated password reaches the next loop
  automatically.
- `ConfigureAwait(false)` everywhere. The codebase omits it consistently; keep
  it omitted.
