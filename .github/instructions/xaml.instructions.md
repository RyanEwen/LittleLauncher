---
description: "Use when editing XAML files for WinUI 3 controls, Fluent Design, NavigationView pages, or resource dictionaries. Covers WinUI 3 control conventions, resource localization, and Mica/Acrylic backdrop patterns."
applyTo: "**/*.xaml"
---

# WinUI 3 XAML Conventions

## Namespaces

Standard WinUI 3 pages use these default namespaces:
```xml
xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
```

## Localization

- String resources live in `Resources/Localization/Dictionary-en-US.xaml`
- Reference via `{StaticResource KeyName}` in XAML (resource dictionary is merged in App.xaml)
- In code: `Application.Current.Resources.TryGetValue("KeyName", out object value)`
- Always add new string keys to the dictionary when adding UI text

## Pages

- Pages are `Page` objects (not `UserControl`)
- Navigation via WinUI 3 `NavigationView` with `TargetPageType` in XAML
- No MVVM routing framework — direct page type references

## Controls

- Use `Border` with `{ThemeResource CardBackgroundFillColorDefaultBrush}` and `{ThemeResource CardStrokeColorDefaultBrush}` for settings cards
- Use `ToggleSwitch` for boolean settings
- Use `NumberBox` for numeric inputs
- Use `FontIcon` with Segoe Fluent Icons glyphs

## Data Binding

- Bind to `SettingsManager.Current.<Property>` for settings
- Use `Mode=TwoWay` for editable settings
- Use `UpdateSourceTrigger=PropertyChanged` when immediate feedback needed

## Backdrops

- **SettingsWindow** uses `MicaBackdrop`
- **FlyoutWindow** uses a transparent backdrop

## Code-Built Dialogs

The add/edit item dialog in `LauncherItemsPage` is built entirely in C# (not XAML). Conventions:

- All input controls (`TextBox`, `ComboBox`, `ToggleSwitch`) use `HorizontalAlignment = HorizontalAlignment.Stretch` to fill the dialog width uniformly.
- The form container is a `StackPanel` with `MinWidth = 400`.
- When a row needs a stretch input + a fixed button (e.g. app path + Browse), use a `Grid` with `Star` + `Auto` column definitions instead of a horizontal `StackPanel`.
- Labels are created via a `Label(string)` helper that returns a styled `TextBlock`.

## Drag-and-Drop (LauncherItemsPage)

ListViews use `CanDragItems="True"` with custom handlers — **never `CanReorderItems`**, which cannot be overridden for cross-list drops. See `drag-drop.instructions.md` for full details.

## Group Expand/Collapse

Groups use a manual `StackPanel` with `Tag="GroupRoot"` / `Tag="GroupChildren"` and a toggle button — **not WinUI Expanders**. This allows the entire group card to be a drag source. The `Loaded` event on `GroupRoot` restores `IsExpanded` state after `RefreshList()` rebuilds the visual tree.
