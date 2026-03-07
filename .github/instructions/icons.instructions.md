---
description: "Use when working with app icons, tray icons, shortcut icons, or window icons. Covers which icon files exist, where each icon surface pulls from, and how to update them correctly."
applyTo: "**/MainWindow.xaml.cs,**/SettingsWindow.xaml.cs,**/SystemPage.xaml*,**/LauncherShortcut/**"
---

# Icon System

Little Launcher uses a flat upright rocket as its identity icon. Users can change the **tray icon** and **pinned taskbar icon** to a different color variant, a glyph preset, or a custom image.

## Icon Surfaces

| Surface | Source | User-configurable? |
|---|---|---|
| **System tray** | `ResolveTrayIcon()` → preset PNG, glyph, or custom image | Yes (`TrayIconMode`) |
| **Pinned taskbar shortcut** | `app-icon.ico` in `%AppData%\LittleLauncher\` | Yes (follows `TrayIconMode`) |
| **Settings window titlebar** | `settings-icon.ico` (app icon + gear overlay) | Yes (follows `TrayIconMode`) |
| **Settings window taskbar entry** | `settings-icon.ico` via `WM_SETICON` + `AppWindow.SetIcon(IconId)` + `ITaskbarList3.SetOverlayIcon` gear badge | Yes (follows `TrayIconMode`) |
| **Settings window Alt-Tab** | `settings-icon.ico` via `AppWindow.SetIcon(IconId)` | Yes (follows `TrayIconMode`) |
| **Start menu shortcut** | `app-icon.ico` via `GetShortcutIconLocation()` | Yes (follows `TrayIconMode`) |
| **Exe embedded icon** | `Resources/LittleLauncher.ico` (compiled into exe) | No — always Blue rocket |
| **Pin-to-taskbar dialog** | `app-icon.ico` loaded via `WM_SETICON` in companion exe | Yes (follows `TrayIconMode`) |

## Key Files

- **`Resources/LittleLauncher.ico`** — Multi-resolution Blue rocket (16–256px). Embedded into the exe at build time. This is the fallback icon for all surfaces. Generated from `Resources/AppIcons/Blue.png`.
- **`Resources/AppIcons/*.png`** — Preset icon PNGs (Blue, Green, Teal, Red, Orange, Purple). Flat upright rockets stretched 20% horizontally for a wider profile. Copied to output at build time. Loaded at runtime by `RenderPresetIcon()`.
- **`%AppData%\LittleLauncher\app-icon.ico`** — Runtime-generated icon matching the current `TrayIconMode`. Written by `SaveResolvedIconToAppData()`. Used by shortcuts and window icons.
- **`%AppData%\LittleLauncher\settings-icon.ico`** — Runtime-generated icon: the current app icon composited with a gear glyph overlay (dark circle + white gear in bottom-right corner). Written by `SaveSettingsIconToAppData()`. Used by the Settings window.
- **`Resources/TrayIcons/TrayWhite.png` / `TrayBlack.png`** — Legacy PNG assets, no longer used by any icon mode. Can be removed.

## TrayIconMode Values

| Mode | Type | Source |
|------|------|--------|
| 0 | Blue (default) | `AppIcons/Blue.png` |
| 1 | Green | `AppIcons/Green.png` |
| 2 | Teal | `AppIcons/Teal.png` |
| 3 | Red | `AppIcons/Red.png` |
| 4 | Orange | `AppIcons/Orange.png` |
| 5 | Purple | `AppIcons/Purple.png` |
| 6 | Pin glyph | Segoe Fluent Icons `\uE718` |
| 7 | Star glyph | Segoe Fluent Icons `\uE734` |
| 8 | Heart glyph | Segoe Fluent Icons `\uEB51` |
| 9 | Lightning glyph | Segoe Fluent Icons `\uEA80` |
| 10 | Search glyph | Segoe Fluent Icons `\uE721` |
| 11 | Globe glyph | Segoe Fluent Icons `\uE774` |
| 12 | Custom | User-provided file |

Preset icons (0–5) are full-color PNGs — they do **not** change with OS theme.
Glyph presets (6–11) render in black (light theme) or white (dark theme) and update automatically on theme change.

## How Icon Updates Flow

1. User changes `TrayIconMode` in Settings → `OnTrayIconModeChanged` fires
2. `ApplyTrayIconChange()` → `MainWindow.UpdateTrayIcon()`
3. `UpdateTrayIcon()` calls `ResolveTrayIcon()` (loads preset PNG, glyph, or custom file) → sets `nIcon.Icon`
4. `UpdateTrayIcon()` calls `UpdateShortcutIcons()` → `SaveResolvedIconToAppData()` writes `app-icon.ico`
5. `UpdateTrayIcon()` calls `SaveSettingsIconToAppData()` → writes `settings-icon.ico` (app icon + gear overlay)
6. `UpdateShortcutIcons()` updates pinned taskbar `.lnk` files that target `LittleLauncherFlyout.exe`
7. `SettingsWindow.RefreshIcon()` reloads `settings-icon.ico` into titlebar, taskbar, and overlay

## Settings Window Icon Strategy

WinUI 3 has a known bug (WindowsAppSDK#2730) where the taskbar ignores `AppWindow.SetIcon()` for
windows in the same process as the exe's embedded icon. The workaround uses three layers:

1. **`SetWindowAppUserModelId(hwnd, "LittleLauncher.Settings")`** — gives the Settings window its own
   taskbar group via the Shell `IPropertyStore` COM API, so the taskbar treats it independently.
2. **`AppWindow.SetIcon(IconId)`** via `GetIconIdFromIcon` interop — sets the app-level icon for
   Alt-Tab and the window's identity.
3. **`ITaskbarList3.SetOverlayIcon`** — adds a gear badge overlay on the taskbar button.
4. **`WM_SETICON`** (ICON_SMALL + ICON_BIG) — sets the Win32 window icon, re-sent on `Activated`
   to counteract WinUI's framework overrides.

## Adding a New Preset Icon

1. Add the PNG file to `Resources/AppIcons/` (transparent background, square)
2. Add entry to `PresetIcons` dictionary in `MainWindow.xaml.cs` with the next mode number
3. Add a `ComboBoxItem` with colored `Ellipse` + `TextBlock` in `SystemPage.xaml` (before `Custom...`)
4. Bump the Custom mode number in: `ResolveTrayIcon()`, `SaveResolvedIconToAppData()`, `SystemPage.xaml.cs` (`UpdateCustomIconCardVisibility`), and `UserSettings.cs` (`OnCustomTrayIconPathChanged`)
5. Add `<Content Include="Resources/AppIcons/NewColor.png">` to `.csproj` (or use the existing `*.png` glob)
6. Update `TrayIconMode` comment in `UserSettings.cs`

## Gotchas

- The bundled `.ico` is the Blue rocket only — it's the exe identity icon and fallback.
- `SaveResolvedIconToAppData()` always writes an `.ico` for all modes (including mode 0). There is no "delete and fall back to exe icon" path.
- The companion exe (`LauncherShortcut/Program.cs`) loads `app-icon.ico` from AppData for the pin dialog via `LoadImage` + `WM_SETICON`.
- `BitmapToIcon()` produces multi-resolution ICO (16, 24, 32, 48, 64, 256) so tray icons render correctly at all DPI scales.
- `MainWindow` listens for `UISettings.ColorValuesChanged` and refreshes the tray icon, `app-icon.ico`, and SettingsWindow icon when the OS theme changes.
- When the user changes `TrayIconMode`, `UpdateTrayIcon()` also calls `SettingsWindow.RefreshIcon()` to update the settings window titlebar immediately.
