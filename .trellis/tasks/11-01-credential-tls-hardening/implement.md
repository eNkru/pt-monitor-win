# Implementation plan — credential and TLS hardening

Order is mandatory: the secret-write fix (1) is the one that hides outages, and
the remaining steps add tests on top of it. Commit per step.

## Validation commands

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1   # this Linux box: no dotnet on PATH, no libicu
dotnet build -c Release
dotnet run --project src/PTMonitor.Cli -c Release --no-build -- --selftest
dotnet test tests/PTMonitor.Core.Tests -c Release --no-build
```

Run all three at every gate. The app is not runnable on this Linux box — UI
verification is by inspection against `design.md`, plus the selftest for every
pure function.

## Checklist

- [x] **1. Secret write failure propagates** (finding 3)
  - `Core/Store/SecretStore.cs`: add `SecretStoreState`, `ISecretStore.State`;
    split `PersistUnsafe` into throwing `Persist()` and `PersistOrIgnore()`;
    `Set` → `Persist()`, `Delete`/`DeleteAll` → `PersistOrIgnore()`;
    `InMemorySecretStore.State => Ok`.
  - `App/Views/ServerEditorWindow.OnSave`: keep the existing `try/catch`, and use
    the `Set` result (`false` = empty password, not a failure — only a throw is).
  - Selftest: target path whose parent is a file → `Set` throws, `State` stays
    `Absent`.
  - Gate.
- [x] **2. HTTPS on by default** (finding 1)
  - `App/Views/ServerEditorWindow.Load()`: new-server branch sets
    `TlsToggle.IsChecked = true`.
  - `Core/Client/HttpTransport.Describe`: add the "or uncheck Use HTTPS" hint for
    TLS failures over an `http` base.
  - Gate. Manual check: `http://` in the host field still forces `useTls: false`
    with the existing notice.
- [x] **3. Plaintext warning** (finding 1)
  - `ShowNotice` → `RefreshNotices` per `design.md` §3; wire to
    `TlsToggle.IsCheckedChanged`, `PasswordField.TextChanged`, `Validate`, `Load`.
  - `App/Services/L10n.cs`: English + Simplified Chinese for the warning.
  - Selftest: warning-composition helper (extract it as a pure `Core` function
    taking `useTls` and `hasPassword` so the selftest can reach it).
  - Gate.
- [x] **4. Certificate thumbprint** (finding 2)
  - `HttpTransport`: `_onCertificateAccepted` ctor arg, own validation callback
    per `design.md` §4, SHA-256 thumbprint via
    `GetCertHashString(HashAlgorithmName.SHA256)`.
  - `QBittorrentClient`/`TransmissionClient`: pass the optional callback through.
  - `ServerEditorWindow.OnTest`: append ` · cert SHA-256 <thumbprint>` when the
    toggle was on.
  - Gate.
- [x] **5. Userinfo in the host field** (finding 5)
  - `HostFieldParser`: `ParsedHostField` + `Username`/`Password`, unescape both.
  - `ServerConfigValidator`: `Notice` → `Notices` list, credential notice,
    typed-user-wins rule, `PasswordFromHost` on `ValidatedServerConfig`.
  - `ServerEditorWindow`: consume `Notices` and `PasswordFromHost` in `OnTest`
    and `OnSave`.
  - Selftest + `CoreTests`: `user:pass@host`, `http://u:p@host:1234/path`,
    `u%40x:p%3Aw@host`, IPv6 with and without userinfo.
  - Gate.
- [x] **6. Corrupt secret file** (finding 4)
  - `DpapiSecretStore.Load`: `Absent` vs `Unreadable`; preserve the unreadable
    bytes as `secrets.dat.corrupt` inside the write path, so the first persist of
    *any* kind (including a `Delete` from Preferences) keeps the only recovery
    copy before overwriting the file.
  - `MonitorStore`: `Classify(password, secrets, failure)` helper per
    `design.md` §2; apply it in `PollLoopAsync`'s catch (the live loop) **and**
    in `PollAsync`'s catch. Never pre-empt a poll — a passwordless server must
    keep polling and stay Online.
  - `PreferencesWindow`: one banner while `State == Unreadable`.
  - Selftest: garbage file → `Unreadable` + `Get` null; a poll that fails auth
    returns "stored password could not be read"; a poll that succeeds while
    `Unreadable` stays Online; `Delete` on an unreadable store leaves
    `secrets.dat.corrupt`; `Set` clears `State`.
  - Gate.
- [x] **7. Response cap** (finding 6)
  - `HttpTransport`: `MaxResponseContentBufferSize = 32 MiB`, map the limit
    breach to `ProtocolViolation`.
  - Gate.
- [x] **8. Redirect credentials** (finding 7)
  - `HttpTransport`: `AllowAutoRedirect = false` plus `SendFollowingRedirects`
    per `design.md` §7; pure helpers `SameOrigin`, `TryGetRedirect`,
    `BuildRedirectRequest` for the selftest.
  - `.trellis/spec/backend/client-and-parser-rules.md`: same-origin redirects
    keep Basic; a different origin, including an https→http downgrade, gets a
    401 challenge first; a redirect carrying a body is not followed across
    origins at all.
  - Verified end to end against loopback listeners (same-origin keeps the
    header, cross-origin does not).
- [x] **9. Docs + final verification**
  - `README.md` "Data, settings and secrets": HTTPS default, plaintext warning,
    unreadable-secret behaviour.
  - `.trellis/spec/backend/persistence-guidelines.md`: throwing `Set` vs
    swallowing `Delete`, `Unreadable` state, `.corrupt` file, and the fact that
    the DPAPI path is only exercised on Windows.
  - Full three-command verification on the final tree.

## Review gates

After step 1, 5 and 6 stop and re-read `design.md` against the diff — those three
change contracts (`ISecretStore`, `ValidatedServerConfig`, `PollAsync`) that the
rest builds on. A defect there invalidates later steps.

## Rollback points

- After step 1: revert `SecretStore.cs` + `OnSave` together; the state member is
  additive and can stay.
- After step 5: revert `HostFieldParser`, `ServerConfigValidator` and the editor
  consumption as one unit — a half-applied `Notices` change does not compile.
- Steps 2, 3, 4, 7, 8 are independent one-file changes and revert individually.
