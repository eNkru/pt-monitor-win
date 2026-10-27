# Core Layer Guidelines

> Rules for `src/PTMonitor.Core`, `src/PTMonitor.Cli`, and `tests/`.

---

## Overview

`PTMonitor.Core` is the whole product minus the UI. It owns the domain model,
the two API clients, persistence, formatting, and the built-in check suite. It
must build and run headlessly — that is what makes `--selftest` possible on any
OS without a NAS.

`PTMonitor.Cli` is a thin argument dispatcher over `Core.Diagnostics`.
`tests/PTMonitor.Core.Tests` is the xUnit mirror of the built-in suite.

---

## Reference

| Guide | Description |
|---|---|
| [Directory Structure](./directory-structure.md) | Folder responsibilities, dependency direction, where new code belongs |
| [Client & Parser Rules](./client-and-parser-rules.md) | The `ITorrentClient` contract, parse-once rule, sentinel tables |
| [Error Handling](./error-handling.md) | `ServerFailure`/`FailureKind`, when to throw, when to swallow |
| [Persistence & Secrets](./persistence-guidelines.md) | `settings.json` shape, atomic writes, DPAPI secret store |
| [Concurrency & Polling](./concurrency-guidelines.md) | Poll loops, cancellation, locking, change detection, backoff |
| [Logging](./logging-guidelines.md) | There is no log file — what is printed and where |
| [Quality Guidelines](./quality-guidelines.md) | Self-test contract, forbidden patterns, review checklist |

---

## The one rule that shapes everything

Both APIs normalise into **one** domain model. Nothing above `Client/` branches
on `ServerKind` to interpret data. If you find yourself writing
`if (kind == ServerKind.X)` outside `Client/`, a normalisation is missing in the
parser layer.

The legitimate kind switches are the ones that only *pick* something, never
interpret a field: `MonitorStore.DefaultClientFactory`, `ProbeRunner.RunAsync`,
`ServerEditorWindow.OnTest` (client construction) and
`ServerConfig.WebUiUri` (one extra path segment for Transmission).

---

## Example of the layer doing its job

`QBittorrentParser.State` and `TransmissionParser.State` collapse ~20 raw
protocol state strings/ints into `TorrentState`. The panel only ever sees
`TorrentState`:

```csharp
// src/PTMonitor.Core/Client/QBittorrentClient.cs
public static TorrentState State(string? raw) => raw switch
{
    "downloading" or "metaDL" or "forcedDL" => TorrentState.Downloading,
    "stalledDL" => TorrentState.Stalled,
    ...
    _ => TorrentState.Other,
};
```

A new qBittorrent state name is a one-line change here plus a selftest row — no
UI edit.
