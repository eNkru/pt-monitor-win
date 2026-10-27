# Localization

> English and Simplified Chinese, applied live. Core stays string-free.

---

## How it works

`Services/L10n.cs` holds two things: the current `Language` and one `Zh`
dictionary. **Keys are the English strings themselves**, so English is the
fallback and the code reads like prose:

```csharp
L10n.S("Free Space")                       // → "剩余空间" in Chinese, else "Free Space"
L10n.Format("%lld servers · %lld online", total, online)
L10n.StateWord(TorrentState.Paused)
```

- `S(key)` looks up `Zh` and falls back to the key.
- `Format` understands the printf-style placeholders kept from the macOS
  original: `%lld` (integer) and `%@` (object), consumed left-to-right as
  `arguments[0…]`, translated to a `StringBuilder` result. `.NET` `{0}`
  placeholders are **not** used in the tables.
- `AppLanguage.System` resolves once per session to `Chinese` when
  `CurrentUICulture` starts with `zh`, else `English`.

---

## Adding a string — the full checklist

1. Add the English key **and** the zh-Hans value to `Zh`.
2. Reference it by the exact English string. A typo silently renders English.
3. If it appears in XAML, give the element an `x:Name` and assign it in that
   window's `ApplyLocalization()`.
4. If it has parameters, keep the placeholder spelling (`%lld`/`%@`) and pass
   arguments in order.
5. Re-open the window in both languages and check for clipping.

`ApplyLocalization()` is called from `OnOpened` in every window (panel,
preferences, editor) and again after a language change
(`PreferencesWindow.OnLanguageChanged`, `AppServices.OnSettingsChanged`). The
panel re-localizes on each open so a Preferences change is visible next time.

---

## Formatting helpers

Use the helper instead of formatting by hand:

| Helper | Renders |
|---|---|
| `Freshness` | `—`, `just now`, `%llds ago`, `%lldm ago`, `%lldh ago` |
| `ServerCount` | `Not configured`, `%lld server · %lld online` |
| `ActiveTorrents` | `%lld · %lld total` |
| `Eta` | `—`, `%llds`, `%lldm %02llds`, `%lldh %02lldm` |
| `StateWord` | state name |
| `ListMode` / `NoTorrents` | filter label / empty note |
| `CauseHeadline` | unreachable / sign-in failed / temporarily banned / … |
| `OpenInWebUi`, `Connected`, `Failed`, `MoreTorrents` | templated wrappers |

Two numeric-table usage notes:

- `"%lldm %02llds"` and `"%lldh %02lldm"` rely on the zh/English value carrying
  `%02lld`; `Format` prints the number as-is, so padding is authored in the
  table.
- `PreferencesWindow.UpdatePollLabel` needs a `{0}` string and therefore calls
  `.Replace("%lld", "{0}")` then `string.Format`. Reuse that trick rather than
  adding a second formatter.

---

## Where localization is allowed

- `App` only. `Core`, `Cli` and `SelfTest` never call `L10n`; that is why
  `SelfTest` can assert decisions (`TorrentRowInfoSegment`, `"1.2M"`).
- `ServerFailure.Message` and the parser messages stay English. The app
  localizes the headline (`CauseHeadline`) and shows the English detail beneath
  it. Do not translate `Core` messages.

---

## Text that is intentionally not localized

- `"PT Monitor"`, `"PT Monitor Preferences"` (window title is localized, the
  product name is not), `"English"`, `"简体中文"`.
- Arrows and glyphs: `↓ ↑ ↗ ⟳ ⚙ ⏻ —`.
- `Consolas,Inter,monospace` and other XAML-only values.

---

## Anti-patterns

- A Chinese string written directly in XAML or code.
- Adding an English key without the zh value.
- Using `.NET` `{0}` in the table and `Format`.
- Localized `Core` output, or a `Core` type depending on `L10n`.
- Concatenating translated fragments (`S("A") + " " + S("B")`) instead of one
  templated key.
- Translating a technical detail (`"host not found"`) and losing the English.
