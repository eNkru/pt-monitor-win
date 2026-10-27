# PTMonitor (Windows)

[![Build & Package](https://github.com/eNkru/pt-monitor-win/actions/workflows/build.yml/badge.svg)](https://github.com/eNkru/pt-monitor-win/actions/workflows/build.yml)

A Windows **system tray app** for watching private tracker (PT) torrent clients:
**qBittorrent** and **Transmission**. It lives in the notification area, polls
your servers on a fixed interval, and shows a dropdown panel with one section
per server: per-torrent name, progress, speeds, and ETA.

This is the Windows port of the macOS menu bar app `pt-monitor` — same
architecture, same behaviours, same built-in check suite, written in
C# / .NET 8 / Avalonia instead of Swift / SwiftUI / AppKit.

- **Read-only by design.** The app only ever polls. It never adds, removes,
  pauses, or edits anything on your server.
- **No telemetry, no accounts, no network calls except to your own servers.**

---

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Quick start (no build needed)](#quick-start-no-build-needed)
- [Using the app](#using-the-app)
- [Building from source](#building-from-source)
- [Command line (no GUI)](#command-line-no-gui)
- [Data, settings and secrets](#data-settings-and-secrets)
- [Architecture](#architecture)
- [Project layout](#project-layout)
- [Behaviour reference](#behaviour-reference)
- [Testing](#testing)
- [Continuous integration & releases](#continuous-integration--releases)
- [Troubleshooting](#troubleshooting)
- [Known limitations & roadmap](#known-limitations--roadmap)
- [Porting notes (macOS → Windows)](#porting-notes-macos--windows)
- [Contributing](#contributing)
- [License](#license)

---

## Features

- **Tray agent** — no window at launch. The tray icon is the only persistent UI:
  - grey circle = idle,
  - green circle = actively transferring,
  - red warning triangle = every enabled server is failing.
  - The tooltip carries live `↓ / ↑` speeds (toggleable).
- **Multiple servers** — any number of qBittorrent WebUI or Transmission RPC
  endpoints, mixed freely. Each gets its own section in the dropdown panel.
- **Per-torrent rows** — name, full-width progress bar, percentage, speeds + ETA,
  and the PT site label. Fixed sort order (downloading → seeding → rest, each by
  down speed, then name); there is deliberately no sort UI.
- **Expandable rows** — click a torrent to expand an inset detail block with
  size, share ratio, seeds/leechers, tracker status, category, and the client's
  own error text. Only fields the server actually reported are shown — never a
  fabricated zero or dash.
- **List filter** — All / ↓ Down / ↑ Up / Active, driven by *live traffic*
  rather than state names (a stalled torrent that still trickles counts as
  downloading). Display-only: switching never rebuilds a poller.
- **Open the web UI from a row** — the trailing `↗` opens *that row's server's*
  own web UI in the default browser, for the things the panel does not do
  (rename, files, peers, categories). No deep link exists in either client, and
  no request leaves the app — your browser brings its own session.
- **Resilient polling** — one independent loop per enabled server, so a slow or
  hanging server cannot delay the others.
  - transient failures (DNS, refused, timeout) retry at the normal interval;
  - **credential failures back off for 10 minutes**;
  - **bans back off for 15 minutes**.
  
  This matters: qBittorrent bans an IP after 5 failed logins and does not
  time-limit the counter, so a 2-second poll timer turns one typo into a
  one-hour ban. Backoff never shortens a longer user-configured interval.
- **Passwords never in the settings file** — they live in `secrets.dat`,
  encrypted per Windows user with **DPAPI** (`CurrentUser` scope).
- **HTTPS + self-signed certificates** — an opt-in "allow self-signed
  certificate" toggle per server.
- **Bilingual** — English and Simplified Chinese, switchable in Preferences and
  applied immediately (no restart, unlike the macOS original).
- **Self-contained build** — a single `PTMonitor.exe`, published for both
  **x64 and ARM64** Windows; no .NET runtime install required on the target
  machine.

---

## Requirements

| | |
|---|---|
| **To run** | Windows 10 (1607+) or Windows 11, x64 or ARM64. Nothing else — the published build is self-contained. |
| **To build** | [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.x is what CI uses). |
| **To build** (optional) | Node.js 18+ — only for the stub test server. |
| **IDE** (optional) | Visual Studio 2022 (17.8+) or JetBrains Rider. |

The app targets `net8.0` and uses **Avalonia 11.3.2** for the UI. It builds and
runs on Windows, and also builds/runs on Linux and macOS for development
(Avalonia is cross-platform), though only Windows is a supported target.

---

## Quick start (no build needed)

1. Download `PTMonitor.exe` from the
   [Releases](https://github.com/eNkru/pt-monitor-win/releases) page — take the
   `win-x64` zip, or the `win-arm64` zip on an ARM64 Windows machine (or the
   `PTMonitor-win-x64` / `PTMonitor-win-arm64` artifacts of the latest CI run).
2. Put it anywhere you like — a conventional spot for a per-user single-file
   app is `%LOCALAPPDATA%\PTMonitor` (no admin rights needed), e.g.
   `C:\Tools\PTMonitor\`.
3. Run it. **Preferences open automatically on first launch.**
4. Add a server, hit **Test Connection**, then **Save**.
5. Close Preferences — the app is now in your tray, polling.

> **Tip:** because it is a single-file app, it self-extracts to a temp folder on
> first run. Running it from a local disk (`C:\…`) is noticeably faster than
> from a network share or a `\\wsl$` path.

There is no installer and nothing is written outside `%APPDATA%\PTMonitor\`.
To uninstall: quit from the tray menu and delete the exe plus that folder.

---

## Using the app

### Tray icon

| Action | Result |
|---|---|
| **Left click** | Toggle the dropdown panel |
| **Right click** | Native menu: Refresh Now / Preferences… / Quit PT Monitor |
| **Hover** | Tooltip with server count and live speeds |

### The dropdown panel

```
┌─────────────────────────────────────────────┐
│ PT Monitor                       2 servers  │
│ just now                          · 1 online│
├─────────────────────────────────────────────┤
│ ↓ SPEED                ↑ SPEED              │
│ ↓ 1.2M                 ↑ 340K               │
│ ACTIVE TORRENTS        FREE SPACE           │
│ 3 · 7 total            412 GB               │
├─────────────────────────────────────────────┤
│ [ All ][ ↓ Down ][ ↑ Up ][ Active ]         │
├─────────────────────────────────────────────┤
│ NAS                          ↓ 1.2M ↑ 340K  │
│  ● Ubuntu 24.04 ISO              62%     ↗  │
│    ▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░               │
│    ↓ 1.2M · 30m              hdchina.org    │
│                                             │
│  ● Seeding release              100%     ↗  │
│    ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓               │
│    ↑ 340K                    hdchina.org    │
├─────────────────────────────────────────────┤
│ ⟳  Refresh Now                              │
│ ⚙  Preferences…                             │
│ ⏻  Quit PT Monitor                          │
└─────────────────────────────────────────────┘
```

- **Header** — relative freshness ("just now", "42s ago") and
  `N servers · M online`.
- **Aggregate grid** — summed ↓/↑ speeds, active vs total torrents, and free
  space (the *most constrained* reporting server, since free space is not
  meaningful to sum).
- **Section header** — server name, its live speeds, and the PT site when every
  visible row in that section shares one (so a single-site box does not repeat
  `hdchina.org` a hundred times).
- **Rows** — capped at **100 visible rows per server**; the rest collapses into
  a "+ N more torrents" footnote. Clients already sorted the list, so the cap
  only ever drops the long idle tail of a very large library.
- **Dismissal** — `Esc`, or clicking anywhere outside the panel. Closing it does
  **not** quit the app.

### Preferences

| Setting | Notes |
|---|---|
| **Servers** list | Enable/disable, Edit, Delete per row |
| **Add Server…** | Opens the server editor |
| **Poll every N seconds** | 1–60 s, default 2 s. Global; one loop per server. |
| **Show speeds in the tray tooltip** | Icon state is always shown regardless |
| **Torrents shown in the panel** | All / ↓ Down / ↑ Up / Active (also settable from the panel) |
| **Language** | Follow System / English / 简体中文 — applies immediately |

### Server editor

| Field | Notes |
|---|---|
| **Type** | qBittorrent or Transmission |
| **Name** | Display name; defaults to the type name |
| **Host** | Bare host, IP, IPv6, `host:port`, or a pasted `http(s)://…` URL |
| **Port** | Defaults to 8080 (qBittorrent) / 9091 (Transmission) |
| **Use HTTPS** | Also inferred from an `https://` host field |
| **Allow self-signed certificate** | Opt-in per server |
| **Enabled** | Disabled servers are never contacted |
| **User name / Password** | Password is written to DPAPI storage, never to `settings.json` |
| **Test Connection** | Validates end-to-end and reports the remote version, e.g. `Connected: qBittorrent v5.2.0 (WebAPI 2.15.1)` |

The host field is deliberately forgiving: `nas.local`, `192.168.1.2:8080`,
`[2001:db8::1]:9091`, `https://nas.local:8443/` all parse. Non-HTTP schemes
(`ws://`) are rejected with a readable message. If you type a port in the host
field *and* in the port field, the host field wins and the app tells you so.

---

## Building from source

### Option 1 — the build script (Windows, recommended)

```powershell
git clone https://github.com/eNkru/pt-monitor-win.git
cd pt-monitor-win
.\scripts\build.ps1
```

`scripts\build.ps1` runs the whole pipeline and fails loudly at the first
problem:

1. `dotnet restore`
2. `dotnet build -c Release`
3. `dotnet run --project src/PTMonitor.Cli -c Release -- --selftest` (204 checks)
4. `dotnet test tests/PTMonitor.Core.Tests` (36 tests)
5. `dotnet publish src/PTMonitor.App -r win-x64 --self-contained -p:PublishSingleFile=true`
6. Re-runs `--selftest` **on the packaged CLI exe**, not just the build output
7. Zips everything into `artifacts\PTMonitor-<version>-win-x64.zip`

Outputs:

```
publish\win-x64\PTMonitor.exe          ~90 MB, self-contained, single file
publish\win-x64\cli\PTMonitor.Cli.exe  headless selftest / probe
artifacts\PTMonitor-1.0.0-win-x64.zip
```

Options:

```powershell
.\scripts\build.ps1 -Version 1.2.3        # stamp a version
.\scripts\build.ps1 -Rid win-arm64        # build the ARM64 exe instead
.\scripts\build.ps1 -SkipZip              # skip the zip step
.\scripts\build.ps1 -SkipGates            # publish only (CI reuse)
```

The packaged-CLI selftest step runs only when the published exe matches the
machine running the script (an ARM64 exe cannot run on an x64 host); the
pre-publish `--selftest` gate is architecture-independent and always runs.

### Option 2 — plain `dotnet` commands

```powershell
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -- --selftest
dotnet test
dotnet run --project src/PTMonitor.App          # run from source
```

To produce the distributable exe by hand:

```powershell
dotnet publish src/PTMonitor.App -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=1.0.0 `
    -o publish/win-x64
```

> `IncludeNativeLibrariesForSelfExtract` is required: Avalonia ships native
> Skia/HarfBuzz binaries, and without it they are left beside the exe instead of
> being bundled.

### Option 3 — an IDE

Open `PTMonitor.sln` in Visual Studio 2022 or Rider, set `PTMonitor.App` as the
startup project, and press <kbd>F5</kbd>.

### Option 4 — building from WSL / Linux / macOS

Cross-targeting Windows works, because the publish is fully self-contained:

```bash
# one-time: get the SDK if you don't have it
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
export PATH="$HOME/.dotnet:$PATH"

cd pt-monitor-win
dotnet publish src/PTMonitor.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/win-x64
```

The resulting `publish/win-x64/PTMonitor.exe` runs on Windows. To *run* the GUI
on Linux for development you additionally need the usual Avalonia natives
(`libfontconfig`, `libfreetype`, `libice`, X11/Wayland libs, and at least one
installed font).

**Launching the Windows exe from WSL** (interop) works, but copy the exe to the
Windows filesystem first and start it detached — a process that inherits WSL's
stdout/stderr pipes dies when the launching command exits:

```bash
powershell.exe -Command "Start-Process -FilePath
  'C:\Users\you\AppData\Local\PTMonitor\PTMonitor.exe'
  -WorkingDirectory 'C:\Users\you\AppData\Local\PTMonitor'"
```

The tray icon then lives on the Windows desktop, and settings/secrets are written
under the Windows `%APPDATA%\PTMonitor\`.

---

## Command line (no GUI)

`PTMonitor.Cli.exe` is the headless entry point — the same binary the build
script and CI use as the gate. It never touches the UI.

### `--selftest`

Runs the built-in check suite and exits `0` (pass) or `1` (fail).

```powershell
PTMonitor.Cli.exe --selftest
# → selftest: OK (204 checks passed)
```

On failure it prints every failing check, e.g.:

```
selftest: FAILED — qb eta: valid: expected 3600, got nil | …
```

### `--probe`

Fetches one snapshot from one server and prints it as JSON.

```powershell
PTMonitor.Cli.exe --probe qbittorrent --host 192.168.1.2 --port 8080 --user admin --pass secret
PTMonitor.Cli.exe --probe transmission --host nas.local --port 9091 --user admin --pass secret
```

Exit codes: `0` success, `1` server failure, `2` usage error.

| Flag | Meaning | Default |
|---|---|---|
| `--probe <type>` | `qbittorrent` or `transmission` | required |
| `--host <host>` | host name, IP, or full URL | required |
| `--port <port>` | server port | 8080 (qb) / 9091 (tr) |
| `--user <username>` | user name | — |
| `--pass <password>` | password | — |
| `--https` | use HTTPS | off |
| `--insecure` | accept a self-signed certificate | off |

Example output (trimmed):

```json
{
  "server": { "kind": "qbittorrent", "name": "qBittorrent", "host": "192.168.1.2", "port": 8080, "tls": false },
  "snapshot": {
    "downSpeed": 1258291,
    "upSpeed": 348160,
    "activeTorrents": 3,
    "totalTorrents": 7,
    "freeSpace": 442918502400,
    "connectionState": "connected",
    "fetchedAt": "2026-10-26T02:05:28.9277292+00:00",
    "torrents": [
      { "id": "aaaa…", "name": "Ubuntu ISO", "state": "downloading", "progress": 0.62,
        "downSpeed": 1258291, "upSpeed": 5120, "etaSeconds": 1800 }
    ]
  }
}
```

Failures print a cause-specific label to stderr:

```
probe: auth failed: invalid username or password
probe: banned: the WebUI has banned this PC's IP after too many failed logins — …
probe: unreachable: connection refused
```

### Verifying the client without a NAS

`scripts/fake-qbittorrent.mjs` is a stub WebUI that mirrors qBittorrent's real
auth behaviour, so transport bugs look like what they are instead of like "the
server banned this IP":

- a private API call **without a session cookie** → `403` (the same status the
  ban uses — this is what makes the distinction matter),
- 5 failed logins → IP ban for the rest of the process,
- `WEBAPI=5.2` switches to the 5.2 login shapes (`204` success / `401` wrong
  credentials) instead of 4.6's (`200 "Ok."` / `200 "Fails."`).

```powershell
node scripts\fake-qbittorrent.mjs 18080
PTMonitor.Cli.exe --probe qbittorrent --host 127.0.0.1 --port 18080 --user admin --pass adminadmin
# → snapshot JSON, exit 0

# the 5.2 shapes — the outcome must be identical
$env:WEBAPI = "5.2"; node scripts\fake-qbittorrent.mjs 18081
PTMonitor.Cli.exe --probe qbittorrent --host 127.0.0.1 --port 18081 --user admin --pass adminadmin
```

Wrong credentials must report **`auth failed`**, not `banned`, for the first
five attempts — and only then `banned`.

The stub's credentials are `admin` / `adminadmin` (qBittorrent's documented
defaults). They are test fixtures, not secrets.

---

## Data, settings and secrets

Everything lives under `%APPDATA%\PTMonitor\`:

| File | Contents | Protection |
|---|---|---|
| `settings.json` | servers (id, kind, name, host, port, TLS flags, username, enabled), poll interval, tray toggle, list mode, language | plain text — **never contains passwords** |
| `secrets.dat` | password per server id | DPAPI, `DataProtectionScope.CurrentUser` |

- The settings file is written atomically (temp file + move), so a crash cannot
  truncate it. A corrupt or older payload is treated as "not configured" rather
  than crashing.
- `secrets.dat` is only readable by the Windows account that wrote it. Copying
  it to another machine or user yields unreadable values.
- On non-Windows development builds the secret store falls back to base64 with a
  `plain:` marker, so `dotnet run` on Linux/macOS still works for UI work. This
  path is never used by the Windows build.
- When you save a server, the **secret is written before the configuration**.
  A failed DPAPI write therefore cannot leave an enabled server polling with an
  empty password — which is exactly how you trip qBittorrent's IP ban.

To wipe everything: quit the app and delete `%APPDATA%\PTMonitor\`.

---

## Architecture

Three projects, strictly layered — the same split as the macOS original.

```
┌──────────────────────────┐   ┌──────────────────────────┐
│      PTMonitor.App       │   │      PTMonitor.Cli       │
│  Avalonia tray agent     │   │  --selftest / --probe    │
│  views, view-models,     │   │  (headless, no UI refs)  │
│  tray icon, L10n         │   │                          │
└────────────┬─────────────┘   └────────────┬─────────────┘
             │                              │
             └──────────┬───────────────────┘
                        ▼
        ┌───────────────────────────────────┐
        │         PTMonitor.Core            │
        │  Model · Client · Store ·         │
        │  Format · Diagnostics             │
        │  (no UI references at all)        │
        └───────────────────────────────────┘
```

**`PTMonitor.Core`** owns everything reusable and testable:

| Folder | Responsibility |
|---|---|
| `Model/` | `ServerConfig`, `ServerStatus`, `ServerFailure`, `TorrentSummary`, `TransferSnapshot`, `TorrentListMode`, `AppLanguage`, `MenuBarState` |
| `Client/` | `ITorrentClient`; `QBittorrentClient`, `TransmissionClient`, `HttpTransport`, `HostFieldParser`, `ServerConfigValidator`, pure parsers |
| `Store/` | `SettingsStore` (JSON), `DpapiSecretStore` / `InMemorySecretStore`, `MonitorStore` (polling loops, aggregation) |
| `Format/` | `ByteRateFormatter` (rates, byte counts, tray text ladder) |
| `Diagnostics/` | `SelfTest` (the 204-check suite), `ProbeRunner` |

**Key design rules** (kept from the macOS original, and enforced by the check
suite):

1. **Both APIs normalise into one domain model.** Nothing above the client layer
   branches on `ServerKind`. The UI never re-reads raw JSON.
2. **Parse each payload exactly once**, in a pure parser
   (`QBittorrentParser` / `TransmissionParser`), so the normalisation tables have
   a single owner.
3. **All presentation *decisions* are pure and string-free** — which row
   segments to show (`TorrentSummary.InfoSegments`), which detail lines exist
   (`ExpandedLines`), the sort (`Sorted`), the site label
   (`SiteFromAnnounceUrl`). The app target turns those decisions into localized
   text, so `--selftest` can assert the decision without depending on a
   translatable string.
4. **`nil` means "the server did not report it"** — never zero. A missing ratio
   or free space renders as nothing / an em dash, not as a fake `0`.
5. **Read-only.** No mutating RPC ever leaves the app.

### Polling model

`MonitorStore` runs **one independent async loop per enabled server**:

```
loop(server):
    client = factory(server, secret(server.id))   # built once, keeps auth state
    forever:
        interval = settings.pollInterval
        try:
            publish(online(client.fetchSnapshot()))
        catch auth   → publish(failed); interval = max(interval, 600s)
        catch banned → publish(failed); interval = max(interval, 900s)
        catch other  → publish(failed); interval = interval
        sleep(interval)
```

- Results are compared before publishing (`IsEquivalent`, which **ignores**
  `fetchedAt`) so an unchanged poll does not churn the UI.
- `Reconcile()` cancels and rebuilds every loop after any configuration change.
  It deliberately rebuilds even when only the password changed, because a
  password edit is invisible in `ServerConfig`.
- Disabled servers are never contacted — the client factory is not even invoked.

---

## Project layout

```
PTMonitor.sln
Directory.Build.props            shared MSBuild settings (nullable, LangVersion)
src/
  PTMonitor.App/                 Avalonia tray app (the GUI)
    Program.cs                   entry point + AppBuilder
    App.axaml / App.axaml.cs     styles, ShutdownMode=OnExplicitShutdown, bootstrap
    app.manifest                 PerMonitorV2 DPI awareness
    app.ico                      exe icon, generated from the macOS AppIcon.icns
    Services/
      AppServices.cs             composition root: stores, tray icon, windows
      L10n.cs                    English + zh-Hans string tables
      TrayIconFactory.cs         tray icons drawn at runtime (no binary assets)
    ViewModels/
      PanelViewModel.cs          sections, rows, expansion, aggregate, filters
    Views/
      MainPanelWindow.axaml(.cs)     the dropdown panel
      PreferencesWindow.axaml(.cs)   server list + preferences + language
      ServerEditorWindow.axaml(.cs)  add/edit + Test Connection
  PTMonitor.Core/                all reusable logic (no UI references)
    Model/                       domain types
    Client/                      API clients, transport, parsers, validation
    Store/                       settings, secrets, polling monitor
    Format/                      ByteRateFormatter
    Diagnostics/                 SelfTest, ProbeRunner
  PTMonitor.Cli/                 headless --selftest / --probe entry point
tests/
  PTMonitor.Core.Tests/          xUnit suite (36 tests, mirrors the selftest)
scripts/
  build.ps1                      restore → build → selftest → test → publish → zip
  icns-to-ico.py                regenerate app.ico from the macOS AppIcon.icns
  fake-qbittorrent.mjs           stub qBittorrent WebUI (4.6 and 5.2 shapes)
.github/workflows/build.yml      windows-latest CI: main → artifacts, tag → release
```

---

## Behaviour reference

Exact values, so you do not have to read the source:

| Thing | Value |
|---|---|
| Default poll interval | 2 s |
| Poll interval range | 1–60 s (clamped, never rejected) |
| Credential-failure backoff | 600 s (10 min) |
| Ban backoff | 900 s (15 min) |
| Visible torrent rows per server | 100 (rest → "+ N more torrents") |
| Panel width | 400 px; height content-driven, list capped at 420 px |
| Panel position | bottom-right of the primary screen's work area |
| Default ports | qBittorrent 8080, Transmission 9091 |
| HTTP timeouts | 15 s per request |
| Transmission `free-space` refresh | every 30 s, independent of the speed poll |
| Torrent list sort | downloading → seeding → rest; then down speed ↓; then name |
| Progress clamp | always 0…1, malformed → 0 |
| qBittorrent "infinite" ETA | `8640000` → no ETA shown |
| Transmission ETA sentinels | `-1`, `-2` → no ETA shown |
| Ratio sentinel (`-1`) | treated as "not reported" |

### API notes worth knowing

**qBittorrent** (`/api/v2`)

- Login is form-encoded `username=&password=` with **no `Origin`/`Referer`**
  (CSRF is on by default and only skipped when both are absent).
- The login verdict **moved from the body to the status code in 5.2**:

  | server | success | wrong credentials | banned IP |
  |---|---|---|---|
  | ≤ 5.1 | `200 "Ok."` | `200 "Fails."` | `403` |
  | 5.2+ | `204`, no body | `401` | `403` |

  All shapes are accepted. `"Ok."` is not proof of credentials (the
  localhost/subnet bypass also returns it).
- A `403` **on `auth/login`** is the IP ban. A `403` on any **other** endpoint
  means "no usable session cookie" — *not* a ban. Conflating them once reported
  a broken cookie as "the server banned this IP".
- A `401` or `403` on a normal GET triggers exactly one re-login and one retry,
  then the failure propagates — never a login loop.
- `free_space_on_disk` is only read when the cached WebAPI version is ≥ 2.1.1.
- Polls `GET /sync/maindata?rid=0` (full snapshot every time, no deltas).
- The top-level `trackers` map is **announce URL → [hash]** (the inverse of what
  it looks like). It is only a fallback for a torrent with no `tracker` field.

**Transmission** (`/transmission/rpc`)

- Every request runs the `X-Transmission-Session-Id` `409` handshake: on a 409
  the id is taken from the response header and the *same* request is resent
  exactly once. A second 409 never recurses.
- HTTP `200` with `result != "success"` is still a failure — the `result`
  string, not the status code, decides.
- Basic auth is set proactively as an `Authorization` header, because
  URLSession-equivalent challenge callbacks are unreliable on some platforms.
- `free-space` does a synchronous `statvfs` that can block on a hung NFS/SMB
  mount, so it runs on its own slow cadence and its failures are swallowed.
- `error != 0` overrides the status mapping → the row shows `.error` with the
  client's own `errorString`.

---

## Testing

Two gates, both run by `build.ps1` and by CI.

### 1. In-binary check suite — `--selftest`

204 assertions, no network, no fixtures on disk. It covers:

- host-field parsing (`host:port`, IPv6 literals, pasted URLs, scheme
  rejection) and config validation/normalisation;
- both API parsers against recorded wire payloads, including qBittorrent's
  announce-URL→hash tracker map and Transmission's 409 handshake;
- the state mapping tables for both clients, ETA/ratio sentinels, progress
  clamping;
- the fixed sort, row-segment decisions, expanded-detail lines, PT site
  extraction and hoisting;
- byte-rate formatting (including printf-style half-to-even rounding);
- settings round-trip, migration, clamping; backoff ladder; aggregate and
  menu-state mapping; disabled servers never being contacted.

Because it is a pure-logic suite, it runs anywhere — that is why it exists
instead of relying only on unit tests.

### 2. xUnit suite

36 tests in `tests/PTMonitor.Core.Tests`, including a test that runs the whole
`SelfTest` suite, so the two can never drift apart.

```powershell
dotnet test
dotnet test --filter "FullyQualifiedName~QBittorrentParserTests"
```

### 3. End-to-end against a stub server

See [Verifying the client without a NAS](#verifying-the-client-without-a-nas).
This is the only way to exercise cookies, redirects, status-code branches, and
the ban logic without a real NAS.

---

## Continuous integration & releases

`.github/workflows/build.yml` runs on `windows-latest` and builds both
architectures on the same x64 runner (ARM64 is cross-compiled):

```
checkout → setup .NET 8 → resolve version → build → selftest → test
        → package x64 → package ARM64 → upload artifacts → (tag only) Release
```

- **Every merge to `main`** runs the gates and publishes both the
  `PTMonitor-win-x64` and `PTMonitor-win-arm64` workflow artifacts.
- **Every push of a tag `v*`** additionally creates a GitHub Release with
  **both zips** attached. The tag (minus the leading `v`) becomes the app
  version.
- **Manual runs** (`workflow_dispatch`, or
  `gh workflow run build.yml --ref main`) produce the artifacts only.
- Pull requests do **not** build.

To cut a release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

> Note on the published exe name: `AssemblyName` is set to `PTMonitor` in
> `PTMonitor.App.csproj`. Do **not** pass `-p:AssemblyName=PTMonitor` on the
> command line — MSBuild applies it to every project in the graph, so
> `PTMonitor.Core` and the app both become "PTMonitor" and NuGet fails with
> *Ambiguous project name*.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| **"Unreachable: connection refused"** | Wrong port, or the WebUI/RPC is not enabled on the server. Check qBittorrent → Options → Web UI, or Transmission → `rpc-enabled`. |
| **"Unreachable: host not found"** | DNS. Try the IP address. |
| **"Unreachable: timed out"** | Firewall, or the server is on a different VLAN. Windows Defender Firewall prompts on first outbound connection — allow it. |
| **"Unreachable: TLS handshake failed"** | Self-signed certificate. Enable **Allow self-signed certificate** for that server. |
| **"Sign-in failed"** | Wrong username/password. Note the app then waits 10 minutes before retrying, by design. Use **Test Connection** in the editor (or **Refresh Now**) to retry immediately. |
| **"Temporarily banned"** | The server banned your IP. qBittorrent's default is 3600 s. Wait it out or restart qBittorrent; the app backs off for 15 minutes so it will not make it worse. |
| **"Unexpected response"** with HTTP 403 on a non-login endpoint | The session cookie was not accepted. Usually a host-header mismatch — connect by IP, or check `WebUI/HostHeaderValidation`. |
| **Free space shows "—"** | qBittorrent older than WebAPI 2.1.1, or the Transmission `free-space` call failed. Not an error. |
| **Panel appears in an odd place** | It anchors to the bottom-right of the *primary* screen's work area. Secondary-taskbar placement is not detected (see limitations). |
| **No tray icon** | Windows hides new tray icons by default — expand the overflow chevron and drag it out. |
| **App starts twice** | There is no single-instance guard; two copies poll independently. Quit one from its tray menu. |
| **Antivirus / SmartScreen warning** | The exe is unsigned. "More info" → "Run anyway", or build it yourself. |

Logs: the app writes nothing by default. For diagnosis use the CLI probe, which
prints the exact failure:

```powershell
PTMonitor.Cli.exe --probe qbittorrent --host 192.168.1.2 --port 8080 --user admin --pass secret
```

---

## Known limitations & roadmap

Deliberate scope decisions for the first release:

- **No single-instance guard** — launching twice starts two independent agents.
- **No "start with Windows" toggle.** Workaround: put a shortcut in
  `shell:startup`.
- **Tray panel position is approximated** at the bottom-right of the primary
  screen, because Avalonia does not expose the tray icon's screen coordinates.
  A secondary monitor with its own taskbar is not handled.
- **No notifications / no sound** — the app is observational.
- **No torrent actions** (add, pause, remove, set category) and no per-torrent
  deep link — neither upstream web UI supports one. Use the `↗` button.
- **The exe is unsigned**, so SmartScreen will warn on first run. Signing needs
  a code-signing certificate.

Plausible next steps, in rough order of value: single-instance guard, start with
Windows, per-server free-space display, a code-signed release.

---

## Porting notes (macOS → Windows)

For anyone comparing the two codebases:

| macOS original | This port |
|---|---|
| `NSStatusItem` + template SF Symbol | Avalonia `TrayIcon` + icons rendered at runtime |
| `AppIcon.icns` (bundle icon) | `app.ico` via `ApplicationIcon`, regenerated from the same `.icns` by `scripts/icns-to-ico.py` |
| `NSPanel` (borderless, non-activating) | Borderless `Window` (`SystemDecorations=None`) anchored bottom-right |
| SwiftUI views | Avalonia XAML + view-models |
| `UserDefaults` | `settings.json` in `%APPDATA%` |
| Keychain (`kSecClassGenericPassword`) | DPAPI (`ProtectedData`, `CurrentUser`) |
| `URLSession` + cookie storage | `HttpClient` + `HttpClientHandler.CookieContainer` |
| `NSAllowsLocalNetworking` ATS exception | no equivalent needed; plain HTTP to a LAN host just works |
| `AppleLanguages` override + relaunch | in-app string tables, applied immediately |
| `swift build` / `build-app.sh` / `.app` bundle | `dotnet publish` single-file exe |
| `LSUIElement` agent | `ShutdownMode.OnExplicitShutdown`, no main window |
| `--selftest` (Swift, no XCTest under CLT) | `--selftest` **plus** a real xUnit suite |

Kept identical on purpose: the domain model, both parsers and their sentinel
tables, the fixed sort, the row/detail decisions, the site derivation and
hoisting rules, the backoff ladder, the secrets-before-config write order, and
the "cause-specific failure headline + English technical detail" split.

---

## Contributing

1. `dotnet build` and `dotnet test` must be green.
2. If you change behaviour covered by `--selftest`, update the checks **and**
   the `tests/` mirror in the same commit — they are the contract.
3. New user-facing strings go into `Services/L10n.cs` in **both** languages.
4. Keep `PTMonitor.Core` free of UI references; that is what makes the suite
   runnable headlessly.
5. For client changes, verify against `scripts/fake-qbittorrent.mjs` in both
   `WEBAPI` modes, and against a real server if you have one.

---

## License

No license file is included yet, so the default "all rights reserved" applies.
If you intend others to reuse this, add a `LICENSE` (MIT is a common choice for
a project like this) — the repository owner can do that at any time.
