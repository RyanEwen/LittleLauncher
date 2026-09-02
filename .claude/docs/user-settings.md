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
- `WebUrl` (`string`) — **legacy**, read once by `MigrateWebModel` and cleared; the page a web
  launcher opened before the bookmark model
- `WebBookmarks` (`ObservableCollection<WebBookmark>`) — the bar. A `WebBookmark` with `IsFolder`
  (`[ObservableProperty]`) is a folder and holds `Children` (a plain property, like
  `LauncherItem.Children`); folders nest without limit and `Flatten()` is how a surface with no
  nesting of its own reads them. `CopyInto` copies the collection wholesale, so nesting travels
  without further work
- `WebShowBookmarkBar` (`bool?`) — **nullable on purpose**, because three states are needed and a
  `bool` has two: on, off, and never chosen. `WhenWritingDefault` omits only `null`, so an explicit
  `false` is written and survives, and `null` falls back to the old rule (a second bookmark shows the
  bar). Read it through `Launcher.ShowsBookmarkBar`, never directly. **A plain `bool` plus a
  one-time seeding pass was tried first and is the thing this replaces** — the seed ran where the
  bookmarks were not in hand, and its "already migrated" marker travelled over sync, so a download
  from a machine that had never seen the setting reset both and switched a bar off again on every
  restart. A nullable needs no marker and no seeding pass, and cannot be undone by a payload written
  before it existed. Do not read `ShowsBookmarkBar` to mean "this launcher holds several sites" —
  that is `Launcher.HoldsSeveralSites`, and the two were one fact only while the bar appeared on its
  own at the second bookmark
- `WebHomeUrl` (`string`) — the page a web launcher opens, as a setting of its own rather than a
  position in the bar. Empty falls back to the first bookmark, which is what every file written
  before this existed relies on, so the CLR default is the compatible direction under
  `WhenWritingDefault`. Read it through `Launcher.WebAddress`, never directly.
  **An earlier version made the address *be* the first bookmark and this reverses that** — see
  the remarks on `WebAddress` for why the original objection does not apply to a home URL, and
  note that a bookmark holding the same URL is an ordinary bookmark that nothing tries to merge
  or notice
- `WebFlyoutWidth` / `WebFlyoutHeight` (`int`, DIPs) — **0 means unset**; read them through
  `ResolvedWebFlyoutWidth` / `ResolvedWebFlyoutHeight`
- `WebZoomPercent` (`int`) — 0 means 100%; read `ResolvedWebZoomPercent` (to show or step it) or
  `ResolvedWebZoomFactor` (to apply it). Any value in `MinWebZoomPercent`…`MaxWebZoomPercent` is
  legal, but the pickers offer `Launcher.WebZoomLevels` and the zoom keys step along it, so a
  surface listing levels must build its list with `Launcher.WebZoomLevelsIncluding(current)` or
  it will have nothing to select for a launcher holding an off-ladder value
- `WebHiddenPolicy` (`int`) — `WebHiddenPolicies.UnloadWhenIdle` (0, default) / `Suspend` (1) / `KeepRunning` (2)
- `WebIdleUnloadMinutes` (`int`) — 0 means the default; read `ResolvedWebIdleUnloadMinutes`
- `WebReloadOnShow` (`bool`) — re-fetch on every open
- `WebShowAddressBar` (`bool`) — keep an address bar under the flyout's header. Defaults `false`,
  which is both the safe direction under `WhenWritingDefault` and the behaviour that shipped
  first. Off does not mean unreachable: the header carries a button that reveals the bar for the
  rest of that visit, and **that reveal is window state, never written back to this property** —
  see [web-launchers.md](web-launchers.md)
- `WebSharedProfile` (`bool`) — pool cookies and logins in `WebProfiles\Shared` with every other
  launcher that sets it, instead of a private per-launcher folder. **What a new launcher gets**, but
  set to `true` at *creation* rather than as a model default: `WhenWritingDefault` omits a property
  holding the CLR default, so a `= true` initialiser would read as `true` for every launcher that
  never stored the field — silently moving existing launchers onto a profile they were never signed
  in to, and making `false` impossible to persist. This one is not merely cosmetic to get wrong; it
  decides which folder a launcher's cookies live in. Same treatment as `ShowTitle`
- `WebAnchor` (`int`) — **the whole answer to "where does this open?"**: `WebAnchors.Tray`
  (0, default), one of the nine corner/edge/centre positions, or `WebAnchors.LastPosition` (10) for
  "wherever you last dragged it". Those are three mutually exclusive answers, which is why they are
  one setting; see `WebAnchors.LastPosition` for the dead cell the old `WebRememberPosition` flag
  produced beside it. Changing it to anything but `LastPosition` clears `WebFlyoutPosition`, so the
  new choice actually takes effect
