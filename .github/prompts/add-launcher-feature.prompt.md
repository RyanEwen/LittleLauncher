---
description: "Add a new launcher item feature to the LauncherItem model, taskbar control, flyout, and editing UI."
agent: "agent"
---

Add a new feature to the launcher items in SelfHostedHelper.

## Steps

1. Extend the `LauncherItem` model in `SelfHostedHelper/Models/LauncherItem.cs`:
   - Add new properties with default values
   - Update constructors if needed

2. Update `SelfHostedHelper/Windows/FlyoutWindow.xaml` and `.cs`:
   - Display the new feature in the flyout popup

3. Update `SelfHostedHelper/Pages/LauncherItemsPage.xaml` and `.cs`:
   - Add editing controls for the new feature

4. Add any new string keys to `SelfHostedHelper/Resources/Localization/Dictionary-en-US.xaml`

5. Build and verify: `dotnet build SelfHostedHelper/SelfHostedHelper.csproj -c Debug`
