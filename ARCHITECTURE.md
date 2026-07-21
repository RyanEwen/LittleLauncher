# Architecture — Little Launcher

## High-level flow

```
App.xaml  →  MainWindow (invisible, owns tray icon)
                ├── FlyoutWindow (launcher popup)
                └── SettingsWindow (WinUI 3 + NavigationView)
                      ├── HomePage
                      ├── LaunchersPage
                      ├── SyncPage
                      ├── SystemPage
                      └── AboutPage
```

## Flyout edit mode

All launcher item editing happens in the flyout. There is no in-settings items editor; the
former `LauncherItemsPage` was removed and its logic consolidated into `FlyoutWindow`.

A hover-revealed pencil in the flyout enters edit mode (`FlyoutWindow.EditMode.cs`), which:

- reveals a toolbar row (add item, add group, add/remove column via overflow, done),
- tints group containers and gives empty groups a drop target,
- enables drag-and-drop reordering (disabled outside edit mode),
- shows a pencil overlay on the hovered item/group,
- restricts item context menus to launcher-level entries outside edit mode,
- pins the flyout open so it cannot dismiss itself while editing.

**Geometry contract:** edit mode may grow the flyout''s *height* but must never change its
width or the size of any item or group. Width is derived arithmetically by `GetFlyoutWidth()`
from fixed column widths and is never measured, so chrome wider than the content is clipped —
hence icon-only toolbar buttons sized for the narrowest case (small-icon mode). Per-item
affordances are `Background`/`CornerRadius` only, which have no layout effect, so
`PackedIconPanel` cannot repack rows.

Height is normally computed arithmetically, because forcing a layout pass on a *hidden* WinUI
window is a hard crash (see the comment in `MeasureContentHeight`). Edit-mode toggles are the
exception: the window is visible then, so `ResizeForEditChrome()` measures `ContentStack`
directly and sizes to the real value.

**Source of truth:** `_launcher.Items` is authoritative and `_columnLists` is a derived view.
Structural edits (add/remove/rename) mutate the flat list and let the rebuild regenerate
columns — they must *not* call `SyncColumnsToFlatList()`, which clears `_launcher.Items` and
would resurrect anything just removed. Drag-and-drop is the opposite: it mutates `_columnLists`
and flushes back via `PersistFlyoutReorder()`.

Drag-and-drop supports cross-column and cross-group moves. All ListViews use
`CanDragItems="True"` with custom `DragOver`/`Drop` handlers — WinUI 3''s `CanReorderItems` is
intentionally avoided because it takes full internal control of drag events and cannot support
cross-collection moves. See [.claude/docs/drag-drop.md](.claude/docs/drag-drop.md).

## Owned editor windows

Editing UI opened from the flyout uses standalone windows, not `ContentDialog`: a dialog
renders inside its host window''s content area and cannot overflow the HWND, and the flyout is
frequently narrower and shorter than the form.

| Window | Purpose |
|---|---|
| `ItemEditorWindow` | Add/edit a launcher item; returns `ItemEditorResult` (Cancelled/Saved/Deleted) |
| `TextPromptWindow` | Single-field prompt (group name) |
| `LauncherSettingsWindow` | Per-launcher settings; opened from both the flyout and the Launchers page |

All three share `WindowChrome` for the app icon and a themed custom title bar (a default WinUI
title bar does not follow `RequestedTheme`). The flyout **drops its always-on-top flag** while
one is open — owner relationship alone does not beat a topmost owner — and closes any open
editor when edit mode ends, so an orphaned window cannot commit into a launcher the user has
navigated away from.
## Launch modes

By default, launching the app opens the Settings window. Silent mode (tray icon only, no Settings window) is used for Windows startup and companion exe cold-starts:

| Scenario | How it launches | Settings window? |
|---|---|---|
| Install / update / Start Menu / double-click exe | No special args | Yes |
| Windows startup (unpackaged) | Registry Run key with `--silent` | No |
| Windows startup (MSIX) | StartupTask → `ExtendedActivationKind.StartupTask` | No |
| Companion exe cold-start | `LittleLauncher.exe --silent` | No |
| Second instance (app already running) | Signals first instance via `PostMessage` → shows Settings | Yes |

