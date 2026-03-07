<p align="center">
  <img src="LittleLauncher/Resources/AppIcons/Blue.png" alt="Little Launcher" width="128">
</p>

<h1 align="center">Little Launcher</h1>

<p align="center">
  A Windows system-tray launcher with settings-sync capability, built with WinUI 3, Windows App SDK, and SSH/SFTP.
</p>

<p align="center">
  <img src="LittleLauncher/Resources/AppIcons/Blue.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Green.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Teal.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Red.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Orange.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Purple.png" width="48">
</p>

## Overview

Little Launcher lives in the Windows system tray. Clicking the tray icon opens a flyout with app and website shortcuts. It also provides SSH/SFTP-based settings synchronisation so you can keep your launcher configuration in sync across machines.

**Key features:**

- **System-tray launcher** — a tray icon that opens a flyout popup for shortcuts.
- **Application & website shortcuts** — launch any executable or URL with one click from the flyout.
- **SSH/SFTP settings sync** — upload/download your `settings.xml` to a remote server using SSH.NET.
- **WinUI 3 settings UI** — a native window with Mica backdrop and page-based `NavigationView`.
- **Theme support** — follows the Windows system theme or can be set to Light/Dark explicitly.
- **Export & import** — back up and restore settings locally via XML.

## Architecture

| Layer | Description |
|---|---|
| `MainWindow` | Invisible host window. Owns the system-tray icon (`H.NotifyIcon`). Enforces single-instance via Mutex. Cross-process IPC via registered window messages. |
| `FlyoutWindow` | A popup window that displays launcher items with icons, positioned above the taskbar. Dismissed on focus loss or Escape. |
| `SettingsWindow` | WinUI 3 window with `MicaBackdrop` and `NavigationView` — pages for Home, Launcher Items, Cloud Sync, Settings, and About. |
| `SftpSyncService` | Static async methods for upload/download/test-connection using SSH.NET (`Renci.SshNet`). Supports private-key and password auth. |
| `SettingsManager` | Fully static. Serialises `UserSettings` to `%AppData%\LittleLauncher\settings.xml` via `XmlSerializer`. |
| `ThemeManager` | Sets `RequestedTheme` on root `FrameworkElement` of each window. Detects system dark/light mode via cached `UISettings`. |

## Tech stack

| Package | Version | Purpose |
|---|---|---|
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) | 1.8.260209005 | WinUI 3 controls, Mica, NavigationView |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | System tray icon |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.0 | Source-gen `[ObservableProperty]`, `RelayCommand` |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | 2025.1.0 | SFTP sync |
| [NLog](https://nlog-project.org/) | 6.1.1 | Logging |

**Target:** .NET 10, `net10.0-windows10.0.22000.0`, unpackaged (`WindowsPackageType=None`), platforms `x64` and `ARM64`.

## Getting started

### Prerequisites

- Windows 10/11 (build 22000+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build

```bash
cd LittleLauncher
dotnet build -c Debug
```

`Directory.Build.props` auto-detects the platform from `PROCESSOR_ARCHITECTURE` (ARM64 → ARM64, otherwise x64). To override: `-p:Platform=x64` or `-p:Platform=ARM64`.

### Run

```bash
dotnet run --project LittleLauncher -c Debug
```

Or open `LittleLauncher.sln` in Visual Studio / Rider and press F5.

## Project structure

```
LittleLauncher/              # WinUI 3 application project
├── App.xaml / App.xaml.cs     # Bootstrap, exception handling, settings restore
├── MainWindow.xaml/.cs        # Invisible host + tray icon + singleton IPC
├── SettingsWindow.xaml/.cs    # WinUI 3 settings UI with Mica backdrop
├── Classes/
│   ├── NativeMethods.cs       # P/Invoke declarations (user32, dwmapi, shcore, comctl32, shlwapi)
│   ├── ThemeManager.cs        # Theme orchestration (ElementTheme)
│   └── Settings/
│       └── SettingsManager.cs # XML serialisation (fully static)
├── Models/
│   ├── LauncherItem.cs
│   └── SshConnectionProfile.cs
├── Pages/
│   ├── HomePage.xaml/.cs
│   ├── LauncherItemsPage.xaml/.cs
│   ├── SyncPage.xaml/.cs
│   ├── SystemPage.xaml/.cs
│   └── AboutPage.xaml/.cs
├── Services/
│   ├── FaviconService.cs      # Website favicon & title fetching
│   └── SftpSyncService.cs     # SSH/SFTP upload/download/test
├── ViewModels/
│   └── UserSettings.cs        # Observable settings (CommunityToolkit.Mvvm)
├── Windows/
│   └── FlyoutWindow.xaml/.cs   # Launcher flyout popup
└── Resources/
    ├── Localization/
    │   └── Dictionary-en-US.xaml
    └── LittleLauncher.ico

LauncherShortcut/              # Companion console exe for pin-to-taskbar helper
```

## License

This project is licensed under the GPL-3.0 License. See [LICENSE](LICENSE) for details.
