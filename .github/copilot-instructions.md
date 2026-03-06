# LittleLauncher — Copilot Instructions

## Project overview

LittleLauncher is a .NET 10 WinUI 3 desktop application (unpackaged) that provides a system-tray launcher with a flyout popup for shortcuts. It also syncs settings to a remote server via SSH/SFTP.

## Architecture

- **Single-instance app** enforced via a named `Mutex` ("LittleLauncher"). A second launch signals the first instance via `PostMessage` with registered window messages (`LittleLauncher_ShowFlyout`, `LittleLauncher_ShowSettings`).
- **MainWindow** is invisible (moved off-screen, 1×1). It owns the system-tray icon (`H.NotifyIcon.TaskbarIcon`). Uses `WS_EX_TOOLWINDOW` to hide from Alt-Tab.
- **FlyoutWindow** is a popup that displays launcher items with icons. Shown from tray icon click, positioned above the taskbar, dismissed on focus loss or Escape.
- **SettingsWindow** is a WinUI 3 window with `MicaBackdrop`. It uses `NavigationView` with page-based navigation (Home, Launcher Items, Cloud Sync, Settings, About).
- **Settings** are serialised to `%AppData%\LittleLauncher\settings.xml` via `XmlSerializer`, managed by the fully static `SettingsManager`.
- **SftpSyncService** uses SSH.NET for async upload/download of the settings file to a configurable remote server.

## Key namespaces

| Namespace | Contents |
|---|---|
| `LittleLauncher` | App, MainWindow, SettingsWindow |
| `LittleLauncher.Classes` | NativeMethods, ThemeManager |
| `LittleLauncher.Classes.Settings` | SettingsManager |
| `LittleLauncher.Models` | LauncherItem, SshConnectionProfile |
| `LittleLauncher.Pages` | All settings pages |
| `LittleLauncher.Services` | SftpSyncService, FaviconService |
| `LittleLauncher.ViewModels` | UserSettings |
| `LittleLauncher.Windows` | FlyoutWindow |

**Note:** The `LittleLauncher.Windows` namespace shadows the WinRT `Windows.*` namespace. Use `global::Windows.` prefix when accessing WinRT types (e.g. `global::Windows.Graphics.PointInt32`).

## Conventions

- Use `[ObservableProperty]` from CommunityToolkit.Mvvm for all bindable settings properties.
- Partial `On<Property>Changed` methods in `UserSettings` handle side-effects (theme changes, taskbar updates).
- An `_initializing` flag in `UserSettings` suppresses change handlers during XML deserialization.
- P/Invoke declarations live in `NativeMethods.cs`. Always use `using static LittleLauncher.Classes.NativeMethods;` imports.
- Use `[LibraryImport]` for new P/Invoke declarations; existing ones use `[DllImport]`.
- Pages are WinUI 3 `Page` objects navigated via `NavigationView`. No MVVM framework routing — just `TargetPageType` in XAML.
- String resources live in `Resources/Localization/Dictionary-en-US.xaml`. In code: `Application.Current.Resources.TryGetValue("KeyName", out object value)`.
- Use `CommunityToolkit.Mvvm.Input.RelayCommand` for ICommand implementations.

## Build

```bash
dotnet build LittleLauncher/LittleLauncher.csproj -c Debug
```

`Directory.Build.props` auto-detects the platform from `PROCESSOR_ARCHITECTURE` (ARM64 → ARM64, otherwise x64). To override: `-p:Platform=x64` or `-p:Platform=ARM64`.

Target: `net10.0-windows10.0.22000.0`, unpackaged (`WindowsPackageType=None`), platforms `x64` and `ARM64`.

## Dependencies

- Microsoft.WindowsAppSDK 1.8.260209005 (WinUI 3)
- H.NotifyIcon.WinUI 2.4.1 (system tray)
- CommunityToolkit.Mvvm 8.4.0
- SSH.NET 2025.1.0
- NLog 6.1.1

## Common tasks

- **Add a new settings page:** Create `Pages/FooPage.xaml` + `.cs`, add a `NavigationViewItem` in `SettingsWindow.xaml`, add any new string keys to `Dictionary-en-US.xaml`.
- **Add a new launcher feature:** Extend `LauncherItem` model, update `FlyoutWindow` to render it, update `LauncherItemsPage` for editing.
- **Add a new setting:** Add an `[ObservableProperty]` to `UserSettings.cs`. It will auto-serialize to XML.
