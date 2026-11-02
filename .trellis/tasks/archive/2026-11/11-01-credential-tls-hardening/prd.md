# Credential and TLS hardening

Findings 1, 2, 3, 4, 5, 6, 7 of `11-01-security-hardening`.

## Goal

Make the credential path safe by default and make its failures visible. Today a
new server is added over plaintext HTTP without a word of warning, any
certificate is accepted when the self-signed toggle is on, a failed secret write
is indistinguishable from a successful one, a corrupt secret file silently
downgrades every server to anonymous polling, and credentials typed into the
host field vanish. Only the last one costs nothing to notice; the rest are
silent.

## Background

- qBittorrent authenticates by POSTing `username`/`password` as a form body
  (`QBittorrentClient.LoginAsync`); Transmission sends HTTP Basic. Over `http://`
  both are cleartext on the wire.
- `ServerEditorWindow.Load()` initialises `TlsToggle.IsChecked = false`, and
  `EndpointParser.BaseUri` maps that to `http://`.
- `HttpTransport` sets `DangerousAcceptAnyServerCertificateValidator` when
  `AllowInsecureTls` is on. That accepts expired, wrong-CN and MITM certificates
  too, and nothing records what was accepted.
- `DpapiSecretStore.PersistUnsafe` catches every exception. `Set` therefore never
  throws and always returns `true`; the `try/catch` and "Could not save the
  password" message in `ServerEditorWindow.OnSave` are dead, the `bool` result is
  ignored, and `settings.json` is written anyway. This contradicts
  `.trellis/spec/backend/persistence-guidelines.md` ("A failed DPAPI write must
  not leave an enabled server polling with an empty password").
- `DpapiSecretStore.Load()`'s catch-all resets `_items = []`; every `Get` then
  returns `null` and all enabled servers poll with an empty password for the rest
  of the session.
- `HostFieldParser.Parse` strips `user:pass@` and returns only host and port;
  `ServerConfigValidator.Validate` never maps it onto `Username`. The password is
  not part of `ServerConfig` at all, so it needs its own channel.
- `HttpTransport` buffers responses with `ReadAsByteArrayAsync` and no cap.
- `AllowAutoRedirect = true`, and .NET's redirect handler **clears
  `Authorization` on every redirect** before following it
  (`RedirectHandler.cs`: `request.Headers.Authorization = null`). A same-host
  redirect therefore silently drops Basic auth and the retried request gets a
  401; a redirect to a different origin or an https→http downgrade is
  re-authenticated only by a fresh 401 challenge, i.e. the credential is sent
  wherever the redirect points. The qBittorrent login POST carries the password
  in the body, which is re-sent as-is on a 307/308, so a downgrade replays the
  password in clear.

## Requirements

1. **Secure by default** — a newly added server starts with HTTPS enabled. A
   hand-typed `http://` scheme still wins (the existing notice covers it).
2. **Plaintext warning** — while HTTPS is off, the editor shows a warning that
   the password will be sent unencrypted. The warning is part of the notice line,
   not a separate modal, and coexists with the validator's own notices.
3. **Honest insecure-TLS** — the self-signed toggle stops meaning "any cert is
   fine, silently". Whenever a certificate is accepted in that mode, the editor
   shows the SHA-256 thumbprint of the leaf certificate, so a swap is visible.
4. **Secret write failures propagate** — `Set` throws on a failed disk write;
   `OnSave` aborts before touching `settings.json` and reports it. `Delete` and
   `DeleteAll` keep swallowing: a failed delete must not break the row removal.
5. **Corrupt secret file is distinguishable** — the store reports
   `Unreadable` vs `Absent`. While unreadable, every server keeps polling; a
   poll that fails authentication has its message replaced by "stored password
   could not be read" and keeps the existing long credential backoff, while a
   server that succeeds without a password (or with one it does not need) stays
   Online. Preferences shows one banner.
6. **Userinfo in the host field is used** — `user:pass@host` prefills the user
   name and password fields, with a notice saying where they came from.
7. **Bounded responses** — the transport refuses a body above a fixed cap and
   maps it to a protocol failure instead of buffering without limit.
8. **Deliberate redirect credentials** — same-origin redirects preserve Basic.
   Cross-origin redirects, including https→http, NEVER reauthenticate, even
   after a 401. The transport's bounded redirect loop retains cookie handling.
   Body replay across origins is blocked; same-origin body replay is permitted.
   POST 301/302/303 rewrites to GET without a body; 303 preserves HEAD.
   The backend spec records these fail-closed semantics.

## Non-goals

- Certificate pinning or a trust-on-first-use store (follow-up to 3).
- TLS version pinning; the OS defaults are correct on Windows 10+.
- Storing the password in `ServerConfig` or in `settings.json`.
- Migrating `secrets.dat` to the Windows Credential Manager.
- Changing the non-Windows `plain:` fallback; it is development-only.

## Acceptance Criteria

All twelve were re-verified on merged `main` on 2026-11-02; the per-criterion
evidence table is in `verification-followup.md` ("Closure verification").

- [x] The Add Server form opens with "Use HTTPS" checked; saving yields
      `useTls: true` and an `https://` endpoint.
      *Code path verified; rendered-dialog confirmation is Windows-pending.*
- [x] Typing `http://nas:8080` still produces `useTls: false` plus the existing
      "Disabled HTTPS…" notice.
- [x] With effective HTTPS off and a non-empty effective password (including
      host userinfo), the live plaintext warning is visible even on incomplete
      forms; it disappears with effective HTTPS on or an empty password.
- [x] With "Allow self-signed certificate" on, a successful Test Connection shows
      the leaf certificate's SHA-256 thumbprint.
      *Code path verified; Windows-pending for the rendered message.*
- [x] With it off and a self-signed cert, the connection still fails and the
      message still points at the toggle.
- [x] A secret write failure (simulated) aborts the save: no server is added or
      updated, and the message names the failure.
- [x] A corrupt `secrets.dat` leaves `State == Unreadable`, `Get` returns
      `null`, and a poll that fails auth reports "stored password could not be
      read" and keeps the long backoff.
- [x] While `Unreadable`, an enabled server that polls successfully (no password
      needed) still shows Online, and deleting a row before re-entering any
      password leaves a `secrets.dat.corrupt` copy behind.
- [x] `http://bob:hunter2@nas:8080/path` prefills host `nas`, port `8080`, user
      `bob`, password `hunter2`, and shows the origin notice.
- [x] A same-origin redirect keeps Basic auth; a cross-origin or https→http
      redirect never reauthenticates, including after 401; the spec agrees.
- [x] `--selftest` and the xUnit suite cover every pure function touched here
      (parser userinfo, warning composition, response cap, unreadable-secret
      message substitution).
- [x] `dotnet build`, `--selftest`, `dotnet test` pass — 0 errors, 642 checks,
      121 tests on `main`.

**Known limitation, deliberately not closed:** valid JSON containing an
undecodable secret still loads as `Ok` and silently returns no password.
Per-entry reporting and recovery need their own design; it is recorded in
`persistence-guidelines.md` and is not claimed fixed here.

## Approved verification follow-up (2026-11-02)

- Effective parsed scheme and password drive live warnings, including incomplete
  forms. Relevant notices survive password/TLS edits.
- At Host LostFocus and Test/Save, visibly normalize host/port/TLS and prefill
  credentials. Explicit fields win; removing userinfo prevents a deliberately
  cleared password from being reinserted. Notices truthfully identify imports.
- Fix HTTPS-to-plaintext TLS/EOF guidance, port-notice localization, redirect
  method coverage and documentation drift; add mirrored network-free regressions.
- `verification.md` remains the unchanged baseline; record new evidence separately.
- Per-entry unreadable-secret reporting/recovery remains **unresolved**, not
  closed or silently excluded. Do not implement it in this focused follow-up.
- Windows DPAPI, trust, rendered UI/scaling and normal-globalization checks are
  **pending**. No Windows-dependent acceptance box is ticked by Linux checks.

## Notes

- Fix order is mandatory: 3 (secret write) → 1 (default) → 2 (thumbprint) →
  5 (userinfo) → 4 (corruption) → 6, 7. See `implement.md`.
- Finding 7 was originally "document only"; implementation uses a bounded
  transport redirect loop, with the approved fail-closed policy above.
- Touches `App` and `Core`; keep the new logic in `Core` where the selftest can
  reach it.
- Independent of `11-01-release-chain-hardening`.
