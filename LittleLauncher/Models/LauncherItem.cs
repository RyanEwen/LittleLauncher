using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Xml.Serialization;

namespace LittleLauncher.Models;

/// <summary>
/// Represents a single application or website shortcut in the little launcher.
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
    /// Segoe Fluent Icons glyph character (e.g. "\uE774" for globe).
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
    /// Whether this item is a Progressive Web App installed in a browser.
    /// PWA items store the browser exe in Path and --app-id args in Arguments.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPwa { get; set; }

    /// <summary>
    /// Whether this item is a heading (true) or a launchable item (false).
    /// Heading items only use the Name property; all other fields are ignored.
    /// Serialized as "IsCategory" for backward compatibility with older settings files.
    /// </summary>
    [ObservableProperty]
    [XmlElement("IsCategory")]
    public partial bool IsHeading { get; set; }

    /// <summary>
    /// Whether this item is a collapsible group that contains child items/headings.
    /// </summary>
    [ObservableProperty]
    public partial bool IsGroup { get; set; }

    /// <summary>
    /// Whether this group is currently expanded in the settings UI.
    /// Not serialized — purely transient UI state.
    /// </summary>
    [XmlIgnore]
    public bool IsExpanded { get; set; } = true;

    /// <summary>
    /// Child items belonging to this group. Only used when <see cref="IsGroup"/> is true.
    /// </summary>
    public ObservableCollection<LauncherItem> Children { get; set; } = [];

    public LauncherItem()
    {
        Name = string.Empty;
        Path = string.Empty;
        Arguments = string.Empty;
        IconGlyph = "\uE8E5";
        IconPath = string.Empty;
        IsWebsite = false;
        OpenInAppWindow = false;
        AppWindowBrowser = string.Empty;
        AppWindowBrowserProfile = string.Empty;
        IsPwa = false;
        IsHeading = false;
        IsGroup = false;
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
        IsPwa = false;
        IsHeading = false;
        IsGroup = false;
    }

    /// <summary>
    /// Creates a heading item with only a name.
    /// </summary>
    public static LauncherItem CreateHeading(string name) => new()
    {
        Name = name,
        IsHeading = true
    };

    /// <summary>
    /// Creates a group item with only a name.
    /// </summary>
    public static LauncherItem CreateGroup(string name) => new()
    {
        Name = name,
        IsGroup = true
    };

    /// <summary>
    /// Normalizes legacy glyph text names (e.g. "Globe24") to Unicode characters.
    /// Called after deserialization from XML import/sync to fix old data.
    /// </summary>
    public void NormalizeGlyph()
    {
        IconGlyph = IconGlyph switch
        {
            "Globe24" => "\uE774",
            "Open24" => "\uE8E5",
            _ => IconGlyph
        };
    }
}