- `WebRememberPosition` (`bool`) — **legacy**, and deliberately not `[ObservableProperty]`. Whether a
  dragged position outlived the visit. `MigrateWebModel` turns it into `WebAnchors.LastPosition` and
  clears it. Still synced, so an older build's launcher still says so when it arrives
- `WebLockSize` (`bool`) — hold the flyout at its configured size, so dragging its edges only
  lasts while it is open. **Surfaced as "Remember Size", which is on by default — this property is
  its inverse**, which is the only phrasing that survives `WhenWritingDefault`; a `bool` defaulting
  to `true` cannot be turned off. `WebFlyoutWidth`/`WebFlyoutHeight` are then set deliberately, in
  launcher settings, and a drag cannot overwrite them
- `WebLinksInBrowser` (`bool`) — hand a link that asks for a new window to the default browser
  instead of opening it in a tab of the flyout. Defaults `false` (a tab), which is both the new
  behaviour and the direction that survives `WhenWritingDefault` — hence "in browser" rather than
  "in tabs". In launcher settings under **Advanced**; it was in the flyout's "…" menu and nowhere
  else, which is neither discoverable nor a per-moment decision. See
  [web-launchers.md](web-launchers.md) for why the browser's own `NewWindowRequested.NewWindow` is
  used rather than the URI
- `WebAlwaysShowTabs` (`bool`) — keep the tab strip on screen with only one tab. Defaults `false`,
  which is not "no tabs": the strip appears on its own as soon as there is a second one, so this
  reads as "keep it" and is what a launcher used as a small browser wants
- `WebBookmarkIconsOnly` (`bool`) — hide the labels in the bookmark bar, leaving just the
  favicons; names become tooltips rather than being discarded. Only meaningful with a bar, and edited in
  the Bookmarks section rather than Advanced, because Advanced is shown for single-address
  launchers too. It is part of the bar's rebuild signature, so toggling it re-renders
  rather than handing back buttons built for the other mode
- `WebPinFlyout` (`bool`) — **two readings, one flag.** As a flyout: stay open when focus is lost.
  Under `WebRegularWindow`: keep the window always on top. Each is meaningless in the other mode —
  a flyout is always-on-top by nature, a regular window never self-dismisses — so a second property
  would only ever be inert wherever the launcher actually is. The header's pin button toggles it and
  re-labels itself for the current mode
- `WebWindowAutoHide` (`bool`) — under `WebRegularWindow`, dismiss on focus loss like a flyout while
  keeping the taskbar button and switcher entry. **Presentation and dismissal are separate axes**;
  the dismissal guards read the `StaysOpenAsWindow` helper rather than `WebRegularWindow` directly.
  Defaults `false` (stay open)
- `WebTaskbarClickCloses` (`bool`) — under `WebRegularWindow`, close on a taskbar-button click
  instead of minimizing. Defaults `false` (minimize), which is both what an ordinary app window does
  and the direction that survives `WhenWritingDefault`
- `PinAumid` (`string`) — the AppUserModelID stamped on this launcher's pinned taskbar button,
  written at pin time. **Not web-specific** despite sitting with the web settings: any launcher can
  be pinned, and only regular-window mode happens to read it today. It exists because the AUMID
  cannot be recovered afterwards for every pin — see [web-launchers.md](web-launchers.md)
- `WebRegularWindow` (`bool`) — present the launcher as an ordinary app window instead of a flyout:
  taskbar button (with the running indicator on its pin), task-switcher entry, not always on top,
  and no dismiss-on-focus-loss. Defaults `false`. **It is deliberately one setting naming a window
  kind rather than a "show in taskbar" toggle** — the taskbar button and the Alt-Tab entry are the
  same switch and cannot be separated, which was measured four ways; see
  [web-launchers.md](web-launchers.md)
- `WebSessionTabs` (`List<string>?`) / `WebSessionActiveTab` (`int`) — the pages this launcher had
  open when it was last used, restored on its next **open** (never at startup, which is what keeps
  the resource contract intact). Plain auto-properties, not `[ObservableProperty]`: nothing binds to
  them. **Deliberately not synced**, for the reason `WebFlyoutPosition` is not — a set of open tabs
  is what one machine was doing, not a preference about the launcher. See
  [web-launchers.md](web-launchers.md)