`SystemPage` manages startup through the install-type-specific mechanism: unpackaged builds write the HKCU Run key, packaged builds use the manifest `StartupTask`, and packaged startup cleanup removes any stale unpackaged Run entry so the Store app wins after install-type switches.

## Settings persistence

- `UserSettings` (the ViewModel) is an `ObservableObject` with `[ObservableProperty]` attributes.
- `SettingsManager` (fully static) serialises it to JSON at `%AppData%\LittleLauncher\settings.json`.
- On first load, migrates from legacy `settings.xml` (XmlSerializer) to `settings.json` (System.Text.Json), renaming the old file to `.bak`.
- On startup, `RestoreSettings()` deserialises and calls `CompleteInitialization()` to enable change handlers.
- `SaveSettings()` is called on settings window close and after SFTP download.

## Launcher item icons

`FaviconService.FetchMissingItemIconsAsync(items)` is the **single pipeline** for fetching launcher item icons. It iterates the items and, for each one missing a valid local icon, fetches a favicon (websites), extracts a shell icon (PWAs via `IShellItemImageFactory`), or extracts an exe icon (apps). All entry points use this one method:

| Trigger | Caller |
|---|---|
| App startup (missing icons on disk) | `MainWindow.FetchMissingIconsOnStartupAsync()` |
| SFTP sync download | `SftpSyncService.DownloadLaunchersAsync()` |
| File import (Launchers page card menu) | `LauncherBulkOps.ImportItemsAsync()` |
| Manual add/edit | `DoFetch()` in add/edit dialog (calls `FaviconService` directly for the single item) |
| PWA add | PWA combo selection handler (`FaviconService.GetBestPwaIconAsync()`: prefers a real site/manifest icon, rejects off-origin login redirects, then falls back to the installed shell icon) |

After bulk icon changes, callers invoke `FlyoutWindow.InvalidateItems()` so the flyout rebuilds its containers on the next toggle.

`FaviconService.RefreshStaleItemIconsAsync(items)` complements the missing-icon pipeline by **re-fetching auto-fetched icons** whose cached file is older than 7 days (`FaviconService.IconMaxAge`), so favicons, app icons, and PWA icons track upstream changes. Custom user-chosen icons are never touched (auto-fetched icons are identified by living in the `favicons` cache folder), and a failed fetch keeps the existing file. It runs at startup (inside `FetchMissingIconsOnStartupAsync`) and on a daily timer in `MainWindow` while the app stays resident in the tray. Because refreshed files keep the same path, item-icon `BitmapImage` loads use `BitmapCreateOptions.IgnoreImageCache` to bypass WinUI's per-URI decoded-image cache.

## SFTP sync

`SftpSyncService` provides static async methods:
- `UploadLaunchersAsync()` — serializes all launchers to JSON and uploads `launchers.json` via SFTP.
- `DownloadLaunchersAsync()` — downloads `launchers.json`, deserializes, replaces the local launchers collection, fetches missing icons via the unified pipeline, and saves. Falls back to legacy `launcher-items.xml` if `launchers.json` doesn't exist.
- `TestConnectionAsync()` — verifies SSH connectivity and SFTP access.

### Shared launcher sync

Individual launchers can be shared via local/network files or per-launcher SFTP connections (separate from the global sync). `Launcher.SharedSyncMode` controls the transport: 0 = File (local or UNC path), 1 = SFTP.
- **Owner** (`IsSharedOwner = true`): `ShareLauncherAsync()` pushes the launcher's items as `List<LauncherItem>` JSON.
- **Subscriber** (`IsSharedOwner = false`): `SyncSharedLauncherAsync()` pulls items (read-only).
- `VerifySharedLauncherAsync()` — validates the file/remote exists and is parseable (used before subscribing).
- `SyncAllSharedLaunchersAsync()` — batch syncs all shared launchers (owners push, subscribers pull). File-mode always syncs; SFTP-mode skips launchers without an auto-detectable SSH key.

