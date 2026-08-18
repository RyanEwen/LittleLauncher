// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Services;
using LittleLauncher.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Pages;

public sealed partial class LaunchersPage : Page
{
    /// <summary>
    /// Set before navigating to LaunchersPage to auto-open the settings dialog for this launcher.
    /// </summary>
    internal static Launcher? PendingSettingsLauncher { get; set; }

    public LaunchersPage()
    {
        InitializeComponent();
        RebuildLauncherCards();

        if (PendingSettingsLauncher is not null)
        {
            // Defer to Loaded — XamlRoot is null during the constructor,
            // so the ContentDialog can't show until the page is in the visual tree.
            Loaded += LaunchersPage_PendingSettingsLoaded;
        }
    }

    private void LaunchersPage_PendingSettingsLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LaunchersPage_PendingSettingsLoaded;
        if (PendingSettingsLauncher is { } pending)
        {
            PendingSettingsLauncher = null;
            _ = ShowLauncherSettingsDialog(pending);
        }
    }

    // ── Build the UI dynamically (one card per launcher) ──────────────

    /// <summary>Rebuilds one card per launcher, in their own container.</summary>
    /// <remarks>
    /// The cards used to share the page's panel with the heading and the Add button, and this
    /// emptied it down to its last child — so the first rebuild, which happens in the constructor,
    /// deleted the "Launchers" heading and its subtitle. They now live outside
    /// <c>LauncherCardsPanel</c>, which this owns entirely and can simply clear.
    /// </remarks>
    private void RebuildLauncherCards()
    {
        LauncherCardsPanel.Children.Clear();

        foreach (var launcher in SettingsManager.Current.Launchers)
            LauncherCardsPanel.Children.Add(BuildLauncherCard(launcher));
    }

    private static int CountLauncherItems(IEnumerable<LauncherItem> items)
    {
        int count = 0;
        foreach (var item in items)
        {
            if (item.IsColumnBreak) continue;
            if (item.IsGroup)
                count += CountLauncherItems(item.Children);
            else
                count++;
        }
        return count;
    }

    private Border BuildLauncherCard(Launcher launcher)
    {
        // ── Items row (clickable drill-in with chevron) ─────────────
        // A web launcher has no items, so the same row reports its address instead and drills
        // in to the one place that can change it.
        int itemCount = CountLauncherItems(launcher.Items);
        bool isWeb = launcher.IsWebLauncher;

        var itemsLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        itemsLabel.Children.Add(new TextBlock { Text = isWeb ? "Web Page" : "Items", FontSize = 14 });
        itemsLabel.Children.Add(new TextBlock
        {
            Text = isWeb
                ? (string.IsNullOrWhiteSpace(launcher.WebAddress) ? "No web address set" : launcher.WebAddress)
                : $"{itemCount} item{(itemCount == 1 ? "" : "s")}",
            FontSize = 12,
            Opacity = 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var chevron = new FontIcon
        {
            Glyph = "",
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var itemsRowInner = new Grid();
        itemsRowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        itemsRowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(itemsLabel, 0);
        Grid.SetColumn(chevron, 1);
        itemsRowInner.Children.Add(itemsLabel);
        itemsRowInner.Children.Add(chevron);

        var itemsRow = new Button
        {
            Content = itemsRowInner,
            Tag = launcher,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        if (isWeb)
            itemsRow.Click += async (_, _) => await ShowLauncherSettingsDialog(launcher);
        else
            itemsRow.Click += ItemsBulkOps_Click;

        // ── Delete button (in header bar) ──────────────────────────
        var deleteBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            Tag = launcher,
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        deleteBtn.Click += DeleteLauncher_Click;

        // ── Settings row (opens settings dialog) ────────────────
        var settingsLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        settingsLabel.Children.Add(new TextBlock { Text = "Settings", FontSize = 14 });
        settingsLabel.Children.Add(new TextBlock { Text = "Name, icon, view mode, and more", FontSize = 12, Opacity = 0.5 });

        var settingsChevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var settingsRowInner = new Grid();
        settingsRowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsRowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(settingsLabel, 0);
        Grid.SetColumn(settingsChevron, 1);
        settingsRowInner.Children.Add(settingsLabel);
        settingsRowInner.Children.Add(settingsChevron);

        var settingsRow = new Button
        {
            Content = settingsRowInner,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        settingsRow.Click += async (s, e) =>
        {
            await ShowLauncherSettingsDialog(launcher);
            RebuildLauncherCards();
        };

        // ── Card container ──────────────────────────────────────────
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(settingsRow);
        content.Children.Add(itemsRow);

        var headerIconElement = BuildLauncherHeaderIcon(launcher);
        var headerTitle = new TextBlock { Text = launcher.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerLeft.Children.Add(headerIconElement);
        headerLeft.Children.Add(headerTitle);

        // ── Shared badge ────────────────────────────────────────────
        if (launcher.IsShared)
        {
            string badgeText = launcher.SharedTwoWay
                ? "Shared"
                : (launcher.IsSharedOwner ? "Shared (owner)" : "Subscribed");

            bool isAccent = launcher.SharedTwoWay || launcher.IsSharedOwner;
            var badge = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isAccent)
                badge.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            else
            {
                badge.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
                badge.BorderThickness = new Thickness(1);
            }

            var badgeFg = isAccent
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            badgeStack.Children.Add(new FontIcon { Glyph = "\uE72D", FontSize = 10, Foreground = badgeFg });
            badgeStack.Children.Add(new TextBlock { Text = badgeText, FontSize = 11, Foreground = badgeFg, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            badge.Child = badgeStack;
            headerLeft.Children.Add(badge);
        }

        // ── Header buttons (sync, settings, share, delete) ─────────
        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

        if (launcher.IsShared)
        {
            var syncBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE895", FontSize = 12 },
                Tag = launcher,
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(syncBtn, "Sync now");
            syncBtn.Click += SyncSharedLauncher_Click;
            headerButtons.Children.Add(syncBtn);

            var settingsBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE713", FontSize = 12 },
                Tag = launcher,
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(settingsBtn, "Sharing settings");
            settingsBtn.Click += ShareLauncher_Click;
            headerButtons.Children.Add(settingsBtn);
        }

        // Sharing publishes a launcher's items, of which a web launcher has none.
        if (!launcher.IsShared && !isWeb)
        {
            var shareBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE72D", FontSize = 12 },
                Tag = launcher,
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(shareBtn, "Share this launcher");
            shareBtn.Click += ShareLauncher_Click;
            headerButtons.Children.Add(shareBtn);
        }

        // ── Move up / down buttons ─────────────────────────────────
        var launchers = SettingsManager.Current.Launchers;
        int launcherIndex = launchers.IndexOf(launcher);

        var moveUpBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE70E", FontSize = 12 },
            Tag = launcher,
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
            IsEnabled = launcherIndex > 0,
        };
        ToolTipService.SetToolTip(moveUpBtn, "Move up");
        moveUpBtn.Click += MoveLauncherUp_Click;
        headerButtons.Children.Add(moveUpBtn);

        var moveDownBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE70D", FontSize = 12 },
            Tag = launcher,
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
            IsEnabled = launcherIndex < launchers.Count - 1,
        };
        ToolTipService.SetToolTip(moveDownBtn, "Move down");
        moveDownBtn.Click += MoveLauncherDown_Click;
        headerButtons.Children.Add(moveDownBtn);

        headerButtons.Children.Add(deleteBtn);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerLeft, 0);
        Grid.SetColumn(headerButtons, 1);
        header.Children.Add(headerLeft);
        header.Children.Add(headerButtons);

        var innerStack = new StackPanel { Spacing = 8 };
        innerStack.Children.Add(header);
        innerStack.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 0, 0, 0),
        });
        innerStack.Children.Add(content);

        var card = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8),
            Child = innerStack,
            Tag = launcher,
        };

        return card;
    }

    /// <summary>
    /// Builds an icon chooser button with a gallery flyout for selecting the launcher's tray icon.
    /// Returns the button and a custom icon path row (visible only in Custom mode).
    /// </summary>

    /// <summary>
    /// Opens launcher settings in its own window (shared with the flyout) and refreshes the
    /// cards afterwards so name and icon changes show up.
    /// </summary>
    private async Task ShowLauncherSettingsDialog(Launcher launcher, bool isNewLauncher = false)
    {
        IntPtr owner = IntPtr.Zero;
        var settingsWindow = SettingsWindow.GetCurrent();
        if (settingsWindow != null)
            owner = WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow);

        await LauncherSettingsWindow.ShowAsync(launcher, owner, isNewLauncher: isNewLauncher);
        RebuildLauncherCards();
    }

    internal async Task ShowLauncherSettingsDialogPublic(Launcher launcher) => await ShowLauncherSettingsDialog(launcher);
    private static FrameworkElement BuildLauncherHeaderIcon(Launcher launcher)
    {
        string mode = launcher.TrayIconMode;

        if (TrayIconModes.IsGlyphMode(mode))
        {
            string glyph = TrayIconModes.GetGlyphCharacter(mode) ?? "";
            string? colorHex = TrayIconModes.GetGlyphColor(mode);
            SolidColorBrush? colorBrush = null;
            if (!string.IsNullOrEmpty(colorHex))
            {
                try
                {
                    string h = colorHex.TrimStart('#');
                    if (h.Length == 6)
                    {
                        byte cr = Convert.ToByte(h[..2], 16);
                        byte cg = Convert.ToByte(h[2..4], 16);
                        byte cb = Convert.ToByte(h[4..6], 16);
                        colorBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, cr, cg, cb));
                    }
                }
                catch { /* ignore */ }
            }

            if (IconGallery.IsFluentGlyph(glyph))
            {
                var icon = new FontIcon { Glyph = glyph, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                if (colorBrush != null) icon.Foreground = colorBrush;
                return icon;
            }
            else
            {
                var tb = new TextBlock { Text = glyph, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                if (colorBrush != null) tb.Foreground = colorBrush;
                return tb;
            }
        }

        if (mode == TrayIconModes.Custom && !string.IsNullOrEmpty(launcher.CustomTrayIconPath) && File.Exists(launcher.CustomTrayIconPath))
        {
            return new Image { Source = new BitmapImage(new Uri(launcher.CustomTrayIconPath)), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        }

        string iconsDir = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons");
        string[] colorNames = ["Blue", "Green", "Teal", "Red", "Orange", "Purple"];
        if (colorNames.Contains(mode))
        {
            string pngPath = Path.Combine(iconsDir, $"{mode}.png");
            if (File.Exists(pngPath))
                return new Image { Source = new BitmapImage(new Uri(pngPath)), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        }

        (string glyph, string label)[] namedGlyphs = [
            ("\uE840", "Pin"), ("\uE734", "Star"), ("\uEB51", "Heart"),
            ("\uE945", "Lightning"), ("\uE721", "Search"), ("\uE774", "Globe"),
        ];
        var match = namedGlyphs.FirstOrDefault(g => g.label == mode);
        if (match.glyph != null)
            return new FontIcon { Glyph = match.glyph, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

        // Default: composite icon
        return new FontIcon { Glyph = "\uF0E2", FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    // ── Event handlers ──────────────────────────────────────────────

    private async void AddLauncherButton_Click(object sender, RoutedEventArgs e)
    {
        var newLauncher = new Launcher
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"Launcher {SettingsManager.Current.Launchers.Count + 1}",
            // Both set here rather than as model defaults: changing a default would flip it on for
            // every *existing* launcher, since the property is omitted from settings.json when it
            // holds its default value.
            ShowTitle = true,
            // And for WebSharedProfile that would be worse than cosmetic — it decides which folder
            // a launcher's cookies live in, so flipping it under an existing launcher silently
            // swaps its browser profile and signs it out.
            WebSharedProfile = true,
        };
        SettingsManager.Current.Launchers.Add(newLauncher);
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyLaunchersChanged();

        // Tell MainWindow to create a tray icon for the new launcher
        MainWindow.Current?.RefreshTrayIcons();

        RebuildLauncherCards();

        // Show settings dialog for new launcher
        await ShowLauncherSettingsDialog(newLauncher, isNewLauncher: true);
        RebuildLauncherCards();

        // A brand-new launcher is empty, so there is nothing on screen to discover editing
        // from. Open it in edit mode; the flyout's own empty-state text takes it from there.
        // A web launcher has no items to edit — its address was set in the settings window
        // that just closed, so there is nothing left to do.
        if (MainWindow.Current is { } owner && !newLauncher.IsWebLauncher)
            FlyoutWindow.ShowInEditMode(owner, newLauncher.Id);
    }

    /// <summary>
    /// Creates a web launcher and opens its settings, where the address is the only thing left
    /// to supply.
    /// </summary>
    /// <remarks>
    /// The kind is otherwise only discoverable by creating an ordinary launcher and noticing the
    /// Type dropdown, which nobody does unprompted. A button that makes the capability visible at
    /// the moment someone is already thinking about launchers beats explaining it after the fact
    /// — and unlike the one-time upgrade notice, it keeps working for everyone who installs later.
    /// </remarks>
    private async void AddWebLauncherButton_Click(object sender, RoutedEventArgs e)
    {
        var newLauncher = new Launcher
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Web Launcher",
            Kind = LauncherKinds.Web,
            ShowTitle = true,

            // One signed-in browser behind every web launcher unless the user asks otherwise. See
            // AddLauncherButton_Click for why this is set at creation and not as a model default.
            WebSharedProfile = true,
        };
        SettingsManager.Current.Launchers.Add(newLauncher);
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyLaunchersChanged();

        MainWindow.Current?.RefreshTrayIcons();
        RebuildLauncherCards();

        // No edit mode afterwards, unlike a shortcut launcher: a web launcher has no items, and
        // its settings window is the whole of its setup.
        await ShowLauncherSettingsDialog(newLauncher, isNewLauncher: true);
        RebuildLauncherCards();
    }

    /// <summary>
    /// Bulk operations on a launcher's items. Per-item editing is done in the flyout's edit
    /// mode; only whole-list operations remain here.
    /// </summary>
    private void ItemsBulkOps_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;

        // Shared launchers the user does not own must not be modified locally.
        bool readOnly = launcher is { IsShared: true, IsSharedOwner: false };

        var menu = new MenuFlyout();

        // First entry, because this row used to be the drill-in to the item editor — it
        // should still answer "how do I edit this?".
        var edit = new MenuFlyoutItem
        {
            Text = "Edit items",
            Icon = new FontIcon { Glyph = "" },
            IsEnabled = !readOnly,
        };
        edit.Click += (_, _) =>
        {
            if (MainWindow.Current is { } main)
                FlyoutWindow.ShowInEditMode(main, launcher.Id);
        };
        menu.Items.Add(edit);
        menu.Items.Add(new MenuFlyoutSeparator());

        var export = new MenuFlyoutItem { Text = "Export items…", Icon = new FontIcon { Glyph = "" } };
        export.Click += async (_, _) => await LauncherBulkOps.ExportItemsAsync(XamlRoot, launcher);
        menu.Items.Add(export);

        var import = new MenuFlyoutItem { Text = "Import items…", Icon = new FontIcon { Glyph = "" }, IsEnabled = !readOnly };
        import.Click += async (_, _) => { await LauncherBulkOps.ImportItemsAsync(XamlRoot, launcher); RebuildLauncherCards(); };
        menu.Items.Add(import);

        menu.Items.Add(new MenuFlyoutSeparator());

        var bookmarks = new MenuFlyoutItem { Text = "Import browser bookmarks…", Icon = new FontIcon { Glyph = "" }, IsEnabled = !readOnly };
        bookmarks.Click += async (_, _) => { await LauncherBulkOps.ImportBookmarksAsync(XamlRoot, launcher); RebuildLauncherCards(); };
        menu.Items.Add(bookmarks);

        menu.ShowAt(fe);
    }

    private void MoveLauncherUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;
        var launchers = SettingsManager.Current.Launchers;
        int idx = launchers.IndexOf(launcher);
        if (idx <= 0) return;
        launchers.Move(idx, idx - 1);
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyLaunchersChanged();
        MainWindow.Current?.RefreshTrayIcons();
        RebuildLauncherCards();
    }

    private void MoveLauncherDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;
        var launchers = SettingsManager.Current.Launchers;
        int idx = launchers.IndexOf(launcher);
        if (idx < 0 || idx >= launchers.Count - 1) return;
        launchers.Move(idx, idx + 1);
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyLaunchersChanged();
        MainWindow.Current?.RefreshTrayIcons();
        RebuildLauncherCards();
    }


    private async void DeleteLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;

        if (SettingsManager.Current.Launchers.Count <= 1)
        {
            await ShowErrorDialog("You must keep at least one launcher.");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Delete Launcher",
            Content = $"Delete the launcher \"{launcher.Name}\" and all its items? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // Dispose whichever panel this launcher owns
        LauncherPanels.Dispose(launcher.Id);

        // And its browser profile, which is hundreds of megabytes for a chat app and was left on
        // disk for good — the folder is named after the launcher id, so once the launcher is gone
        // nothing can identify it again. After Dispose, so no browser still has its files open.
        Services.WebProfileCleanupService.DeleteFor(launcher);

        SettingsManager.Current.Launchers.Remove(launcher);
        SettingsManager.SaveSettings();

        // Tell MainWindow to remove the tray icon
        MainWindow.Current?.RefreshTrayIcons();

        RebuildLauncherCards();
    }

    // ── Shared launcher handlers ───────────────────────────────────

    private async void AddSharedLauncherButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddSharedLauncherDialog();
    }

    private async void ShareLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Launcher launcher)
            await ShowShareLauncherDialog(launcher);
    }

    private async void SyncSharedLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;

        string? password = null;
        if (!SftpSyncService.HasAutoKeyForShared(launcher))
        {
            password = await ShowPasswordPrompt();
            if (password == null) return; // user cancelled
        }

        bool canPush = launcher.SharedTwoWay || launcher.IsSharedOwner;
        bool canPull = launcher.SharedTwoWay || !launcher.IsSharedOwner;
        bool ok = true;
        string msg = "";

        if (canPush)
        {
            (ok, msg) = await SftpSyncService.ShareLauncherAsync(launcher, password);
        }
        if (ok && canPull)
        {
            (ok, msg) = await SftpSyncService.SyncSharedLauncherAsync(launcher, password);
        }

        if (!ok)
        {
            await ShowErrorDialog(msg);
            return;
        }

        // Refresh flyouts after sync
        FlyoutWindow.InvalidateAllItems();

        RebuildLauncherCards();
    }

    private async Task ShowShareLauncherDialog(Launcher launcher)
    {
        var (formPanel, modeCombo, pathBox, hostBox, portBox, userBox, keyBox, directionCombo,
             davUrlBox, davUserBox, davPasswordBox, oneDriveLinkBox) = BuildShareForm(launcher, isSubscribing: false);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = launcher.IsShared
                ? "Sharing Settings"
                : "Share Launcher",
            Content = new ScrollViewer { Content = formPanel, MaxHeight = 400 },
            PrimaryButtonText = launcher.IsShared ? "Update" : "Share",
            SecondaryButtonText = launcher.IsShared
                ? "Stop Sharing"
                : null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            // Stop sharing
            launcher.IsShared = false;
            launcher.IsSharedOwner = false;
            launcher.SharedTwoWay = false;
            launcher.SharedSyncMode = 0;
            launcher.SharedPath = "";
            launcher.SharedSftpHost = "";
            launcher.SharedSftpPort = 22;
            launcher.SharedSftpUsername = "";
            launcher.SharedSftpPrivateKeyPath = "";

            // The password is outside settings.json, so clearing the launcher's fields would
            // otherwise leave a live credential on disk for a launcher that is no longer shared.
            WebDavSharedStore.ClearPassword(launcher);
            launcher.SharedWebDavUrl = "";
            launcher.SharedWebDavUsername = "";

            // Leaving these behind would have a re-shared launcher silently publish to the old
            // file — and keep the old link live for whoever already had it.
            launcher.SharedLinkUrl = "";
            launcher.SharedItemId = "";
            launcher.SharedDriveId = "";

            SettingsManager.SaveSettings();
            RebuildLauncherCards();
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        int mode = modeCombo.SelectedIndex;
        string path = pathBox.Text.Trim();

        if (mode == SharedSyncModes.OneDrive)
        {
            var store = CloudSyncService.StoreFor(SyncProviders.OneDrive);
            if (store is not { IsSignedIn: true })
            {
                await ShowErrorDialog("Sign in to OneDrive on the Cloud Sync page first.");
                return;
            }

            // Incremental consent, asked for at the moment it is justified rather than at
            // sign-in: publishing needs write access outside the private app folder.
            if (!OneDriveSharedStore.HasConsent)
            {
                var (granted, consentMessage) = await OneDriveSharedStore.RequestConsentAsync();
                if (!granted)
                {
                    await ShowErrorDialog(consentMessage);
                    return;
                }
            }

            // Empty is the normal case for an owner — the link is minted on the first push.
            launcher.SharedLinkUrl = oneDriveLinkBox.Text.Trim();
        }
        else if (mode == SharedSyncModes.WebDav)
        {
            if (string.IsNullOrWhiteSpace(davUrlBox.Text) || string.IsNullOrWhiteSpace(davUserBox.Text))
            {
                await ShowErrorDialog("WebDAV needs a file URL and a username.");
                return;
            }

            launcher.SharedWebDavUrl = davUrlBox.Text.Trim();
            launcher.SharedWebDavUsername = davUserBox.Text.Trim();

            // An empty box means "keep what is stored", so re-opening the dialog to change the
            // direction does not silently wipe a working password.
            if (davPasswordBox.Password.Length > 0)
                WebDavSharedStore.SetPassword(launcher, davPasswordBox.Password);

            if (!WebDavSharedStore.HasCredentials(launcher))
            {
                await ShowErrorDialog("Enter the WebDAV password.");
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                await ShowErrorDialog("Path is required.");
                return;
            }

            if (mode == SharedSyncModes.Sftp && string.IsNullOrWhiteSpace(hostBox.Text))
            {
                await ShowErrorDialog("SFTP host is required.");
                return;
            }
        }

        launcher.SharedSyncMode = mode;
        launcher.SharedPath = path;
        launcher.SharedSftpHost = hostBox.Text.Trim();
        launcher.SharedSftpPort = int.TryParse(portBox.Text, out int p) ? p : 22;
        launcher.SharedSftpUsername = userBox.Text.Trim();
        launcher.SharedSftpPrivateKeyPath = keyBox.Text.Trim();

        bool isTwoWay = directionCombo.SelectedIndex == 0;
        launcher.SharedTwoWay = isTwoWay;
        launcher.IsShared = true;
        if (!isTwoWay)
            launcher.IsSharedOwner = true;
        SettingsManager.SaveSettings();

        // Initial push
        string? password = null;
        if (!SftpSyncService.HasAutoKeyForShared(launcher))
        {
            password = await ShowPasswordPrompt();
            if (password == null) { RebuildLauncherCards(); return; }
        }

        var (ok, msg) = await SftpSyncService.ShareLauncherAsync(launcher, password);
        if (!ok)
            await ShowErrorDialog(msg);
        else if (launcher.IsOneDriveSync && launcher.SharedLinkUrl.Length > 0)
            await ShowShareLinkDialog(launcher);

        SettingsManager.SaveSettings();
        RebuildLauncherCards();
    }

    /// <summary>
    /// Show the link a cloud share just produced, ready to send.
    /// </summary>
    /// <remarks>
    /// The point of sharing this way is the link, so it has to appear the moment it exists rather
    /// than being buried in a settings dialog the owner would have to go looking for. Selectable
    /// and copyable, because the alternative is retyping a hundred-character URL by hand.
    /// </remarks>
    private async Task ShowShareLinkDialog(Launcher launcher)
    {
        var linkBox = new TextBox
        {
            Text = launcher.SharedLinkUrl,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 90,
        };

        var copyButton = new Button { Content = "Copy link", Margin = new Thickness(0, 8, 0, 0) };
        copyButton.Click += (_, _) =>
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(launcher.SharedLinkUrl);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            copyButton.Content = "Copied";
        };

        var panel = new StackPanel { Spacing = 4, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "Send this to whoever should get the launcher. Anyone with the link can open "
                 + "and edit it, so treat it like a password.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(linkBox);
        panel.Children.Add(copyButton);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = $"'{launcher.Name}' is shared",
            Content = panel,
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    private async Task ShowAddSharedLauncherDialog()
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "Launcher name",
            Text = "Shared Launcher",
            Margin = new Thickness(0, 0, 0, 12),
        };

        var tempLauncher = new Launcher();
        var (formPanel, modeCombo, pathBox, hostBox, portBox, userBox, keyBox, directionCombo,
             davUrlBox, davUserBox, davPasswordBox, oneDriveLinkBox) = BuildShareForm(tempLauncher, isSubscribing: true);

        var fullPanel = new StackPanel { Spacing = 4 };
        fullPanel.Children.Add(new TextBlock { Text = "Name", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        fullPanel.Children.Add(nameBox);
        fullPanel.Children.Add(formPanel);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Add Shared Launcher",
            Content = new ScrollViewer { Content = fullPanel, MaxHeight = 400 },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        int mode = modeCombo.SelectedIndex;
        string path = pathBox.Text.Trim();

        if (mode == SharedSyncModes.OneDrive)
        {
            if (string.IsNullOrWhiteSpace(oneDriveLinkBox.Text))
            {
                await ShowErrorDialog("Paste the OneDrive share link you were sent.");
                return;
            }

            var store = CloudSyncService.StoreFor(SyncProviders.OneDrive);
            if (store is not { IsSignedIn: true })
            {
                await ShowErrorDialog("Sign in to OneDrive on the Cloud Sync page first.");
                return;
            }
        }
        else if (mode == SharedSyncModes.WebDav)
        {
            if (string.IsNullOrWhiteSpace(davUrlBox.Text)
                || string.IsNullOrWhiteSpace(davUserBox.Text)
                || davPasswordBox.Password.Length == 0)
            {
                await ShowErrorDialog("WebDAV needs a file URL, a username and a password.");
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                await ShowErrorDialog("Path is required.");
                return;
            }

            if (mode == SharedSyncModes.Sftp && string.IsNullOrWhiteSpace(hostBox.Text))
            {
                await ShowErrorDialog("SFTP host is required.");
                return;
            }
        }

        // Provisional only. The real value arrives with the file on the verify below, which is
        // what SharedLauncherPayload.ApplyAsync adopts.
        var newLauncher = new Launcher
        {
            Id = Guid.NewGuid().ToString(),
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "Shared Launcher" : nameBox.Text.Trim(),
            IsShared = true,
            IsSharedOwner = false,
            SharedSyncMode = mode,
            SharedPath = path,
            SharedSftpHost = hostBox.Text.Trim(),
            SharedSftpPort = int.TryParse(portBox.Text, out int p) ? p : 22,
            SharedSftpUsername = userBox.Text.Trim(),
            SharedSftpPrivateKeyPath = keyBox.Text.Trim(),
            SharedWebDavUrl = davUrlBox.Text.Trim(),
            SharedWebDavUsername = davUserBox.Text.Trim(),
            SharedLinkUrl = oneDriveLinkBox.Text.Trim(),
        };

        // The password has to be stored before verifying — it is keyed by launcher id, and
        // verification is what proves the whole set of details actually works.
        if (mode == SharedSyncModes.WebDav)
            WebDavSharedStore.SetPassword(newLauncher, davPasswordBox.Password);

        // Verify remote before adding
        string? password = null;
        if (!SftpSyncService.HasAutoKeyForShared(newLauncher))
        {
            password = await ShowPasswordPrompt();
            if (password == null) return;
        }

        var (verified, itemCount, error) = await SftpSyncService.VerifySharedLauncherAsync(newLauncher, password);
        if (!verified)
        {
            // The launcher is never added, so its stored password would be orphaned.
            WebDavSharedStore.ClearPassword(newLauncher);
            await ShowErrorDialog($"Could not verify: {error}");
            return;
        }

        SettingsManager.Current.Launchers.Add(newLauncher);
        SettingsManager.SaveSettings();

        // Initial pull
        var (ok, msg) = await SftpSyncService.SyncSharedLauncherAsync(newLauncher, password);
        if (!ok)
            await ShowErrorDialog(msg);

        MainWindow.Current?.RefreshTrayIcons();
        RebuildLauncherCards();
    }

    // ── Shared dialog helpers ───────────────────────────────────────

    /// <summary>Example path shown for File mode, kept in one place so the two uses agree.</summary>
    private const string FilePathPlaceholder =
        @"C:\shared\launcher.json or \\server\share\launcher.json";

    private static (StackPanel Panel, ComboBox ModeCombo, TextBox PathBox,
        TextBox HostBox, TextBox PortBox, TextBox UserBox, TextBox KeyBox,
        ComboBox DirectionCombo, TextBox DavUrlBox, TextBox DavUserBox, PasswordBox DavPasswordBox,
        TextBox OneDriveLinkBox)
        BuildShareForm(Launcher launcher, bool isSubscribing)
    {
        // ── Direction ───────────────────────────────────────────────
        var directionCombo = new ComboBox { MinWidth = 160 };
        directionCombo.Items.Add("2-way (all participants can edit)");
        directionCombo.Items.Add("1-way (owner publishes, others subscribe)");
        directionCombo.SelectedIndex = launcher.SharedTwoWay ? 0 : 1;

        // "How do you want to share this?" comes first and decides everything below it. The
        // previous form showed Direction, Mode, Path and every provider panel at once, with Path
        // meaning something different per mode — so the first thing a reader had to do was work
        // out which half of the dialog applied to them.
        var modeCombo = new ComboBox { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch };
        modeCombo.Items.Add("File: a folder, network share, or synced cloud folder");
        modeCombo.Items.Add("SFTP server");
        modeCombo.Items.Add("WebDAV server (Nextcloud, ownCloud...)");
        modeCombo.Items.Add(isSubscribing ? "OneDrive: I have a share link" : "OneDrive: create a share link");
        modeCombo.SelectedIndex = launcher.SharedSyncMode;

        // What each choice actually means, in the dialog rather than in docs no one opens.
        var modeCaption = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 4),
        };

        var pathBox = new TextBox
        {
            PlaceholderText = launcher.IsFileSync ? @"C:\shared\launcher.json or \\server\share\launcher.json" : "~/shared/launcher.json",
            Text = launcher.SharedPath,
        };

        var pathCaption = new TextBlock
        {
            Text = "Any folder works, including a OneDrive, Google Drive or network share folder. "
                 + "Whoever you are sharing with points at their own copy of the same folder.",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };
        pathCaption.Visibility = launcher.IsFileSync ? Visibility.Visible : Visibility.Collapsed;

        var hostBox = new TextBox { PlaceholderText = "hostname or IP", Text = launcher.SharedSftpHost };
        var portBox = new TextBox { PlaceholderText = "22", Text = launcher.SharedSftpPort == 22 ? "" : launcher.SharedSftpPort.ToString() };
        var userBox = new TextBox { PlaceholderText = Environment.UserName, Text = launcher.SharedSftpUsername };
        var keyBox = new TextBox { PlaceholderText = "auto-detect (~/.ssh/)", Text = launcher.SharedSftpPrivateKeyPath };

        // WebDAV-specific fields. Its own URL and account rather than the global WebDAV
        // settings: the server a colleague shares from is routinely not the one you sync to.
        var davUrlBox = new TextBox
        {
            PlaceholderText = "https://cloud.example.com/remote.php/dav/files/me/Shared/team.json",
            Text = launcher.SharedWebDavUrl,
        };
        var davUserBox = new TextBox { PlaceholderText = "username", Text = launcher.SharedWebDavUsername };
        var davPasswordBox = new PasswordBox { PlaceholderText = WebDavSharedStore.HasCredentials(launcher) ? "saved, type to replace" : "app password" };

        var davPanel = new StackPanel { Spacing = 4 };

        // ── OneDrive ────────────────────────────────────────────────
        // No path anywhere. The owner never chooses where the file goes, and a subscriber only
        // ever holds a link — showing a file path here is exactly what made this confusing.
        var oneDriveLinkBox = new TextBox
        {
            PlaceholderText = "https://1drv.ms/... (paste the link you were sent)",
            Text = launcher.SharedLinkUrl,
        };
        var oneDrivePanel = new StackPanel { Spacing = 4 };
        var oneDriveNote = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // SFTP-specific fields panel
        var sftpPanel = new StackPanel { Spacing = 4 };

        void AddField(StackPanel target, string label, UIElement element)
        {
            target.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
            target.Children.Add(element);
        }

        // The link is an *input* only when subscribing. Publishing produces one, so an owner
        // was previously shown a box captioned "leave this empty" — a field that exists to be
        // ignored, in a dialog whose whole purpose is the thing it would have contained.
        if (isSubscribing)
            AddField(oneDrivePanel, "Share link", oneDriveLinkBox);
        oneDrivePanel.Children.Add(oneDriveNote);

        AddField(davPanel, "File URL", davUrlBox);
        AddField(davPanel, "Username", davUserBox);
        AddField(davPanel, "Password", davPasswordBox);
        davPanel.Children.Add(new TextBlock
        {
            Text = "Everyone sharing this launcher points at the same URL with their own account. "
                 + "Both sides can write, so 2-way sharing works.",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        AddField(sftpPanel, "SFTP Host", hostBox);
        AddField(sftpPanel, "Port", portBox);
        AddField(sftpPanel, "Username", userBox);
        AddField(sftpPanel, "Private Key", keyBox);

        sftpPanel.Visibility = launcher.IsSftpSync ? Visibility.Visible : Visibility.Collapsed;
        davPanel.Visibility = launcher.IsWebDavSync ? Visibility.Visible : Visibility.Collapsed;
        oneDrivePanel.Visibility = launcher.IsOneDriveSync ? Visibility.Visible : Visibility.Collapsed;

        // Update visibility and placeholder when mode changes
        var pathRow = new StackPanel { Spacing = 4 };
        AddField(pathRow, "Path", pathBox);

        // One place decides what a mode shows, so the initial state and the change handler
        // cannot drift apart — they did before, which is how a stale panel could linger.
        void ApplyMode(int mode)
        {
            bool isFile = mode == SharedSyncModes.File;
            bool isSftp = mode == SharedSyncModes.Sftp;
            bool isDav = mode == SharedSyncModes.WebDav;
            bool isOneDrive = mode == SharedSyncModes.OneDrive;

            sftpPanel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
            davPanel.Visibility = isDav ? Visibility.Visible : Visibility.Collapsed;
            oneDrivePanel.Visibility = isOneDrive ? Visibility.Visible : Visibility.Collapsed;

            // Only File and SFTP are addressed by a path. WebDAV carries its whole location in
            // its URL, and OneDrive has no user-visible location at all, so a Path row for
            // either would be a second place to put the same thing — or an outright lie.
            pathRow.Visibility = isFile || isSftp ? Visibility.Visible : Visibility.Collapsed;
            pathCaption.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;

            pathBox.PlaceholderText = isSftp ? "~/shared/launcher.json" : FilePathPlaceholder;

            modeCaption.Text = mode switch
            {
                SharedSyncModes.Sftp =>
                    "Everyone sharing this needs an account on the same SSH server.",
                SharedSyncModes.WebDav =>
                    "Everyone points at the same URL with their own account. Both sides can edit.",
                SharedSyncModes.OneDrive =>
                    "Little Launcher puts the file in your OneDrive and gives you a link to send. "
                    + "Anyone with the link can open and edit it, so treat it like a password.",
                _ => "Any folder both of you can reach: a network share, or a cloud folder you "
                     + "have already shared with them through that service.",
            };

            oneDriveNote.Text = isOneDrive && !isSubscribing
                ? "A link is created in your OneDrive when you press Share, and shown so you can "
                  + "send it. Anyone with it can edit this launcher."
                : "";
            oneDriveNote.Visibility = oneDriveNote.Text.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        modeCombo.SelectionChanged += (s, e) => ApplyMode(modeCombo.SelectedIndex);

        var panel = new StackPanel { Spacing = 4 };

        // Provider first, then only its inputs, then direction — the order the decisions
        // are actually made in.
        AddField(panel, isSubscribing ? "How was it shared with you?" : "How do you want to share this?", modeCombo);
        panel.Children.Add(modeCaption);
        panel.Children.Add(pathRow);
        panel.Children.Add(pathCaption);
        panel.Children.Add(sftpPanel);
        panel.Children.Add(davPanel);
        panel.Children.Add(oneDrivePanel);
        // Only the owner chooses direction. A subscriber cannot know what the owner intended,
        // and guessing wrong means either losing their edits or pushing changes into a share
        // that was meant to be read-only, so they are told rather than asked: the answer is
        // published in the file and adopted on the first pull.
        if (!isSubscribing)
            AddField(panel, "Direction", directionCombo);
        else
            panel.Children.Add(new TextBlock
            {
                Text = "Whether you can edit this launcher or only receive it is set by whoever "
                     + "shared it, and applies automatically.",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });

        ApplyMode(modeCombo.SelectedIndex);

        return (panel, modeCombo, pathBox, hostBox, portBox, userBox, keyBox, directionCombo,
                davUrlBox, davUserBox, davPasswordBox, oneDriveLinkBox);
    }



    private async Task<string?> ShowPasswordPrompt()
    {
        var passwordBox = new PasswordBox { PlaceholderText = "SSH key passphrase or password" };

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Authentication Required",
            Content = passwordBox,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return passwordBox.Password;
    }

    private async Task ShowErrorDialog(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name)
                return fe;
            var result = FindChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