- `WebBookmarks` (`ObservableCollection<WebBookmark>`) — **the launcher's content**, not an extra:
  the first entry is the address it opens and the rest are the bar. `WebBookmark`
  (`Models/WebBookmark.cs`) is `Name` + `Url` + `IconPath` + `IconsOnly`, observable because the
  icon arrives after the bookmark does. `IconsOnly` collapses **that** bookmark to its icon; the
  launcher-wide `WebBookmarkIconsOnly` does it to all of them and wins where the two disagree
- `WebUrl` (`string`) — **legacy**. The single address a web launcher used to hold, before one
  address and a bar of them became the same thing. `MigrateWebModel` turns it into the first
  bookmark and clears it; nothing else should read it. Still synced, because a machine on an older
  build goes on writing it
- `WebDefaultBookmarkUrl` (`string`) — **legacy**, and deliberately not `[ObservableProperty]`:
  which bookmark a bar launcher opened on. `MigrateWebModel` moves that bookmark to the front and
  clears it, because position is the answer now — one the user can see and drag
- `IsWebLauncher` / `ShowsBookmarkBar` / `WebAddress` — `[JsonIgnore]` conveniences.
  **`ShowsBookmarkBar` is `WebBookmarks.Count > 1` and is deliberately not backed by a setting:**
  one bookmark is the launcher's address and there is nothing to pick between, two is a choice.
  Adding the second page and wanting somewhere to click it are the same act, so a toggle would be
  a second step that is useless without the first.
  `MigrateWebModel()` is the one-way door onto this model and runs both on load
  (`SettingsManager.NormalizeAllGlyphs`) and on every sync merge (`LauncherPayload.MergeInto`)

### Web launcher settings that live on `UserSettings`, not on `Launcher`

These three configure web launchers but are **app-wide**, because what they govern is scoped to a
WebView2 *profile* rather than to a launcher, and most launchers share one profile. A per-launcher
copy would be several names for one thing — and, for the password one, several answers to a question
the profile can only answer once.

- `BrowserExtensions` (`List<BrowserExtension>?`) — **app-wide, not per launcher.** The extensions
  loaded into every web launcher's profile as its browser starts. App-wide because extensions belong
  to a WebView2 *profile* and most launchers share one, so a per-launcher list would be several names
  for one thing. It replaced a bare `BrowserExtensionFolders: List<string>?`, which could say where a
  copy was and nothing about what it *is* — so nothing could be synced and nothing could be shown in
  a list before it was fetched. `BrowserExtension` (`Models/BrowserExtension.cs`) is `Id` (the Chrome
  Web Store id, empty for one added from a folder) + `Name` + `Folder`. **`Folder` must not be
  `[JsonIgnore]`**: it was, to keep it out of the sync payload, and since local settings use the same
  class that dropped it from disk too — every extension came back after a restart with an empty path
  and vanished. `BrowserExtensionService.Portable()` is what keeps it out of the payload, by
  projecting id and name into fresh objects
- `PinnedBrowserExtensions` (`List<string>?`) — which extensions get a header button of their own
  beside the puzzle menu, by store id or (for a local-only one) name. Absent means none pinned, which
  is the shipped behaviour and the state `WhenWritingDefault` writes as nothing
- `ProfilesWithoutPasswordManager` (`List<string>?`) — the profiles where web launchers should not
  offer to save logins or fill them in, for when a password manager extension is doing the job.
  **Keyed by profile** (`"Shared"` or a launcher id, matching the folder names under `WebProfiles`),
  because saved logins belong to a profile: every launcher sharing one shares the answer, and a
  launcher with a private profile gets its own. The platform scopes it neither way —
  `IsPasswordAutosaveEnabled` is per browser instance — so this is what decides. A list of the
  profiles where it is *off* rather than a flag per profile, so the default stays the absent case.
  Read when a browser is created, so `WebFlyoutWindow.ReloadProfile` is what makes a change immediate

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

`UserSettings` appearance/behaviour properties currently include `AppTheme`, `Startup`, `ShowWebLauncherNotice`, `FlyoutAnimationsEnabled` (default `true`, controls whether `FlyoutWindow` uses animated open/close transitions), and `DisableWebLauncherShortcuts`.

`DisableWebLauncherShortcuts` (`bool`, default `false` = shortcuts on) controls the per-web-launcher
Start Menu group that makes them findable from Start search and PowerToys Command Palette. **Stored
in the negative, shown in the positive** ("Start Menu Shortcuts") — the third example of that
bargain after `Launcher.WebLockSize`, and for the same reason: a `bool` defaulting to `true` cannot
be turned off here. The inversion lives in `SystemPage`'s code-behind, not in the model. Its
`On…Changed` handler re-syncs immediately, since the shortcuts are built once at startup and would
otherwise not appear (or disappear) until the next launch — the "a setting that configures
something built at startup needs a change handler" rule above.
