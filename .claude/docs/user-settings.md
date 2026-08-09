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

## Defaults vs. `WhenWritingDefault` — the trap that eats settings

`SettingsManager.JsonOptions` sets `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault`,
which omits any property currently holding **the CLR default for its type** — `false`, `0`, `null`.
Combine that with a field initialiser or constructor assignment that sets a *non*-CLR-default value
and the setting becomes impossible to change:

> The user picks the CLR-default value → the key is omitted on save → the initialiser puts the old
> value back on load. The setting silently reverts, forever.

This is not hypothetical. `FlyoutAnimationsEnabled` defaulted to `true`, so turning animations
**off** wrote nothing at all and they were on again after the next restart. Verified by toggling it
off, closing the settings window, and finding no `FlyoutAnimationsEnabled` key in settings.json.

### Fixing it

Pick one, in this order of preference:

1. **Phrase the property so `false` / `0` is the default behaviour.** `WebPinFlyout` rather than an
   auto-hide flag; `WebHiddenPolicies.UnloadWhenIdle == 0`. Nothing to remember later.
2. **Treat `0` as "unset" and resolve the real default in a `[JsonIgnore]` `Resolved*` property.**
   What `WebFlyoutWidth` / `WebZoomPercent` / `WebIdleUnloadMinutes` do.
3. **Opt the property out of the policy** with `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]`,
   which forces it to be written even at its default. Used on `FlyoutAnimationsEnabled`, where
   renaming the key would have orphaned every existing settings file for no user-visible gain.

Never leave a `bool` that defaults to `true` un-annotated.

### Audit — the remaining non-CLR-default initialisers

These share the shape but not the bug, because the CLR default is not a value the UI can produce
or a configuration that would work:

| Property | Default | Why it is safe |
|---|---|---|
| `SftpPort`, `Launcher.SharedSftpPort` | 22 | Port `0` is not a valid port |
| `SftpAutoSyncInterval` | 5 | A `0`-minute interval is not offered |
| `SyncProvider` | `SyncProviders.Sftp` (0) | The default *is* the CLR default, by design — see below |
| `SftpRemotePath` | `~/.config/LittleLauncher/` | Clearing the box reverts to the default — which is the desirable outcome, since an empty remote path is not a usable configuration |
| `Launcher.Name` | `"Launcher"` | `LauncherSettingsWindow.CommitName` refuses an empty name |
| `Launcher.TrayIconMode` | `Composite` | The gallery cannot produce an empty mode |
| `Launcher.IconModeIconsPerRow` | 3 | The picker offers 1–12, never 0 |
| `LauncherItem.IsExpanded` | `true` | `[JsonIgnore]` — never serialised at all |

`Launcher.ShowTitle` is the case that was already handled correctly: it defaults to `false` in the
model, and `LaunchersPage` sets it to `true` on *newly created* launchers rather than changing the
model default, which would have flipped it on for every existing launcher.

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

### Web launcher properties (all `[ObservableProperty]`)

- `Kind` (`int`) — `LauncherKinds.Items` (0, default) or `LauncherKinds.Web` (1)
- `WebUrl` (`string`) — the page a web launcher opens
- `WebFlyoutWidth` / `WebFlyoutHeight` (`int`, DIPs) — **0 means unset**; read them through
  `ResolvedWebFlyoutWidth` / `ResolvedWebFlyoutHeight`
- `WebZoomPercent` (`int`) — 0 means 100%; read `ResolvedWebZoomFactor`
- `WebHiddenPolicy` (`int`) — `WebHiddenPolicies.UnloadWhenIdle` (0, default) / `Suspend` (1) / `KeepRunning` (2)
- `WebIdleUnloadMinutes` (`int`) — 0 means the default; read `ResolvedWebIdleUnloadMinutes`
- `WebReloadOnShow` (`bool`) — re-fetch on every open
- `WebSharedProfile` (`bool`) — pool cookies and logins in `WebProfiles\Shared` with every other
  launcher that sets it, instead of a private per-launcher folder. Defaults `false`, which is both
  the safe direction under `WhenWritingDefault` and the behaviour that shipped first — an upgrade
  must not move a launcher onto a profile it was never signed in to
- `WebAnchor` (`int`) — where the flyout opens when it has not been moved: `WebAnchors.Tray`
  (0, default) or one of the nine corner/edge/centre positions. Outranked by `WebFlyoutPosition`,
  so with `WebRememberPosition` on it decides only the first open; changing it clears
  `WebFlyoutPosition` so the new choice actually takes effect
- `WebPinFlyout` (`bool`) — stay open when focus is lost
- `WebAllowAllPermissions` (`bool`) — grant camera, microphone, location and notifications without
  asking. Defaults `false` (ask), which is both the safe direction and the only one that survives
  `WhenWritingDefault`. A request answered by this toggle is **not** saved into the WebView2
  profile, so turning it off returns the launcher to asking — see
  [web-launchers.md](web-launchers.md)
- `WebUseBookmarks` (`bool`) — bar of bookmarks instead of a single address. Defaults `false`, so
  the safe direction under `WhenWritingDefault`
- `WebDefaultBookmarkUrl` (`string`) — which bookmark opens with the flyout; empty means none, and
  it is a URL rather than an index so reordering cannot change what opens
- `WebBookmarks` (`ObservableCollection<WebBookmark>`) — the bar's entries. `WebBookmark`
  (`Models/WebBookmark.cs`) is `Name` + `Url` + `IconPath`, observable because the icon arrives
  after the bookmark does
- `IsWebLauncher` / `HasWebBookmarkBar` / `DefaultWebBookmark` — `[JsonIgnore]` conveniences

