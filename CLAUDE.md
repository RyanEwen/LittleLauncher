# Little Launcher — Claude Code Instructions

## Project overview

Little Launcher is a .NET 10 WinUI 3 desktop application (unpackaged) that provides a system-tray launcher with a flyout popup for shortcuts. It also syncs settings to a remote server via SSH/SFTP.

## Architecture

- **Single-instance app** enforced via a named `Mutex` ("LittleLauncher"). A second launch signals the first instance via `PostMessage` with registered window messages (`LittleLauncher_ShowFlyout_{launcherId}`, `LittleLauncher_ShowSettings`).
- **Launchers** — users define one or more named `Launcher` objects (model: `LittleLauncher.Models.Launcher`). Each launcher has its own `Items` collection, tray icon (mode + optional custom path), and `NIconHide` visibility flag. Settings include a `Launchers: ObservableCollection<Launcher>`. On first run with old settings, the legacy `LauncherItems` list is migrated into a "Default" launcher.
- **Launcher kinds** — `Launcher.Kind` decides what the tray icon opens: `LauncherKinds.Items` (0, the default — a `FlyoutWindow` of launcher items) or `LauncherKinds.Web` (1 — a `WebFlyoutWindow` showing `Launcher.WebUrl`). **Never call `FlyoutWindow.Toggle` directly**: `Windows/LauncherPanels.cs` is the single place that resolves a launcher's kind, and every tray click, pinned shortcut and settings entry point routes through it.
- **MainWindow** is invisible (moved off-screen, 1×1). It owns one native tray icon per launcher via `Shell_NotifyIcon` (stored in `_trayIcons: Dictionary<string, TrayIconEntry>`). Uses `WS_EX_TOOLWINDOW` to hide from Alt-Tab.
- **FlyoutWindow** is a popup that displays launcher items. Supports two view modes per launcher: list view (icon + name side by side) and icon grid view (larger icon with name below, configurable icons-per-row wrapping grid). One instance is maintained per launcher (`_instances: Dictionary<string, FlyoutWindow>`), created up front by `WarmUp()` and parked off the virtual screen to compose its first frame — a window WinUI has never drawn presents a black rectangle when it is first shown. **Dismissing parks the window off the virtual screen rather than hiding it** (`ParkOffScreen`); `ShowWindow(SW_HIDE)` let WinUI release the composition surfaces, so the next cold open spent ~100ms re-rasterising every text run and item icon and the flyout slid in as an empty rectangle with the items popping in at the end. Because the window is therefore visible in the Win32 sense for the life of the app, **`_isOpen` — not `WS_VISIBLE` — is the test for "the flyout is open"**. Shown from a launcher's tray icon click, positioned above the taskbar, and when `UserSettings.FlyoutAnimationsEnabled` is on the full rendered window slides in and out from its anchored edge using short eased position updates, dismissed on focus loss or Escape. In icon mode, top-level groups are arranged by the custom `PackedIconPanel`, which lets narrower groups share a row beside wider groups while keeping row height based on the measured group content, and dragging near the flyout's left or right edge snaps the layout wider or narrower by whole icon columns and persists `Launcher.IconModeIconsPerRow` immediately.
- **Flyout edit mode** (`Windows/FlyoutWindow.EditMode.cs`) is where **all** launcher item editing happens. A hover-revealed pencil, the flyout's context menu, or **Edit items** on a launcher card enters it. A toolbar floats in its own window (`EditToolbarWindow`) above the flyout with add item/group/column, launcher settings and Done; group containers are tinted, empty groups and columns become drop targets, hovering shows a pencil overlay, the window border takes the accent colour, and drag-and-drop reordering and edge resize are enabled (both disabled outside edit mode). An empty launcher shows placeholder text explaining how to add items. Edit mode pins the flyout open — which is also what lets it accept **drops from outside the app** (`Windows/FlyoutWindow.ExternalDrop.cs`): files, folders, shortcuts and browser links dragged in from File Explorer, the desktop or a browser become items at the hovered position, mapped by `Services/DroppedItemFactory.cs`. The Windows 11 Start Menu is not a usable drag source — see [.claude/docs/drag-drop.md](.claude/docs/drag-drop.md). **Geometry contract:** it may grow the flyout's height but must never change its width or the size of any item or group — see [.claude/docs/drag-drop.md](.claude/docs/drag-drop.md). Item/group edits use owned windows (`ItemEditorWindow`, `TextPromptWindow`, `LauncherSettingsWindow`) rather than `ContentDialog`, which cannot overflow the flyout's HWND.
- **WebFlyoutWindow** (`Windows/WebFlyoutWindow.cs`) is the flyout a web launcher opens: a borderless, always-on-top, tray-anchored window hosting a **WebView2** on the launcher's URL, with a slim header (back, forward, pin, launcher settings, reload, open in browser, close). It presents like `FlyoutWindow` but its resource model is the **opposite**: nothing is created until the user first opens it (it is deliberately excluded from `WarmUp`), and dismissing it collapses the control, calls `CoreWebView2.TrySuspendAsync()` and drops `MemoryUsageTargetLevel` to `Low`. Under the default `WebHiddenPolicies.UnloadWhenIdle` the whole browser is then closed after `Launcher.WebIdleUnloadMinutes`, so a hidden dashboard costs nothing at all. Each web launcher gets its own WebView2 user-data folder (`%AppData%\LittleLauncher\WebProfiles\{launcherId}`), so logins survive restarts and launchers stay signed in independently. The window is fully borderless (no non-client area), so it has no system sizing border: resizing is done with invisible XAML edge/corner grips that drive `SetWindowPos` and persist the dragged size onto the launcher. See [.claude/docs/web-launchers.md](.claude/docs/web-launchers.md).
- **SettingsWindow** is a WinUI 3 window with `MicaBackdrop`. It uses `NavigationView` with page-based navigation (Home, Launchers, Cloud Sync, Settings, About). The Launchers page shows launcher cards with a Settings button (opens `LauncherSettingsWindow`) and an Items row (item count + a bulk-ops menu: export, import, import bookmarks — the bookmark selector is searchable). Creating a new launcher auto-opens launcher settings. **There is no in-settings item editor** — all item editing happens in the flyout's edit mode.
- **Settings** are serialised to `%AppData%\LittleLauncher\settings.json` via `System.Text.Json`, managed by the fully static `SettingsManager`. On first load, migrates from legacy `settings.xml` (XmlSerializer) to JSON.
- **SftpSyncService** uses SSH.NET for async upload/download of all launchers (as `launchers.json`) to a configurable remote server. Also handles per-launcher shared sync: supports 1-way (owner pushes, subscribers pull) and 2-way (all participants push and pull, last save wins) modes via separate SFTP connections. `AutoSyncService` skips automatic downloads while local launcher item edits are still pending upload so periodic syncs do not overwrite newer local flyout/editor changes. Periodic/shared downloads refresh flyouts via `FlyoutWindow.InvalidateItems(force: false)` — a no-op sync then skips the rebuild instead of tearing down and re-creating every flyout's visual tree on a timer (that unnecessary rebuild churn drove a large container-leak; see [.claude/docs/drag-drop.md](.claude/docs/drag-drop.md)).
- **LauncherShortcut** (`LittleLauncherFlyout.exe`) is a companion exe pinned to the taskbar. It signals the main app via `PostMessage` with a launcher-specific message (`LittleLauncher_ShowFlyout_{launcherId}`) then exits. Accepts `--launcher {guid}`, `--name {name}`, `--icon {path}`, and `--pin` arguments (also supports legacy `--layout` for backward compat). In `--pin` mode, sets relaunch properties (`PKEY_AppUserModel_ID`, `RelaunchCommand`, `RelaunchIconResource`, `RelaunchDisplayNameResource`) on the MessageBox HWND for taskbar pinning identity. AUMID format: `LittleLauncher.Launcher.{guid}.{TickCount64}` (unique per pin attempt to bust per-AUMID icon cache). Release builds are Native AOT for instant startup — see the performance warning in `ARCHITECTURE.md`.

