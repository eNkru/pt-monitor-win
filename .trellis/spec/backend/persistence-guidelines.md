# Persistence & Secrets

> Two files under `%APPDATA%\PTMonitor\`: `settings.json` and `secrets.dat`.

---

## AppPaths

```csharp
// src/PTMonitor.Core/Store/SecretStore.cs
public static string DataDirectory => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PTMonitor");
public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
public static string SecretsFile  => Path.Combine(DataDirectory, "secrets.dat");
```

Nothing is written outside `DataDirectory`. There is no registry use, no
installer, no machine-wide state.

---

## settings.json

Shape is owned by the private `SettingsStore.SettingsFile` DTO — deliberately
**not** the runtime types:

```csharp
private sealed class SettingsFile
{
    public List<ServerConfig>? Servers { get; set; }
    public double? PollInterval { get; set; }        // all nullable → old payloads load
    public bool? ShowSpeedsInMenuBar { get; set; }
    public bool? ShowFloatingSpeeds { get; set; }
    public FloatingDisplayMode? FloatingDisplay { get; set; }   // numbers (default) | graph
    public int? FloatingPositionX { get; set; }      // physical pixels, null = never moved
    public int? FloatingPositionY { get; set; }
    public TorrentListMode? TorrentListMode { get; set; }
    public AppLanguage? AppLanguage { get; set; }
}
```

Serializer options are fixed:

```csharp
new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
}
```

Consequences to preserve:

- On-disk keys are `servers`, `pollInterval`, `torrentListMode`,
  `appLanguage`, and `"qbittorrent"`/`"transmission"` for `kind`. Renaming a
  property or enum member silently breaks existing files — add, do not rename.
- Load clamps and defaults every field
  (`Clamp(file.PollInterval ?? DefaultPollInterval)`). A missing or older
  payload is "not configured", never an error.
- Any deserialize failure falls back to defaults (`Load` catch-all).

### Writes are atomic and coalesced

```csharp
var temp = _path + ".tmp";
File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
File.Move(temp, _path, true);
```

- Serialize a snapshot taken under the lock, then write outside it.
- Setters are no-ops when the value did not change, and `PersistAndNotify`
  fires `Changed` only after a real change.
- `Servers` returns a fresh `List` copy under the lock.

### SettingsStore API shape

Mutators, not a public setter: `Add`, `Update`, `Delete`, `ReplaceServers`,
`SetEnabled`, `SetPollInterval`, `SetShowSpeedsInMenuBar`,
`SetShowFloatingSpeeds`, `SetFloatingDisplayMode`, `SetFloatingPosition`, `SetTorrentListMode`,
`SetAppLanguage`. `Clamp` is public static for the selftest.

---

## Passwords — `secrets.dat` (DPAPI)

`ServerConfig` **never** carries the password. It lives in `ISecretStore`
keyed by `serverId.ToString("N")`, protected with
`ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`.

- Non-Windows development builds write `plain:` + base64 so `dotnet run` works;
  the Windows build never produces that form.
- `Set` with an empty password removes the entry and returns `false`. `false` is
  **not** a success flag — the two outcomes are "throws, nothing stored" and
  "returns whether a password remains".
- An unreadable entry decodes to `null` rather than throwing. Per-entry decode
  failures in otherwise valid JSON do not currently change `State`; reporting
  and recovery semantics remain unresolved, not covered by the malformed-file
  handling below.
- `InMemorySecretStore` is the test/`SelfTest` implementation; its `State` is
  always `Ok`.
- The DPAPI path only runs on Windows. `--selftest` and the xUnit suite exercise
  `DpapiSecretStore` through the plaintext fallback, so DPAPI-specific failures
  are **not** covered by either gate — verify them by hand on Windows.

### `State`: Ok, Absent, Unreadable

`Load` distinguishes a missing file (`Absent`, a fresh install) from a file that
exists but does not parse (`Unreadable`). While unreadable, every `Get` returns
`null` and the state stays that way until a successful `Set` rewrites the file.

- Before **any** persist while `Unreadable`, the existing bytes are moved to
  `secrets.dat.corrupt` only if no backup already exists (an older copy wins).
  Backup is best-effort. This happens in the write path, not in `Set`, so a
  `Delete` from Preferences cannot destroy the only recovery copy before a
  password is re-entered; repeated deletes cannot overwrite that backup either.
- `MonitorStore.Classify` relabels an auth failure as "stored password could not
  be read" when no password was available and the store is unreadable. It runs in
  both poll paths (`PollLoopAsync` and `PollAsync`), and only **after** the poll
  fails: a server that needs no password keeps working through a corruption.
- `PreferencesWindow` shows one banner while the state is `Unreadable`.

### Writes: throwing vs swallowing

`Persist()` throws; `PersistOrIgnore()` swallows with a comment. `Set` uses the
former, `Delete`/`DeleteAll` the latter — the preferences row is already gone
from the UI by the time the delete runs, so a throw there would be a second
error path with nothing to show. `Set` restores the previous in-memory entry if
the write fails, so the store never claims a password the file does not hold.

### Order matters on save

```csharp
// ServerEditorWindow.OnSave
try { services.Secrets.Set(password, config.Id); }
catch { showMessage(...); return; }   // nothing else is written

if (_existing is null) services.Settings.Add(config);
else services.Settings.Update(config);
```

Secret first, configuration second. A failed DPAPI write must not leave an
enabled server polling with an empty password — that is the fast path to a
qBittorrent IP ban.

Editing **any** server setting requires a `MonitorStore.Reconcile()`, because a
password change is invisible to `ServerConfig.Equals`. It runs automatically via
`SettingsStore.Changed`.

---

## Adding a preference

1. Field + nullable property on `SettingsFile`.
2. Private field with the default, read under the lock, `SetX` mutator that
   no-ops on no-change, `Clamp` if numeric.
3. Include it in the `snapshot` built in `Persist` and in the `Load` defaults.
4. Selftest round-trip + clamp case; xUnit mirror in `SettingsStoreTests`.
5. Preferences UI.

---

## Anti-patterns

- Username/password in `settings.json`.
- Direct `File.WriteAllText(_path, …)` without the temp file.
- A public setter, or mutating the returned `Servers` list expecting it to stick.
- Adding a non-nullable field to `SettingsFile` (old files then fail to load).
- `File.Exists` guards replacing the catch-all in `Load`.
- Per-server files, a database, or a roaming-profile store.
