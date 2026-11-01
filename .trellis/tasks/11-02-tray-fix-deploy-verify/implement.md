# Implementation plan — deploy and verify the tray fix

Ordered: record the evidence, ship the build, measure, then close the loop.
The soak is the long pole, so it starts as early as the build allows and is
measured while it runs.

## Validation commands

Windows side, from this WSL2 shell (interop is available):

```bash
cd /tmp && powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script>.ps1
```

`-ExecutionPolicy Bypass` is required: scripts under `\\wsl.localhost\...` are
unsigned and blocked by default. Code gates are not needed here — no C# changes —
but `dotnet build`/`--selftest`/`dotnet test` still run once at the end to prove
the tree the build came from is green.

## Checklist

### Evidence (requirement 1)

- [ ] **1. Record crash #6 in `11-01-tray-icon-crash/prd.md`**
  - Update "five identical crashes" to six, add the 2026-11-01 23:36:24 entry.
  - Add the uptime table (all six, uptime + implied poll count) and the
    observation that the last four died within 29 seconds of each other.
  - Add the faulting-binary row: `1.0.0+84749ea…`, predates `5a94cdd`.
  - Keep the task's own acceptance criterion unticked — this task closes it.

### Build (requirement 2)

- [ ] **2. Get a build that contains `5a94cdd`**
  - Preferred: PR #10 merged, then `gh run list --branch main --limit 1` and
    download `PTMonitor-win-x64` from that run.
  - Fallback: run `41290631815` (sha `e6064d6`) — has the fix, no log; criteria
    6-7 then need a second run after #10 lands.
  - Record `headSha` and `sha256sum PTMonitor.exe` in `task.json` `notes`.
  - Verify `git merge-base --is-ancestor 5a94cdd <sha>`.

### Deploy (requirement 7)

- [ ] **3. Back up the data directory, then replace the stale exes**
  - `Copy-Item $env:APPDATA\PTMonitor $env:APPDATA\PTMonitor.bak-<yyyyMMdd-HHmm>`.
  - Move `C:\Users\enkru\Downloads\PTMonitor.exe` → `PTMonitor.exe.pre-fix-84749ea`.
  - Same for `%LOCALAPPDATA%\PTMonitor\PTMonitor.exe` (`…pre-fix-503be4d`).
  - Put the soak build at the `Downloads` path (the user's launch path) and keep
    the artifact copy in the task dir as the fingerprint reference.

### Soak (requirements 3-6)

- [ ] **4. Start the soak**
  - `Start-Process` detached from the `Downloads` path, no `-Wait`.
  - Confirm `Get-Process PTMonitor | Select Path,StartTime` points at the soak
    build, not a stale copy.
  - Record start timestamp (UTC and local).
- [ ] **5. Sample handles every 5 minutes** (`design.md` §2)
  - `GetGuiResources` GR_GDIOBJECTS + GR_USEROBJECTS, plus `Handles`, appended to
    `soak-samples.csv` with uptime.
  - First sample at t≈0 (after the tray icon first renders), then every 5 min.
  - Do not touch the tray, panel or Preferences during the soak.
- [ ] **6. Run past the ceiling**
  - Minimum 60 minutes; the observed ceiling is 58:45-59:14, so 65+ minutes is
    the target. Soak end timestamp recorded.
- [ ] **7. Query the event log for the soak window** (`design.md` §5)
  - Zero 1000/1026 events for `PTMonitor.exe` in `[start, end]`.
- [ ] **8. Inspect `ptmonitor.log`** (only if the build has the log sink)
  - Exists, first line is `startup`, `Select-String` for `password=`/`SID=`/
    `Authorization` finds nothing, and the line count is tens not thousands.

### Close out

- [ ] **9. Write `soak-verdict.md`** with the pass/fail per criterion, the handle
      trend, the event-log result, and the log excerpt.
- [ ] **10. Tick `11-01-tray-icon-crash`'s last criterion and archive it** — only
      if the soak passed. If handles climbed without a crash, do **not** tick it;
      open a follow-up for state-oscillation churn instead (`design.md` §6).
- [ ] **11. Final gates** on the tree the build came from: `dotnet build -c
      Release`, `--selftest`, `dotnet test`.

## Review gates

- After step 2: confirm the SHA contains `5a94cdd` **before** spending an hour.
  Getting this wrong is how the bug appeared unfixed for two days.
- After step 5's third sample: if the handle trend is already climbing, stop —
  the soak has answered the question and 60 minutes of waiting adds nothing.
- Before step 10: re-read `design.md` §6 so a different-stack crash is not
  mistaken for "the fix failed".

## Rollback points

- Steps 1, 9-10: documentation only.
- Step 3: move the `.pre-fix-*` binaries back; the `%APPDATA%\PTMonitor.bak-*`
  copy restores settings and secrets.
- Steps 4-8: quitting the soak process is the whole rollback — nothing was
  modified outside `%APPDATA%\PTMonitor` (log file, appended to by design).