## Key namespaces

| Namespace | Contents |
|---|---|
| `LittleLauncher` | App, MainWindow, SettingsWindow |
| `LittleLauncher.Classes` | NativeMethods, ThemeManager, IconGallery, ShellIcons, WindowChrome |
| `LittleLauncher.Classes.Settings` | SettingsManager |
| `LittleLauncher.Controls` | PackedIconPanel |
| `LittleLauncher.Models` | LauncherItem (its `ToString()` is the accessible name for every flyout row), Launcher, WebBookmark, SshConnectionProfile |
| `LittleLauncher.Pages` | All settings pages, LauncherBulkOps (item export/import, bookmark import), BookmarkPicker (searchable single-bookmark chooser) |
| `LittleLauncher.Services` | SftpSyncService, AutoSyncService, FaviconService, UpdateService, AppPickerService, AppCatalog, BrowserCatalog, BookmarkImport, DroppedItemFactory |
| `LittleLauncher.ViewModels` | UserSettings, AppPickerEntry |
| `LittleLauncher.Windows` | FlyoutWindow (+ `FlyoutWindow.EditMode.cs`, `FlyoutWindow.ExternalDrop.cs`), WebFlyoutWindow, LauncherPanels, EditToolbarWindow, ItemEditorWindow, TextPromptWindow, LauncherSettingsWindow |

