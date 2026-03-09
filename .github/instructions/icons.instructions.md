---
description: "Use when working with app icons, tray icons, shortcut icons, or window icons. Covers which icon files exist, where each icon surface pulls from, and how to update them correctly."
applyTo: "**/MainWindow.xaml.cs,**/SettingsWindow.xaml.cs,**/SystemPage.xaml*,**/LauncherShortcut/**,**/HomePage.xaml.cs,**/FlyoutConverters.cs,**/FlyoutWindow.xaml*"
---

# Icon System

Little Launcher uses a flat upright rocket as its identity icon. Users can change the **tray icon** and **pinned taskbar icon** to a different color variant, a glyph preset, or a custom image.

## Icon Surfaces

| Surface | Source | User-configurable? |
|---|---|---|
| **System tray** | `ResolveTrayIcon()` → preset PNG, glyph, or custom image | Yes (`TrayIconMode`) |
| **Pinned taskbar shortcut** | `app-icon.ico` in `<AppDataDir>` via `.lnk` IconLocation | Yes (follows `TrayIconMode`) |
| **Settings window titlebar** | `settings-icon.ico` (app icon + gear overlay) | Yes (follows `TrayIconMode`) |
| **Settings window taskbar entry** | `settings-icon.ico` via `WM_SETICON` + `AppWindow.SetIcon(IconId)` | Yes (follows `TrayIconMode`) |
| **Settings window Alt-Tab** | `settings-icon.ico` via `AppWindow.SetIcon(IconId)` | Yes (follows `TrayIconMode`) |
| **Start menu shortcut** | `app-icon.ico` via `GetShortcutIconLocation()` | Yes (follows `TrayIconMode`) |
| **Exe embedded icon** | `Resources/LittleLauncher.ico` (compiled into exe) | No — always Blue rocket |
| **Pin-to-taskbar dialog** | `app-icon.ico` loaded via `WM_SETICON` in companion exe | Yes (follows `TrayIconMode`) |

## Key Files

- **`Resources/LittleLauncher.ico`** — Multi-resolution Blue rocket (16–256px). Embedded into the exe at build time. This is the fallback icon for all surfaces. Generated from `Resources/AppIcons/Blue.png`.
- **`Resources/AppIcons/*.png`** — Preset icon PNGs (Blue, Green, Teal, Red, Orange, Purple). Flat upright rockets stretched 20% horizontally for a wider profile. Copied to output at build time. Loaded at runtime by `ResolveBaseIconBitmap()`.
- **`<AppDataDir>/app-icon.ico`** — Runtime-generated icon matching the current `TrayIconMode`. Written by `SaveResolvedIconToAppData()`. Used by shortcuts and window icons.
- **`<AppDataDir>/settings-icon.ico`** — Runtime-generated icon: the current app icon composited with a gear glyph overlay (dark circle + white gear in bottom-right corner). Written by `SaveSettingsIconToAppData()`. Used by the Settings window.
- **`<AppDataDir>/LittleLauncherFlyout.exe`** — Copy of the companion exe deployed by `EnsureFlyoutShortcut()` for all build types. Pinning uses this copy.
- **`<AppDataDir>/main-exe-path.txt`** — Breadcrumb file containing the main exe path. Read by the companion exe as a fallback when `FindWindow` fails.

