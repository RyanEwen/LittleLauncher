// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LittleLauncher.Classes.Settings;
using Microsoft.UI.Xaml;

namespace LittleLauncher.Classes;

/// <summary>
/// Manages the application theme settings and applies the selected theme (WinUI 3).
/// </summary>
internal static class ThemeManager
{
    public static void ApplySavedTheme(Window window)
    {
        ApplyTheme(SettingsManager.Current.AppTheme, window);
    }

    public static void ApplyAndSaveTheme(int theme)
    {
        SettingsManager.Current.AppTheme = theme;
        SettingsManager.SaveSettings();

        if ((Application.Current as App)?.m_window is MainWindow mw)
            ApplyTheme(theme, mw);
    }

    private static void ApplyTheme(int theme, Window window)
    {
        var requestedTheme = theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (window.Content is FrameworkElement fe)
            fe.RequestedTheme = requestedTheme;

        // Apply to settings window too
        var settingsWindow = SettingsWindow.GetCurrent();
        if (settingsWindow?.Content is FrameworkElement sfe)
            sfe.RequestedTheme = requestedTheme;

        // Apply to flyout window too
        var flyout = Windows.FlyoutWindow.GetCurrent();
        if (flyout?.Content is FrameworkElement ffe)
            ffe.RequestedTheme = requestedTheme;
    }

    private static readonly global::Windows.UI.ViewManagement.UISettings s_uiSettings = new();

    public static bool IsDarkTheme()
    {
        var fg = s_uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground);
        // Bright foreground = dark theme
        return fg.R > 128;
    }
}