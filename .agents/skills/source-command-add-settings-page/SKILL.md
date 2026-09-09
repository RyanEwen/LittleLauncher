---
name: "source-command-add-settings-page"
description: "Scaffold a new settings page with XAML and code-behind, add NavigationViewItem, and register localization strings."
---

# source-command-add-settings-page

Use this skill when the user asks to run the migrated source command `add-settings-page`.

## Command Template

Create a new settings page for the Little Launcher WinUI 3 application.

## Steps

1. Create `LittleLauncher/Pages/{PageName}Page.xaml` following the pattern of existing pages (e.g., HomePage.xaml, SystemPage.xaml):
   - Use WinUI 3 controls
   - Reference strings via `{StaticResource KeyName}`
   - Set `DataContext` or bind to `SettingsManager.Current` as needed

2. Create `LittleLauncher/Pages/{PageName}Page.xaml.cs` with:
   - Namespace: `LittleLauncher.Pages`
   - Inherit from `Page`
   - Constructor calls `InitializeComponent()`

3. Add a `NavigationViewItem` in `LittleLauncher/SettingsWindow.xaml`:
   - Set `TargetPageType` to the new page type
   - Set `Content` to a `{StaticResource}` string key
   - Choose an appropriate `Icon` from Segoe Fluent Icons

4. Add all new string keys to `LittleLauncher/Resources/Localization/Dictionary-en-US.xaml`

5. Build and verify through the [sideloaded Release MSIX workflow](../source-command-rebuild/SKILL.md)

See [.codex/docs/xaml.md](../../../.codex/docs/xaml.md) for the full WinUI 3 XAML conventions.
