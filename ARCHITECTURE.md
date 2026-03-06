# Architecture — SelfHostedHelper

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
- `SettingsManager` (fully static) serialises it to XML at `%AppData%\SelfHostedHelper\settings.xml`.
- On startup, `RestoreSettings()` deserialises and calls `CompleteInitialization()` to enable change handlers.
- `SaveSettings()` is called on settings window close and after SFTP download.

## SFTP sync

`SftpSyncService` provides three static async methods:
- `UploadSettingsAsync()` — saves current settings, connects via SSH.NET, uploads `settings.xml`.
- `DownloadSettingsAsync()` — downloads remote file to temp, swaps it in, reloads settings.
- `TestConnectionAsync()` — verifies SSH connectivity and SFTP access.

Supports both private-key (`PrivateKeyFile`) and password-based authentication.

## Theme system

`ThemeManager` controls the app theme via WinUI 3's `ElementTheme` system:
- Sets `RequestedTheme` on the root `FrameworkElement` of each window.
- `IsDarkTheme()` reads the system foreground colour from a cached `UISettings` instance to detect light/dark mode.
- Theme 0 = system default, 1 = Light, 2 = Dark.

## Backdrop

- **SettingsWindow** uses `MicaBackdrop` (WinUI 3 built-in).
- **FlyoutWindow** uses a transparent backdrop for seamless integration.