**Note:** The `LittleLauncher.Windows` namespace shadows the WinRT `Windows.*` namespace. Use `global::Windows.` prefix when accessing WinRT types (e.g. `global::Windows.Graphics.PointInt32`).

**Note:** `LittleLauncher.Models.Launcher` conflicts with other framework types. Use `using Launcher = LittleLauncher.Models.Launcher;` at the top of any file where disambiguation is needed.

## Topic-specific guidance (scoped instructions)

These guides hold detailed conventions for specific areas. Claude Code auto-loads the nearest `CLAUDE.md` when you work inside a subdirectory, and those nested files import the relevant guide(s) below. When working in a given area, read the matching guide:

| Guide | Read when working on | Files it governs |
|---|---|---|
| [.claude/docs/pinvoke.md](.claude/docs/pinvoke.md) | P/Invoke, Win32 interop, native signatures | `Classes/NativeMethods.cs` |
| [.claude/docs/user-settings.md](.claude/docs/user-settings.md) | Observable settings, Launcher model, serialization | `ViewModels/UserSettings*.cs`, `Models/Launcher.cs` |
| [.claude/docs/drag-drop.md](.claude/docs/drag-drop.md) | Flyout drag-and-drop and edit mode | `Windows/FlyoutWindow.xaml*`, `Windows/FlyoutWindow.EditMode.cs` |
| [.claude/docs/web-launchers.md](.claude/docs/web-launchers.md) | Web launchers, the WebView2 flyout, its resource policy | `Windows/WebFlyoutWindow.cs`, `Windows/LauncherPanels.cs` |
| [.claude/docs/xaml.md](.claude/docs/xaml.md) | WinUI 3 XAML, controls, localization, backdrops | `**/*.xaml` |
| [.claude/docs/icons.md](.claude/docs/icons.md) | App/tray/shortcut/window icons | `MainWindow.xaml.cs`, `SettingsWindow.xaml.cs`, `LaunchersPage.xaml*`, `HomePage.xaml.cs`, `FlyoutConverters.cs`, `FlyoutWindow.xaml*`, `IconGallery.cs`, `LauncherShortcut/**` |
| [.claude/docs/installer.md](.claude/docs/installer.md) | WiX MSI, MSIX packaging, install/upgrade | `Package.wxs`, `LittleLauncherSetup.wixproj`, `UpdateService.cs`, `build-msix.ps1`, `Package.appxmanifest` |
| [.claude/docs/versioning.md](.claude/docs/versioning.md) | Version bumps and releases | `Directory.Build.props`, `MainWindow.xaml.cs`, `Package.appxmanifest`, `Package.wxs`, `build-msix.ps1`, `.github/workflows/build-msix.yml` |

## Conventions

- Use `[ObservableProperty]` from CommunityToolkit.Mvvm for all bindable settings properties.
- Partial `On<Property>Changed` methods in `UserSettings` handle side-effects (theme changes, taskbar updates).
- An `_initializing` flag in `UserSettings` suppresses change handlers during deserialization.
- P/Invoke declarations live in `NativeMethods.cs`. Always use `using static LittleLauncher.Classes.NativeMethods;` imports.
- Use `[LibraryImport]` for new P/Invoke declarations; existing ones use `[DllImport]`.
- Pages are WinUI 3 `Page` objects navigated via `NavigationView`. No MVVM framework routing — just `TargetPageType` in XAML.
- String resources live in `Resources/Localization/Dictionary-en-US.xaml`. In code: `Application.Current.Resources.TryGetValue("KeyName", out object value)`.
- Use `CommunityToolkit.Mvvm.Input.RelayCommand` for ICommand implementations.
- **MSIX VFS rule:** Any file path referenced by external processes (shell `.lnk` files, companion exe) must use `MainWindow.GetPhysicalAppDataDir()`, not raw `Environment.GetFolderPath(ApplicationData)`. The latter is VFS-redirected inside MSIX. See [.claude/docs/icons.md](.claude/docs/icons.md) for details.

