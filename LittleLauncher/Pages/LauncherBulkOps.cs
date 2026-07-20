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

            // Gecko profiles store the full profile path in DirectoryName; Chromium stores a relative subdirectory name
            string profileDir = lastBrowser.Engine == BrowserEngine.Gecko
                ? lastProfile.DirectoryName
                : Path.Combine(lastBrowser.ProfileDataDir, lastProfile.DirectoryName);

            var bookmarkRoots = lastBrowser.Engine == BrowserEngine.Gecko
                ? BookmarkImport.ReadGeckoBookmarks(profileDir)
                : BookmarkImport.ReadChromiumBookmarks(profileDir);

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
    /// Shows a TreeView dialog for selecting bookmarks from a folder hierarchy.
    /// Returns (selected URL nodes, goBack=false) on confirm, (null, goBack=true) on Back,
    /// or (null, goBack=false) on Cancel.
    /// </summary>
    private static async Task<(List<BookmarkNode>? Selected, bool GoBack)> ShowBookmarkSelectorAsync(XamlRoot xamlRoot,
        List<BookmarkNode> roots)
    {
        var nodeMap = new Dictionary<TreeViewNode, BookmarkNode>();
        var contentToNode = new Dictionary<BookmarkLabel, TreeViewNode>();

        TreeViewNode MakeNode(BookmarkNode bm, bool isRoot = false)
        {
            string text = bm.IsFolder
                ? $"{bm.Name}  ({bm.CountLeaves()})"
                : bm.Name;
            var label = new BookmarkLabel(text);
            var node = new TreeViewNode { Content = label, IsExpanded = isRoot };
            nodeMap[node] = bm;
            contentToNode[label] = node;
            foreach (var child in bm.Children)
                node.Children.Add(MakeNode(child));
            return node;
        }

        var treeView = new TreeView
        {
            SelectionMode = TreeViewSelectionMode.Multiple,
            MaxHeight = 420
        };

        int totalCount = roots.Sum(r => r.CountLeaves());
        foreach (var root in roots)
            treeView.RootNodes.Add(MakeNode(root, isRoot: true));

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

        // Flat list of all nodes for Select All / Deselect All
        var allNodes = new List<TreeViewNode>();
        void CollectNodes(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes) { allNodes.Add(n); CollectNodes(n.Children); }
        }
        CollectNodes(treeView.RootNodes);

        var selectedCountText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            FontSize = 13,
            Text = "None selected"
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

        bool batchUpdating = false;

        void UpdateCount()
        {
            int count = treeView.SelectedNodes.Count(n => nodeMap.TryGetValue(n, out var bm) && !bm.IsFolder);
            selectedCountText.Text = count > 0 ? $"{count} selected" : "None selected";
            dialog.IsPrimaryButtonEnabled = count > 0;
            dialog.PrimaryButtonText = count > 0 ? $"Import {count}" : "Import";
        }

        treeView.SelectionChanged += (_, _) => { if (!batchUpdating) UpdateCount(); };

        var selectAllButton = new HyperlinkButton { Content = "Select All", Padding = new Thickness(0) };
        var deselectAllButton = new HyperlinkButton { Content = "Deselect All", Padding = new Thickness(0) };

        selectAllButton.Click += (_, _) =>
        {
            batchUpdating = true;
            treeView.SelectedNodes.Clear();
            foreach (var n in allNodes) treeView.SelectedNodes.Add(n);
            batchUpdating = false;
            UpdateCount();
        };
        deselectAllButton.Click += (_, _) =>
        {
            batchUpdating = true;
            treeView.SelectedNodes.Clear();
            batchUpdating = false;
            UpdateCount();
        };

        var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolRow.Children.Add(selectAllButton);
        toolRow.Children.Add(new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.4 });
        toolRow.Children.Add(deselectAllButton);
        toolRow.Children.Add(new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.4 });
        toolRow.Children.Add(selectedCountText);

        var content = new StackPanel { Spacing = 8, MinWidth = 480 };
        content.Children.Add(toolRow);
        content.Children.Add(treeView);

        dialog.Content = content;

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary) return (null, true);   // Back
        if (result != ContentDialogResult.Primary) return (null, false);    // Cancel

        var selected = treeView.SelectedNodes
            .Where(n => nodeMap.TryGetValue(n, out var bm) && !bm.IsFolder)
            .Select(n => nodeMap[n])
            .ToList();

        return (selected, false);
    }
}
