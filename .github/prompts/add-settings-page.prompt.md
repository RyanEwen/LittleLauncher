---
description: "Scaffold a new settings page with XAML and code-behind, add NavigationViewItem, and register localization strings."
agent: "agent"
---

Create a new settings page for the SelfHostedHelper WPF application.

## Steps

1. Create `SelfHostedHelper/Pages/{PageName}Page.xaml` following the pattern of existing pages (e.g., HomePage.xaml, SystemPage.xaml):
   - Use WPF-UI controls (`ui:` namespace)
   - Reference strings via `{DynamicResource KeyName}`
   - Set `DataContext` or bind to `SettingsManager.Current` as needed

2. Create `SelfHostedHelper/Pages/{PageName}Page.xaml.cs` with:
   - Namespace: `SelfHostedHelper.Pages`
   - Inherit from `Page`
   - Constructor calls `InitializeComponent()`

3. Add a `NavigationViewItem` in `SelfHostedHelper/SettingsWindow.xaml`:
   - Set `TargetPageType` to the new page type
   - Set `Content` to a `{DynamicResource}` string key
   - Choose an appropriate `Icon` from Segoe Fluent Icons

4. Add all new string keys to `SelfHostedHelper/Resources/Localization/Dictionary-en-US.xaml`

5. Build and verify: `dotnet build SelfHostedHelper/SelfHostedHelper.csproj -c Debug -p:Platform=x64`
