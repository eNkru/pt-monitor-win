# Component Guidelines

> XAML structure, compiled bindings, and the style-class system.

---

## Compiled bindings are mandatory

`PTMonitor.App.csproj` sets:

```xml
<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
```

So every scope that contains a `{Binding}` needs a reachable `x:DataType`. In
practice: `MainPanelWindow` declares it on the `Window` root and on all three
`DataTemplate`s; `PreferencesWindow` and `ServerEditorWindow` omit it on the
root because they contain **no bindings** (they wire via `x:Name` + `Click`
handlers). A binding without a reachable `x:DataType` is a build error — do
not fall back to reflection bindings; add the type or move the wiring to
code-behind like the dialogs do.

Bindings used in this codebase: plain `{Binding X}`, negation `{Binding
!IsEmptyState}`, and `{Binding X, Converter={x:Static StringConverters.IsNotNullOrEmpty}}`
for visibility.

---

## Style classes, not inline styling

Reusable looks are class selectors in `App.axaml`; XAML applies them with
`Classes`:

```xml
<Style Selector="Border.card">…</Style>
<Style Selector="TextBlock.statLabel">…</Style>
<Style Selector="Button.footerRow">…</Style>
```

Existing classes: `card`, `statLabel`, `statValue`, `floatingRate`,
`floatingArrow`, `sectionHeader`, `sectionSpeed`, `segment`, `prefSegment`,
`torrentBar`, `link`, `footerRow`, `accent`, `danger`.

- One-off values (a margin, a font size one notch smaller) stay inline.
- A new look used twice becomes a class.
- Hover states use `/template/ ContentPresenter` where a control has one:

```xml
<Style Selector="Button.footerRow:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="#0F000000" />
</Style>
```

- Colours are hex literals (`#E5484D` error, `#2D7D46` success, `#5A8FD8`
  progress). There is no resource dictionary.

---

## Window conventions

- Panel: `SystemDecorations="None"`, `ShowInTaskbar="False"`,
  `TransparencyLevelHint="Transparent"`, `Background="Transparent"`,
  `SizeToContent="Height"`, `WindowStartupLocation="Manual"`.
- Floating chip: `Topmost="True"`, `ShowActivated="False"`, `CanResize="False"`,
  an **opaque** `Background`, no transparency hint / corner radius / shadow, and
  `SizeToContent="WidthAndHeight"` so the 56 px border is the whole window.
- Dialogs: `CanResize="False"`, `CenterOwner`/`CenterScreen`, fixed `Width`.
- Root content is a `StackPanel`/`Grid` with `Margin="16"` in dialogs; the panel
  uses the `card` border with `CornerRadius="14"` and `Padding="14"`.
- Lists are `ItemsControl` (panel) or `ListBox` (preferences). Capped heights:
  `ScrollViewer MaxHeight="420"`, `ListBox MaxHeight="220"`.
- Separators are `Rectangle Height="1" Opacity="0.1" Fill="White"` or
  `<Separator />`.

---

## Event wiring

Handlers are `private void OnX(object? sender, RoutedEventArgs e)` in
code-behind, referenced by name from XAML (`Click="OnX"`). `sender` is only used
to get `DataContext`:

```csharp
if ((sender as Control)?.DataContext is not TorrentRowViewModel row) return;
```

- Set `e.Handled = true` when a nested button must not also trigger the parent
  (`OnOpenWebUi`), and guard the parent handler (`OnRowPointerPressed`).
- Re-entrancy guards are explicit fields (`_isTesting`, `_loadingUi`).
- Never wire a handler in code that XAML could declare.

---

## Accessibility

The project has no automated a11y gate. Keep these in force:

- Every input has a visible label (`TextBlock`) or `ToolTip.Tip`.
- `Watermark` shows host format (`nas.local or 192.168.1.2`).
- Escape/Cancel hides the panel; buttons have text, not icon-only `Content`
  (the `↗` row button is the exception and carries a `ToolTip.Tip`).
- Text sizes stay ≥ 9.5 px and `TextWrapping` is used on detail/error text.
- DPI awareness is declared in `app.manifest`; do not remove it or hard-code
  pixel sizes that assume 100 %.

---

## Anti-patterns

- A binding with no `x:DataType`.
- A `<Styles>` block inside a window.
- A resource dictionary or theme file.
- Binary *tray icon* assets — tray icons are drawn at runtime from the same
  tile/arrows/pill geometry as the exe icon (see `TrayIconFactory`). The single
  committed binary asset is the exe icon `src/PTMonitor.App/app.ico`, embedded
  via `<ApplicationIcon>` and drawn by `scripts/make-app-icon.py`; do not add
  image assets for anything else.
- Hard-coded user-facing text in XAML without an `x:Name` (see
  [localization](./localization.md)).
