using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Classes;

/// <summary>
/// Shared chrome for the app's small owned windows (item editor, text prompt): the app icon
/// on the HWND/taskbar/Alt-Tab, plus a Mica-friendly custom title bar.
/// </summary>
/// <remarks>
/// These windows extend content into the title bar for the same reason
/// <c>SettingsWindow</c> does: a default WinUI title bar does not follow the app's
/// <c>RequestedTheme</c>, so it renders light chrome above dark content. Drawing our own
/// keeps the caption on-theme and lets Mica run the full height of the window.
/// </remarks>
internal static class WindowChrome
{
    private const double TitleBarHeight = 40;

    /// <summary>
    /// Resolves the icon to show: the user's chosen app icon if it has been generated,
    /// otherwise the packaged fallback.
    /// </summary>
    internal static string? ResolveAppIconPath()
    {
        try
        {
            string appData = Path.Combine(MainWindow.GetPhysicalAppDataDir(), "app-icon.ico");
            if (File.Exists(appData)) return appData;
        }
        catch { /* fall through to packaged icon */ }

        string fallback = Path.Combine(AppContext.BaseDirectory, "Resources", "LittleLauncher.ico");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// Sets the window icon for the title bar, taskbar and Alt-Tab.
    /// </summary>
    /// <remarks>
    /// Both paths are needed: <c>WM_SETICON</c> drives the HWND (taskbar/Alt-Tab) and
    /// <c>AppWindow.SetIcon</c> drives the WinUI title bar. The HICONs are destroyed after
    /// <c>SetIcon</c> copies them; the <c>WM_SETICON</c> handles are intentionally leaked for
    /// the window's lifetime, as the shell keeps referencing them.
    /// </remarks>
    internal static void ApplyIcon(IntPtr hwnd, string? icoPath = null)
    {
        icoPath ??= ResolveAppIconPath();
        if (icoPath == null || hwnd == IntPtr.Zero) return;

        try
        {
            var small = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            var big = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            if (small != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, ICON_SMALL, small);
            if (big != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, ICON_BIG, big);

            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            if (appWindow != null)
            {
                var native = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
                if (native != IntPtr.Zero)
                {
                    try { appWindow.SetIcon(Win32Interop.GetIconIdFromIcon(native)); }
                    finally { DestroyIcon(native); }
                }
            }
        }
        catch { /* a missing icon is cosmetic only */ }
    }

    /// <summary>
    /// Builds a draggable title bar row (icon + caption). Pass the result to
    /// <c>Window.SetTitleBar</c> and place it in row 0 of the window's root grid.
    /// </summary>
    internal static Grid BuildTitleBar(string title, string? icoPath = null)
    {
        icoPath ??= ResolveAppIconPath();

        var bar = new Grid
        {
            Height = TitleBarHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(12, 0, 0, 0),
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        if (icoPath != null)
        {
            try { icon.Source = new BitmapImage(new Uri(icoPath)); }
            catch { /* leave blank */ }
        }
        Grid.SetColumn(icon, 0);

        var caption = new TextBlock
        {
            Text = title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(caption, 1);

        bar.Children.Add(icon);
        bar.Children.Add(caption);
        return bar;
    }
}
