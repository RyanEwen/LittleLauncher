# SelfHostedHelper — Copilot Instructions

## Project overview

SelfHostedHelper is a .NET 10 WPF desktop application that embeds a launcher widget directly into the Windows 11 taskbar. It also syncs settings to a remote server via SSH/SFTP.

## Architecture

- **Single-instance app** enforced via a named `Mutex` ("TaskbarLauncher"). A second launch signals the first instance to open the settings window through an `EventWaitHandle`.
- **MainWindow** is invisible (0×0, hidden). It owns the system-tray `NotifyIcon` (WPF-UI Tray) and the `TaskbarWindow`.
- **TaskbarWindow** is reparented into the native Windows taskbar (`Shell_TrayWnd`) using `SetParent` P/Invoke. It converts its style from `WS_POPUP` to `WS_CHILD` and is tightly sized to the widget area. Position updates are event-driven (`WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `TaskbarCreated`) with a 5 s `DispatcherTimer` fallback.
- **FlyoutWindow** is a popup that displays launcher items with icons. Shown from the taskbar widget or tray icon click, positioned above the taskbar, dismissed on focus loss or Escape.
- **SettingsWindow** is a WPF-UI `FluentWindow` with Mica backdrop. It uses `NavigationView` with page-based navigation (Home, Launcher Items, Cloud Sync, Settings, About).
- **Settings** are serialised to `%AppData%\SelfHostedHelper\settings.xml` via `XmlSerializer`, managed by the singleton `SettingsManager`.
- **SftpSyncService** uses SSH.NET for async upload/download of the settings file to a configurable remote server.

## Key namespaces

| Namespace | Contents |
|---|---|
| `SelfHostedHelper` | App, MainWindow, SettingsWindow |
| `SelfHostedHelper.Classes` | NativeMethods, ThemeManager, WindowBlurHelper, WindowHelper |
| `SelfHostedHelper.Classes.Settings` | SettingsManager |
| `SelfHostedHelper.Classes.Utils` | MonitorUtil, BoolToEnabledDisabledConverter |
| `SelfHostedHelper.Controls` | TaskbarLauncherControl |
| `SelfHostedHelper.Models` | LauncherItem, SshConnectionProfile |
| `SelfHostedHelper.Pages` | All settings pages |
| `SelfHostedHelper.Services` | SftpSyncService, FaviconService |
| `SelfHostedHelper.ViewModels` | UserSettings |
| `SelfHostedHelper.Windows` | TaskbarWindow, FlyoutWindow |

## Conventions

- Use `[ObservableProperty]` from CommunityToolkit.Mvvm for all bindable settings properties.
- Partial `On<Property>Changed` methods in `UserSettings` handle side-effects (theme changes, taskbar updates).
- An `_initializing` flag in `UserSettings` suppresses change handlers during XML deserialization.
- P/Invoke declarations live in `NativeMethods.cs`. Always use `static SelfHostedHelper.Classes.NativeMethods` imports.
- Pages are WPF `Page` objects navigated via WPF-UI's `NavigationView`. No MVVM framework routing — just `TargetPageType` in XAML.
- XML resource strings live in `Resources/Localization/Dictionary-en-US.xaml`. Access them via `{DynamicResource KeyName}` in XAML or `Application.Current.TryFindResource("KeyName")` in code.

## Build

```bash
cd SelfHostedHelper
dotnet build -c Debug -p:Platform=x64
```

Target: `net10.0-windows10.0.22000.0`, platforms `x64` and `ARM64`.

## Dependencies

- WPF-UI 4.2.0, WPF-UI.Tray 4.2.0
- MicaWPF 6.3.2
- CommunityToolkit.Mvvm 8.4.0
- SSH.NET 2025.1.0
- NLog 6.1.1

## Common tasks

- **Add a new settings page:** Create `Pages/FooPage.xaml` + `.cs`, add a `NavigationViewItem` in `SettingsWindow.xaml`, add any new string keys to `Dictionary-en-US.xaml`.
- **Add a new launcher feature:** Extend `LauncherItem` model, update `TaskbarLauncherControl` to render it, update `LauncherItemsPage` for editing.
- **Add a new setting:** Add an `[ObservableProperty]` to `UserSettings.cs`. It will auto-serialize to XML.
- **Modify taskbar embedding behaviour:** Edit `TaskbarWindow.xaml.cs` — the `CalculateAndSetPosition`, `SetupWindow`, and `UpdatePosition` methods.
