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
/// <para><b>The chooser and its host are separate</b> (<see cref="BookmarkPickerView"/>), because
/// the two callers cannot share one. From a settings window a <c>ContentDialog</c> is fine: it is
/// a full-size window with room for it. From the web flyout it is not: a <c>ContentDialog</c>
/// renders inside its host's content area and cannot overflow the HWND, and a flyout is often a
/// few hundred pixels each way. That caller gets <see cref="Windows.BookmarkPickerWindow"/>
/// instead, which is the same rule <c>ItemEditorWindow</c> and <c>TextPromptWindow</c> follow.</para>
/// </remarks>
internal static class BookmarkPicker
{
    /// <summary>True when there is at least one browser to read bookmarks out of.</summary>
    /// <remarks>Asked by each host before building anything, so the "none installed" message is
    /// theirs to word for the surface it appears on.</remarks>
    public static bool AnyBrowsersInstalled => BrowserCatalog.GetInstalledBrowsers().Count > 0;

    /// <summary>The sentence every host says when there is nothing to pick from.</summary>
    public const string NoBrowsersMessage =
        "No supported browsers were found. Bookmarks can be read from Microsoft Edge, " +
        "Google Chrome, Brave, Vivaldi, Chromium, Firefox, Zen, Waterfox, and LibreWolf.";

    /// <summary>
    /// Shows the picker in a <c>ContentDialog</c>. Returns the chosen bookmark, or null if
    /// cancelled or unavailable. For callers that are a full-size window.
    /// </summary>
    public static async Task<FlatBookmark?> PickAsync(XamlRoot xamlRoot)
    {
        if (!AnyBrowsersInstalled)
        {
            await ShowMessageAsync(xamlRoot, "No Browsers Found", NoBrowsersMessage);
            return null;
        }

        var view = new BookmarkPickerView();

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Choose a Bookmark",
            PrimaryButtonText = "Use",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            Content = view.Root,
        };

        view.CanConfirmChanged += can => dialog.IsPrimaryButtonEnabled = can;

        // Double-click is the natural "this one" gesture in a list of results. Hiding the dialog
        // this way reports ContentDialogResult.None, indistinguishable from Cancel, hence the
        // flag, so a double-click confirms rather than silently discarding the choice.
        bool confirmedByDoubleTap = false;
        view.Confirmed += () =>
        {
            confirmedByDoubleTap = true;
            dialog.Hide();
        };

        var result = await dialog.ShowAsync();
        bool confirmed = result == ContentDialogResult.Primary || confirmedByDoubleTap;
        return confirmed ? view.Selected : null;
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

/// <summary>
/// The chooser itself: browser, profile, a search box and the matching bookmarks, with no opinion
/// about what is holding it.
/// </summary>
/// <remarks>
/// It reports rather than commands: <see cref="CanConfirmChanged"/> for whether a selection exists,
/// <see cref="Confirmed"/> for the double-click that means "this one". A host turns those into
/// whatever its own accept affordance is: a dialog's primary button, or a window's.
/// </remarks>
internal sealed class BookmarkPickerView
{
    /// <summary>Remembered across calls so picking a second bookmark starts where the last ended.</summary>
    private static string? _lastBrowserName;
    private static string? _lastProfileDirectory;

    private readonly ListView _list;
    private readonly TextBox _searchBox;

    /// <summary>Raised with whether a bookmark is selected. Drives the host's accept button.</summary>
    public event Action<bool>? CanConfirmChanged;

    /// <summary>Raised when the user double-clicks a result, which means "take this one and close".</summary>
    public event Action? Confirmed;

    /// <summary>The chooser's UI, for the host to place.</summary>
    public FrameworkElement Root { get; }

    /// <summary>The bookmark currently picked, or null.</summary>
    public FlatBookmark? Selected => _list.SelectedItem as FlatBookmark;

    public BookmarkPickerView()
    {
        var browsers = BrowserCatalog.GetInstalledBrowsers();

        // ── Source pickers ──────────────────────────────────────────
        var browserCombo = new ComboBox { PlaceholderText = "Browser", MinWidth = 180 };
        foreach (var browser in browsers)
            browserCombo.Items.Add(browser.DisplayName);

        var profileCombo = new ComboBox { PlaceholderText = "Profile", MinWidth = 180, IsEnabled = false };

        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sourceRow.Children.Add(browserCombo);
        sourceRow.Children.Add(profileCombo);

        // ── Search + results ────────────────────────────────────────
        _searchBox = new TextBox
        {
            PlaceholderText = "Search bookmarks",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var results = new ObservableCollection<FlatBookmark>();
        _list = new ListView
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

        List<FlatBookmark> all = [];
        List<BrowserProfile> profiles = [];

        void ApplyFilter()
        {
            string[] terms = _searchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

            CanConfirmChanged?.Invoke(Selected != null);
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
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        _list.SelectionChanged += (_, _) => CanConfirmChanged?.Invoke(Selected != null);

        _list.DoubleTapped += (_, _) =>
        {
            if (Selected == null) return;
            Confirmed?.Invoke();
        };

        var content = new StackPanel { Spacing = 12, MinWidth = 460 };
        content.Children.Add(sourceRow);
        content.Children.Add(_searchBox);
        content.Children.Add(_list);
        content.Children.Add(statusText);
        Root = content;

        int rememberedBrowser = browsers.FindIndex(b => b.DisplayName == _lastBrowserName);
        browserCombo.SelectedIndex = rememberedBrowser >= 0 ? rememberedBrowser : 0;

        // Focus the search box, not the browser combo: the source is usually already right, and
        // the user came here to type a name.
        _searchBox.Loaded += (_, _) => _searchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Puts the caret in the search box. For a host that opens after the box has loaded.</summary>
    public void FocusSearch() => _searchBox.Focus(FocusState.Programmatic);
}
