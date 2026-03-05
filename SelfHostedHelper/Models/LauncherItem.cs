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

    public LauncherItem()
    {
        Name = string.Empty;
        Path = string.Empty;
        Arguments = string.Empty;
        IconGlyph = "Open24";
        IconPath = string.Empty;
        IsWebsite = false;
    }

    public LauncherItem(string name, string path, string iconGlyph, bool isWebsite = false, string arguments = "", string iconPath = "")
    {
        Name = name;
        Path = path;
        Arguments = arguments;
        IconGlyph = iconGlyph;
        IconPath = iconPath;
        IsWebsite = isWebsite;
    }
}
