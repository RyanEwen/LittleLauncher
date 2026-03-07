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

`MajorUpgrade` handles version upgrades automatically — the old version is uninstalled before the new one is installed. `UpgradeCode` must never change.

## Per-user install notes

- `Scope="perUser"` means no elevation, installs to `LocalAppDataFolder`
- WiX ICE validations ICE38, ICE64, ICE91 are suppressed in `.wixproj` — these fire for per-user installs writing to profile directories, which is expected
- The update service (`UpdateService.cs`) launches `msiexec /i` without elevation (`-Verb RunAs` is NOT used)

## Auto-update flow

`UpdateService` downloads the MSI to a temp folder, removes the Zone.Identifier ADS (Mark of the Web), then spawns a `.cmd` script that waits for the app to exit before running `msiexec /i`. The app exits after a short delay to allow the script to start.
