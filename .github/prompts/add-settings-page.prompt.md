---
description: "Scaffold a new settings page with XAML and code-behind, add NavigationViewItem, and register localization strings."
agent: "agent"
---

Create a new settings page for the LittleLauncher WPF application.

## Steps

1. Create `LittleLauncher/Pages/{PageName}Page.xaml` following the pattern of existing pages (e.g., HomePage.xaml, SystemPage.xaml):
   - Use WPF-UI controls (`ui:` namespace)
   - Reference strings via `{DynamicResource KeyName}`
   - Set `DataContext` or bind to `SettingsManager.Current` as needed

2. Create `LittleLauncher/Pages/{PageName}Page.xaml.cs` with:
   - Namespace: `LittleLauncher.Pages`
   - Inherit from `Page`
   - Constructor calls `InitializeComponent()`

3. Add a `NavigationViewItem` in `LittleLauncher/SettingsWindow.xaml`:
   - Set `TargetPageType` to the new page type
   - Set `Content` to a `{DynamicResource}` string key
   - Choose an appropriate `Icon` from Segoe Fluent Icons

4. Add all new string keys to `LittleLauncher/Resources/Localization/Dictionary-en-US.xaml`

5. Build and verify: `dotnet build LittleLauncher/LittleLauncher.csproj -c Debug -p:Platform=x64`