> **`<AppDataDir>`** = the path returned by `MainWindow.GetPhysicalAppDataDir()`. See [MSIX VFS Redirection](#msix-vfs-redirection) below.

## TrayIconMode Values

| Mode | Type | Source |
|------|------|--------|
| 0 | Blue (default) | `AppIcons/Blue.png` |
| 1 | Green | `AppIcons/Green.png` |
| 2 | Teal | `AppIcons/Teal.png` |
| 3 | Red | `AppIcons/Red.png` |
| 4 | Orange | `AppIcons/Orange.png` |
| 5 | Purple | `AppIcons/Purple.png` |
| 6 | Pin glyph | Segoe Fluent Icons `\uE840` |
| 7 | Star glyph | Segoe Fluent Icons `\uE734` |
| 8 | Heart glyph | Segoe Fluent Icons `\uEB51` |
| 9 | Lightning glyph | Segoe Fluent Icons `\uE945` |
| 10 | Search glyph | Segoe Fluent Icons `\uE721` |
| 11 | Globe glyph | Segoe Fluent Icons `\uE774` |
| 12 | Custom | User-provided file |

Preset icons (0–5) are full-color PNGs — they do **not** change with OS theme.
Glyph presets (6–11) render in black (light theme) or white (dark theme) and update automatically on theme change.

## How Icon Updates Flow

All icon surfaces derive from a single source of truth: `ResolveBaseIconBitmap()`, which returns a 256×256 `System.Drawing.Bitmap`.

1. User changes `TrayIconMode` in Settings → `OnTrayIconModeChanged` fires
2. `ApplyTrayIconChange()` → `MainWindow.UpdateTrayIcon()`
3. `UpdateTrayIcon()` calls `ResolveTrayIcon()` → `ResolveBaseIconBitmap()` → `BitmapToIcon()` → sets `nIcon.Icon`
4. `UpdateTrayIcon()` calls `UpdateShortcutIcons()` → `SaveResolvedIconToAppData()` → `ResolveBaseIconBitmap()` → `BitmapToIcon()` → writes `app-icon.ico`
5. `UpdateTrayIcon()` calls `SaveSettingsIconToAppData()` → `ResolveBaseIconBitmap()` → gear overlay → `BitmapToIcon()` → writes `settings-icon.ico`
6. `UpdateShortcutIcons()` updates pinned taskbar `.lnk` files that target `LittleLauncherFlyout.exe`
7. `SettingsWindow.RefreshIcon()` reloads `settings-icon.ico` into titlebar, taskbar, and overlay

### Key rendering methods

| Method | Purpose |
|---|---|
| `ResolveBaseIconBitmap()` | Single source of truth — returns 256×256 bitmap for any `TrayIconMode` |
| `RenderGlyphBitmap()` | Renders a Segoe Fluent Icons glyph to 256×256 bitmap (called by `ResolveBaseIconBitmap` for modes 6–11) |
| `TrimAndResizeTo256()` | Trims transparent padding, centers on 256×256 canvas (called for presets + custom images) |
| `BitmapToIcon()` | Converts a bitmap to multi-resolution ICO (16–256px) |
| `ResolveTrayIcon()` | `ResolveBaseIconBitmap()` → `BitmapToIcon()` |
| `SaveResolvedIconToAppData()` | `ResolveBaseIconBitmap()` → `BitmapToIcon()` → write file |
| `SaveSettingsIconToAppData()` | `ResolveBaseIconBitmap()` → gear overlay → `BitmapToIcon()` → write file |

## Settings Window Icon Strategy

WinUI 3 has a known bug (WindowsAppSDK#2730) where the taskbar ignores `AppWindow.SetIcon()` for
windows in the same process as the exe's embedded icon. The workaround uses three layers:

1. **`SetWindowAppUserModelId(hwnd, "LittleLauncher.Settings")`** — gives the Settings window its own
   taskbar group via the Shell `IPropertyStore` COM API, so the taskbar treats it independently.
2. **`AppWindow.SetIcon(IconId)`** via `GetIconIdFromIcon` interop — sets the app-level icon for
   Alt-Tab and the window's identity.
3. **`WM_SETICON`** (ICON_SMALL + ICON_BIG) — sets the Win32 window icon, re-sent on `Activated`
   to counteract WinUI's framework overrides.

## Pinned Taskbar Icon Strategy

The companion exe is deployed to `<AppDataDir>` by `EnsureFlyoutShortcut()` for **all build types** (WiX, MSIX, unpackaged). This gives it a consistent, non-packaged location so the shell treats the `.lnk` like a normal shortcut:

1. **`EnsureFlyoutShortcut()`** copies `LittleLauncherFlyout.exe` from the app directory to `<AppDataDir>`.
2. Writes a `main-exe-path.txt` breadcrumb so the companion exe can launch the main app if it's not running.
3. `IconLocation` on the `.lnk` controls the pinned taskbar icon.
4. `UpdateShortcutIcons()` re-stamps the `.lnk`'s `IconLocation` with the current `app-icon.ico` when the user changes their icon preference.
5. The companion exe loads `app-icon.ico` from `AppContext.BaseDirectory` (its own directory, which is the same `<AppDataDir>`).

## Adding a New Preset Icon

1. Add the PNG file to `Resources/AppIcons/` (transparent background, square)
2. Add entry to `PresetIcons` dictionary in `MainWindow.xaml.cs` with the next mode number
3. Add a `ComboBoxItem` with colored `Ellipse` + `TextBlock` in `SystemPage.xaml` (before `Custom...`)
4. Bump the Custom mode number in: `ResolveBaseIconBitmap()`, `SystemPage.xaml.cs` (`UpdateCustomIconCardVisibility`), and `UserSettings.cs` (`OnCustomTrayIconPathChanged`)
5. Add `<Content Include="Resources/AppIcons/NewColor.png">` to `.csproj` (or use the existing `*.png` glob)
6. Update `TrayIconMode` comment in `UserSettings.cs`

## Regenerating `LittleLauncher.ico`

`Resources/LittleLauncher.ico` is the embedded exe icon and the MSI installer shortcut icon. It **must be regenerated** whenever `Resources/AppIcons/Blue.png` changes. The `.ico` is not auto-generated by the build — it's a committed binary.

To regenerate, create a multi-resolution ICO (16, 24, 32, 48, 64, 256px) from `Blue.png` using System.Drawing or any ICO tool, and overwrite `Resources/LittleLauncher.ico`. If the `.ico` is stale, the installer and exe will show an outdated icon.

## MSI Installer Shortcut

The MSI (`Package.wxs`) creates an initial Start Menu shortcut at `Programs\Little Launcher.lnk` using `Resources/LittleLauncher.ico` as the icon. On first launch, `EnsureStartMenuShortcuts()` in `MainWindow.xaml.cs` overwrites this shortcut with the user's chosen icon from `app-icon.ico`. The shortcut must be placed directly in `ProgramMenuFolder` (not a subfolder) so the app's runtime management can find and update it.

## Launcher Item Icons (Favicons & App Icons)

`FaviconService.FetchMissingItemIconsAsync(IEnumerable<LauncherItem>)` is the **unified pipeline** for fetching launcher item icons. It handles both websites (favicon download) and apps (exe icon extraction). All bulk import paths must use this single method — do not duplicate the fetch logic.

Entry points that call the pipeline:
- **Startup**: `MainWindow.FetchMissingIconsOnStartupAsync()` — fire-and-forget, covers settings-import-then-restart and machine migration scenarios.
- **Sync download**: `SftpSyncService.DownloadLauncherItemsAsync()` — awaited before save.
- **File import**: `LauncherItemsPage.ImportItems_Click()` — awaited before save.
- **Manual add/edit**: calls `FaviconService.FetchAndCacheAsync()` / `GetApplicationIcon()` directly for the single item in the dialog.

After any bulk icon change, call `FlyoutWindow.InvalidateItems()` so the flyout rebuilds its item containers on the next toggle. The flyout's `RebuildItemsIfNeeded()` nulls `ItemsSource` before reassigning to force full container recreation.

## Gotchas

- **MSIX VFS redirection** — see the [dedicated section below](#msix-vfs-redirection). All file paths referenced by external processes (shell `.lnk` files, companion exe) **must** use the physical path from `MainWindow.GetPhysicalAppDataDir()`, never raw `Environment.GetFolderPath(ApplicationData)`. The latter is VFS-redirected inside MSIX and invisible to the shell.
- **`LauncherItem.IconGlyph` must be a Unicode character**, not a text name. Use `"\uE774"` (globe) for websites and `"\uE8E5"` (open) for apps. Text strings like `"Globe24"` render as rectangle tofu in `FontIcon`.
- The bundled `.ico` is the Blue rocket only — it's the exe identity icon and fallback.
- `SaveResolvedIconToAppData()` always writes an `.ico` for all modes (including mode 0). There is no "delete and fall back to exe icon" path.
- The companion exe (`LauncherShortcut/Program.cs`) loads `app-icon.ico` from `AppContext.BaseDirectory` for the pin dialog via `LoadImage` + `WM_SETICON`.
- `BitmapToIcon()` produces multi-resolution ICO (16, 24, 32, 48, 64, 256) so tray icons render correctly at all DPI scales.
- `MainWindow` listens for `UISettings.ColorValuesChanged` and refreshes the tray icon, `app-icon.ico`, and SettingsWindow icon when the OS theme changes.
- When the user changes `TrayIconMode`, `UpdateTrayIcon()` also calls `SettingsWindow.RefreshIcon()` to update the settings window titlebar immediately.

## MSIX VFS Redirection

MSIX packages use **filesystem virtualization (VFS)**. When the packaged app calls `Environment.GetFolderPath(ApplicationData)`, it returns the standard `%AppData%` path, but writes are **silently redirected** to a package-specific location:

```
%LocalAppData%\Packages\<PackageFamilyName>\LocalCache\Roaming\
```

Files written to this redirected path are **invisible** to external processes (the Windows shell, the companion exe, any other non-packaged process) that read from the standard `%AppData%` path.

### The Problem

Icon files (`app-icon.ico`, `settings-icon.ico`) and the companion exe (`LittleLauncherFlyout.exe`) are written by the MSIX-packaged app. The shell reads `.lnk` shortcut `IconLocation` fields and the companion exe loads icon files — both operate outside the VFS.

### The Solution: `GetPhysicalAppDataDir()`

`MainWindow.GetPhysicalAppDataDir()` returns the **real filesystem path** that external processes can see:

- **MSIX**: `ApplicationData.Current.RoamingFolder.Path` → resolves to the `LocalCache\Roaming` path
- **Unpackaged/WiX**: `Environment.GetFolderPath(ApplicationData)` → standard `%AppData%` (no redirection)

### Rules

1. **Any path that will be referenced by the shell or external processes** (`.lnk` IconLocation, companion exe file reads, Start Menu shortcuts) → use `GetPhysicalAppDataDir()`.
2. **Internal-only paths** (settings.xml read/written only by the main app, favicon cache) → `Environment.GetFolderPath(ApplicationData)` is fine because VFS redirects reads too within the same package context.
3. **Companion exe** should use `AppContext.BaseDirectory` to find `app-icon.ico` — since the exe resides in the physical AppData dir, this resolves correctly for both MSIX and unpackaged.
4. **Never hardcode `%AppData%\LittleLauncher`** in any code path that writes files referenced externally. Always call `GetPhysicalAppDataDir()`.
