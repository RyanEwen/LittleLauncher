// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace LittleLauncher.Pages;

/// <summary>
/// Picks a single bookmark out of an installed browser profile, with a search box.
/// </summary>
/// <remarks>
/// <para>Shares the reading half with the multi-select import flow in
/// <see cref="LauncherBulkOps"/> — <see cref="BookmarkImport.ReadBookmarks"/> and
/// <see cref="BrowserCatalog"/> — but not its presentation. That flow is a tree the user
/// browses to tick a set of bookmarks; this one answers "which single page?", where the user
/// already knows the answer and just needs to find it. So it flattens the tree
/// (<see cref="BookmarkImport.Flatten"/>) and filters as you type, with the folder path kept
/// only as context and as something else to match on.</para>
/// <para>A <c>ContentDialog</c> is fine here: unlike the flyout, every caller is a full-size
/// window with room for it.</para>
/// </remarks>
internal static class BookmarkPicker
{
    /// <summary>Remembered across calls so picking a second bookmark starts where the last ended.</summary>
    private static string? _lastBrowserName;
    private static string? _lastProfileDirectory;

    /// <summary>
    /// Shows the picker. Returns the chosen bookmark, or null if cancelled or unavailable.
    /// </summary>
    public static async Task<FlatBookmark?> PickAsync(XamlRoot xamlRoot)
    {
        var browsers = BrowserCatalog.GetInstalledBrowsers();
        if (browsers.Count == 0)
        {
            await ShowMessageAsync(xamlRoot, "No Browsers Found",
                "No supported browsers were found. Bookmarks can be read from Microsoft Edge, " +
                "Google Chrome, Brave, Vivaldi, Chromium, Firefox, Zen, Waterfox, and LibreWolf.");
            return null;
        }

        // ── Source pickers ──────────────────────────────────────────
        var browserCombo = new ComboBox { PlaceholderText = "Browser", MinWidth = 180 };
        foreach (var browser in browsers)
            browserCombo.Items.Add(browser.DisplayName);

        var profileCombo = new ComboBox { PlaceholderText = "Profile", MinWidth = 180, IsEnabled = false };

        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sourceRow.Children.Add(browserCombo);
        sourceRow.Children.Add(profileCombo);

        // ── Search + results ────────────────────────────────────────
        var searchBox = new TextBox
        {
            PlaceholderText = "Search bookmarks",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var results = new ObservableCollection<FlatBookmark>();
        var list = new ListView
        {
            ItemsSource = results,
            SelectionMode = ListViewSelectionMode.Single,
            Height = 320,
            ItemTemplate = (DataTemplate)XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <StackPanel Margin="0,4,0,4">
                        <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" />
                        <TextBlock Text="{Binding Url}" FontSize="12" Opacity="0.6" TextTrimming="CharacterEllipsis" />
                        <TextBlock Text="{Binding FolderPath}" FontSize="11" Opacity="0.4" TextTrimming="CharacterEllipsis" />
                    </StackPanel>
                </DataTemplate>
                """),
        };

        var statusText = new TextBlock { FontSize = 12, Opacity = 0.6 };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Choose a Bookmark",
            PrimaryButtonText = "Use",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        List<FlatBookmark> all = [];
        List<BrowserProfile> profiles = [];

        void ApplyFilter()
        {
            string[] terms = searchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matches = terms.Length == 0 ? all : all.Where(b => b.Matches(terms)).ToList();

            results.Clear();
            // Capped because the list is rebuilt on every keystroke and a large profile runs to
            // thousands of bookmarks; the search is how you reach the rest.
            foreach (var match in matches.Take(300))
                results.Add(match);

            statusText.Text = all.Count == 0
                ? "No bookmarks in this profile"
                : matches.Count > results.Count
                    ? $"{results.Count} of {matches.Count} matches — keep typing to narrow"
                    : $"{matches.Count} of {all.Count} bookmarks";

            dialog.IsPrimaryButtonEnabled = list.SelectedItem is FlatBookmark;
        }

        void LoadBookmarks()
        {
            all = [];
            if (browserCombo.SelectedIndex < 0 || profileCombo.SelectedIndex < 0)
            {
                ApplyFilter();
                return;
            }

            var browser = browsers[browserCombo.SelectedIndex];
            var profile = profiles[profileCombo.SelectedIndex];
            _lastBrowserName = browser.DisplayName;
            _lastProfileDirectory = profile.DirectoryName;

            try
            {
                all = BookmarkImport.Flatten(BookmarkImport.ReadBookmarks(browser, profile));
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Reading bookmarks failed for {Browser}", browser.DisplayName);
            }

            ApplyFilter();
        }

        void PopulateProfiles()
        {
            profileCombo.Items.Clear();
            profileCombo.IsEnabled = false;
            profiles = [];
            if (browserCombo.SelectedIndex < 0) return;

            profiles = BrowserCatalog.GetBrowserProfiles(browsers[browserCombo.SelectedIndex].ProfileDataDir,
                browsers[browserCombo.SelectedIndex].Engine);
            if (profiles.Count == 0)
            {
                profileCombo.PlaceholderText = "No profiles found";
                return;
            }

            foreach (var profile in profiles)
                profileCombo.Items.Add(profile.DisplayName);
            profileCombo.IsEnabled = true;

            int remembered = profiles.FindIndex(p => p.DirectoryName == _lastProfileDirectory);
            profileCombo.SelectedIndex = remembered >= 0 ? remembered : 0;
        }

        browserCombo.SelectionChanged += (_, _) => PopulateProfiles();   // selecting a profile loads
        profileCombo.SelectionChanged += (_, _) => LoadBookmarks();
        searchBox.TextChanged += (_, _) => ApplyFilter();
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is FlatBookmark;

        // Double-click is the natural "this one" gesture in a list of results. Hiding the dialog
        // this way reports ContentDialogResult.None, indistinguishable from Cancel — hence the
        // flag, so a double-click confirms rather than silently discarding the choice.
        bool confirmedByDoubleTap = false;
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is not FlatBookmark) return;
            confirmedByDoubleTap = true;
            dialog.Hide();
        };

        var content = new StackPanel { Spacing = 12, MinWidth = 460 };
        content.Children.Add(sourceRow);
        content.Children.Add(searchBox);
        content.Children.Add(list);
        content.Children.Add(statusText);
        dialog.Content = content;

        int rememberedBrowser = browsers.FindIndex(b => b.DisplayName == _lastBrowserName);
        browserCombo.SelectedIndex = rememberedBrowser >= 0 ? rememberedBrowser : 0;

        // Focus the search box, not the browser combo: the source is usually already right, and
        // the user came here to type a name.
        searchBox.Loaded += (_, _) => searchBox.Focus(FocusState.Programmatic);

        var result = await dialog.ShowAsync();
        bool confirmed = result == ContentDialogResult.Primary || confirmedByDoubleTap;
        return confirmed ? list.SelectedItem as FlatBookmark : null;
    }

    private static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}
