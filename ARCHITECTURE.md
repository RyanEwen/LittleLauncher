# Architecture — Little Launcher

## High-level flow

```
App.xaml  →  MainWindow (invisible, owns tray icon)
                ├── FlyoutWindow (launcher popup)
                └── SettingsWindow (WinUI 3 + NavigationView)
                      ├── HomePage
                      ├── LauncherItemsPage
                      ├── SyncPage
                      ├── SystemPage
                      └── AboutPage
```

## LauncherItemsPage drag-and-drop

The Launcher Items settings page supports full cross-list drag-and-drop: items can be dragged between the top-level list and group child lists, between different groups, and out of groups via a drop zone. All ListViews use `CanDragItems="True"` with custom `DragOver`/`Drop` handlers — WinUI 3's `CanReorderItems` is intentionally avoided because it takes full internal control of drag events and cannot support cross-collection moves. See `.github/instructions/drag-drop.instructions.md` for implementation details.

## Launch modes

By default, launching the app opens the Settings window. Silent mode (tray icon only, no Settings window) is used for Windows startup and companion exe cold-starts:

| Scenario | How it launches | Settings window? |
|---|---|---|
| Install / update / Start Menu / double-click exe | No special args | Yes |
| Windows startup (unpackaged) | Registry Run key with `--silent` | No |
| Windows startup (MSIX) | StartupTask → `ExtendedActivationKind.StartupTask` | No |
| Companion exe cold-start | `LittleLauncher.exe --silent` | No |
| Second instance (app already running) | Signals first instance via `PostMessage` → shows Settings | Yes |

## Settings persistence

- `UserSettings` (the ViewModel) is an `ObservableObject` with `[ObservableProperty]` attributes.
- `SettingsManager` (fully static) serialises it to XML at `%AppData%\LittleLauncher\settings.xml`.
- On startup, `RestoreSettings()` deserialises and calls `CompleteInitialization()` to enable change handlers.
- `SaveSettings()` is called on settings window close and after SFTP download.

## Launcher item icons

`FaviconService.FetchMissingItemIconsAsync(items)` is the **single pipeline** for fetching launcher item icons. It iterates the items and, for each one missing a valid local icon, fetches a favicon (websites), extracts a shell icon (PWAs via `IShellItemImageFactory`), or extracts an exe icon (apps). All entry points use this one method:

| Trigger | Caller |
|---|---|
| App startup (missing icons on disk) | `MainWindow.FetchMissingIconsOnStartupAsync()` |
| SFTP sync download | `SftpSyncService.DownloadLauncherItemsAsync()` |
| File import (Launcher Items page) | `LauncherItemsPage.ImportItems_Click()` |
| Manual add/edit | `DoFetch()` in add/edit dialog (calls `FaviconService` directly for the single item) |
| PWA add | PWA combo selection handler (extracts shell icon via `FaviconService.GetPwaIconFromShell`, falls back to web favicon) |

After bulk icon changes, callers invoke `FlyoutWindow.InvalidateItems()` so the flyout rebuilds its containers on the next toggle.

## SFTP sync

`SftpSyncService` provides static async methods:
- `UploadLauncherItemsAsync()` — serializes launcher items and uploads `launcher-items.xml` via SFTP.
- `DownloadLauncherItemsAsync()` — downloads `launcher-items.xml`, deserializes, replaces the local launcher items collection, fetches missing icons via the unified pipeline, and saves.
- `TestConnectionAsync()` — verifies SSH connectivity and SFTP access.

`AutoSyncService` manages automatic sync triggers:
- Downloads launcher items on startup.
- Debounced upload (3 s) when items change.
- Periodic download on a configurable interval.

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

The flyout popup dismisses when focus is lost via the WinUI `Activated` event (`Deactivated` state). It uses `WS_EX_TOOLWINDOW` to stay out of Alt-Tab and `OverlappedPresenter.CreateForContextMenu()` for borderless always-on-top presentation.

## Companion exe (`LauncherShortcut`)

`LittleLauncherFlyout.exe` is a tiny companion binary pinned to the taskbar. Clicking it sends a `PostMessage` to the main app to show the flyout, then exits. Because it launches on every click, startup latency is critical:

- **Release builds** use Native AOT (`dotnet publish`) producing a single ~1.6 MB native binary with no .NET runtime dependency.
- **Debug builds** copy the framework-dependent output for fast iteration (startup is slower but build is faster).
- **Never add heavy dependencies** (NuGet packages, large frameworks) to the `LauncherShortcut` project — it must remain a minimal P/Invoke-only program.
- **Never run expensive work** (icon regeneration, shell notifications, file I/O) synchronously in the main app's `_wmShowFlyout` WndProc handler — it blocks the flyout from appearing.

### Companion exe deployment

At startup, `EnsureFlyoutShortcut()` copies the companion exe to `%AppData%\\LittleLauncher\\` and creates a Start Menu shortcut pointing to that copy. This is done for **all** build types (WiX, MSIX, unpackaged) so there is a single, consistent location for pinning. A `main-exe-path.txt` breadcrumb file is written alongside the companion exe so it can launch the main app with `--silent` if `FindWindow` fails (main app not running).

## MSIX packaging

`build-msix.ps1` produces a sideloadable MSIX package for Windows Store-like installation. Key details:

- **Publishes with `-p:WindowsPackageType=MSIX`** to suppress the unpackaged-only auto-bootstrapper (`MICROSOFT_WINDOWSAPPSDK_BOOTSTRAP_AUTO_INITIALIZE`), which fails in a packaged context.
- **Declares `<PackageDependency>`** on `Microsoft.WindowsAppRuntime.1.8` so the framework package provides WinRT activation factories.
- **Copies compiled XAML (.xbf)** files manually from the RID build directory to the layout — `dotnet publish` omits them.
- **Version and architecture** are stamped from `Directory.Build.props` into the manifest at build time (`VERSION_PLACEHOLDER`, `ARCH_PLACEHOLDER`).
- **Image assets** in `LittleLauncherMSIX/Images/` use standard MRT naming qualifiers (e.g. `.scale-200.`, `.targetsize-48.`) and are indexed into `resources.pri` by `makepri`.
- **Companion exe** is deployed to `%AppData%\\LittleLauncher\\` at startup for all build types. See \"Companion exe\" section above.
