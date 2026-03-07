---
description: "Use when working with app icons, tray icons, shortcut icons, or window icons. Covers which icon files exist, where each icon surface pulls from, and how to update them correctly."
applyTo: "**/MainWindow.xaml.cs,**/SettingsWindow.xaml.cs,**/SystemPage.xaml*,**/LauncherShortcut/**"
---

# Icon System

LittleLauncher uses the Pin glyph (Segoe Fluent Icons `\uE840`) as its identity icon. Users can change the **tray icon** and **pinned taskbar icon** to a different preset or custom image, but all other icon surfaces always show the Pin.

## Icon Surfaces

| Surface | Source | User-configurable? |
|---|---|---|
| **System tray** | `ResolveTrayIcon()` → glyph or custom image | Yes (`TrayIconMode`) |
| **Pinned taskbar shortcut** | `app-icon.ico` in `%AppData%\LittleLauncher\` | Yes (follows `TrayIconMode`) |
| **Settings window titlebar** | `app-icon.ico` fallback to bundled `.ico` | Yes (follows `TrayIconMode`) |
| **Settings window taskbar entry** | Same as settings window titlebar | Yes (follows `TrayIconMode`) |
| **Start menu shortcut** | `app-icon.ico` via `GetShortcutIconLocation()` | Yes (follows `TrayIconMode`) |
| **Exe embedded icon** | `Resources/LittleLauncher.ico` (compiled into exe) | No — always Pin |
| **Pin-to-taskbar dialog** | `app-icon.ico` loaded via `WM_SETICON` in companion exe | Yes (follows `TrayIconMode`) |

## Key Files

- **`Resources/LittleLauncher.ico`** — Multi-resolution Pin glyph (16–256px, black foreground). Embedded into the exe at build time. This is the fallback icon for all surfaces. To regenerate, render the Pin glyph at each size using `Segoe Fluent Icons` font at 93.75% fill.
- **`%AppData%\LittleLauncher\app-icon.ico`** — Runtime-generated icon matching the current `TrayIconMode`. Written by `SaveResolvedIconToAppData()`. Used by shortcuts and window icons.
- **`Resources/TrayIcons/TrayWhite.png` / `TrayBlack.png`** — Legacy PNG assets, no longer used by any icon mode. Can be removed.

## TrayIconMode Values

| Mode | Glyph | Name |
|------|-------|------|
| 0 | `\uE840` | Pin (default) |
| 1 | `\uE734` | Star |
| 2 | `\uEB51` | Heart |
| 3 | `\uE945` | Lightning |
| 4 | `\uE721` | Search |
| 5 | `\uE774` | Globe |
| 6 | — | Custom (user file) |

All preset glyphs auto-detect OS theme: white on dark, black on light.

## How Icon Updates Flow

1. User changes `TrayIconMode` in Settings → `OnTrayIconModeChanged` fires
2. `ApplyTrayIconChange()` → `MainWindow.UpdateTrayIcon()`
3. `UpdateTrayIcon()` calls `ResolveTrayIcon()` (renders glyph or loads custom file) → sets `nIcon.Icon`
4. `UpdateTrayIcon()` calls `UpdateShortcutIcons()` → `SaveResolvedIconToAppData()` writes `app-icon.ico`
5. `UpdateShortcutIcons()` updates pinned taskbar `.lnk` files that target `LittleLauncherFlyout.exe`
6. Next time SettingsWindow opens, it picks up the new `app-icon.ico`

## Adding a New Preset Icon

1. Add entry to `PresetIcons` dictionary in `MainWindow.xaml.cs` with the next mode number
2. Add a `ComboBoxItem` with `FontIcon` + `TextBlock` in `SystemPage.xaml` (before `Custom...`)
3. Bump the Custom mode number in: `ResolveTrayIcon()`, `SaveResolvedIconToAppData()`, `SystemPage.xaml.cs` (`UpdateCustomIconCardVisibility`), and `UserSettings.cs` (`OnCustomTrayIconPathChanged`)
4. Update `TrayIconMode` comment in `UserSettings.cs`

## Gotchas

- The bundled `.ico` uses **black** foreground only (no theme awareness) — it's the exe identity icon, not a tray icon.
- `SaveResolvedIconToAppData()` always writes an `.ico` for all modes (including mode 0). There is no "delete and fall back to exe icon" path.
- The companion exe (`LauncherShortcut/Program.cs`) loads `app-icon.ico` from AppData for the pin dialog via `LoadImage` + `WM_SETICON`.
- `BitmapToIcon()` produces multi-resolution ICO (16, 24, 32, 48, 64, 256) so tray icons render correctly at all DPI scales.
- `MainWindow` listens for `UISettings.ColorValuesChanged` and refreshes the tray icon, `app-icon.ico`, and SettingsWindow icon when the OS theme changes.
- When the user changes `TrayIconMode`, `UpdateTrayIcon()` also calls `SettingsWindow.RefreshIcon()` to update the settings window titlebar immediately.
