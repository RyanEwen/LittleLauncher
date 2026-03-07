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

## Settings persistence

- `UserSettings` (the ViewModel) is an `ObservableObject` with `[ObservableProperty]` attributes.
- `SettingsManager` (fully static) serialises it to XML at `%AppData%\LittleLauncher\settings.xml`.
- On startup, `RestoreSettings()` deserialises and calls `CompleteInitialization()` to enable change handlers.
- `SaveSettings()` is called on settings window close and after SFTP download.

## SFTP sync

`SftpSyncService` provides static async methods:
- `UploadLauncherItemsAsync()` — serializes launcher items and uploads `launcher-items.xml` via SFTP.
- `DownloadLauncherItemsAsync()` — downloads `launcher-items.xml`, deserializes, and replaces the local launcher items collection on the UI thread.
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
