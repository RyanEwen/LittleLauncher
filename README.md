# SelfHostedHelper

A Windows taskbar launcher and settings-sync utility built with WPF, Fluent Design, and SSH/SFTP.

## Overview

SelfHostedHelper embeds a launcher widget directly into the Windows 11 taskbar (not just the system tray). Clicking the widget icon opens a flyout with app and website shortcuts. It also provides SSH/SFTP-based settings synchronisation so you can keep your launcher configuration in sync across machines.

**Key features:**

- **Taskbar launcher widget** — a globe icon rendered inside the native Windows taskbar via `SetParent` P/Invoke, with a flyout for shortcuts.
- **Application & website shortcuts** — launch any executable or URL with one click from the flyout.
- **SSH/SFTP settings sync** — upload/download your `settings.xml` to a remote server using SSH.NET.
- **Fluent Design settings UI** — a WPF-UI `FluentWindow` with Mica backdrop and page-based navigation.
- **Multi-monitor support** — choose which display hosts the widget and configure padding/position.
- **Theme support** — follows the Windows system theme or can be set to Light/Dark explicitly.
- **Export & import** — back up and restore settings locally via XML.

## Architecture

| Layer | Description |
|---|---|
| `MainWindow` | Invisible host window. Owns the tray `NotifyIcon` and the `TaskbarWindow`. Enforces single-instance via Mutex. |
| `TaskbarWindow` | A WPF `Window` reparented into `Shell_TrayWnd` (the Windows taskbar) using `SetParent`. Converts `WS_POPUP` → `WS_CHILD` and is tightly sized to the widget area. Position updates are event-driven (`WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `TaskbarCreated`) with a 5 s fallback timer. |
| `TaskbarLauncherControl` | A `UserControl` displaying a single globe icon. Clicking it toggles the `FlyoutWindow`. |
| `FlyoutWindow` | A popup window that displays launcher items with icons, positioned above the taskbar. Dismissed on focus loss or Escape. |
| `SettingsWindow` | `FluentWindow` with `NavigationView` — pages for Home, Launcher Items, Cloud Sync, Settings, and About. |
| `SftpSyncService` | Static async methods for upload/download/test-connection using SSH.NET (`Renci.SshNet`). Supports private-key and password auth. |
| `SettingsManager` | Singleton. Serialises `UserSettings` to `%AppData%\SelfHostedHelper\settings.xml` via `XmlSerializer`. |
| `ThemeManager` | Applies WPF-UI + MicaWPF themes and updates the tray icon to match. |

## Tech stack

| Package | Version | Purpose |
|---|---|---|
| [WPF-UI](https://github.com/lepoco/wpfui) | 4.2.0 | Fluent Design controls |
| [MicaWPF](https://github.com/Simnico99/MicaWPF) | 6.3.2 | Mica backdrop |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.0 | Source-gen `[ObservableProperty]` |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | 2025.1.0 | SFTP sync |
| [NLog](https://nlog-project.org/) | 6.1.1 | Logging |

**Target:** .NET 10, `net10.0-windows10.0.22000.0`, platforms `x64` and `ARM64`.

## Getting started

### Prerequisites

- Windows 10/11 (build 22000+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build

```bash
cd SelfHostedHelper
dotnet build -c Debug -p:Platform=x64
```

### Run

```bash
dotnet run --project SelfHostedHelper -c Debug -p:Platform=x64
```

Or open `SelfHostedHelper.sln` in Visual Studio / Rider and press F5.

## Project structure

```
SelfHostedHelper/              # WPF application project
├── App.xaml / App.xaml.cs     # Bootstrap, resource dictionaries
├── MainWindow.xaml/.cs        # Invisible host + tray icon
├── SettingsWindow.xaml/.cs    # Fluent settings UI
├── Classes/
│   ├── NativeMethods.cs       # P/Invoke declarations
│   ├── ThemeManager.cs        # Theme orchestration
│   ├── WindowBlurHelper.cs    # Acrylic blur via SetWindowCompositionAttribute
│   ├── WindowHelper.cs        # Window positioning helpers
│   ├── Settings/
│   │   └── SettingsManager.cs # XML serialisation
│   └── Utils/
│       ├── FileSystemHelper.cs
│       ├── MonitorUtil.cs     # Multi-monitor enumeration
│       └── ...converters
├── Controls/
│   └── TaskbarLauncherControl.xaml/.cs
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
│   ├── FaviconService.cs
│   └── SftpSyncService.cs
├── ViewModels/
│   └── UserSettings.cs
├── Windows/
│   ├── TaskbarWindow.xaml/.cs  # Taskbar-embedded window
│   └── FlyoutWindow.xaml/.cs   # Launcher flyout popup
└── Resources/
    ├── Localization/
    │   └── Dictionary-en-US.xaml
    ├── TrayIcons/
    └── SelfHostedHelper.ico
```

## How the taskbar embedding works

1. The `TaskbarWindow` finds the `Shell_TrayWnd` handle and calls `SetParent` to reparent itself as a child.
2. Window style is changed from `WS_POPUP` to `WS_CHILD` via `SetWindowLong`.
3. The window is sized to exactly the widget area, so only visible content intercepts mouse clicks.
4. UI Automation (`AutomationElement`) locates the Widgets button, system tray icon area, and taskbar frame to calculate pixel-precise positioning.
5. Position updates are event-driven (`WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `TaskbarCreated`), with a 5 s `DispatcherTimer` fallback.
6. Multi-monitor support enumerates `Shell_SecondaryTrayWnd` handles.

## Credits

Based on the architecture of [FluentFlyout](https://github.com/unchihugo/FluentFlyout) by [@unchihugo](https://github.com/unchihugo).

## License

This project is licensed under the GPL-3.0 License. See [LICENSE](LICENSE) for details.
