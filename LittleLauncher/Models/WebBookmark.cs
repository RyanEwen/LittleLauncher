// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace LittleLauncher.Models;

/// <summary>
/// One entry in a web launcher's bookmark bar.
/// </summary>
/// <remarks>
/// <para>Deliberately its own type rather than a reused <see cref="LauncherItem"/>. An item is a
/// thing to <em>launch</em> — it carries a glyph, colour, arguments, PWA and app-window flags,
/// group children, column breaks — and none of that means anything for a strip of links inside a
/// browser. Keeping them separate also keeps `Items` unambiguous: it is shortcuts, for shortcut
/// launchers, whatever kind the launcher is.</para>
/// <para>Observable because the icon arrives after the bookmark does: it is fetched on add, and
/// replaced later by whatever the page itself declares once it has been visited with a signed-in
/// browser.</para>
/// </remarks>
public partial class WebBookmark : ObservableObject
{
    /// <summary>Label shown in the bar, beside the icon.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    /// <summary>The page this bookmark opens.</summary>
    [ObservableProperty]
    public partial string Url { get; set; } = "";

    /// <summary>Local path to the cached icon, or empty until one has been fetched.</summary>
    [ObservableProperty]
    public partial string IconPath { get; set; } = "";

    /// <summary>Show this one as its icon alone, without its label.</summary>
    /// <remarks>
    /// <para>Per bookmark, and separate from the launcher's <c>WebBookmarkIconsOnly</c>, which does
    /// the same to all of them. Both exist because they answer different questions: a bar of
    /// familiar sites wants every label gone, while a bar with one awkwardly long name wants that
    /// one collapsed and the rest left readable — and a launcher-wide switch cannot express the
    /// second.</para>
    /// <para>The launcher-wide setting wins where they disagree. It is the blunter instrument and
    /// the one the user reached for last; a bookmark left un-collapsed under "icons only" would be
    /// the setting quietly failing to do what it says.</para>
    /// <para>Off by default, which is both the shipped behaviour and the direction that survives
    /// <c>WhenWritingDefault</c>.</para>
    /// </remarks>
    [ObservableProperty]
    public partial bool IconsOnly { get; set; }

    /// <summary>Whether this is a folder holding other bookmarks rather than an address.</summary>
    /// <remarks>
    /// <para>The same shape a group takes in an item launcher (<c>LauncherItem.IsGroup</c> and its
    /// <c>Children</c>), deliberately: two collections of the same idea should not be two designs.
    /// A folder has no <see cref="Url"/> and nothing should read one from it.</para>
    /// <para><b>The model nests without limit; the bar does not.</b> Folders hold bookmarks and not
    /// other folders, because that is what a bookmarks bar is actually used for and it keeps the
    /// bar one menu deep. That is a rule the UI applies, not one the data enforces, so allowing
    /// deeper later needs no migration.</para>
    /// </remarks>
    [ObservableProperty]
    public partial bool IsFolder { get; set; }

    /// <summary>The bookmarks inside this folder. Only meaningful when <see cref="IsFolder"/>.</summary>
    public ObservableCollection<WebBookmark> Children { get; set; } = [];

    public WebBookmark() { }

    public WebBookmark(string name, string url)
    {
        Name = name;
        Url = url;
    }

    /// <summary>A new, empty folder.</summary>
    public static WebBookmark CreateFolder(string name) => new() { Name = name, IsFolder = true };

    /// <summary>This bookmark, or every bookmark inside it when it is a folder.</summary>
    /// <remarks>
    /// What a surface with no nesting of its own wants - a taskbar jump list has no submenus, and
    /// a search for "which bookmark is this page" does not care where it sits.
    /// </remarks>
    public IEnumerable<WebBookmark> Flatten()
    {
        if (!IsFolder)
        {
            yield return this;
            yield break;
        }

        foreach (var child in Children)
        {
            foreach (var nested in child.Flatten())
                yield return nested;
        }
    }
}
