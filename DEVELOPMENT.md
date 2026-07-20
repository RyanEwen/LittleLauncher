# Developing Little Launcher

Build prerequisites, architecture overview, and project layout for working on Little Launcher. For installation and features, see the [README](README.md).

## Prerequisites

- Windows 10/11 (build 22000+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build

```bash
cd LittleLauncher
dotnet build -c Debug
```

`Directory.Build.props` auto-detects the platform from `PROCESSOR_ARCHITECTURE` (ARM64 → ARM64, otherwise x64). To override: `-p:Platform=x64` or `-p:Platform=ARM64`.

## Run

```bash
dotnet run --project LittleLauncher -c Debug
```

Or open `LittleLauncher.sln` in Visual Studio / Rider and press F5.

## Architecture

| Layer | Description |
|---|---|
| `MainWindow` | Invisible host window. Owns the system-tray icon (`H.NotifyIcon`). Enforces single-instance via Mutex. Cross-process IPC via registered window messages. |
| `FlyoutWindow` | A popup window that displays launcher items in list view, icon grid view, or a compact tray-sized small-icon grid, positioned above the taskbar. Dismissed on focus loss or Escape. Supports direct drag-and-drop reordering, item right-click move/edit/remove actions, and icon-grid edge resizing. |
| `SettingsWindow` | WinUI 3 window with `MicaBackdrop` and `NavigationView` — pages for Home, Launchers, Launcher Items, Cloud Sync, Settings, and About. |
| `SftpSyncService` | Static async methods for upload/download/test-connection using SSH.NET (`Renci.SshNet`). Also handles per-launcher shared sync (file or SFTP). Supports private-key and password auth. |
| `AutoSyncService` | Manages automatic sync: startup download, debounced upload, periodic download, and shared launcher sync. |
| `SettingsManager` | Fully static. Serialises `UserSettings` to `%AppData%\LittleLauncher\settings.json` via `System.Text.Json`. Migrates from legacy `settings.xml` on first load. |
| `ThemeManager` | Sets `RequestedTheme` on root `FrameworkElement` of each window. Detects system dark/light mode via cached `UISettings`. |

See [ARCHITECTURE.md](ARCHITECTURE.md) for detailed flows — launch modes, settings persistence, the launcher-item icon pipeline, SFTP sync, theming, the companion exe, and MSIX packaging.

## Tech stack

| Package | Version | Purpose |
|---|---|---|
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) | 1.8.260209005 | WinUI 3 controls, Mica, NavigationView |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | System tray icon |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.0 | Source-gen `[ObservableProperty]`, `RelayCommand` |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | 2025.1.0 | SFTP sync |
| [NLog](https://nlog-project.org/) | 6.1.1 | Logging |

**Target:** .NET 10, `net10.0-windows10.0.22000.0`, unpackaged (`WindowsPackageType=None`), platforms `x64` and `ARM64`.

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
│       └── SettingsManager.cs # JSON serialisation (fully static)
├── Models/
│   ├── LauncherItem.cs
│   ├── Launcher.cs            # Multi-launcher model with sharing properties
│   └── SshConnectionProfile.cs
├── Pages/
│   ├── HomePage.xaml/.cs
│   ├── LaunchersPage.xaml/.cs  # Launcher card management + sharing UI
│   ├── LauncherBulkOps.cs       # Item export/import + bookmark import
│   ├── SyncPage.xaml/.cs
│   ├── SystemPage.xaml/.cs
│   └── AboutPage.xaml/.cs
├── Services/
│   ├── AutoSyncService.cs     # Automatic sync triggers
│   ├── FaviconService.cs      # Website favicon & title fetching
│   ├── SftpSyncService.cs     # SSH/SFTP upload/download/test + shared sync
│   └── UpdateService.cs       # Update checking
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

## Releasing

Version is defined once in `Directory.Build.props`. Pushing a `v*` tag triggers two GitHub Actions:

- **`build-msix.yml`** — builds the signed MSI installers, portable zips, and the GitHub Release.
- **`store-publish.yml`** — builds the unsigned MSIX packages (x64 + ARM64), bundles them into a single `.msixupload`, and submits a package update to the Microsoft Store via the [Microsoft Store Developer CLI](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/overview) (`msstore`). The Store re-signs on ingestion.

You can also build the Store MSIX locally with `LittleLauncherMSIX/build-msix.ps1 -NoSign`.

### Microsoft Store auto-publish setup

`store-publish.yml` only submits when the following repository secrets are set (otherwise it just builds and uploads the `.msixupload` as an artifact). Add them under **Settings → Secrets and variables → Actions**:

| Secret | Value |
|---|---|
| `AZURE_AD_TENANT_ID` | Microsoft Entra tenant ID |
| `AZURE_AD_APPLICATION_CLIENT_ID` | App registration (client) ID |
| `AZURE_AD_APPLICATION_SECRET` | Client secret for that app registration |
| `SELLER_ID` | Partner Center seller / publisher ID |

One-time Partner Center setup: register an app in Microsoft Entra ID, then add it under **Account settings → User management → Microsoft Entra applications** with the **Manager** role. The Store product ID (`9P3ZZBDQ6PJF`) is set as `STORE_PRODUCT_ID` in the workflow. See [Publish app updates with GitHub Actions](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/github-actions). Note: Microsoft currently supports this GitHub Actions update path for **free products only**, and the submission goes through normal Store certification before going live.

See the versioning and installer guides under [`.claude/docs/`](.claude/docs/) for more detail.
