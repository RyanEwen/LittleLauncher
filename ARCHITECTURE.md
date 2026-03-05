# Architecture — SelfHostedHelper

## High-level flow

```
App.xaml  →  MainWindow (invisible, owns tray icon)
                ├── TaskbarWindow (embedded in Shell_TrayWnd)
                │     └── TaskbarLauncherControl (icon buttons)
                └── SettingsWindow (FluentWindow + NavigationView)
                      ├── HomePage
                      ├── LauncherItemsPage
                      ├── SyncPage
                      ├── SystemPage
                      └── AboutPage
```

## Taskbar embedding (the core trick)

The `TaskbarWindow` is a standard WPF `Window` that gets **reparented** into the Windows taskbar:

1. **Find the taskbar** — `FindWindow("Shell_TrayWnd", null)` returns the primary taskbar handle.
2. **Reparent** — `SetParent(ourHwnd, taskbarHwnd)` makes our window a child of the taskbar.
3. **Change window style** — `SetWindowLong` switches from `WS_POPUP` to `WS_CHILD` so the window behaves as a child inside the taskbar rather than a floating popup.
4. **Tight sizing** — The window is sized to exactly the launcher area (not the full taskbar), so only the visible content area intercepts mouse clicks.
5. **Position calculation** — UI Automation (`AutomationElement`) finds:
   - The **Widgets button** (`ClassName == "ToggleButton"` with `AutomationId == "WidgetsButton"`)
   - The **system tray icon** area
   - The **taskbar frame** bounds
   
   The widget is placed between the Widgets button and the system tray, with configurable padding.
6. **Refresh loop** — A `DispatcherTimer` every 5 seconds acts as a fallback; position updates are primarily event-driven via `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, and `TaskbarCreated` messages.
7. **Multi-monitor** — `Shell_SecondaryTrayWnd` handles are enumerated for secondary displays.

## Settings persistence

- `UserSettings` (the ViewModel) is an `ObservableObject` with `[ObservableProperty]` attributes.
- `SettingsManager` serialises it to XML at `%AppData%\SelfHostedHelper\settings.xml`.
- On startup, `RestoreSettings()` deserialises and calls `CompleteInitialization()` to enable change handlers.
- `SaveSettings()` is called on settings window close and after SFTP download.

## SFTP sync

`SftpSyncService` provides three static async methods:
- `UploadSettingsAsync()` — saves current settings, connects via SSH.NET, uploads `settings.xml`.
- `DownloadSettingsAsync()` — downloads remote file to temp, swaps it in, reloads settings.
- `TestConnectionAsync()` — verifies SSH connectivity and SFTP access.

Supports both private-key (`PrivateKeyFile`) and password-based authentication.

## Theme system

Two theme libraries work together:
- **WPF-UI** (`ApplicationThemeManager`) — controls Fluent Design control appearance.
- **MicaWPF** (`MicaWPFServiceUtility.ThemeService`) — controls Mica backdrop and accent colours.

`ThemeManager.ApplyTheme(int)` synchronises both. Theme 0 = system default (watches `SystemThemeWatcher`), 1 = Light, 2 = Dark.
