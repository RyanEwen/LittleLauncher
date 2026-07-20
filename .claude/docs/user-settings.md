> **Scope:** Use when adding or modifying observable settings properties in UserSettings.cs, Launcher model properties, handling property change side-effects, or extending the serialized settings schema.
> **Governs:** `**/ViewModels/UserSettings*.cs`, `**/Models/Launcher.cs`.

# UserSettings Conventions

## Adding a New Setting

1. Add an `[ObservableProperty]` field (lowercase with underscore prefix):
   ```csharp
   [ObservableProperty]
   private bool _myNewFeature;
   ```
2. CommunityToolkit.Mvvm generates `MyNewFeature` property + `OnMyNewFeatureChanged` partial method
3. The property auto-serializes to JSON via `System.Text.Json` — no extra config needed

## Side-Effects

- Implement `partial void OnMyNewFeatureChanged(bool value)` for reactive changes
- Always check `_initializing` flag to skip logic during deserialization:
  ```csharp
  partial void OnMyNewFeatureChanged(bool value)
  {
      if (_initializing) return;
      // side-effect logic here
  }
  ```

## JSON Serialization

- Properties marked `[JsonIgnore]` are excluded from settings.json
- `ObservableCollection<T>` properties serialize as JSON arrays
- Default values in field initializers are used when the property is missing from JSON
- `DefaultIgnoreCondition = WhenWritingDefault` omits default-valued properties from the output
- After deserialization, `CompleteInitialization()` is called to finalize state

## Non-Serialized Model Properties

`LauncherItem.IsExpanded` is `[JsonIgnore]` (defaults `true`) — it tracks the group expand/collapse state in the settings UI but is not persisted to disk. It is a plain property (not `[ObservableProperty]`) since it doesn't need data binding or change notification.

## LauncherItem Icon Properties

- `IconGlyph` (`[ObservableProperty]`, `string`) — Unicode glyph character (Segoe Fluent Icons PUA or emoji). Default `""` (Segoe Fluent "open" glyph, code point U+E8E5).
- `IconPath` (`[ObservableProperty]`, `string`) — Local file path to a cached favicon or custom image. Takes priority over `IconGlyph` when set.
- `IconColor` (`[ObservableProperty]`, `string`) — Optional hex color for the glyph (e.g. `"#FF0000"`). Empty string means default theme color. Only affects glyph rendering (no effect when `IconPath` image is used). Serialized to JSON; omitted when empty (`DefaultIgnoreCondition = WhenWritingDefault`).

## Launchers Collection

`UserSettings.Launchers` is an `ObservableCollection<Launcher>`. Each `Launcher` holds:
- `Id` (GUID string, readonly key)
- `Name` (`[ObservableProperty]`)
- `TrayIconMode` (`[ObservableProperty]`, `string` — uses `TrayIconModes` constants like `"Composite"`, `"Blue"`, etc. A `TrayIconModeJsonConverter` handles migration from legacy integer values)
- `CustomTrayIconPath` (`[ObservableProperty]`)
- `NIconHide` (`[ObservableProperty]`)
- `ViewMode` (`[ObservableProperty]`, `int` — `0 = Icons`, `1 = List`, `2 = Small Icons`; non-list values use icon-style column layout in the flyout/editor)
- `IconModeIconsPerRow` (`[ObservableProperty]`, default 3, clamped to 1–12, controls icon density in icon-mode flyouts and the launcher item editor)
- `ShowTitle` (`[ObservableProperty]`, shows launcher name at top of flyout)
- `Items: ObservableCollection<LauncherItem>`

### Sharing Properties (plain auto-properties, not `[ObservableProperty]`)
- `IsShared` (bool) — whether this launcher participates in sharing
- `IsSharedOwner` (bool) — `true` = publisher, `false` = subscriber; only meaningful when `SharedTwoWay` is `false`
- `SharedTwoWay` (bool) — `true` = all participants push and pull (last save wins); `false` = 1-way (owner pushes, subscribers pull)
- `SharedSyncMode` (int) — 0 = File (local/network path), 1 = SFTP
- `SharedPath` (string) — file path (local/UNC) or SFTP remote path depending on mode
- `SharedSftpHost`, `SharedSftpPort` (int, default 22), `SharedSftpUsername`, `SharedSftpPrivateKeyPath` — SFTP connection fields (only used when `SharedSyncMode == 1`)
- `SharedSftpRemotePath` — legacy migration-only setter that populates `SharedPath` + sets SFTP mode on deserialization
- `IsFileSync`, `IsSftpSync` — `[JsonIgnore]` convenience properties derived from `SharedSyncMode`

**Migration**: On first run with old settings, `CompleteInitialization()` checks `Launchers.Count == 0` and migrates `LauncherItems` + `TrayIconMode`/`NIconHide`/`CustomTrayIconPath` into a "Default" launcher (legacy int `TrayIconMode` is converted via `TrayIconModes.FromLegacyInt()`). The legacy properties remain in the schema but are not observable. On first load, migrates from legacy `settings.xml` to `settings.json`. The `TrayIconModeJsonConverter` on `Launcher.TrayIconMode` also handles reading legacy integer values from old JSON files.

**Do not** add `[ObservableProperty]` to the legacy migration fields (`LauncherItems`, `TrayIconMode`, `NIconHide`, `CustomTrayIconPath` on `UserSettings`) — they are plain migration-only properties marked with `[JsonIgnore]`.

## Upgrade tracking

`LastRunVersion` (plain `string`, serialized) records the app version that last wrote the
settings file. `SettingsManager.StampVersion()` sets it on **all three** load paths (JSON,
legacy XML migration, and the defaults fallback).

It exists so a future release can detect an upgrade. Two rules if you add a one-time notice:

- **Only fire when a settings file already existed.** A fresh install has never seen whatever
  is being announced, so the notice is pure noise. `RestoreSettings` reaches the defaults path
  only when no file was found.
- **Treat a missing version as older than any threshold.** No file in the wild carries the
  field yet, so `previous == null` is what identifies an upgrader.

```csharp
if (existingInstall && (previous == null || previous < SomeFeatureVersion))
    _current.ShowSomeNotice = true;
```

Prefer making the new behaviour discoverable in place over announcing it. The "item editing has
moved" notice was removed once the Launchers page gained an **Edit items** entry that opens the
flyout directly — an action beats an explanation, and it needs no version tracking at all.

## Property Categories

Group related properties together with comment headers matching existing style:
- Appearance & Behaviour
- Taskbar Widget
- Launchers
- SFTP Sync

`UserSettings` appearance/behaviour properties currently include `AppTheme`, `Startup`, and `FlyoutAnimationsEnabled` (default `true`, controls whether `FlyoutWindow` uses animated open/close transitions).