**Why every one of those defaults to 0 / false:** `WhenWritingDefault` omits a property holding the
CLR default, so a non-zero field initialiser is silently restored on the next load. Numeric settings
therefore treat `0` as "unset" and resolve the real default in a `Resolved*` property, and booleans
are phrased so `false` is the default behaviour (`WebPinFlyout`, not an auto-hide flag). A `bool`
that defaults to `true` **cannot be turned off** in this settings file.

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

## Sync destination: `SyncProvider` / `SyncFolderPath`

`SyncProvider` (`[ObservableProperty]`, int) selects where global sync reads and writes — see
`Models/SyncProviders.cs`: `Sftp` (0, default), `OneDrive` (1), `GoogleDrive` (2),
`NetworkShare` (3), `Folder` (4), `WebDav` (5). `WebDavUrl` / `WebDavUsername` configure the
WebDAV server — **its password is not here**, see below. `SyncFolderPath` (`[ObservableProperty]`, string) is the
directory used by the two **folder** providers only — OneDrive and Google Drive sign in and use
their vendor APIs, so they store nothing here. `IsFolderSync` is a `[JsonIgnore]` convenience;
`SyncProviders.IsCloudAccount` is its counterpart.

**Cloud credentials are not in this file and must never be** — OAuth tokens *and* the WebDAV
password. settings.json is exported,
imported, backed up, and *uploaded by the sync feature itself* — a refresh token in it would be
copied to every machine and into whatever server or folder is configured. They live in
`ProtectedStore` (`%AppData%\LittleLauncher\cloud-{name}.dat`, DPAPI-encrypted to the current
Windows user). A URL and a username are ordinary settings and do belong here; only the secret
does not.

**`Sftp` is 0 because of the `WhenWritingDefault` policy above.** Every settings file written
before this property existed omits it, so the CLR default is what those files resolve to — it has
to be the transport they were already configured for. Never renumber these constants: they are
serialized.

The SFTP fields and `SyncFolderPath` **coexist rather than replace each other**, so switching
provider and back does not mean re-entering a connection.

`SftpAutoSync` / `SftpAutoSyncInterval` keep their names but apply to whichever provider is
selected — renaming the keys would orphan every existing settings file for no user-visible gain,
the same trade `FlyoutAnimationsEnabled` makes. Nothing may gate a sync trigger on `SftpHost`;
use `LauncherSyncService.IsConfigured`. See [sync.md](sync.md).

Both have `On…Changed` handlers that call `AutoSyncService.RestartPeriodicTimer()`. This is the
side-effect case the section above describes, and it was missing: the timer was only ever built
at startup, so switching auto-sync on started no timer, switching it off left the old one
running, and a changed interval took effect on the next launch. **A setting that configures a
timer, a window, or anything else built once at startup needs a change handler, or it silently
does nothing until restart.**

## Sync safety: `LaunchersModifiedUtc`

Records when launchers were last changed locally without having been uploaded; `default` means
nothing is pending. Set by `AutoSyncService.NotifyLaunchersChanged`, cleared once an upload
succeeds.

It exists because the in-memory `_hasPendingLocalItemChanges` flag **did not survive a restart**.
Quitting between a change and its debounced upload meant the next startup download saw no pending
work and applied the server's older copy over it — losing changes that had been saved minutes
before.

**Every automatic download is guarded by it.** The periodic sync previously skipped the
newer-local check entirely (only startup passed `isStartupSync: true`), so the server overwrote
local launchers every few minutes regardless of how recently they had been edited. Only an
explicit user-initiated download passes `force: true`.

## Upgrade tracking

`LastRunVersion` (plain `string`, serialized) records the app version that last wrote the
settings file. `SettingsManager.StampVersion()` sets it on **all three** load paths (JSON,
legacy XML migration, and the defaults fallback).

It is consumed by `SettingsManager.RaiseUpgradeNotices`, which turns on one-time notices for
people upgrading — currently `ShowWebLauncherNotice`, the Home page banner introducing web
launchers in 1.25.0. **Call it before `StampVersion`**, which overwrites the value being compared
against. Two rules, both load-bearing:

- **Only fire when a settings file already existed.** A fresh install has never seen whatever
  is being announced, so the notice is pure noise. `RestoreSettings` reaches the defaults path
  only when no file was found.
- **Treat a missing version as older than any threshold.** No file in the wild carries the
  field yet, so `previous == null` is what identifies an upgrader.

```csharp
if (IsOlderThan(previousVersion, WebLauncherVersion))
    _current.ShowWebLauncherNotice = true;
```

`RestoreSettings` calls it from the JSON and legacy-XML paths only — the two that found an
existing file. The defaults path deliberately does not.

The flag itself defaults to `false` and is switched *on* by the upgrade check. That direction is
what makes it dismissible at all: dismissing writes `false`, `WhenWritingDefault` drops the key,
and the notice stays gone. Phrased the other way round it could never be turned off — see the
`WhenWritingDefault` section above.

Verified across all three paths: upgrading from 1.24.2 raises it, a second launch on 1.25.0 does
not re-raise it, and a fresh install with no settings file never sees it.

Prefer making the new behaviour discoverable in place over announcing it. The "item editing has
moved" notice was removed once the Launchers page gained an **Edit items** entry that opens the
flyout directly — an action beats an explanation, and it needs no version tracking at all.

## Property Categories

Group related properties together with comment headers matching existing style:
- Appearance & Behaviour
- Taskbar Widget
- Launchers
- Sync destination
- SFTP Sync

`UserSettings` appearance/behaviour properties currently include `AppTheme`, `Startup`, `ShowWebLauncherNotice`, and `FlyoutAnimationsEnabled` (default `true`, controls whether `FlyoutWindow` uses animated open/close transitions).