The sharing UI lives in `LaunchersPage.xaml.cs`: "Share" button on unshared launcher cards, "Sync" and "Settings" buttons on shared cards, "Shared"/"Subscribed" badges, "Add Shared Launcher" subscribe dialog (with File/SFTP mode picker), and "Stop Sharing" via the share dialog's secondary button.

`AutoSyncService` manages automatic sync triggers:
- Downloads launchers on startup, then syncs shared launchers.
- Debounced upload (3 s) when items change.
- Periodic download on a configurable interval, followed by shared launcher sync.
- Automatic downloads are skipped while local launcher item edits are pending upload, so a periodic pull cannot overwrite newer flyout/editor changes before the debounced upload completes.

Supports both private-key (`PrivateKeyFile`) and password-based authentication.

## Theme system

`ThemeManager` controls the app theme via WinUI 3's `ElementTheme` system:
- Sets `RequestedTheme` on the root `FrameworkElement` of each window.
- `IsDarkTheme()` reads the system foreground colour from a cached `UISettings` instance to detect light/dark mode.
- Theme 0 = system default, 1 = Light, 2 = Dark.

## Backdrop

- **SettingsWindow** uses `MicaBackdrop` (WinUI 3 built-in).
- **FlyoutWindow** uses a transparent backdrop for seamless integration.

## FlyoutWindow

The flyout popup dismisses when focus is lost via the WinUI `Activated` event (`Deactivated` state). It also toggles closed when the user clicks the same tray icon or pinned taskbar shortcut while that launcher's flyout is already open. It uses `WS_EX_TOOLWINDOW` to stay out of Alt-Tab and `OverlappedPresenter.CreateForContextMenu()` for borderless always-on-top presentation. When the `UserSettings.FlyoutAnimationsEnabled` setting is on, the fully rendered flyout window is moved a short distance from its anchored edge with eased `SetWindowPos` steps, so the whole popup slides as one surface instead of revealing its frame and contents in stages.

**Warm-up**: `FlyoutWindow.WarmUp()` creates one window per launcher at startup (and again whenever launchers change) so no flyout is built on the click that opens it. Creating a window is not enough on its own — WinUI only draws a window that has actually been visible, so each warmed-up flyout is briefly parked outside the virtual screen with `SWP_SHOWWINDOW`, given `PreRenderDurationMs` to compose its first frame, and then hidden. Without that first frame the DWM has nothing to present and the flyout's opening frames are a black rectangle. Every path that shows or hides a flyout calls `EndPreRender()` first, so a window that is still warming up is never mistaken for one that is open on screen.

For pinned taskbar launches, `MainWindow` now tries to resolve the actual taskbar button bounds via UI Automation before showing the flyout. When that succeeds, the flyout anchors to the center of the launcher button instead of the companion exe's fallback cursor coordinates, keeping both mouse and touch launches centered directly over the icon.

**Direct reordering** (edit mode only): FlyoutWindow uses a custom drag-drop implementation instead of WinUI `CanReorderItems`. Each column is rendered as a drag-enabled `ListView`, and grouped sections render nested child `ListView`s. In icon mode, consecutive ungrouped items are wrapped into synthetic groups for display so icon tiles can still be reordered in a wrapping `ItemsWrapGrid`. Drops use custom insertion indicators (horizontal for list sections, vertical for icon grids), then sync the per-column view back into the launcher's flat `Items` collection, save settings, notify auto-sync, and invalidate tray/flyout surfaces.

