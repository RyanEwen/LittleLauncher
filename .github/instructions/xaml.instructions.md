---
description: "Use when editing XAML files for WPF-UI controls, Fluent Design, NavigationView pages, or resource dictionaries. Covers WPF-UI control conventions, DynamicResource localization, and Mica backdrop patterns."
applyTo: "**/*.xaml"
---

# WPF-UI XAML Conventions

## Namespaces

Always include these for WPF-UI pages:
```xml
xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
```

## Localization

- String resources live in `Resources/Localization/Dictionary-en-US.xaml`
- Reference via `{DynamicResource KeyName}` in XAML
- In code: `Application.Current.TryFindResource("KeyName")`
- Always add new string keys to the dictionary when adding UI text

## Pages

- Pages are `Page` objects (not `UserControl`)
- Navigation via WPF-UI `NavigationView` with `TargetPageType` in XAML
- No MVVM routing framework — direct page type references

## WPF-UI Controls

- Use `ui:CardControl`, `ui:CardAction` for settings cards
- Use `ui:TextBlock` with `FontTypography` for consistent typography
- Use `ui:ToggleSwitch` instead of `CheckBox` for boolean settings
- Use `ui:NumberBox` for numeric inputs

## Data Binding

- Bind to `SettingsManager.Current.<Property>` for settings
- Use `Mode=TwoWay` for editable settings
- Use `UpdateSourceTrigger=PropertyChanged` when immediate feedback needed
