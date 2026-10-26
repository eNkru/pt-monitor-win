# PTMonitor (Windows)

A Windows **system tray app** for watching private tracker (PT) torrent
clients: qBittorrent and Transmission. It lives in the notification area
(tray), polls your servers on a fixed interval, and shows a dropdown panel
with one section per server: per-torrent name, progress, speeds, and ETA.

This is the Windows port of the macOS menu bar app
[`pt-monitor`](../pt-monitor) — same architecture, same behaviours, same
built-in check suite, C#/.NET 8 instead of Swift.

## Features

- **Tray agent** — no window at launch; the tray icon is the only persistent
  UI (idle / active / all-failing states), with live speeds in its tooltip.
- **Multiple servers** — any number of qBittorrent WebUI or Transmission RPC
  endpoints; each gets its own section in the dropdown panel.
- **Per-torrent rows** — name, progress bar, percentage, speeds + ETA, and
  the PT site, sorted by a fixed order (downloading → seeding → rest, each
  by down speed). Click a row to expand its detail block (size, ratio,
  seeds/leechers, tracker status, category, error).
- **Open the web UI from a row** — the trailing `↗` opens *that server's*
  own web UI in the default browser.
- **Resilient polling** — one loop per enabled server; auth failures back off
  for 10 minutes and bans for 15 (so a typo'd password can't trip
  qBittorrent's IP ban), while transient failures retry at the normal
  interval (1–60 s, configurable).
- **DPAPI-stored secrets** — passwords are never written to `settings.json`;
  they live in `secrets.dat`, protected per-user with Windows DPAPI.
- **HTTPS + self-signed certs** — an opt-in "allow self-signed certificate"
  toggle per server.
- **Bilingual** — English and Simplified Chinese, switchable in Preferences,
  applied immediately (no restart).
- **Read-only** — the app only ever polls; it never mutates anything on the
  server.

## Requirements

- Windows 10 (1607+) / 11, x64
- To **run**: nothing else — the published build is self-contained.
- To **build**: [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0)
  and (for the stub server) Node.js 18+.

## Project layout

```
PTMonitor.sln
src/
  PTMonitor.App/                Avalonia tray app (the GUI)
    Program.cs / App.axaml.cs    bootstrap; no main window, tray agent
    Services/AppServices.cs      composition root: stores, tray icon, windows
    Services/L10n.cs             English + zh-Hans string tables
    Services/TrayIconFactory.cs  tray icons rendered at runtime (no assets)
    ViewModels/PanelViewModel.cs per-server sections, torrent rows, expansion
    Views/MainPanelWindow.axaml  the dropdown panel
    Views/PreferencesWindow.axaml
    Views/ServerEditorWindow.axaml  add/edit + Test Connection
  PTMonitor.Core/                All reusable logic (no UI references)
    Model/                       ServerConfig, ServerStatus, TorrentSummary,
                                 TransferSnapshot, TorrentListMode, …
    Client/                      ITorrentClient; TransmissionClient,
                                 QBittorrentClient, HttpTransport, parsers
    Store/                       SettingsStore (settings.json), DpapiSecretStore,
                                 MonitorStore (polling loops)
    Format/                      ByteRateFormatter
    Diagnostics/                 SelfTest (in-binary checks), ProbeRunner
  PTMonitor.Cli/                 headless --selftest / --probe entry point
tests/PTMonitor.Core.Tests/      xUnit suite over the same logic
scripts/
  build.ps1                      build + selftest + test + publish + zip
  fake-qbittorrent.mjs           stub qBittorrent WebUI for client verification
.github/workflows/build.yml      windows-latest CI (tag -> release)
```

Data lives under `%APPDATA%\PTMonitor\` (`settings.json`, `secrets.dat`).

## Building

```powershell
.\scripts\build.ps1                 # full pipeline: build, selftest, test, publish, zip
# or by hand:
dotnet run --project src/PTMonitor.Cli -- --selftest
dotnet test
dotnet run --project src/PTMonitor.App
```

`build.ps1` publishes a **self-contained single-file** `PTMonitor.exe` to
`publish\win-x64\` and zips it into `artifacts\`.

## Running / using the app

1. Run `PTMonitor.exe` (or `dotnet run --project src/PTMonitor.App`).
   On first launch the preferences open directly.
2. Add a server: pick a type (qBittorrent / Transmission), host, port,
   credentials, HTTPS/insecure toggles. Use **Test Connection** to verify.
3. Left-click the tray icon to open the panel; right-click for the menu
   (Refresh Now / Preferences / Quit). Esc or clicking elsewhere closes the
   panel — the agent keeps running.

Base URL shortcuts: qBittorrent WebUI `/api/v2`, Transmission RPC
`/transmission/rpc` — hosts may be given as bare host names, IP, IPv6, or URL.

## CLI (no GUI)

The CLI binary has headless modes useful for development and diagnostics:

```powershell
# Run all built-in checks (the same suite as --selftest), exit 0/1:
dotnet run --project src/PTMonitor.Cli -- --selftest
# → selftest: OK (204 checks passed)

# Fetch one snapshot from a server as JSON (exit 0 ok / 1 server fail / 2 usage):
dotnet run --project src/PTMonitor.Cli -- --probe qbittorrent --host 192.168.1.2 --port 8080 --user admin --pass secret
```

Probe options:

| Flag | Meaning | Default |
|---|---|---|
| `--probe <type>` | `qbittorrent` or `transmission` | required |
| `--host <host>` | host name, IP, or full URL | required |
| `--port <port>` | server port | 8080 (qb) / 9091 (tr) |
| `--user`, `--pass` | credentials | — |
| `--https` | use HTTPS | off |
| `--insecure` | accept self-signed certificates | off |

### Verifying the client without a NAS

The stub WebUI mirrors qBittorrent's exact auth behaviours (IP bans at 5
failed attempts, HTTP 403 for private endpoints without an `SID` cookie),
so transport bugs look like what they are instead of like "the server banned
this IP":

```powershell
node scripts\fake-qbittorrent.mjs              # stub on 127.0.0.1:18080
dotnet run --project src/PTMonitor.Cli -- --probe qbittorrent `
    --host 127.0.0.1 --port 18080 --user admin --pass adminadmin
# Repeat with $env:WEBAPI = "5.2" (204/401 login shapes) — the outcome must be identical.
```

## Checks & development notes

- **The check suite is the contract.** `--selftest` (and the xUnit mirror)
  covers the host parser, config validator, both API parsers, eta/ratio
  sentinels, progress clamps, the fixed sort, row/detail decisions, site
  extraction, hoisting, the formatter (including printf-style rounding),
  settings round-trips, backoff ladder, aggregate/menu-state mapping, and the
  Transmission 409 handshake.
- **qBittorrent login shapes**: both generations are accepted — ≤ 5.1
  (`200 "Ok."` / `200 "Fails."`) and 5.2+ (`204` / `401`); a 403 on
  `auth/login` is the IP ban, but a 403 on any other endpoint means the
  session cookie was not accepted — never a ban.
- **Transmission**: every request runs the `X-Transmission-Session-Id` 409
  handshake (retried exactly once); `free-space` is fetched on a slow cadence
  of its own so a hung mount can never stall the speed poll.
- **Secrets**: the DPAPI write happens *before* the config write when saving
  a server, so a failed write can never leave an enabled server polling with
  an empty password.
