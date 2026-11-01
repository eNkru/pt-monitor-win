# Design — deploy and verify the tray fix

Mostly operational: the code change already landed. The only real design question
is **how to prove the leak is gone without waiting for a crash**, and that is what
this document settles.

---

## 1. Which build to soak

| Option | Verdict |
|---|---|
| CI artifact for `e6064d6` (run `41290631815`) | has the fix, no log — cannot satisfy requirement 6 |
| Build from merged `main` after PR #10 lands | has the fix **and** the log — preferred |
| Local `scripts/build.ps1` on Windows | works, but the artifact must then be fingerprinted by hand; use only if CI is unavailable |

**Decision:** merge PR #10, then soak the `main` artifact. One soak then covers
the tray fix (criteria 3-5) and the log's real-session behaviour (criteria 6-7),
and the log is what makes the soak self-documenting.

Recorded provenance, both taken at soak time and written into the task's
`notes`:

```bash
gh run view <run-id> --json headSha --jq .headSha
sha256sum PTMonitor.exe
```

The SHA must contain `5a94cdd`; that check is the whole point — the previous six
crashes all came from binaries that did not.

## 2. Proving the leak is gone, not just "no crash yet"

Waiting 61 minutes only proves "did not crash *this* time". The leak is a native
handle leak, so measure the handles directly — that turns an hour-long binary
outcome into a trend visible in minutes.

The handle that matters is the **USER** handle for the `HICON` created by
`Win32Icon.CreateIcon`; the bitmap behind it also consumes a GDI handle. Both are
per-process counters readable from outside:

```powershell
Add-Type -Name U -Namespace W -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr h, uint f);
'@
$p = Get-Process PTMonitor
[pscustomobject]@{
  Gdi  = [W.U]::GetGuiResources($p.Handle, 0)   # GR_GDIOBJECTS
  User = [W.U]::GetGuiResources($p.Handle, 1)   # GR_USEROBJECTS  <- HICONs
  Handles = $p.Handles
}
```

Expected with the fix: `User` and `Gdi` are **flat** across the soak (a handful of
objects for the three cached icons and the windows). Expected without it: growth
proportional to status changes — ~1770 rebuilds in an hour, so a steady climb
into the thousands.

**Pass rule:** the last sample is not more than a small constant above the first
(say +20 for `User`), and there is no monotonic climb across the samples. A climb
is a finding even if the process has not crashed yet.

Samples: every 5 minutes for the soak duration, appended to a CSV in the task
directory (`soak-samples.csv`) so the trend survives the session.

## 3. Running the soak

The soak must not depend on this agent session staying alive, and must not be
disturbed by it.

- Launch the exe **detached** from the WSL shell, from a Windows path, with its
  working directory on Windows: `Start-Process -FilePath <exe>` (no `-Wait`).
- Verify the process exists and that its `Path` is the soak build — not the stale
  `Downloads` copy. `Get-Process PTMonitor | Select Path, StartTime`.
- Do **not** touch the tray menu, the panel or Preferences during the soak: a
  manual icon toggle would pollute the handle trend. The app polls on its own.
- The soak runs on the real `%APPDATA%\PTMonitor` data directory (2 servers,
  2 s interval), because that is the configuration that crashed. Back it up first
  (copy to `%APPDATA%\PTMonitor.bak-<date>`), so a bad soak cannot cost the user
  their settings or secrets.
- Nothing is killed at the end. The point is a long-running instance; the user
  keeps it, and it becomes the new normal build.

Note: the previous instance is already dead (crashed 23:36:24), so there is no
running process to replace — only a stale exe on disk.

## 4. Replacing the stale exes

Three locations matter and only one is the user's actual launch path:

| Path | Action |
|---|---|
| `C:\Users\enkru\Downloads\PTMonitor.exe` (`84749ea`, crashes) | **replace** with the soak build; this is what the user launches |
| `%LOCALAPPDATA%\PTMonitor\PTMonitor.exe` (`503be4d`, crashes) | replace too, or leave with a `.stale` suffix — either way it must not be launchable as-is |
| CI artifact copy in the task dir | keep as the fingerprint reference |

Rationale: the reason this bug looked "unfixed" for two days is that the fixed
build never reached the launch path. Overwriting `Downloads` is what actually
closes the loop. Old binaries are moved aside rather than deleted, so the
`sha256sum` in this task's notes still identifies what was replaced.

## 5. Evidence capture

All of it goes into `.trellis/tasks/11-02-tray-fix-deploy-verify/`:

| Artifact | Contents |
|---|---|
| `soak-samples.csv` | timestamp, uptime, GDI, USER, kernel handles |
| `soak-verdict.md` | build SHA + hash, soak window, handle trend, event-log query result, log excerpt, pass/fail per criterion |

The event-log query for the soak window, which must return nothing:

```powershell
Get-WinEvent -FilterHashtable @{
  LogName='Application'; StartTime=<soak start>; EndTime=<soak end>
} | Where-Object { $_.Id -in 1000,1026 -and $_.Message -match 'PTMonitor' }
```

The log check, which must return nothing:

```powershell
Select-String -Path "$env:APPDATA\PTMonitor\ptmonitor.log" -Pattern 'password=[^*]|SID=[^*]|Authorization'
```

## 6. Failure modes and what they mean

| Observation | Meaning | Action |
|---|---|---|
| No crash, flat handles | fix works | close, tick the tray task's last criterion |
| No crash, but handles climb | fix reduces churn but does not eliminate it (state oscillation still assigns) | new task: the PRD's own note predicted oscillation can still assign |
| Crash before 60 min, same stack | the fix is not in the running binary | re-check `Path` and the commit SHA — do not conclude the fix failed |
| Crash, different stack | new bug | new task with the new stack; the log now has the record |
| Log empty or missing | `Log.Initialize` did not run, or `%APPDATA%` is unwritable | investigate before ticking anything |

The distinction in row 3 is the entire lesson of this task: a crash is not
evidence about a fix unless the running binary contains it.
