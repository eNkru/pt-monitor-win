# Deploy the tray fix and prove the 59-minute crash is gone

## Goal

Stop the recurring `PTMonitor.exe` crash for real. The fix for it has been merged
since `e6064d6`, but every crash on this machine came from a binary built *before*
that merge — so the bug is fixed in source and still live in the build the user
runs. This task ships a build that contains the fix and verifies it on Windows.

It also closes the one acceptance criterion `11-01-tray-icon-crash` could not
close on a Linux box, and records the sixth crash that was found after that task
was written.

## Evidence gathered 2026-11-01 (this machine)

Six crashes, all the identical unhandled exception, all from the same binary:

```
Win32Exception (0x80004005): The operation completed successfully.
  at Avalonia.Win32.Interop.Win32Icon.CreateIcon(Bitmap bitmap, PixelPoint hotSpot)
  at Avalonia.Win32.Interop.Win32Icon..ctor(Win32Icon original, PixelSize size)
  at Avalonia.Win32.TrayIconImpl.UpdateIcon(Boolean remove)
  at PTMonitor.App.Services.AppServices.UpdateTray()
```

| Crash (local) | Uptime at death | Polls @2 s |
|---|---|---|
| 2026-10-30 15:44:49 | 1:28:53 | 2666 |
| 2026-10-30 20:17:04 | 1:31:29 | 2744 |
| 2026-11-01 10:34:08 | 0:58:57 | 1768 |
| 2026-11-01 16:54:08 | 0:59:14 | 1777 |
| 2026-11-01 18:59:07 | 0:58:45 | 1762 |
| 2026-11-01 23:36:24 | 0:58:50 | 1765 |

The last four died within **29 seconds of each other**. A race does not reproduce
that tightly; a fixed resource ceiling does. That is the fingerprint the fix must
erase.

Faulting binary, from the `Application Error` (1000) event:

| | |
|---|---|
| Path | `C:\Users\enkru\Downloads\PTMonitor.exe` |
| Product version | `1.0.0+84749ea…` (built 2026-10-31 22:08) |
| Contains `5a94cdd` (the fix) | **no** — merged 2026-11-01 20:13 |
| Contains the log sink | no |

Second exe on disk, `%LOCALAPPDATA%\PTMonitor\PTMonitor.exe`, is `1.0.0+503be4d…`
from 2026-10-27 — also pre-fix. No `ptmonitor.log` exists in
`%APPDATA%\PTMonitor\`, which is exactly what a pre-log build leaves behind.

Relevant config: `pollInterval=2`, 2 servers, floating window on — so
`MenuBarState` flips on most ticks, which is why the ceiling is reached in ~59 min.

## Requirements

1. `11-01-tray-icon-crash/prd.md` records crash #6 and the uptime table, so the
   ceiling fingerprint is preserved as evidence rather than living in a chat log.
2. A build containing `e6064d6` (cached icons + transition guard) is produced from
   a committed ref, not from a dirty working tree, and its provenance is recorded
   (commit SHA + `sha256sum`).
3. That build runs on Windows for **more than 60 minutes** — past the observed
   59-minute ceiling — with 2 enabled servers at the default 2 s interval.
4. During the soak, the process's native handle count is sampled and stays flat:
   no monotonic growth. This is the objective signal, and it is measurable in
   minutes rather than by waiting for a crash.
5. The soak produces **no** new `Application Error` (1000) / `.NET Runtime` (1026)
   event for `PTMonitor.exe`.
6. The soak produces a `ptmonitor.log` that starts with the startup line, holds no
   per-poll chatter, and contains no secret — which requires the log build
   (PR #10) to be in the same binary, so the log doubles as the soak's record.
7. The stale pre-fix exes are replaced or clearly marked so the crash cannot be
   "fixed" on paper while the old binary keeps being launched.

## Acceptance Criteria

- [ ] `11-01-tray-icon-crash/prd.md` contains the sixth crash and the uptime
      table above, with the crash count updated from five to six.
- [ ] A soak build is identified by commit SHA and `sha256sum`, and that SHA
      contains `5a94cdd`.
- [ ] Soak duration > 60 min is recorded with start/end timestamps.
- [ ] Handle samples are recorded at intervals; the last sample is not
      meaningfully above the first (the fix means no per-tick native allocation).
- [ ] `Get-WinEvent` over the soak window shows zero 1000/1026 events for
      `PTMonitor.exe`.
- [ ] `%APPDATA%\PTMonitor\ptmonitor.log` exists, has a `startup` line, and
      `Select-String -Pattern 'password=|SID='` finds nothing.
- [ ] `%APPDATA%\PTMonitor\ptmonitor.log` line count is small (tens, not
      thousands) — proving the transition-only rule holds in a real session.
- [ ] `11-01-tray-icon-crash`'s last open criterion can then be ticked and the
      task archived.

## Non-goals

- Changing the tray implementation again. The fix is merged; this task deploys and
  verifies it.
- Adding a handle-leak watchdog to the app. If the soak shows growth, that is a
  new finding for a new task, not scope creep here.
- Code signing, or a new release cut. A CI artifact is enough to soak.
- Re-running the credential/TLS verification from `11-01-credential-tls-hardening`.

## Ordering

Merging PR #10 first is preferred: the soak build then carries both the tray fix
and the log, so the soak verifies two things at once and leaves its own record.
If #10 is not ready, soak the `e6064d6` artifact and verify criteria 3-5 only;
criteria 6-7 then need a second, shorter confirmation run after #10 lands.

## Notes

- CI artifact for `e6064d6` exists and is unexpired: run `41290631815`, artifact
  `PTMonitor-win-x64`.
- The machine is WSL2 with Windows interop, so the Windows side is reachable from
  the dev shell (`powershell.exe`, `Get-WinEvent`, `Get-Process`). This is how the
  crash evidence above was collected — the same route verifies the soak.
- `%APPDATA%` resolves to `C:\Users\enkru\AppData\Roaming`, so the data directory
  is `C:\Users\enkru\AppData\Roaming\PTMonitor`.