**Multi-column & multi-view layout**: The flyout renders items into a horizontal `ColumnsPanel` (a `StackPanel`). Each `LauncherItem` with `IsColumnBreak = true` starts a new column. The display mode is controlled by `Launcher.ViewMode`:
- **Icon view** (ViewMode = 0, default): Each column is a `GridView` of grouped sections whose top-level layout is handled by the custom `PackedIconPanel`. Real groups render a heading plus a nested icon-grid child `ListView`, while consecutive ungrouped items are wrapped into synthetic groups so they render in the same wrapping grid surface. `Launcher.IconModeIconsPerRow` controls the maximum icons shown across each row (default 3, configurable from 1 to 12). Top-level groups use their visible child count as a width span so narrower groups can share a row beside wider groups, while the packed panel keeps row heights based on the measured group cards instead of fixed slot heights. Dragging near the flyout's left or right edge snaps the layout wider or narrower by whole icon columns without opening settings.
- **List view** (ViewMode = 1): Each column is a drag-enabled `ListView` of top-level items and groups. Real groups render a heading plus a nested child `ListView`, so items can be dragged within groups, out to the top level, or between columns while keeping the flyout visuals close to the editor.

`RebuildColumnsPanel()` rebuilds all columns (icon grid or ListView) from scratch whenever items change. Window width scales per column: 175 px for list view, or a dynamic icon-mode width derived from the configured icons-per-row value.

**Right-click context menu**: Right-clicking empty space in the flyout shows a `ContextFlyout` with launcher/settings shortcuts. Right-clicking an item opens an item menu with Move up/down, Move to…, Edit, and Remove actions, while in edit mode. Outside edit mode the item menu shows only launcher-level entries.

## Companion exe (`LauncherShortcut`)

`LittleLauncherFlyout.exe` is a tiny companion binary pinned to the taskbar. Clicking it sends a `PostMessage` to the main app to show the flyout, then exits. Because it launches on every click, startup latency is critical:

- **Release builds** use Native AOT (`dotnet publish`) producing a single ~1.6 MB native binary with no .NET runtime dependency.
- **Debug builds** copy the framework-dependent output for fast iteration (startup is slower but build is faster).
- **Never add heavy dependencies** (NuGet packages, large frameworks) to the `LauncherShortcut` project — it must remain a minimal P/Invoke-only program.
- **Never run expensive work** (icon regeneration, shell notifications, file I/O) synchronously in the main app's `_wmShowFlyout` WndProc handler — it blocks the flyout from appearing.

### Companion exe deployment

At startup, `EnsureFlyoutShortcut()` copies the companion exe to the external helper directory returned by `GetPhysicalAppDataDir()` and writes a `main-exe-path.txt` breadcrumb alongside it so the helper can launch the main app with `--silent` if `FindWindow` fails. In packaged builds it also mirrors the helper into the real shared `%AppData%\\LittleLauncher\\` path so old unpackaged launcher pins stop cold-starting stale debug/WiX builds after an install-type switch. No Start Menu flyout shortcut is created anymore.

## MSIX packaging

`LittleLauncherMSIX/build-msix.ps1` produces an MSIX package for Microsoft Store distribution or sideloading. Key details:

- **Publishes with `-p:WindowsPackageType=MSIX`** to suppress the unpackaged-only auto-bootstrapper (`MICROSOFT_WINDOWSAPPSDK_BOOTSTRAP_AUTO_INITIALIZE`), which fails in a packaged context.
- **Declares `<PackageDependency>`** on `Microsoft.WindowsAppRuntime.1.8` so the framework package provides WinRT activation factories.
- **Copies compiled XAML (.xbf)** files manually from the RID build directory to the layout — `dotnet publish` omits them.
- **Version and architecture** are stamped from `Directory.Build.props` into the manifest at build time (`VERSION_PLACEHOLDER`, `ARCH_PLACEHOLDER`).
- **Image assets** in `LittleLauncherMSIX/Images/` use standard MRT naming qualifiers (e.g. `.scale-200.`, `.targetsize-48.`) and are indexed into `resources.pri` by `makepri`.
- **Companion exe** is deployed at startup to the external helper directory, and packaged builds also mirror it into the shared raw `%AppData%\\LittleLauncher\\` path for backward compatibility with old launcher pins. See "Companion exe" section above.
- **`-NoSign` flag** skips all signing for Store uploads (Microsoft re-signs during ingestion). Without `-NoSign`, the script signs with a self-signed dev cert or a trusted PFX.
- **Update checks and toast notifications** are disabled in MSIX builds — the Store handles updates. The GitHub-based update UI on Home/About pages is hidden.
