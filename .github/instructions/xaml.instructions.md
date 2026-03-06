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
