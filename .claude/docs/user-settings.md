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

## Upgrade tracking & one-time notices

Two properties support version-aware, one-time UI:

- `LastRunVersion` (plain `string`, serialized) — the app version that last wrote the settings
  file. Empty on a fresh install and on any file written before this field existed.
- `ShowEditingMovedNotice` (`[ObservableProperty]`, `bool`) — drives the one-time "item editing
  has moved" `InfoBar` on `LaunchersPage`. Cleared permanently when dismissed.

`SettingsManager.StampVersion(bool existingInstall)` runs on **all three** load paths (JSON,
legacy XML migration, and the defaults fallback) and records the running version.

**Only raise an upgrade notice when `existingInstall` is true.** A fresh install has never seen
the feature being described, so the notice is pure noise. `RestoreSettings` passes `false` only
on the defaults path, where no settings file was found.

A settings file with **no** recorded version necessarily predates the field, so it is treated as
older than any threshold — that is what catches everyone upgrading, since no existing file has
the field yet:

```csharp
if (existingInstall && (previous == null || previous < EditingMovedToFlyoutVersion))
    _current.ShowEditingMovedNotice = true;
```

To add another one-time notice, add a `bool` property, compare against a new threshold
`Version` constant in `StampVersion`, and clear the flag from the UI when dismissed.

## Property Categories

Group related properties together with comment headers matching existing style:
- Appearance & Behaviour
- Taskbar Widget
- Launchers
- SFTP Sync

`UserSettings` appearance/behaviour properties currently include `AppTheme`, `Startup`, and `FlyoutAnimationsEnabled` (default `true`, controls whether `FlyoutWindow` uses animated open/close transitions).
