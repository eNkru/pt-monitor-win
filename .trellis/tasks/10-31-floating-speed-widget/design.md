# Design — Floating desktop speed widget

## Boundaries

| Concern | Owner |
|---|---|
| Aggregate rates, offline detection | `MonitorStore.Aggregate` / `PanelViewModel` rule (App reads it) |
| Rate text formatting | `ByteRateFormatter.Rate` |
| Persisted enable flag + position | `SettingsStore` |
| Window instance, show/hide sync, click routing | `AppServices` |
| Chip layout, drag/click gesture, placement, rate text | `Views/FloatingSpeedWindow` (code-behind, like Preferences) |
| Strings | `L10n` (both tables) |

No `Core` logic is added except two persisted settings fields.

## Settings contract

`SettingsStore` gains:

```csharp
public bool ShowFloatingSpeeds { get; }          // default false
public int? FloatingPositionX { get; }           // physical pixels
public int? FloatingPositionY { get; }

public void SetShowFloatingSpeeds(bool enabled);
public void SetFloatingPosition(int x, int y);
```

All go through `PersistAndNotify` (atomic temp-file write, `Changed` event).
`SettingsFile` adds nullable members so an absent file means "off / no saved
position". Note: persisted window position deliberately departs from the
"UI-only state is not persisted" guideline, because the user asked for a
remembered position.

## Window

`Views/FloatingSpeedWindow.axaml`:

```xml
<Window SystemDecorations="None" ShowInTaskbar="False" ShowActivated="False"
        Topmost="True" TransparencyLevelHint="Transparent"
        Background="Transparent" SizeToContent="WidthAndHeight"
        CanResize="False" WindowStartupLocation="Manual">
  <Border Classes="card floatingChip">
    <StackPanel Orientation="Horizontal" Spacing="10">
      <TextBlock x:Name="DownText" Classes="floatingRate" />
      <TextBlock x:Name="UpText" Classes="floatingRate" />
    </StackPanel>
  </Border>
</Window>
```

- Rates are set in code-behind after `InitializeComponent()`; arrows are
  `ByteRateFormatter.DownArrow`/`UpArrow`, so no localization is required.
- `ToolTip.Tip` is assigned in `ApplyLocalization()` and re-applied on
  `OnOpened`, matching the panel's re-localize-on-open rule.

### Gesture

- `PointerPressed`: remember the press point (`_pressed`, `_pressPoint`).
- `PointerMoved` while pressed and beyond a 4 px threshold: set
  `_dragged = true` and call `BeginMoveDrag(e)` once.
- `PointerReleased` with `_pressed && !_dragged`: `AppServices.Instance.ShowPanel()`.
  Reset state. A drag therefore never opens the panel.
- Persist the position from the `PositionChanged` handler while `_dragged` (a
  `BeginMoveDrag` may consume the release), guarded so placement itself does not
  write.

### Placement

- Restore the saved position when it is inside any screen's work area.
- Otherwise default to the primary work area's bottom-right with a 12 px gap.
- Clamp the restored/default point so the chip stays fully on-screen. Do this in
  `OnOpened` before the user can interact (the panel already uses the
  `Opened` hook for the same purpose).

## AppServices integration

- New private `FloatingSpeedWindow? _floating`.
- `UpdateFloatingRates()`: reads `Monitor.Aggregate`; when nothing is online,
  writes `—`, else `↓ rate` / `↑ rate`.
- `SyncFloatingWindow()`: honours the setting — create/show on enable, hide on
  disable. Called from `OnSettingsChanged`, which also handles language changes.
- `UpdateTray()` (already posted to the UI thread on every status change) also
  calls `UpdateFloatingRates()`.
- `TogglePanel()` splits into `ShowPanel()` (show + activate) and `HidePanel()`;
  the chip calls `ShowPanel()`.
- Tray menu gets a `NativeMenuItem { ToggleType = MenuItemToggleType.CheckBox,
  IsChecked = Settings.ShowFloatingSpeeds }` whose click writes the setting.

## Preferences

A checkbox after the tooltip toggle, wired in `Reload`, `ApplyLocalization` and
`OnFloatingToggled` → `Settings.SetShowFloatingSpeeds(...)`.

## Lifecycle & threading

- One cached window; no timers (poll events drive updates).
- Hidden window is kept alive but inert; nothing is scheduled while hidden.
- Updates are posted by the existing `Dispatcher.UIThread.Post(UpdateTray)`.
- `ShutdownMode.OnExplicitShutdown` keeps the process alive; `Quit()` closes the
  app and the chip with it.

## Compatibility / rollback

- New JSON members are nullable and ignored by older builds; removing the
  feature leaves harmless keys behind.
- Rollback is deleting the window, the menu/preference wiring and the settings
  fields; no migration is needed.

## Risks

- `Topmost` chips can cover other apps; mitigated by drag and the off default.
- Multi-monitor coordinates are physical pixels; stale positions are clamped to
  the primary work area when the saved monitor is gone.
- `ShowActivated=False` keeps the panel's focus model intact but means the chip
  never steals the foreground.
