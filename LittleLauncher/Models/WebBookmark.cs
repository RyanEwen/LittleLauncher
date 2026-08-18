// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using CommunityToolkit.Mvvm.ComponentModel;

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

    public WebBookmark() { }

    public WebBookmark(string name, string url)
    {
        Name = name;
        Url = url;
    }
}
