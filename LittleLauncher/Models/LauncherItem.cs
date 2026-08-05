using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

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
    /// Optional hex color for the icon glyph (e.g. "#FF0000"). Empty means use the default theme color.
    /// Only applies when IconGlyph is displayed (no effect when IconPath image is used).
    /// </summary>
    [ObservableProperty]
    public partial string IconColor { get; set; }

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
    /// Whether this item is a collapsible group that contains child items/headings.
    /// </summary>
    [ObservableProperty]
    public partial bool IsGroup { get; set; }

    /// <summary>
    /// Whether this item is a column break. Column breaks split the flyout into multiple
    /// side-by-side columns. Not launchable — purely a structural divider.
    /// </summary>
    [ObservableProperty]
    public partial bool IsColumnBreak { get; set; }

    /// <summary>
    /// Whether this group is currently expanded in the settings UI.
    /// Not serialized — purely transient UI state.
    /// </summary>
    [JsonIgnore]
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
        IconColor = string.Empty;
        IsWebsite = false;
        OpenInAppWindow = false;
        AppWindowBrowser = string.Empty;
        AppWindowBrowserProfile = string.Empty;
        IsPwa = false;
        IsGroup = false;
        IsColumnBreak = false;
    }

    public LauncherItem(string name, string path, string iconGlyph, bool isWebsite = false, string arguments = "", string iconPath = "", bool openInAppWindow = false)
    {
        Name = name;
        Path = path;
        Arguments = arguments;
        IconGlyph = iconGlyph;
        IconPath = iconPath;
        IconColor = string.Empty;
        IsWebsite = isWebsite;
        OpenInAppWindow = openInAppWindow;
        AppWindowBrowser = string.Empty;
        AppWindowBrowserProfile = string.Empty;
        IsPwa = false;
        IsGroup = false;
        IsColumnBreak = false;
    }

    /// <summary>
    /// Creates a group item with only a name.
    /// </summary>
    public static LauncherItem CreateGroup(string name) => new()
    {
        Name = name,
        IsGroup = true
    };

    /// <summary>
    /// Creates a column-break item. Column breaks split the flyout into side-by-side columns.
    /// </summary>
    public static LauncherItem CreateColumnBreak() => new() { IsColumnBreak = true };

    /// <summary>
    /// The item as a person would name it.
    /// </summary>
    /// <remarks>
    /// A <c>ListViewItem</c> derives its accessible name from <c>ToString()</c> when the row is
    /// built from a template rather than plain text — which every flyout list is. Without this,
    /// a screen reader announced "LittleLauncher.Models.LauncherItem" for every shortcut in
    /// every launcher. Column breaks are invisible sentinels and headings have no target, so
    /// they say what they are rather than pretending to be launchable.
    /// </remarks>
    public override string ToString()
    {
        if (IsColumnBreak) return "Column break";
        if (IsGroup) return $"{Name} (group)";
        if (string.IsNullOrEmpty(Path)) return Name;   // heading
        return Name;
    }

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
