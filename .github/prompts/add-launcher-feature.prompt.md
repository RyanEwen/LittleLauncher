---
description: "Add a new launcher item feature to the LauncherItem model, taskbar control, flyout, and editing UI."
agent: "agent"
---

Add a new feature to the launcher items in LittleLauncher.

## Steps

1. Extend the `LauncherItem` model in `LittleLauncher/Models/LauncherItem.cs`:
   - Add new properties with default values
   - Update constructors if needed

2. Update `LittleLauncher/Windows/FlyoutWindow.xaml` and `.cs`:
   - Display the new feature in the flyout popup

3. Update `LittleLauncher/Pages/LauncherItemsPage.xaml` and `.cs`:
   - Add editing controls for the new feature

4. Add any new string keys to `LittleLauncher/Resources/Localization/Dictionary-en-US.xaml`

5. Build and verify: `dotnet build LittleLauncher/LittleLauncher.csproj -c Debug`