## Build

```bash
dotnet build LittleLauncher/LittleLauncher.csproj -c Debug
```

`Directory.Build.props` auto-detects the platform from `PROCESSOR_ARCHITECTURE` (ARM64 → ARM64, otherwise x64). To override: `-p:Platform=x64` or `-p:Platform=ARM64`.

Target: `net10.0-windows10.0.22000.0`, unpackaged (`WindowsPackageType=None`), platforms `x64` and `ARM64`.

Release builds AOT-publish the companion exe (`LauncherShortcut`) automatically. Debug builds copy the framework-dependent output for faster iteration.

## Dependencies

- Microsoft.WindowsDesktop.App (framework reference for `System.Windows.Automation` taskbar button detection)
- Microsoft.WindowsAppSDK 1.8.260209005 (WinUI 3)
- H.NotifyIcon.WinUI 2.4.1 (system tray)
- CommunityToolkit.Mvvm 8.4.0
- SSH.NET 2025.1.0
- NLog 6.1.1
- Microsoft.Data.Sqlite 9.0.3 (Firefox bookmark import from places.sqlite)
- Microsoft Edge WebView2 — the managed API arrives transitively with the Windows App SDK (no explicit `PackageReference`), and web launchers need the **Evergreen WebView2 Runtime** on the machine. It ships with Windows 11; `WebFlyoutWindow` surfaces an install prompt if `CoreWebView2Environment` cannot start.
- WixToolset.Util.wixext 5.0.2 (WiX util extension for installer close-application actions)

## Common tasks

- **Add a new settings page:** Create `Pages/FooPage.xaml` + `.cs`, add a `NavigationViewItem` in `SettingsWindow.xaml`, add any new string keys to `Dictionary-en-US.xaml`.
- **Add a new launcher feature:** Extend `LauncherItem` model, update `FlyoutWindow` to render it, update `ItemEditorWindow` for editing.
- **Add a new setting:** Add an `[ObservableProperty]` to `UserSettings.cs`. It will auto-serialize to JSON.
- **Add/extend icon gallery tabs:** Edit `Classes/IconGallery.cs` — add entries to `FluentIconCategories` or `EmojiCategories` arrays. The gallery supports Segoe Fluent Icons (PUA glyphs), emojis, and app color icons. Icon selection uses a pending-selection model with Confirm/Cancel buttons. Selected icons get an accent border. Color swatches override `ButtonBackgroundPointerOver`/`ButtonBackgroundPressed` to keep their color on hover. When opened, the gallery pre-selects the current icon (correct tab, color swatch, and icon button) so the user can change just the color without re-finding the icon.

## Launcher kinds

| Kind | `Launcher.Kind` | Tray click opens | Editing |
|---|---|---|---|
| Shortcuts | `LauncherKinds.Items` (0, default) | `FlyoutWindow` with the launcher's items | Flyout edit mode |
| Web page | `LauncherKinds.Web` (1) | `WebFlyoutWindow` on `Launcher.WebUrl`, or a bookmark bar when `WebUseBookmarks` is set | Launcher settings — address (typed or chosen from browser bookmarks) and size, with zoom/hidden-policy/reload/pin/browsing-data under **Advanced** |

A web launcher has no items, so item editing, sharing and bulk operations are hidden for it. See [.claude/docs/web-launchers.md](.claude/docs/web-launchers.md).

## Launcher item types

| Type | `IsWebsite` | `IsPwa` | `Path` | `Arguments` | Launch method |
|---|---|---|---|---|---|
| Website | `true` | `false` | URL | — | `UseShellExecute` or app-window mode |
| Application | `false` | `false` | exe path | optional args | `Process.Start(Path, Arguments)` |
| Progressive Web App | `false` | `true` | AUMID (e.g. `domain-HEX_hash!App`) | — | `explorer shell:AppsFolder\{Path}` |
| Heading | — | — | — | — | Not launchable (visual divider, renamed from Category) |
| Group | — | — | — | — | Not launchable (collapsible parent containing child items/headings via `Children` collection) |
| Column Break | — | — | — | — | Not launchable (splits the flyout into a new side-by-side column; `IsColumnBreak = true`) |

