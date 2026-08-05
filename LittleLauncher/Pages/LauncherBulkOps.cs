using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Services;
using LittleLauncher.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Serialization;
using global::Windows.Storage.Pickers;
using WinRT.Interop;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Pages;

/// <summary>
/// Bulk operations on a launcher's items — JSON export/import and browser bookmark import.
/// </summary>
/// <remarks>
/// These are launcher-level operations rather than per-item editing, so they live on the
/// Launchers page card menu. Per-item editing moved into the flyout's edit mode.
/// </remarks>
internal static class LauncherBulkOps
{
    /// <summary>
    /// Saves, marks the change pending for auto-sync, and refreshes the flyout. Marking
    /// pending matters: saving alone lets a periodic sync download revert the import.
    /// </summary>
    private static void PersistBulkChange(Launcher launcher)
    {
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyItemsChanged();
        FlyoutWindow.InvalidateItems(launcher.Id);
    }

    /// <summary>Pickers are COM objects and need an owning HWND in a desktop app.</summary>
    private static void InitializePicker(object picker)
    {
        var window = SettingsWindow.GetCurrent();
        if (window == null) return;
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }

    public static async Task ExportItemsAsync(XamlRoot xamlRoot, Launcher launcher)
    {
        var items = launcher.Items;
        if (items.Count == 0) return;

        var picker = new FileSavePicker();
        InitializePicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "launcher-items";
        picker.FileTypeChoices.Add("JSON files", [".json"]);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var list = new List<LauncherItem>(items);
            string json = JsonSerializer.Serialize(list, SettingsManager.JsonOptions);
            await File.WriteAllTextAsync(file.Path, json);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(xamlRoot, "Export Failed", ex.Message);
        }
    }

    public static async Task ImportItemsAsync(XamlRoot xamlRoot, Launcher launcher)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".xml");

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        List<LauncherItem>? imported;
        try
        {
            if (file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var serializer = new XmlSerializer(typeof(List<LauncherItem>));
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read);
                imported = serializer.Deserialize(stream) as List<LauncherItem>;
            }
            else
            {
                string text = await File.ReadAllTextAsync(file.Path);
                imported = JsonSerializer.Deserialize<List<LauncherItem>>(text, SettingsManager.JsonOptions);
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(xamlRoot, "Import Failed", $"Could not read the file: {ex.Message}");
            return;
        }

        if (imported == null || imported.Count == 0)
        {
            await ShowMessageAsync(xamlRoot, "Import", "The file contained no items.");
            return;
        }

        var modeDialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Import Items",
            Content = $"Found {imported.Count} item(s). Replace all existing items or add to the current list?",
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Add",
            CloseButtonText = "Cancel"
        };

        var result = await modeDialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        if (result == ContentDialogResult.Primary)
            launcher.Items.Clear();

        foreach (var item in imported)
        {
            item.NormalizeGlyph();
            launcher.Items.Add(item);
        }

        PersistBulkChange(launcher);

        // IconPath changes fire INPC, so bindings update as icons arrive.
        await FaviconService.FetchMissingItemIconsAsync(imported);
        SettingsManager.SaveSettings();
        FlyoutWindow.InvalidateItems(launcher.Id);
    }

    private static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    // -- Bookmark import --

    /// <summary>Wraps a bookmark label string so TreeView can display it via ToString()
    /// and we can use reference equality for the ItemInvoked reverse lookup.</summary>
    private sealed class BookmarkLabel(string text)
    {
        public override string ToString() => text;
    }

    public static async Task ImportBookmarksAsync(XamlRoot xamlRoot, Launcher launcher)
    {
        var allBrowsers = BrowserCatalog.GetInstalledBrowsers();

        if (allBrowsers.Count == 0)
        {
            var d = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "No Supported Browsers Found",
                Content = "No supported browsers were found. Bookmark import supports Microsoft Edge, Google Chrome, Brave, Vivaldi, Chromium, Firefox, Zen, Waterfox, and LibreWolf.",
                CloseButtonText = "OK"
            };
            await d.ShowAsync();
            return;
        }

        KnownBrowser? lastBrowser = null;
        BrowserProfile? lastProfile = null;

        while (true)
        {
            var step1 = await ShowBookmarkBrowserPickerAsync(xamlRoot, allBrowsers, lastBrowser, lastProfile);
            if (step1 == null) return;
            (lastBrowser, lastProfile) = step1.Value;

            // Shared with the single-bookmark picker, so the engine switch and the
            // Gecko-vs-Chromium profile-path rule live in one place.
            var bookmarkRoots = BookmarkImport.ReadBookmarks(lastBrowser, lastProfile);

            if (bookmarkRoots.Count == 0 || bookmarkRoots.Sum(r => r.CountLeaves()) == 0)
            {
                var d = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "No Bookmarks Found",
                    Content = $"No web bookmarks were found in the selected {lastBrowser.DisplayName} profile.",
                    CloseButtonText = "OK"
                };
                await d.ShowAsync();
                continue;
            }

            var (selected, goBack) = await ShowBookmarkSelectorAsync(xamlRoot, bookmarkRoots);
            if (goBack) continue;
            if (selected == null || selected.Count == 0) return;

            var newItems = selected
                .Select(b => new LauncherItem { Name = b.Name, Path = b.Url!, IsWebsite = true })
                .ToList();

            foreach (var item in newItems)
                item.NormalizeGlyph();

            foreach (var item in newItems)
                launcher.Items.Add(item);

            PersistBulkChange(launcher);
            await FaviconService.FetchMissingItemIconsAsync(newItems);
            SettingsManager.SaveSettings();
            FlyoutWindow.InvalidateItems();
            return;
        }
    }

    /// <summary>
    /// Shows a dialog for the user to pick a browser and profile.
    /// Returns null if the user cancels. Pre-populates selections when returning via Back.
    /// </summary>
    private static async Task<(KnownBrowser Browser, BrowserProfile Profile)?> ShowBookmarkBrowserPickerAsync(XamlRoot xamlRoot,
        List<KnownBrowser> browsers, KnownBrowser? defaultBrowser, BrowserProfile? defaultProfile)
    {
        var browserCombo = new ComboBox
        {
            PlaceholderText = "Select browser",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var b in browsers)
            browserCombo.Items.Add(b.DisplayName);

        var profileCombo = new ComboBox
        {
            PlaceholderText = "Select profile",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false
        };

        List<BrowserProfile> currentProfiles = [];

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Import Browser Bookmarks",
            PrimaryButtonText = "Next",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        void PopulateProfiles()
        {
            profileCombo.Items.Clear();
            profileCombo.IsEnabled = false;
            if (browserCombo.SelectedIndex < 0) { currentProfiles = []; return; }

            var browser = browsers[browserCombo.SelectedIndex];
            currentProfiles = BrowserCatalog.GetBrowserProfiles(browser.ProfileDataDir, browser.Engine);
            if (currentProfiles.Count == 0)
            {
                profileCombo.PlaceholderText = "No profiles found";
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }
            foreach (var p in currentProfiles)
                profileCombo.Items.Add(p.DisplayName);
            profileCombo.IsEnabled = true;
            profileCombo.SelectedIndex = 0;
        }

        browserCombo.SelectionChanged += (_, _) =>
        {
            PopulateProfiles();
            dialog.IsPrimaryButtonEnabled = profileCombo.SelectedIndex >= 0;
        };
        profileCombo.SelectionChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = browserCombo.SelectedIndex >= 0 && profileCombo.SelectedIndex >= 0;
        };

        // Pre-select defaults when the user navigates Back from the bookmark picker
        if (defaultBrowser != null)
        {
            int idx = browsers.FindIndex(b => b.DisplayName == defaultBrowser.DisplayName);
            if (idx >= 0)
            {
                browserCombo.SelectedIndex = idx; // fires SelectionChanged → PopulateProfiles()
                if (defaultProfile != null)
                {
                    int pidx = currentProfiles.FindIndex(p => p.DirectoryName == defaultProfile.DirectoryName);
                    if (pidx >= 0) profileCombo.SelectedIndex = pidx;
                }
            }
        }

        var browserGroup = new StackPanel { Spacing = 4 };
        browserGroup.Children.Add(new TextBlock { Text = "Browser", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        browserGroup.Children.Add(browserCombo);

        var profileGroup = new StackPanel { Spacing = 4 };
        profileGroup.Children.Add(new TextBlock { Text = "Profile", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        profileGroup.Children.Add(profileCombo);

        var content = new StackPanel { Spacing = 16, MinWidth = 380 };
        content.Children.Add(new TextBlock
        {
            Text = "Select a browser and profile to import bookmarks from.",
            TextWrapping = TextWrapping.WrapWholeWords,
            Opacity = 0.7
        });
        content.Children.Add(browserGroup);
        content.Children.Add(profileGroup);

        dialog.Content = content;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        if (browserCombo.SelectedIndex < 0 || profileCombo.SelectedIndex < 0) return null;

        return (browsers[browserCombo.SelectedIndex], currentProfiles[profileCombo.SelectedIndex]);
    }

    /// <summary>
    /// Shows a searchable TreeView dialog for selecting bookmarks from a folder hierarchy.
    /// Returns (selected URL nodes, goBack=false) on confirm, (null, goBack=true) on Back,
    /// or (null, goBack=false) on Cancel.
    /// </summary>
    /// <remarks>
    /// <para>The tree is rebuilt on every keystroke, keeping folders that still have a matching
    /// descendant. A folder whose own name matches keeps all of its children, so searching for a
    /// folder name offers up that whole folder's worth in one go.</para>
    /// <para><b>Selection lives in <c>chosen</c>, not in the TreeView.</b> Filtering destroys and
    /// re-creates nodes, so a selection held only by the control would be silently dropped every
    /// time the search text changed — tick three bookmarks, search for something else, and those
    /// three would never be imported. The tree is authoritative only for the rows it is currently
    /// showing; everything outside the filter keeps the state it had.</para>
    /// </remarks>
    private static async Task<(List<BookmarkNode>? Selected, bool GoBack)> ShowBookmarkSelectorAsync(XamlRoot xamlRoot,
        List<BookmarkNode> roots)
    {
        var chosen = new HashSet<BookmarkNode>();
        var nodeMap = new Dictionary<TreeViewNode, BookmarkNode>();
        var contentToNode = new Dictionary<BookmarkLabel, TreeViewNode>();
        var visibleLeaves = new List<TreeViewNode>();
        bool batchUpdating = false;

        int totalCount = roots.Sum(r => r.CountLeaves());

        var treeView = new TreeView
        {
            SelectionMode = TreeViewSelectionMode.Multiple,
            MaxHeight = 380
        };

        var searchBox = new TextBox
        {
            PlaceholderText = "Search bookmarks",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var selectedCountText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            FontSize = 13,
            Text = "None selected"
        };

        var matchCountText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Opacity = 0.55
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"Select Bookmarks ({totalCount})",
            PrimaryButtonText = "Import",
            SecondaryButtonText = "← Back",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        static bool NodeMatches(BookmarkNode bm, string[] terms)
        {
            foreach (string term in terms)
            {
                if (bm.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                if (bm.Url?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) continue;
                return false;
            }
            return true;
        }

        void UpdateCount(int shown)
        {
            int count = chosen.Count;
            selectedCountText.Text = count > 0 ? $"{count} selected" : "None selected";
            dialog.IsPrimaryButtonEnabled = count > 0;
            dialog.PrimaryButtonText = count > 0 ? $"Import {count}" : "Import";
            matchCountText.Text = shown == totalCount ? "" : $"showing {shown} of {totalCount}";
        }

        // The tree speaks only for the rows it is showing; anything filtered out keeps its state.
        void SyncChosenFromTree()
        {
            if (batchUpdating) return;

            var selectedNow = treeView.SelectedNodes
                .Where(n => nodeMap.TryGetValue(n, out var bm) && !bm.IsFolder)
                .Select(n => nodeMap[n])
                .ToHashSet();

            foreach (var leaf in visibleLeaves)
            {
                var bm = nodeMap[leaf];
                if (selectedNow.Contains(bm)) chosen.Add(bm);
                else chosen.Remove(bm);
            }

            UpdateCount(visibleLeaves.Count);
        }

        // Returns the built node and how many bookmarks are under it, or null when nothing
        // inside it survives the filter.
        (TreeViewNode Node, int Leaves)? Build(BookmarkNode bm, string[] terms, bool isRoot, bool ancestorMatched)
        {
            bool selfMatches = terms.Length == 0 || NodeMatches(bm, terms);

            if (!bm.IsFolder)
            {
                if (!selfMatches && !ancestorMatched) return null;
                var leafLabel = new BookmarkLabel(bm.Name);
                var leafNode = new TreeViewNode { Content = leafLabel };
                nodeMap[leafNode] = bm;
                contentToNode[leafLabel] = leafNode;
                visibleLeaves.Add(leafNode);
                return (leafNode, 1);
            }

            var children = new List<TreeViewNode>();
            int leaves = 0;
            foreach (var child in bm.Children)
            {
                var built = Build(child, terms, isRoot: false, ancestorMatched || selfMatches);
                if (built == null) continue;
                children.Add(built.Value.Node);
                leaves += built.Value.Leaves;
            }

            if (children.Count == 0) return null;

            var label = new BookmarkLabel($"{bm.Name}  ({leaves})");
            // Searching expands everything: a match buried three folders deep is no use hidden.
            var node = new TreeViewNode { Content = label, IsExpanded = isRoot || terms.Length > 0 };
            nodeMap[node] = bm;
            contentToNode[label] = node;
            foreach (var child in children)
                node.Children.Add(child);
            return (node, leaves);
        }

        void Rebuild()
        {
            string[] terms = searchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            batchUpdating = true;
            treeView.RootNodes.Clear();
            nodeMap.Clear();
            contentToNode.Clear();
            visibleLeaves.Clear();

            foreach (var root in roots)
            {
                var built = Build(root, terms, isRoot: true, ancestorMatched: false);
                if (built != null) treeView.RootNodes.Add(built.Value.Node);
            }

            // Re-tick whatever was already chosen and is visible again.
            foreach (var leaf in visibleLeaves)
            {
                if (chosen.Contains(nodeMap[leaf]))
                    treeView.SelectedNodes.Add(leaf);
            }

            batchUpdating = false;
            UpdateCount(visibleLeaves.Count);
        }

        // Clicking a folder name toggles expand/collapse
        treeView.ItemInvoked += (_, args) =>
        {
            if (args.InvokedItem is BookmarkLabel invokedLabel &&
                contentToNode.TryGetValue(invokedLabel, out var node) &&
                nodeMap.TryGetValue(node, out var bm) && bm.IsFolder)
            {
                node.IsExpanded = !node.IsExpanded;
            }
        };

        treeView.SelectionChanged += (_, _) => SyncChosenFromTree();
        searchBox.TextChanged += (_, _) => Rebuild();

        // Both act on what is currently shown, so "Select All" under a search means "select all
        // matches" — which is the reason to search before selecting in the first place.
        var selectAllButton = new HyperlinkButton { Content = "Select All", Padding = new Thickness(0) };
        var deselectAllButton = new HyperlinkButton { Content = "Deselect All", Padding = new Thickness(0) };

        selectAllButton.Click += (_, _) =>
        {
            batchUpdating = true;
            foreach (var leaf in visibleLeaves)
            {
                chosen.Add(nodeMap[leaf]);
                if (!treeView.SelectedNodes.Contains(leaf))
                    treeView.SelectedNodes.Add(leaf);
            }
            batchUpdating = false;
            UpdateCount(visibleLeaves.Count);
        };
        deselectAllButton.Click += (_, _) =>
        {
            batchUpdating = true;
            foreach (var leaf in visibleLeaves)
                chosen.Remove(nodeMap[leaf]);
            treeView.SelectedNodes.Clear();
            batchUpdating = false;
            UpdateCount(visibleLeaves.Count);
        };

        var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolRow.Children.Add(selectAllButton);
        toolRow.Children.Add(new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.4 });
        toolRow.Children.Add(deselectAllButton);
        toolRow.Children.Add(new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.4 });
        toolRow.Children.Add(selectedCountText);
        toolRow.Children.Add(matchCountText);

        var content = new StackPanel { Spacing = 8, MinWidth = 480 };
        content.Children.Add(searchBox);
        content.Children.Add(toolRow);
        content.Children.Add(treeView);

        dialog.Content = content;

        Rebuild();

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary) return (null, true);   // Back
        if (result != ContentDialogResult.Primary) return (null, false);    // Cancel

        return (chosen.ToList(), false);
    }
}
