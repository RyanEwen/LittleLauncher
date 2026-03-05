using CommunityToolkit.Mvvm.ComponentModel;

namespace SelfHostedHelper.Models;

/// <summary>
/// Represents a single application or website shortcut in the taskbar launcher.
/// </summary>
public partial class LauncherItem : ObservableObject
{
    /// <summary>
    /// Display name shown in the launcher and settings.
    /// </summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>
    /// Full path to an executable, or a URL (http/https) for websites.
    /// </summary>
    [ObservableProperty]
    public partial string Path { get; set; }

    /// <summary>
    /// Optional arguments passed when launching an executable.
    /// </summary>
    [ObservableProperty]
    public partial string Arguments { get; set; }

    /// <summary>
    /// Segoe Fluent Icons glyph name (e.g. "Globe24", "Desktop24").
    /// Used as the fallback icon when no favicon is available.
    /// </summary>
    [ObservableProperty]
    public partial string IconGlyph { get; set; }

    /// <summary>
    /// Local file path to a cached favicon or custom icon image.
    /// When set, this takes priority over IconGlyph.
    /// </summary>
    [ObservableProperty]
    public partial string IconPath { get; set; }

    /// <summary>
    /// Whether this is a website (true) or a local application (false).
    /// </summary>
    [ObservableProperty]
    public partial bool IsWebsite { get; set; }

    /// <summary>
    /// For website items, opens the URL in an app-style standalone browser window.
    /// </summary>
    [ObservableProperty]
    public partial bool OpenInAppWindow { get; set; }

    /// <summary>
    /// Path to the browser executable used for app-window mode.
    /// Empty string means auto-detect (tries Edge, then Chrome, then other Chromium browsers).
    /// </summary>
    [ObservableProperty]
    public partial string AppWindowBrowser { get; set; }

    /// <summary>
    /// Browser profile directory name for app-window mode (e.g. "Default", "Profile 1").
    /// Empty string means use an isolated sandbox profile per URL.
    /// </summary>
    [ObservableProperty]
    public partial string AppWindowBrowserProfile { get; set; }

    /// <summary>
    /// Whether this item is a category heading (true) or a launchable item (false).
    /// Category items only use the Name property; all other fields are ignored.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCategory { get; set; }

    public LauncherItem()
    {
        Name = string.Empty;
        Path = string.Empty;
        Arguments = string.Empty;
        IconGlyph = "Open24";
        IconPath = string.Empty;
        IsWebsite = false;
        OpenInAppWindow = false;
        AppWindowBrowser = string.Empty;
        AppWindowBrowserProfile = string.Empty;
        IsCategory = false;
    }

    public LauncherItem(string name, string path, string iconGlyph, bool isWebsite = false, string arguments = "", string iconPath = "", bool openInAppWindow = false)
    {
        Name = name;
        Path = path;
        Arguments = arguments;
        IconGlyph = iconGlyph;
        IconPath = iconPath;
        IsWebsite = isWebsite;
        OpenInAppWindow = openInAppWindow;
        AppWindowBrowser = string.Empty;
        AppWindowBrowserProfile = string.Empty;
        IsCategory = false;
    }

    /// <summary>
    /// Creates a category heading item with only a name.
    /// </summary>
    public static LauncherItem CreateCategory(string name) => new()
    {
        Name = name,
        IsCategory = true
    };
}