Groups have a `Children` (`ObservableCollection<LauncherItem>`) that holds nested items and headings. In the settings page, groups render as custom expand/collapse cards (StackPanel with `Tag="GroupRoot"` / `Tag="GroupChildren"`), not WinUI Expanders — this allows the entire group card to be a drag-and-drop source. `LauncherItem.IsExpanded` (`[JsonIgnore]`, defaults `true`) tracks the collapse state so it survives `RebuildColumns()` re-renders. In the flyout, the hierarchy is flattened for display.

Column breaks (`IsColumnBreak = true`) are structural dividers stored as sentinel items in the flat `Items` list. They cause both the flyout and the settings page to render items in side-by-side columns. In the settings page, `RebuildColumns()` splits the flat list at column break sentinels into per-column `ListView` controls (fixed 280px wide) within a dynamic `Grid` (`ColumnsPanel`). Column breaks are not displayed as inline items — they are invisible. Users add/remove columns via "Add Column" / "Remove Column" buttons. Changes sync back to the flat list via `SyncColumnsToFlatList()`. Created via `LauncherItem.CreateColumnBreak()`.

Item and group cards show a single `...` context menu button (hidden via `Opacity=0` / `IsHitTestVisible=false`, revealed on hover) that opens a `MenuFlyout` with Move up/down, Move to… (groups and other launchers), Edit, and Remove actions. The Move to… submenu lists available groups within the current launcher, plus all other launchers for cross-launcher moves.

PWAs are auto-detected by enumerating `shell:AppsFolder` for Chromium-registered app entries (AUMIDs matching `{domain}-{HEX}_{hash}!App`). Icons are fetched from the PWA domain via `FaviconService.FetchAndCacheAsync()`.
- **Release a new version:** Edit `<Version>` in `Directory.Build.props`, update fallback version in `Package.wxs`, commit, tag `vX.Y.Z`, push. The MSIX manifest version is auto-stamped by `LittleLauncherMSIX/build-msix.ps1`. See [.claude/docs/versioning.md](.claude/docs/versioning.md) for the full checklist.
- **Update the app icon:** Replace `Resources/AppIcons/Blue.png`, then regenerate `Resources/LittleLauncher.ico` from it (multi-resolution ICO: 16–256px). The `.ico` is committed — it's not auto-generated by the build. See [.claude/docs/icons.md](.claude/docs/icons.md).

## Documentation maintenance

**Documentation updates are part of the task — a task is not done until docs are updated.**

After completing any feature, bug fix, or structural change, review and update the affected documentation before considering the task done. This includes:

| What changed | Update these |
|---|---|
| New/removed service or class | `CLAUDE.md` (Key namespaces table), repo memory |
| New/changed settings property | [.claude/docs/user-settings.md](.claude/docs/user-settings.md), repo memory |
| New/changed P/Invoke | [.claude/docs/pinvoke.md](.claude/docs/pinvoke.md) |
| Icon system changes | [.claude/docs/icons.md](.claude/docs/icons.md) (surfaces table, TrayIconMode table, gotchas) |
| Installer changes | [.claude/docs/installer.md](.claude/docs/installer.md) |
| Version bump | [.claude/docs/versioning.md](.claude/docs/versioning.md) if the process changed; fallback versions in `Package.wxs` + `Package.appxmanifest` |
| New/changed XAML patterns | [.claude/docs/xaml.md](.claude/docs/xaml.md) |
| Drag-and-drop changes | [.claude/docs/drag-drop.md](.claude/docs/drag-drop.md) |
| New page or navigation change | `CLAUDE.md` (Architecture section) |
| New dependency added/removed | `CLAUDE.md` (Dependencies list) |
| Any structural change | `ARCHITECTURE.md`, `README.md` if affected |

**Rule:** When you create new files for an area, make sure the matching topic guide in `.claude/docs/` still describes them, and that the nested `CLAUDE.md` in that directory imports the right guide(s). A guide that isn't referenced from the directory you're editing won't be loaded.

**Rule:** Read the relevant topic guide before deciding whether it needs updating — don't skip this based on assumptions.
