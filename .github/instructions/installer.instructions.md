---
description: "Use when modifying the MSI installer (WiX), changing install paths, shortcuts, or upgrade behavior. Covers per-user install, Start Menu shortcut lifecycle, and common WiX pitfalls."
applyTo: "**/Package.wxs,**/LittleLauncherSetup.wixproj,**/UpdateService.cs"
---

# MSI Installer (WiX)

Little Launcher ships as a **per-user MSI** built with WiX Toolset 5. No elevation is required.

## Install layout

| What | Where |
|---|---|
| App files | `%LocalAppData%\Little Launcher\` |
| Start Menu shortcut | `%AppData%\Microsoft\Windows\Start Menu\Programs\Little Launcher.lnk` |
| Settings/data | `%AppData%\LittleLauncher\` (created by the app, not the MSI) |

## Start Menu shortcut lifecycle

1. **MSI creates** `Programs\Little Launcher.lnk` at install time using the embedded `LittleLauncher.ico` (always Blue rocket). This gives users something to click before the app ever runs.
2. **On first launch** `EnsureStartMenuShortcuts()` in `MainWindow.xaml.cs` overwrites the same shortcut with the user's chosen icon (`app-icon.ico` from AppData).
3. **On icon change** `UpdateShortcutIcons()` re-stamps the shortcut with the new icon.
4. **On uninstall** the MSI removes the shortcut via the component's registry key.

**Critical:** The MSI shortcut must be placed directly in `ProgramMenuFolder` (not a subfolder), so its path matches what the app writes at runtime. If the MSI uses a subfolder, you get duplicate shortcuts — one stale (MSI's) and one current (app's).

## Version injection

The installer version comes from `Directory.Build.props` → `LittleLauncherSetup.wixproj` passes `ProductVersion=$(Version).0` via `DefineConstants`. CI also injects it. A fallback `<?define ProductVersion = "X.Y.Z.0" ?>` exists in `Package.wxs` for local builds — **keep it in sync** when bumping versions.

## Upgrade behavior

`MajorUpgrade` with `AllowSameVersionUpgrades="yes"` handles upgrades and reinstalls — the old version is uninstalled before the new one is installed, even when the version number is unchanged. `UpgradeCode` must never change.

## Auto-launch after install

A `CustomAction` in `Package.wxs` launches `LittleLauncher.exe` after `InstallFinalize` (condition `NOT REMOVE`). It uses `asyncNoWait` so the installer doesn't block. This ensures the app is running in the tray immediately after a fresh install or upgrade.

## Per-user install notes

- `Scope="perUser"` means no elevation, installs to `LocalAppDataFolder`
- WiX ICE validations ICE38, ICE64, ICE91 are suppressed in `.wixproj` — these fire for per-user installs writing to profile directories, which is expected
- The update service (`UpdateService.cs`) launches `msiexec /i` without elevation (`-Verb RunAs` is NOT used)

## Auto-update flow

`UpdateService` downloads the MSI to a temp folder, removes the Zone.Identifier ADS (Mark of the Web), then spawns a `.cmd` helper script:

1. Script waits for the current app process to exit
2. Runs `msiexec /i <path> /passive` — installs silently with progress bar (no user interaction; they already consented in-app)
3. MSI's `CustomAction` auto-launches the app in the tray
4. Script launches `LittleLauncher.exe --settings` — the single-instance mutex detects the running app and sends `LittleLauncher_ShowSettings`, re-opening the Settings window

## Uninstall cleanup

On `REMOVE="ALL"`, a `CustomAction` runs `cleanup-uninstall.ps1` (shipped in the install folder) via `powershell.exe -File`. It cleans up:

| What | Where |
|---|---|
| App data folder | `%AppData%\LittleLauncher\` (settings, companion exe, icons) |
| Flyout Start Menu shortcut | `%AppData%\...\Start Menu\Programs\Little Launcher Flyout.lnk` |
| Startup registry entry | `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Little Launcher` |
| Pinned taskbar shortcuts | Any `.lnk` in `User Pinned\TaskBar\` targeting `LittleLauncherFlyout.exe` |

The action uses `Return="asyncNoWait"` so the installer doesn't block on PowerShell.

**MSIX limitation:** MSIX has no custom uninstall actions. When an MSIX package is removed, Windows deletes the package files and its own Start Menu entry, but leaves behind `%AppData%\LittleLauncher\` and any pinned taskbar shortcuts. The pinned `.lnk` becomes a dead shortcut — Windows 11 eventually detects and offers to remove stale pins.
