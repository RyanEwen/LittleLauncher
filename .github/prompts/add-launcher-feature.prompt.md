---
description: "Add a new launcher item feature to the LauncherItem model, taskbar control, flyout, and editing UI."
agent: "agent"
---

Add a new feature to the launcher items in SelfHostedHelper.

## Steps

1. Extend the `LauncherItem` model in `SelfHostedHelper/Models/LauncherItem.cs`:
   - Add new properties with default values
   - Update constructors if needed

2. Update `SelfHostedHelper/Controls/TaskbarLauncherControl.xaml` and `.cs`:
   - Render the new feature in the taskbar widget

3. Update `SelfHostedHelper/Windows/FlyoutWindow.xaml` and `.cs`:
   - Display the new feature in the flyout popup

4. Update `SelfHostedHelper/Pages/LauncherItemsPage.xaml` and `.cs`:
   - Add editing controls for the new feature

5. Add any new string keys to `SelfHostedHelper/Resources/Localization/Dictionary-en-US.xaml`

6. Build and verify: `dotnet build SelfHostedHelper/SelfHostedHelper.csproj -c Debug -p:Platform=x64`
