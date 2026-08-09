using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using LittleLauncher.Models;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace LittleLauncher.Classes;

/// <summary>
/// Provides a gallery-style icon chooser Flyout with tabs for Segoe Fluent Icons,
/// emojis, app color icons, selfh.st icons, and custom file browse.
/// </summary>
internal static class IconGallery
{
    /// <summary>Result of an icon gallery selection.</summary>
    /// <param name="Glyph">Unicode glyph character (Fluent icon or emoji). Null when an image was selected.</param>
    /// <param name="ImagePath">Local file path for image-based icons. Null when a glyph was selected.</param>
    /// <param name="PresetMode">TrayIconMode preset string (e.g. "Blue", "Composite"). Null when a glyph/image was selected.</param>
    /// <param name="Color">Optional hex color for the glyph (e.g. "#FF0000"). Null or empty for default theme color.</param>
    public record IconResult(string? Glyph, string? ImagePath, string? PresetMode = null, string? Color = null);

    // ── Tab indices ──────────────────────────────────────────
    private const int TabGlyphs = 0;
    private const int TabEmoji = 1;
    private const int TabAppIcons = 2;
    private const int TabSelfhSt = 3;

    // ── Layout constants ─────────────────────────────────────
    private const double IconButtonSize = 40;
    private const int IconsPerRow = 9;
    private const int SelfhStIconsPerRow = 4;
    private const int MaxVisibleSelfhStIcons = 120;

    private static readonly HttpClient SelfhStHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim SelfhStCatalogLock = new(1, 1);

    /// <summary>
    /// Caps how many icon PNGs are in flight at once. The gallery shows up to
    /// <see cref="MaxVisibleSelfhStIcons"/> at a time, and firing that many requests at the CDN
    /// together makes every one of them slower than fetching them a handful at a time.
    /// </summary>
    private static readonly SemaphoreSlim SelfhStDownloadGate = new(8, 8);

    private static readonly JsonSerializerOptions SelfhStJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static IReadOnlyList<SelfhStIconEntry>? _selfhStCatalog;
    private static DateTimeOffset _selfhStCatalogFetchedAt;

    private const string SelfhStIndexUrl = "https://raw.githubusercontent.com/selfhst/icons/refs/heads/main/index.json";
    private const string SelfhStPngUrlPrefix = "https://cdn.jsdelivr.net/gh/selfhst/icons@main/png/";
    private static readonly Uri SelfhStIconsUri = new("https://selfh.st/icons/");
    private static readonly Uri SelfhStLicenseUri = new("https://github.com/selfhst/icons/blob/main/LICENSE");
    private static readonly TimeSpan SelfhStCatalogTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// Creates a Flyout containing the icon gallery. Attach it to a Button.
    /// </summary>
    /// <param name="onSelected">Invoked when the user picks an icon.</param>
    /// <param name="onBrowseRequested">Invoked when the user wants to browse for a custom file (caller handles the picker).</param>
    /// <param name="onReset">Invoked when the user clicks "Reset to default".</param>
    /// <param name="currentGlyph">Current glyph to pre-select when the flyout opens.</param>
    /// <param name="currentColor">Current hex color to pre-select in the color palette.</param>
    /// <param name="currentImagePath">Current image path to pre-select in the App Icons tab.</param>
    public static Flyout CreateFlyout(
        Action<IconResult> onSelected,
        Action onBrowseRequested,
        Action onReset,
        string? currentGlyph = null,
        string? currentColor = null,
        string? currentImagePath = null)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom
        };

        // ── Flyout presenter sizing ──
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 480.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 420.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 600.0));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        presenterStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        presenterStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        flyout.FlyoutPresenterStyle = presenterStyle;

        // ── State ──
        int activeTab = TabGlyphs;
        IconResult? pendingSelection = null;
        int selfhStRequestVersion = 0;

        // ── Root layout ──
        var root = new Grid { Width = 396 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Search box ──
        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = "Search icons\u2026",
            QueryIcon = new SymbolIcon(Symbol.Find),
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(searchBox, 0);
        root.Children.Add(searchBox);

        // ── Tab bar ──
        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        var tabButtons = new List<Button>();
        var contentScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinHeight = 140,
            MaxHeight = 320,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var contentPanel = new StackPanel();
        contentScroller.Content = contentPanel;

        // Color getter — assigned after BuildColorPalette below
        Func<string> getSelectedColor = () => "";
        StackPanel colorPalette = null!;
        FrameworkElement selfhStAttribution = CreateSelfhStAttributionBar();

        // ── Confirm / Cancel bar ──
        var confirmBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var confirmBtn = new Button
        {
            Content = "Confirm",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = false
        };
        var cancelBtn = new Button { Content = "Cancel" };
        confirmBar.Children.Add(confirmBtn);
        confirmBar.Children.Add(cancelBtn);

        confirmBtn.Click += (s, e) =>
        {
            if (pendingSelection != null)
            {
                // Reapply current color in case user changed it after selecting the icon
                var sel = pendingSelection;
                if (sel.Glyph != null)
                    sel = sel with { Color = getSelectedColor() };
                onSelected(sel);
            }
            flyout.Hide();
        };
        cancelBtn.Click += (s, e) => flyout.Hide();

        Button? selectedIconButton = null;

        void HighlightIconButton(Button btn)
        {
            if (selectedIconButton != null)
            {
                selectedIconButton.BorderThickness = new Thickness(0);
                selectedIconButton.BorderBrush = null;
            }
            selectedIconButton = btn;
            btn.BorderThickness = new Thickness(2);
            btn.BorderBrush = GetAccentBrush();
        }

        void SetPending(IconResult result)
        {
            pendingSelection = result;
            confirmBtn.IsEnabled = true;
        }

        void UpdateFlyoutHeights()
        {
            double availableHeight = root.XamlRoot?.Size.Height ?? 720;
            double maxRootHeight = Math.Max(340, Math.Min(560, availableHeight - 24));
            root.MaxHeight = maxRootHeight;

            double reservedHeight = 164;
            if (colorPalette.Visibility == Visibility.Visible)
                reservedHeight += 64;
            if (selfhStAttribution.Visibility == Visibility.Visible)
                reservedHeight += 32;

            double contentHeight = Math.Max(140, Math.Min(320, maxRootHeight - reservedHeight));
            contentScroller.Height = contentHeight;
        }

        void SelectTab(int tabIndex, string? searchQuery = null, string? currentGlyphToSelect = null, string? currentImageToSelect = null)
        {
            activeTab = tabIndex;
            selectedIconButton = null;
            for (int i = 0; i < tabButtons.Count; i++)
            {
                tabButtons[i].Background = i == tabIndex
                    ? GetAccentBrush()
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                tabButtons[i].Foreground = i == tabIndex
                    ? new SolidColorBrush(Microsoft.UI.Colors.White)
                    : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            }

            contentPanel.Children.Clear();
            string query = (searchQuery ?? "").Trim().ToLowerInvariant();

            // Color palette only applies to glyph/emoji tabs
            colorPalette.Visibility = tabIndex is TabGlyphs or TabEmoji
                ? Visibility.Visible : Visibility.Collapsed;
            selfhStAttribution.Visibility = tabIndex == TabSelfhSt
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateFlyoutHeights();

            switch (tabIndex)
            {
                case TabGlyphs:
                    BuildGlyphsContent(contentPanel, query, icon =>
                    {
                        SetPending(new IconResult(icon.Glyph, null, Color: getSelectedColor()));
                    }, getSelectedColor(), HighlightIconButton, currentGlyphToSelect);
                    break;
                case TabEmoji:
                    BuildEmojiContent(contentPanel, query, emoji =>
                    {
                        SetPending(new IconResult(emoji, null, Color: getSelectedColor()));
                    }, getSelectedColor(), HighlightIconButton, currentGlyphToSelect);
                    break;
                case TabAppIcons:
                    BuildAppIconsContent(contentPanel, query, path =>
                    {
                        SetPending(new IconResult(null, path));
                    }, HighlightIconButton, currentImageToSelect);
                    break;
                case TabSelfhSt:
                    int requestVersion = ++selfhStRequestVersion;
                    BuildSelfhStLoadingContent(contentPanel);
                    _ = LoadSelfhStContentAsync(
                        contentPanel,
                        query,
                        path => SetPending(new IconResult(null, path)),
                        HighlightIconButton,
                        currentImageToSelect,
                        () => requestVersion == selfhStRequestVersion && activeTab == TabSelfhSt);
                    break;
            }
        }

        // Tab: Glyphs
        var glyphsBtn = MakeTabButton(new FontIcon { Glyph = "\uE790", FontSize = 16 }, "Segoe Fluent Icons");
        glyphsBtn.Click += (s, e) => { searchBox.Text = ""; SelectTab(TabGlyphs); };
        tabButtons.Add(glyphsBtn);
        tabBar.Children.Add(glyphsBtn);

        // Tab: Emoji
        var emojiBtn = MakeTabButton(new TextBlock { Text = "😀", FontSize = 16 }, "Emoji");
        emojiBtn.Click += (s, e) => { searchBox.Text = ""; SelectTab(TabEmoji); };
        tabButtons.Add(emojiBtn);
        tabBar.Children.Add(emojiBtn);

        // Tab: App Icons
        var appBtn = MakeTabButton(new FontIcon { Glyph = "\uE737", FontSize = 16 }, "App Icons");
        appBtn.Click += (s, e) => { searchBox.Text = ""; SelectTab(TabAppIcons); };
        tabButtons.Add(appBtn);
        tabBar.Children.Add(appBtn);

        var selfhStBtn = MakeTabButton(new TextBlock { Text = "sh", FontSize = 13, FontWeight = FontWeights.Bold }, "selfh.st Icons");
        selfhStBtn.Click += (s, e) => { searchBox.Text = ""; SelectTab(TabSelfhSt); };
        tabButtons.Add(selfhStBtn);
        tabBar.Children.Add(selfhStBtn);

        Grid.SetRow(tabBar, 1);
        root.Children.Add(tabBar);

        // ── Color palette ──
        var (palette, colorGetter) = BuildColorPalette(currentColor,
            onColorChanged: () => SelectTab(activeTab, searchBox.Text));
        colorPalette = palette;
        getSelectedColor = colorGetter;
        Grid.SetRow(colorPalette, 2);
        root.Children.Add(colorPalette);

        Grid.SetRow(contentScroller, 3);
        root.Children.Add(contentScroller);

        // ── Bottom buttons ──
        var bottomBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var browseBtn = new HyperlinkButton
        {
            Content = "Browse for image\u2026",
            Padding = new Thickness(0)
        };
        browseBtn.Click += (s, e) =>
        {
            flyout.Hide();
            onBrowseRequested();
        };
        bottomBar.Children.Add(browseBtn);

        var resetBtn = new HyperlinkButton
        {
            Content = "Reset to default",
            Padding = new Thickness(0)
        };
        resetBtn.Click += (s, e) =>
        {
            flyout.Hide();
            onReset();
        };
        bottomBar.Children.Add(resetBtn);
        Grid.SetRow(bottomBar, 4);
        root.Children.Add(bottomBar);
        Grid.SetRow(selfhStAttribution, 5);
        root.Children.Add(selfhStAttribution);
        Grid.SetRow(confirmBar, 6);
        root.Children.Add(confirmBar);

        // ── Search handler ──
        searchBox.TextChanged += (s, e) =>
        {
            SelectTab(activeTab, searchBox.Text);
        };

        flyout.Content = root;

        // Initial load — pre-select the current icon if provided
        flyout.Opened += (s, e) =>
        {
            pendingSelection = null;
            confirmBtn.IsEnabled = false;
            UpdateFlyoutHeights();

            // Determine which tab to open based on current icon
            if (!string.IsNullOrEmpty(currentImagePath))
            {
                // Check if image is an app color icon
                string fileName = Path.GetFileName(currentImagePath);
                if (fileName.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                    SelectTab(TabAppIcons, null, currentImageToSelect: currentImagePath);
                else if (IsSelfhStImagePath(currentImagePath))
                    SelectTab(TabSelfhSt, null, currentImageToSelect: currentImagePath);
                else
                    SelectTab(TabGlyphs); // custom file — no tab to pre-select in
            }
            else if (!string.IsNullOrEmpty(currentGlyph) && !IsFluentGlyph(currentGlyph))
                SelectTab(TabEmoji, null, currentGlyphToSelect: currentGlyph);
            else if (!string.IsNullOrEmpty(currentGlyph))
                SelectTab(TabGlyphs, null, currentGlyphToSelect: currentGlyph);
            else
                SelectTab(TabGlyphs);
        };

        return flyout;
    }

    /// <summary>
    /// Creates a Flyout for choosing a launcher's tray icon. Includes all icon gallery tabs
    /// plus a "Launcher Presets" tab with Composite and app color icons.
    /// </summary>
    public static Flyout CreateLauncherIconFlyout(
        string currentMode,
        Action<IconResult> onSelected,
        Action onBrowseRequested)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom
        };

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 480.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 420.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 600.0));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        presenterStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        presenterStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        flyout.FlyoutPresenterStyle = presenterStyle;

        const int TabPresets = 0;
        const int TabGlyphsL = 1;
        const int TabEmojiL = 2;
        const int TabSelfhStL = 3;

        int activeTab = TabPresets;
        IconResult? pendingSelectionL = null;
        int selfhStRequestVersionL = 0;
        var root = new Grid { Width = 396 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = "Search icons\u2026",
            QueryIcon = new SymbolIcon(Symbol.Find),
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(searchBox, 0);
        root.Children.Add(searchBox);

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        var tabButtons = new List<Button>();
        var contentScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinHeight = 140,
            MaxHeight = 320,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var contentPanel = new StackPanel();
        contentScroller.Content = contentPanel;

        // Color getter — assigned after BuildColorPalette below
        Func<string> getSelectedColorL = () => "";
        StackPanel colorPaletteL = null!;
        FrameworkElement selfhStAttributionL = CreateSelfhStAttributionBar();

        // ── Confirm / Cancel bar ──
        var confirmBarL = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var confirmBtnL = new Button
        {
            Content = "Confirm",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = false
        };
        var cancelBtnL = new Button { Content = "Cancel" };
        confirmBarL.Children.Add(confirmBtnL);
        confirmBarL.Children.Add(cancelBtnL);

        confirmBtnL.Click += (s, e) =>
        {
            if (pendingSelectionL != null)
            {
                var sel = pendingSelectionL;
                if (sel.Glyph != null)
                    sel = sel with { Color = getSelectedColorL() };
                onSelected(sel);
            }
            flyout.Hide();
        };
        cancelBtnL.Click += (s, e) => flyout.Hide();

        Button? selectedIconButtonL = null;

        void HighlightIconButtonL(Button btn)
        {
            if (selectedIconButtonL != null)
            {
                selectedIconButtonL.BorderThickness = new Thickness(0);
                selectedIconButtonL.BorderBrush = null;
            }
            selectedIconButtonL = btn;
            btn.BorderThickness = new Thickness(2);
            btn.BorderBrush = GetAccentBrush();
        }

        void SetPendingL(IconResult result)
        {
            pendingSelectionL = result;
            confirmBtnL.IsEnabled = true;
        }

        void UpdateFlyoutHeightsL()
        {
            double availableHeight = root.XamlRoot?.Size.Height ?? 720;
            double maxRootHeight = Math.Max(320, Math.Min(540, availableHeight - 24));
            root.MaxHeight = maxRootHeight;

            double reservedHeight = 128;
            if (colorPaletteL.Visibility == Visibility.Visible)
                reservedHeight += 64;
            if (selfhStAttributionL.Visibility == Visibility.Visible)
                reservedHeight += 32;

            double contentHeight = Math.Max(140, Math.Min(320, maxRootHeight - reservedHeight));
            contentScroller.Height = contentHeight;
        }

        void SelectTab(int tabIndex, string? searchQuery = null, string? currentGlyphToSelect = null, string? currentPresetToSelect = null)
        {
            activeTab = tabIndex;
            selectedIconButtonL = null;
            for (int i = 0; i < tabButtons.Count; i++)
            {
                tabButtons[i].Background = i == tabIndex
                    ? GetAccentBrush()
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                tabButtons[i].Foreground = i == tabIndex
                    ? new SolidColorBrush(Microsoft.UI.Colors.White)
                    : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            }

            contentPanel.Children.Clear();
            string query = (searchQuery ?? "").Trim().ToLowerInvariant();

            // Color palette only applies to glyph/emoji tabs
            colorPaletteL.Visibility = tabIndex is TabGlyphsL or TabEmojiL
                ? Visibility.Visible : Visibility.Collapsed;
            selfhStAttributionL.Visibility = tabIndex == TabSelfhStL
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateFlyoutHeightsL();

            switch (tabIndex)
            {
                case TabPresets:
                    BuildLauncherPresetsContent(contentPanel, query, mode =>
                    {
                        SetPendingL(new IconResult(null, null, mode));
                    }, () =>
                    {
                        flyout.Hide();
                        onBrowseRequested();
                    }, HighlightIconButtonL, currentPresetToSelect);
                    break;
                case TabGlyphsL:
                    BuildGlyphsContent(contentPanel, query, icon =>
                    {
                        SetPendingL(new IconResult(icon.Glyph, null, Color: getSelectedColorL()));
                    }, getSelectedColorL(), HighlightIconButtonL, currentGlyphToSelect);
                    break;
                case TabEmojiL:
                    BuildEmojiContent(contentPanel, query, emoji =>
                    {
                        SetPendingL(new IconResult(emoji, null, Color: getSelectedColorL()));
                    }, getSelectedColorL(), HighlightIconButtonL, currentGlyphToSelect);
                    break;
                case TabSelfhStL:
                    int requestVersion = ++selfhStRequestVersionL;
                    BuildSelfhStLoadingContent(contentPanel);
                    _ = LoadSelfhStContentAsync(
                        contentPanel,
                        query,
                        path => SetPendingL(new IconResult(null, path)),
                        HighlightIconButtonL,
                        null,
                        () => requestVersion == selfhStRequestVersionL && activeTab == TabSelfhStL);
                    break;
            }
        }

        // Tab: Presets
        var presetsBtn = MakeTabButton(new FontIcon { Glyph = "\uE737", FontSize = 16 }, "Launcher Presets");
        presetsBtn.Click += (s, e) => { searchBox.Text = ""; SelectTab(TabPresets); };
        tabButtons.Add(presetsBtn);
        tabBar.Children.Add(presetsBtn);

        // Tab: Glyphs
        var glyphsBtn = MakeTabButton(new FontIcon { Glyph = "\uE790", FontSize = 16 }, "Segoe Fluent Icons");
        glyphsBtn.Click += (s, e) => { searchBox.Text = ""; SelectTab(TabGlyphsL); };
        tabButtons.Add(glyphsBtn);
        tabBar.Children.Add(glyphsBtn);

        // Tab: Emoji
        var emojiBtn = MakeTabButton(new TextBlock { Text = "😀", FontSize = 16 }, "Emoji");
        emojiBtn.Click += (s, e) => { searchBox.Text = ""; SelectTab(TabEmojiL); };
        tabButtons.Add(emojiBtn);
        tabBar.Children.Add(emojiBtn);

        var selfhStBtn = MakeTabButton(new TextBlock { Text = "sh", FontSize = 13, FontWeight = FontWeights.Bold }, "selfh.st Icons");
        selfhStBtn.Click += (s, e) => { searchBox.Text = ""; SelectTab(TabSelfhStL); };
        tabButtons.Add(selfhStBtn);
        tabBar.Children.Add(selfhStBtn);

        Grid.SetRow(tabBar, 1);
        root.Children.Add(tabBar);

        // ── Color palette ──
        string? initialColorL = TrayIconModes.IsGlyphMode(currentMode) ? TrayIconModes.GetGlyphColor(currentMode) : null;
        var (paletteL, colorGetterL) = BuildColorPalette(initialColorL,
            onColorChanged: () => SelectTab(activeTab, searchBox.Text));
        colorPaletteL = paletteL;
        getSelectedColorL = colorGetterL;
        Grid.SetRow(colorPaletteL, 2);
        root.Children.Add(colorPaletteL);

        Grid.SetRow(contentScroller, 3);
        root.Children.Add(contentScroller);
        Grid.SetRow(selfhStAttributionL, 4);
        root.Children.Add(selfhStAttributionL);
        Grid.SetRow(confirmBarL, 5);
        root.Children.Add(confirmBarL);

        searchBox.TextChanged += (s, e) => SelectTab(activeTab, searchBox.Text);

        flyout.Content = root;

        // Initial load — pre-select the current icon if provided
        flyout.Opened += (s, e) =>
        {
            pendingSelectionL = null;
            confirmBtnL.IsEnabled = false;
            UpdateFlyoutHeightsL();

            if (TrayIconModes.IsGlyphMode(currentMode))
            {
                string? glyph = TrayIconModes.GetGlyphCharacter(currentMode);
                if (!string.IsNullOrEmpty(glyph) && !IsFluentGlyph(glyph))
                    SelectTab(TabEmojiL, null, currentGlyphToSelect: glyph);
                else if (!string.IsNullOrEmpty(glyph))
                    SelectTab(TabGlyphsL, null, currentGlyphToSelect: glyph);
                else
                    SelectTab(TabPresets);
            }
            else if (!string.IsNullOrEmpty(currentMode) && currentMode != TrayIconModes.Custom)
                SelectTab(TabPresets, null, currentPresetToSelect: currentMode);
            else
                SelectTab(TabPresets);
        };

        return flyout;
    }

    // ═══════════════════════════════════════════════════════════
    //  Tab Content Builders
    // ═══════════════════════════════════════════════════════════

    private static void BuildGlyphsContent(StackPanel panel, string query, Action<(string Glyph, string Name)> onPick, string? previewColor = null, Action<Button>? onButtonClicked = null, string? currentGlyph = null)
    {
        Brush? colorBrush = ParseHexBrush(previewColor);
        foreach (var (category, icons) in FluentIconCategories)
        {
            var filtered = string.IsNullOrEmpty(query)
                ? icons
                : icons.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (filtered.Length == 0) continue;

            if (string.IsNullOrEmpty(query))
                panel.Children.Add(CategoryHeader(category));

            var grid = new StackPanel();
            var currentRow = new StackPanel { Orientation = Orientation.Horizontal };
            int count = 0;

            foreach (var icon in filtered)
            {
                var btn = new Button
                {
                    Width = IconButtonSize,
                    Height = IconButtonSize,
                    Padding = new Thickness(0),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };
                var fi = new FontIcon { Glyph = icon.Glyph, FontSize = 18 };
                if (colorBrush != null) fi.Foreground = colorBrush;
                btn.Content = fi;
                ToolTipService.SetToolTip(btn, icon.Name);
                btn.Click += (s, e) => { onButtonClicked?.Invoke((Button)s!); onPick(icon); };

                // Pre-select if this matches the current icon
                if (currentGlyph != null && icon.Glyph == currentGlyph)
                {
                    onButtonClicked?.Invoke(btn);
                    onPick(icon);
                }

                currentRow.Children.Add(btn);

                if (++count % IconsPerRow == 0)
                {
                    grid.Children.Add(currentRow);
                    currentRow = new StackPanel { Orientation = Orientation.Horizontal };
                }
            }
            if (currentRow.Children.Count > 0)
                grid.Children.Add(currentRow);

            panel.Children.Add(grid);
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(NoResults());
    }

    private static void BuildEmojiContent(StackPanel panel, string query, Action<string> onPick, string? previewColor = null, Action<Button>? onButtonClicked = null, string? currentGlyph = null)
    {
        Brush? colorBrush = ParseHexBrush(previewColor);
        foreach (var (category, emojis) in EmojiCategories)
        {
            var filtered = string.IsNullOrEmpty(query)
                ? emojis
                : emojis.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (filtered.Length == 0) continue;

            if (string.IsNullOrEmpty(query))
                panel.Children.Add(CategoryHeader(category));

            var grid = new StackPanel();
            var currentRow = new StackPanel { Orientation = Orientation.Horizontal };
            int count = 0;

            foreach (var emoji in filtered)
            {
                var btn = new Button
                {
                    Width = IconButtonSize,
                    Height = IconButtonSize,
                    Padding = new Thickness(0),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };
                btn.Content = new TextBlock
                {
                    Text = emoji.Emoji,
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                ToolTipService.SetToolTip(btn, emoji.Name);
                btn.Click += (s, e) => { onButtonClicked?.Invoke((Button)s!); onPick(emoji.Emoji); };

                // Pre-select if this matches the current icon
                if (currentGlyph != null && emoji.Emoji == currentGlyph)
                {
                    onButtonClicked?.Invoke(btn);
                    onPick(emoji.Emoji);
                }

                currentRow.Children.Add(btn);

                if (++count % IconsPerRow == 0)
                {
                    grid.Children.Add(currentRow);
                    currentRow = new StackPanel { Orientation = Orientation.Horizontal };
                }
            }
            if (currentRow.Children.Count > 0)
                grid.Children.Add(currentRow);

            panel.Children.Add(grid);
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(NoResults());
    }

    private static void BuildAppIconsContent(StackPanel panel, string query, Action<string> onImageSelected, Action<Button>? onButtonClicked = null, string? currentImagePath = null)
    {
        var colors = new[] { "Blue", "Green", "Teal", "Red", "Orange", "Purple" };
        var filtered = string.IsNullOrEmpty(query)
            ? colors
            : colors.Where(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (filtered.Length == 0)
        {
            panel.Children.Add(NoResults());
            return;
        }

        panel.Children.Add(CategoryHeader("App Color Icons"));

        var grid = new StackPanel();
        var currentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        foreach (string color in filtered)
        {
            string sourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", $"{color}.png");
            if (!File.Exists(sourcePath)) continue;

            var btn = new Button
            {
                Width = 60,
                Height = 72,
                Padding = new Thickness(4),
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };

            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var img = new Image
            {
                Width = 36,
                Height = 36,
                Source = new BitmapImage(new Uri(sourcePath, UriKind.Absolute)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            content.Children.Add(img);
            content.Children.Add(new TextBlock
            {
                Text = color,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            btn.Content = content;
            ToolTipService.SetToolTip(btn, $"{color} app icon");

            btn.Click += (s, e) =>
            {
                onButtonClicked?.Invoke((Button)s!);
                // Copy the app icon to the cache directory so IconPath can reference it
                string cacheDir = GetItemIconCacheDir();
                string dest = Path.Combine(cacheDir, $"app-{color.ToLowerInvariant()}.png");
                File.Copy(sourcePath, dest, true);
                onImageSelected(dest);
            };

            // Pre-select if this matches the current icon
            if (currentImagePath != null &&
                Path.GetFileName(currentImagePath).Equals($"app-{color.ToLowerInvariant()}.png", StringComparison.OrdinalIgnoreCase))
            {
                onButtonClicked?.Invoke(btn);
                string cacheDir = GetItemIconCacheDir();
                string dest = Path.Combine(cacheDir, $"app-{color.ToLowerInvariant()}.png");
                File.Copy(sourcePath, dest, true);
                onImageSelected(dest);
            }

            currentRow.Children.Add(btn);
        }

        grid.Children.Add(currentRow);
        panel.Children.Add(grid);
    }

    private static void BuildSelfhStLoadingContent(StackPanel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 12)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Loading selfh.st icons...",
            FontSize = 13,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center
        });
    }

    private static async Task LoadSelfhStContentAsync(
        StackPanel panel,
        string query,
        Action<string> onImageSelected,
        Action<Button>? onButtonClicked = null,
        string? currentImagePath = null,
        Func<bool>? isCurrentRequest = null)
    {
        try
        {
            var catalog = await GetSelfhStCatalogAsync();
            if (isCurrentRequest?.Invoke() == false)
                return;

            var filtered = FilterSelfhStIcons(catalog, query).Take(MaxVisibleSelfhStIcons).ToArray();

            panel.Children.Clear();
            panel.Children.Add(CategoryHeader("selfh.st Icons"));

            if (string.IsNullOrWhiteSpace(query) && catalog.Count > MaxVisibleSelfhStIcons)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Showing the first {MaxVisibleSelfhStIcons} icons. Search to narrow the list.",
                    FontSize = 12,
                    Opacity = 0.65,
                    Margin = new Thickness(4, 0, 0, 8),
                    TextWrapping = TextWrapping.WrapWholeWords
                });
            }

            if (filtered.Length == 0)
            {
                panel.Children.Add(NoResults());
                return;
            }

            var grid = new StackPanel();
            var currentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            int count = 0;

            foreach (var icon in filtered)
            {
                string reference = icon.Reference;

                var btn = new Button
                {
                    Width = 88,
                    Height = 92,
                    Padding = new Thickness(4),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };

                var content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 4
                };

                var thumbnail = new Image { Width = 32, Height = 32 };
                ShowSelfhStThumbnail(thumbnail, reference, isCurrentRequest);
                content.Children.Add(thumbnail);

                content.Children.Add(new TextBlock
                {
                    Text = icon.Name,
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    MaxWidth = 76,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                btn.Content = content;
                ToolTipService.SetToolTip(btn, icon.Name);
                btn.Click += async (s, e) =>
                {
                    string? localPath = await CacheSelfhStIconAsync(icon);
                    if (string.IsNullOrEmpty(localPath))
                        return;

                    onButtonClicked?.Invoke((Button)s!);
                    onImageSelected(localPath);
                };

                if (IsMatchingSelfhStImagePath(currentImagePath, reference))
                {
                    onButtonClicked?.Invoke(btn);
                    onImageSelected(currentImagePath!);
                }

                currentRow.Children.Add(btn);
                count++;

                if (count % SelfhStIconsPerRow == 0)
                {
                    grid.Children.Add(currentRow);
                    currentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                }
            }

            if (currentRow.Children.Count > 0)
                grid.Children.Add(currentRow);

            panel.Children.Add(grid);
        }
        catch
        {
            if (isCurrentRequest?.Invoke() == false)
                return;

            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = "Could not load selfh.st icons. Check your internet connection and try again.",
                FontSize = 13,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(16, 24, 16, 0)
            });
        }
    }

    private static void BuildLauncherPresetsContent(StackPanel panel, string query,
        Action<string> onPresetMode, Action onBrowseRequested, Action<Button>? onButtonClicked = null, string? currentPresetMode = null)
    {
        // ── Composite mode ──
        if (string.IsNullOrEmpty(query) || "composite".Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(CategoryHeader("Special"));
            var compositeBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            var compositeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            compositeRow.Children.Add(new FontIcon { Glyph = "\uF0E2", FontSize = 20 });
            var compositeText = new StackPanel();
            compositeText.Children.Add(new TextBlock { Text = "Composite", FontWeight = FontWeights.Medium });
            compositeText.Children.Add(new TextBlock { Text = "Shows first 4 item icons in a 2\u00d72 grid", FontSize = 12, Opacity = 0.6 });
            compositeRow.Children.Add(compositeText);
            compositeBtn.Content = compositeRow;
            compositeBtn.Click += (s, e) => { onButtonClicked?.Invoke((Button)s!); onPresetMode("Composite"); };
            if (currentPresetMode == "Composite") { onButtonClicked?.Invoke(compositeBtn); onPresetMode("Composite"); }
            panel.Children.Add(compositeBtn);
        }

        // ── Color preset icons (wrapping) ──
        var colors = new[] { "Blue", "Green", "Teal", "Red", "Orange", "Purple" };
        var filteredColors = string.IsNullOrEmpty(query)
            ? colors
            : colors.Where(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (filteredColors.Length > 0)
        {
            panel.Children.Add(CategoryHeader("App Color Icons"));

            var wrapGrid = new StackPanel();
            var currentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            int count = 0;

            foreach (string color in filteredColors)
            {
                string sourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", $"{color}.png");
                if (!File.Exists(sourcePath)) continue;

                var btn = new Button
                {
                    Width = 60,
                    Height = 72,
                    Padding = new Thickness(4),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };

                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new Image
                {
                    Width = 36, Height = 36,
                    Source = new BitmapImage(new Uri(sourcePath, UriKind.Absolute)),
                    Margin = new Thickness(0, 0, 0, 4)
                });
                content.Children.Add(new TextBlock
                {
                    Text = color, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center
                });
                btn.Content = content;
                ToolTipService.SetToolTip(btn, $"{color} app icon");
                btn.Click += (s, e) => { onButtonClicked?.Invoke((Button)s!); onPresetMode(color); };
                if (currentPresetMode == color) { onButtonClicked?.Invoke(btn); onPresetMode(color); }
                currentRow.Children.Add(btn);

                // Wrap after 6 items per row (fits ~396px width)
                if (++count % 6 == 0)
                {
                    wrapGrid.Children.Add(currentRow);
                    currentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                }
            }
            if (currentRow.Children.Count > 0)
                wrapGrid.Children.Add(currentRow);

            panel.Children.Add(wrapGrid);
        }

        // ── Browse for image ──
        if (string.IsNullOrEmpty(query) || "browse image custom".Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(CategoryHeader("Custom"));
            var browseBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            var browseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            browseRow.Children.Add(new FontIcon { Glyph = "\uE8B9", FontSize = 20 });
            var browseText = new StackPanel();
            browseText.Children.Add(new TextBlock { Text = "Browse for image\u2026", FontWeight = FontWeights.Medium });
            browseText.Children.Add(new TextBlock { Text = "Choose a .png, .ico, or other image file", FontSize = 12, Opacity = 0.6 });
            browseRow.Children.Add(browseText);
            browseBtn.Content = browseRow;
            browseBtn.Click += (s, e) => onBrowseRequested();
            panel.Children.Add(browseBtn);
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(NoResults());
    }

    // ═══════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if the glyph is a Segoe Fluent Icons private-use-area character.
    /// Returns false for emojis and standard Unicode characters.
    /// </summary>
    public static bool IsFluentGlyph(string? glyph)
    {
        if (string.IsNullOrEmpty(glyph)) return true; // treat default/empty as fluent
        int cp = char.IsHighSurrogate(glyph[0]) && glyph.Length > 1
            ? char.ConvertToUtf32(glyph[0], glyph[1])
            : glyph[0];
        return cp >= 0xE000 && cp <= 0xF8FF;
    }

    private static Button MakeTabButton(UIElement icon, string tooltip)
    {
        var btn = new Button
        {
            Content = icon,
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(btn, tooltip);
        return btn;
    }

    private static TextBlock CategoryHeader(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Opacity = 0.7,
        Margin = new Thickness(4, 4, 0, 6)
    };

    private static TextBlock NoResults() => new()
    {
        Text = "No matching icons",
        FontSize = 13,
        Opacity = 0.5,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 24, 0, 0)
    };

    private static FrameworkElement CreateSelfhStAttributionBar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };

        row.Children.Add(new TextBlock
        {
            Text = "Icons by ",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new HyperlinkButton
        {
            Content = "selfh.st",
            NavigateUri = SelfhStIconsUri,
            Padding = new Thickness(0, 0, 6, 0),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new TextBlock
        {
            Text = "under ",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new HyperlinkButton
        {
            Content = "CC BY 4.0",
            NavigateUri = SelfhStLicenseUri,
            Padding = new Thickness(0),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });

        return row;
    }

    private static string GetItemIconCacheDir()
    {
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher", "icons");
        Directory.CreateDirectory(cacheDir);
        return cacheDir;
    }

    private static string GetSelfhStPngUrl(string reference) =>
        $"{SelfhStPngUrlPrefix}{Uri.EscapeDataString(reference)}.png";

    /// <summary>
    /// Where browsed selfh.st icons are kept, separate from the icons folder itself.
    /// </summary>
    /// <remarks>
    /// Browsing the tab caches every icon it shows, which is a great many more than the user will
    /// ever pick. Keeping them out of <c>icons</c> leaves that folder as what it has always been —
    /// the icons actually in use — so nothing else has to learn to tell the two apart.
    /// </remarks>
    private static string GetSelfhStCacheDir()
    {
        string cacheDir = Path.Combine(GetItemIconCacheDir(), "selfhst");
        Directory.CreateDirectory(cacheDir);
        return cacheDir;
    }

    /// <summary>Reference names come from a remote index, so they are never trusted as a filename.</summary>
    private static string SelfhStFileName(string reference)
    {
        var name = new System.Text.StringBuilder(reference.Length);
        foreach (char c in reference.ToLowerInvariant())
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return name.ToString();
    }

    private static string GetSelfhStCachePath(string reference) =>
        Path.Combine(GetSelfhStCacheDir(), $"{SelfhStFileName(reference)}.png");

    private static string GetSelfhStCatalogPath() =>
        Path.Combine(GetSelfhStCacheDir(), "index.json");

    /// <summary>
    /// Points a gallery tile at its icon: straight from disk when it has been seen before,
    /// otherwise downloaded once and kept.
    /// </summary>
    /// <remarks>
    /// Binding the tile to the CDN URL directly was what made this tab slow every single time it
    /// was opened — nothing survived the process, so a hundred icons were re-fetched over the wire
    /// on each visit. Downloading them here instead costs the same first time and nothing after.
    /// </remarks>
    private static void ShowSelfhStThumbnail(Image image, string reference, Func<bool>? isCurrentRequest)
    {
        string cachePath = GetSelfhStCachePath(reference);
        if (File.Exists(cachePath))
        {
            image.Source = CreateSelfhStBitmap(cachePath);
            return;
        }

        _ = LoadSelfhStThumbnailAsync(image, reference, cachePath, isCurrentRequest);
    }

    private static async Task LoadSelfhStThumbnailAsync(
        Image image, string reference, string cachePath, Func<bool>? isCurrentRequest)
    {
        var dispatcher = image.DispatcherQueue;

        await SelfhStDownloadGate.WaitAsync();
        try
        {
            // The user has typed on since this tile was built; the panel it lives in is gone.
            if (isCurrentRequest?.Invoke() == false)
                return;

            byte[] bytes = await SelfhStHttp.GetByteArrayAsync(GetSelfhStPngUrl(reference));
            await WriteSelfhStCacheFileAsync(cachePath, bytes);
        }
        catch
        {
            return;   // a tile without an icon, rather than an error over the whole tab
        }
        finally
        {
            SelfhStDownloadGate.Release();
        }

        dispatcher?.TryEnqueue(() => image.Source = CreateSelfhStBitmap(cachePath));
    }

    private static BitmapImage CreateSelfhStBitmap(string path) => new()
    {
        DecodePixelWidth = 32,
        UriSource = new Uri(path),
    };

    /// <summary>
    /// Writes a cache file whole or not at all.
    /// </summary>
    /// <remarks>
    /// A cached file is never re-downloaded, so a half-written one is permanent: an interrupted
    /// write would leave a tile that renders blank for good. Writing beside it and moving into
    /// place also settles the race between two galleries caching the same icon at once.
    /// </remarks>
    private static async Task WriteSelfhStCacheFileAsync(string path, byte[] bytes)
    {
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* nothing further to do */ }
            throw;
        }
    }

    private static bool IsSelfhStImagePath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        Path.GetFileName(path).StartsWith("selfhst-", StringComparison.OrdinalIgnoreCase);

    private static bool IsMatchingSelfhStImagePath(string? path, string reference) =>
        !string.IsNullOrEmpty(path) &&
        Path.GetFileName(path).Equals($"selfhst-{SelfhStFileName(reference)}.png", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<SelfhStIconEntry> FilterSelfhStIcons(IEnumerable<SelfhStIconEntry> icons, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return icons;

        return icons.Where(icon =>
            icon.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            icon.Reference.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(icon.Tags) && icon.Tags.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(icon.Category) && icon.Category.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The icon index, from memory, then disk, then the network.
    /// </summary>
    /// <remarks>
    /// The disk copy is what makes the tab open instantly rather than on a spinner: the in-memory
    /// copy dies with the process, and the index is a megabyte of JSON fetched before a single icon
    /// can be drawn. A stale copy is also better than none — if the fetch fails, whatever was last
    /// written is served rather than failing the whole tab, and it is treated as current for the
    /// rest of the session so every keystroke does not re-attempt a download that just timed out.
    /// </remarks>
    private static async Task<IReadOnlyList<SelfhStIconEntry>> GetSelfhStCatalogAsync()
    {
        if (_selfhStCatalog != null && DateTimeOffset.UtcNow - _selfhStCatalogFetchedAt < SelfhStCatalogTtl)
            return _selfhStCatalog;

        await SelfhStCatalogLock.WaitAsync();
        try
        {
            if (_selfhStCatalog != null && DateTimeOffset.UtcNow - _selfhStCatalogFetchedAt < SelfhStCatalogTtl)
                return _selfhStCatalog;

            string catalogPath = GetSelfhStCatalogPath();
            var cached = ReadSelfhStCatalogFile(catalogPath);
            if (cached != null &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(catalogPath) < SelfhStCatalogTtl)
            {
                return CacheSelfhStCatalog(cached);
            }

            try
            {
                string json = await SelfhStHttp.GetStringAsync(SelfhStIndexUrl);
                var icons = ParseSelfhStCatalog(json);

                try
                {
                    await WriteSelfhStCacheFileAsync(catalogPath, System.Text.Encoding.UTF8.GetBytes(json));
                }
                catch { /* the index still works this session; it just re-fetches next time */ }

                return CacheSelfhStCatalog(icons);
            }
            catch when (cached != null)
            {
                return CacheSelfhStCatalog(cached);
            }
        }
        finally
        {
            SelfhStCatalogLock.Release();
        }
    }

    private static IReadOnlyList<SelfhStIconEntry> CacheSelfhStCatalog(IReadOnlyList<SelfhStIconEntry> icons)
    {
        _selfhStCatalog = icons;
        _selfhStCatalogFetchedAt = DateTimeOffset.UtcNow;
        return icons;
    }

    private static IReadOnlyList<SelfhStIconEntry> ParseSelfhStCatalog(string json) =>
        JsonSerializer.Deserialize<List<SelfhStIconEntry>>(json, SelfhStJsonOptions)?
            .Where(icon => !string.IsNullOrWhiteSpace(icon.Name) && !string.IsNullOrWhiteSpace(icon.Reference))
            .OrderBy(icon => icon.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray() ?? [];

    private static IReadOnlyList<SelfhStIconEntry>? ReadSelfhStCatalogFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var icons = ParseSelfhStCatalog(File.ReadAllText(path));
            return icons.Count > 0 ? icons : null;
        }
        catch
        {
            return null;   // truncated or half-written: treat it as absent and re-fetch
        }
    }

    /// <summary>
    /// Puts the chosen icon at its permanent path in the icons folder, taking it from the
    /// browsing cache when it is already there rather than fetching it a second time.
    /// </summary>
    private static async Task<string?> CacheSelfhStIconAsync(SelfhStIconEntry icon)
    {
        string cachePath = Path.Combine(GetItemIconCacheDir(), $"selfhst-{SelfhStFileName(icon.Reference)}.png");
        if (File.Exists(cachePath))
            return cachePath;

        try
        {
            string browsePath = GetSelfhStCachePath(icon.Reference);
            if (File.Exists(browsePath))
            {
                File.Copy(browsePath, cachePath, overwrite: true);
                return cachePath;
            }

            byte[] bytes = await SelfhStHttp.GetByteArrayAsync(GetSelfhStPngUrl(icon.Reference));
            await File.WriteAllBytesAsync(cachePath, bytes);
            return cachePath;
        }
        catch
        {
            return null;
        }
    }

    private sealed class SelfhStIconEntry
    {
        [JsonPropertyName("Name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("Reference")]
        public string Reference { get; init; } = "";

        [JsonPropertyName("Tags")]
        public string? Tags { get; init; }

        [JsonPropertyName("Category")]
        public string? Category { get; init; }
    }

    /// <summary>Preset glyph colors available in the icon gallery.</summary>
    private static readonly (string Hex, string Name)[] GlyphColors =
    [
        ("", "Default"),
        ("#E74856", "Red"),
        ("#FF8C00", "Orange"),
        ("#FFC83D", "Yellow"),
        ("#47B353", "Green"),
        ("#00B7C3", "Teal"),
        ("#0078D4", "Blue"),
        ("#8764B8", "Purple"),
        ("#E3008C", "Pink"),
        ("#6B6B6B", "Gray"),
        ("#FFFFFF", "White"),
        ("#000000", "Black"),
    ];

    private static SolidColorBrush? ParseHexBrush(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return null;
        try
        {
            byte r = Convert.ToByte(hex.Substring(1, 2), 16);
            byte g = Convert.ToByte(hex.Substring(3, 2), 16);
            byte b = Convert.ToByte(hex.Substring(5, 2), 16);
            return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
        }
        catch { return null; }
    }

    /// <summary>
    /// Builds a color palette row. Returns the panel and a function to get the current selected color hex.
    /// </summary>
    private static (StackPanel Panel, Func<string> GetSelectedColor) BuildColorPalette(
        string? initialColor, Action? onColorChanged = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        string selectedColor = initialColor ?? "";
        Button? selectedButton = null;

        foreach (var (hex, name) in GlyphColors)
        {
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
            };

            Brush colorBrush;
            if (string.IsNullOrEmpty(hex))
            {
                colorBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            }
            else
            {
                byte r = Convert.ToByte(hex[1..3], 16);
                byte g = Convert.ToByte(hex[3..5], 16);
                byte b = Convert.ToByte(hex[5..7], 16);
                colorBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
            }
            btn.Background = colorBrush;

            // Override hover/press backgrounds to keep the swatch color visible
            btn.Resources["ButtonBackgroundPointerOver"] = colorBrush;
            btn.Resources["ButtonBackgroundPressed"] = colorBrush;
            btn.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            void UpdateSelection(Button b, string h)
            {
                if (selectedButton != null)
                    selectedButton.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

                selectedButton = b;
                selectedColor = h;
                b.BorderBrush = GetAccentBrush();
                onColorChanged?.Invoke();
            }

            // Set initial selection
            if (hex.Equals(selectedColor, StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrEmpty(hex) && string.IsNullOrEmpty(selectedColor)))
            {
                btn.BorderBrush = GetAccentBrush();
                selectedButton = btn;
            }
            else
            {
                btn.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }

            ToolTipService.SetToolTip(btn, name);
            string capturedHex = hex;
            btn.Click += (s, e) => UpdateSelection((Button)s!, capturedHex);
            row.Children.Add(btn);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Color",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.7,
            Margin = new Thickness(4, 0, 0, 4)
        });
        panel.Children.Add(row);
        return (panel, () => selectedColor);
    }

    private static Brush GetAccentBrush()
    {
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var b))
            return (Brush)b;
        return new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
    }

    // ═══════════════════════════════════════════════════════════
    //  Icon Data — Segoe Fluent Icons
    // ═══════════════════════════════════════════════════════════

    private static readonly (string Category, (string Glyph, string Name)[] Icons)[] FluentIconCategories =
    [
        ("Web & Communication", [
            ("\uE774", "Globe"),
            ("\uF49A", "Globe 2"),
            ("\uEB41", "Website"),
            ("\uE715", "Mail"),
            ("\uE8A8", "Mail Fill"),
            ("\uE89C", "Mail Forward"),
            ("\uE8CA", "Mail Reply"),
            ("\uE8C2", "Mail Reply All"),
            ("\uE717", "Phone"),
            ("\uE8EA", "Cell Phone"),
            ("\uE77E", "Incoming Call"),
            ("\uE778", "Hang Up"),
            ("\uE8BD", "Message"),
            ("\uE90A", "Comment"),
            ("\uEA91", "Thought Bubble"),
            ("\uE8F2", "Chat Bubbles"),
            ("\uEAB7", "Chat Sparkle"),
            ("\uE8AA", "Video Chat"),
            ("\uE71B", "Link"),
            ("\uE72D", "Share"),
            ("\uE8EB", "Reshare"),
            ("\uE724", "Send"),
            ("\uE725", "Send Fill"),
            ("\uF3E2", "Nearby Sharing"),
            ("\uE716", "People"),
            ("\uE77B", "Contact"),
            ("\uE8D4", "Contact 2"),
            ("\uE779", "Contact Info"),
            ("\uE902", "Group"),
            ("\uEBDA", "Family"),
            ("\uE8FA", "Add Friend"),
            ("\uE8F8", "Block Contact"),
            ("\uE7EE", "Other User"),
            ("\uEE57", "Guest User"),
            ("\uE910", "Accounts"),
            ("\uF3B1", "Sign Out"),
            ("\uE701", "WiFi"),
            ("\uE702", "Bluetooth"),
            ("\uE839", "Ethernet"),
            ("\uE753", "Cloud"),
            ("\uEBD3", "Cloud Download"),
            ("\uEDE4", "Cloud Search"),
            ("\uE896", "Download"),
            ("\uE898", "Upload"),
            ("\uE703", "Connect"),
            ("\uE704", "Internet Sharing"),
            ("\uEC05", "Network Tower"),
            ("\uE968", "Network"),
            ("\uF385", "Network Connected"),
            ("\uF384", "Network Offline"),
            ("\uF193", "Network Sharing"),
            ("\uEB77", "Gateway Router"),
            ("\uE705", "VPN"),
            ("\uE88A", "WiFi Hotspot"),
        ]),
        ("Files & Documents", [
            ("\uE8E5", "Open File"),
            ("\uE8A5", "Document"),
            ("\uE8A6", "Protected Document"),
            ("\uEA90", "PDF"),
            ("\uE9F9", "Report Document"),
            ("\uF28B", "Document Approval"),
            ("\uE8B7", "Folder"),
            ("\uE8D5", "Folder Fill"),
            ("\uE838", "Folder Open"),
            ("\uE8F4", "New Folder"),
            ("\uED25", "Open Folder"),
            ("\uEC25", "Personal Folder"),
            ("\uF012", "Zip Folder"),
            ("\uE8DE", "Move to Folder"),
            ("\uE74E", "Save"),
            ("\uE792", "Save As"),
            ("\uE78C", "Save Local"),
            ("\uEA35", "Save Copy"),
            ("\uE8F1", "Library"),
            ("\uE7C3", "Page"),
            ("\uE8C8", "Copy"),
            ("\uF413", "Copy To"),
            ("\uE77F", "Paste"),
            ("\uE8C6", "Cut"),
            ("\uE74D", "Delete"),
            ("\uE723", "Attach"),
            ("\uE710", "Add"),
            ("\uE711", "Cancel"),
            ("\uE738", "Remove"),
            ("\uF56D", "Print Default"),
            ("\uE8A4", "Bookmarks"),
            ("\uE728", "Favorite List"),
            ("\uE8C3", "Read"),
            ("\uE736", "Reading Mode"),
            ("\uE7BC", "Reading List"),
            ("\uE8AC", "Rename"),
            ("\uE8EC", "Tag"),
            ("\uE932", "Label"),
            ("\uE762", "Multi-Select"),
            ("\uE8B3", "Select All"),
            ("\uE8CB", "Sort"),
            ("\uE71C", "Filter"),
            ("\uE8B5", "Import"),
            ("\uEDE1", "Export"),
            ("\uEC50", "File Explorer"),
            ("\uED41", "Tree Folder"),
            ("\uE8F7", "Sync Folder"),
            ("\uE8F6", "Unsync Folder"),
            ("\uE8FF", "Preview"),
            ("\uE8A1", "Preview Link"),
        ]),
        ("Media & Entertainment", [
            ("\uE768", "Play"),
            ("\uF5B0", "Play Solid"),
            ("\uE769", "Pause"),
            ("\uE71A", "Stop"),
            ("\uE8D6", "Audio"),
            ("\uEC4F", "Music Note"),
            ("\uE93C", "Music Album"),
            ("\uE90B", "Music Info"),
            ("\uE93E", "Streaming"),
            ("\uE722", "Camera"),
            ("\uE89E", "Rotate Camera"),
            ("\uE714", "Video"),
            ("\uF131", "Video 360"),
            ("\uF7EE", "Video Capture"),
            ("\uE91B", "Photo"),
            ("\uEB9F", "Photo 2"),
            ("\uE7AA", "Photo Collection"),
            ("\uE8B9", "Picture"),
            ("\uE767", "Volume"),
            ("\uE74F", "Mute"),
            ("\uF4C3", "Mix Volumes"),
            ("\uE786", "Slideshow"),
            ("\uE720", "Microphone"),
            ("\uEC71", "Mic On"),
            ("\uEC54", "Mic Off"),
            ("\uE7F6", "Headphone"),
            ("\uE95B", "Headset"),
            ("\uF4C0", "Earbud"),
            ("\uE7F5", "Speakers"),
            ("\uE7FC", "Game"),
            ("\uE967", "Game Console"),
            ("\uE899", "Emoji"),
            ("\uE76E", "Emoji 2"),
            ("\uEB9D", "Fast Forward"),
            ("\uEB9E", "Rewind"),
            ("\uE892", "Previous"),
            ("\uE893", "Next"),
            ("\uE8B1", "Shuffle"),
            ("\uE8EE", "Repeat All"),
            ("\uE8ED", "Repeat One"),
            ("\uEF3B", "Replay"),
            ("\uE8B2", "Movies"),
            ("\uE7AD", "Rotate"),
            ("\uE8AB", "Switch"),
            ("\uE8BA", "Caption"),
            ("\uED1E", "Subtitles"),
            ("\uE7F0", "Closed Captions"),
            ("\uE7E6", "Highlight"),
            ("\uF4A9", "GIF"),
            ("\uE9E9", "Equalizer"),
        ]),
        ("Tools & Development", [
            ("\uE756", "Command Prompt"),
            ("\uE943", "Code"),
            ("\uEC7A", "Developer Tools"),
            ("\uEBE8", "Bug"),
            ("\uE9D9", "Diagnostic"),
            ("\uE713", "Settings"),
            ("\uF8B0", "Settings Solid"),
            ("\uE90F", "Repair"),
            ("\uE912", "Manage"),
            ("\uE721", "Search"),
            ("\uED37", "Search Sparkle"),
            ("\uF6FA", "Web Search"),
            ("\uE70F", "Edit"),
            ("\uE72C", "Refresh"),
            ("\uE895", "Sync"),
            ("\uEA6A", "Sync Error"),
            ("\uE790", "Color"),
            ("\uF354", "Color Solid"),
            ("\uEF3C", "Eyedropper"),
            ("\uE8A3", "Zoom In"),
            ("\uE71F", "Zoom Out"),
            ("\uE7A7", "Undo"),
            ("\uE7A6", "Redo"),
            ("\uE7A8", "Crop"),
            ("\uE7EF", "Admin"),
            ("\uED5E", "Ruler"),
            ("\uF0B4", "Protractor"),
            ("\uE7AC", "Open With"),
            ("\uE8FE", "Scan"),
            ("\uEE6F", "Generic Scan"),
            ("\uED14", "QR Code"),
            ("\uE78B", "New Window"),
            ("\uE8A7", "Open in New Window"),
            ("\uE944", "Return to Window"),
            ("\uE90C", "Dock Left"),
            ("\uE90D", "Dock Right"),
            ("\uE90E", "Dock Bottom"),
            ("\uE952", "Dock"),
            ("\uF120", "Task Manager"),
            ("\uE950", "Component"),
            ("\uE9F3", "Process"),
            ("\uE82F", "Tooltip"),
            ("\uE76D", "Inking Tool"),
            ("\uED63", "Pencil"),
            ("\uED64", "Marker"),
            ("\uEC87", "Draw"),
            ("\uF196", "Beaker"),
            ("\uEB3C", "Design"),
            ("\uF406", "Clipping Tool"),
            ("\uF7ED", "Window Snipping"),
            ("\uEBC6", "Project"),
            ("\uE794", "Effects"),
            ("\uE799", "Aspect Ratio"),
            ("\uEDA7", "Keyboard Shortcut"),
            ("\uEA99", "Broom"),
            ("\uF034", "Widget"),
        ]),
        ("Text & Formatting", [
            ("\uE8D2", "Font"),
            ("\uE8D3", "Font Color"),
            ("\uE8E8", "Font Increase"),
            ("\uE8E7", "Font Decrease"),
            ("\uE8E9", "Font Size"),
            ("\uE8DD", "Bold"),
            ("\uE8DB", "Italic"),
            ("\uE8DC", "Underline"),
            ("\uEDE0", "Strikethrough"),
            ("\uE8E4", "Align Left"),
            ("\uE8E3", "Align Center"),
            ("\uE8E2", "Align Right"),
            ("\uE8FD", "Bulleted List"),
            ("\uEA37", "List"),
            ("\uE9D5", "Checklist"),
            ("\uE8C1", "Characters"),
            ("\uEC34", "Format Text"),
            ("\uEF60", "Text Edit"),
            ("\uE929", "Handwriting"),
            ("\uE82D", "Dictionary"),
            ("\uE87B", "Spelling"),
        ]),
        ("Shopping & Finance", [
            ("\uE719", "Shop"),
            ("\uE7BF", "Shopping Cart"),
            ("\uEAFC", "Market"),
            ("\uE8EF", "Calculator"),
            ("\uE825", "Bank"),
            ("\uEB95", "Certificate"),
            ("\uE8C7", "Payment Card"),
            ("\uE7B8", "Package"),
            ("\uEB0F", "Stock Up"),
            ("\uEB11", "Stock Down"),
            ("\uE8C9", "Important"),
            ("\uE8D0", "Priority"),
            ("\uEC09", "Groceries"),
            ("\uE821", "Work"),
        ]),
        ("Calendar & Time", [
            ("\uE787", "Calendar"),
            ("\uE8BF", "Calendar Day"),
            ("\uE8C0", "Calendar Week"),
            ("\uE8D1", "Go to Today"),
            ("\uE8F5", "Calendar Reply"),
            ("\uE917", "Clock"),
            ("\uE916", "Stopwatch"),
            ("\uEC92", "Date Time"),
            ("\uE81C", "History"),
            ("\uE823", "Recent"),
            ("\uE708", "Quiet Hours"),
            ("\uEB50", "Reminder"),
            ("\uF4BD", "Snooze"),
        ]),
        ("Places & Travel", [
            ("\uE80F", "Home"),
            ("\uEA8A", "Home Solid"),
            ("\uEC06", "City"),
            ("\uE7BE", "Education"),
            ("\uF7BB", "Education Icon"),
            ("\uE822", "Construction"),
            ("\uE81D", "Location"),
            ("\uE707", "Map Pin"),
            ("\uE7B7", "Map Pin 2"),
            ("\uE81E", "Map Layers"),
            ("\uE8F0", "Directions"),
            ("\uE816", "Map Directions"),
            ("\uE804", "Car"),
            ("\uE806", "Bus"),
            ("\uE7E3", "Ferry"),
            ("\uE7C0", "Train"),
            ("\uE709", "Airplane"),
            ("\uE909", "World"),
            ("\uE805", "Walk"),
            ("\uE811", "Parking Location"),
            ("\uEC08", "Courthouse"),
            ("\uEC32", "Cafe"),
            ("\uEF31", "Traffic Light"),
            ("\uE81F", "Accident"),
            ("\uE98F", "Construction Cone"),
            ("\uE7EC", "Driving Mode"),
        ]),
        ("Devices & Hardware", [
            ("\uEC4E", "This PC"),
            ("\uE977", "PC"),
            ("\uE7F7", "Laptop"),
            ("\uF552", "Laptop Secure"),
            ("\uE7F4", "TV Monitor"),
            ("\uE70A", "Tablet"),
            ("\uE8CC", "Mobile Tablet"),
            ("\uE8B8", "Webcam"),
            ("\uE960", "Webcam 2"),
            ("\uE914", "3D Printer"),
            ("\uE749", "Printer"),
            ("\uE7E8", "Power Button"),
            ("\uEBDE", "Device Discovery"),
            ("\uE8AF", "Remote"),
            ("\uE772", "Devices"),
            ("\uE88E", "USB"),
            ("\uEDA2", "Hard Drive"),
            ("\uEDA3", "Network Adapter"),
            ("\uE7F1", "SD Card"),
            ("\uE961", "Input"),
            ("\uE962", "Mouse"),
            ("\uEFA5", "Touchpad"),
            ("\uEDA4", "Touchscreen"),
            ("\uE765", "Keyboard Classic"),
            ("\uE957", "Sensor"),
            ("\uE95D", "Projector"),
            ("\uEEA0", "RAM"),
            ("\uEEA1", "CPU"),
            ("\uEC94", "HoloLens"),
            ("\uF847", "Eject"),
        ]),
        ("System & Apps", [
            ("\uE770", "System"),
            ("\uE771", "Personalize"),
            ("\uE777", "Update Restore"),
            ("\uE8F9", "Switch Apps"),
            ("\uED35", "Apps"),
            ("\uEB3B", "Generic App"),
            ("\uECAA", "App Icon Default"),
            ("\uE7C4", "Task View"),
            ("\uE748", "Switch User"),
            ("\uEE3F", "Lockscreen Desktop"),
            ("\uF182", "Screen Time"),
            ("\uEF90", "Flow"),
            ("\uE9A3", "AI Sparkle"),
            ("\uE99A", "Robot"),
            ("\uF7EC", "Task"),
            ("\uE706", "Brightness"),
            ("\uE793", "Light"),
            ("\uF08C", "Blue Light"),
            ("\uE776", "Ease of Access"),
            ("\uE775", "Time Language"),
            ("\uF2B7", "Locale Language"),
            ("\uE939", "Feedback App"),
            ("\uED15", "Feedback"),
            ("\uEEA3", "Virtual Machine"),
            ("\uEA86", "Puzzle"),
            ("\uF000", "Knowledge Article"),
            ("\uF246", "View Dashboard"),
            ("\uF404", "Interactive Dashboard"),
            ("\uECA5", "Tiles"),
            ("\uF0E2", "Grid View"),
        ]),
        ("Security & Privacy", [
            ("\uE72E", "Lock"),
            ("\uE785", "Unlock"),
            ("\uEA18", "Shield"),
            ("\uE83D", "Defender"),
            ("\uF5B4", "Shield Lock"),
            ("\uE727", "InPrivate"),
            ("\uE8D7", "Permissions"),
            ("\uE8D8", "Disable Updates"),
            ("\uE730", "Report Hacked"),
            ("\uE733", "Blocked"),
            ("\uE928", "Fingerprint"),
            ("\uF439", "Dynamic Lock"),
            ("\uF427", "ID Badge"),
            ("\uE963", "Smartcard"),
            ("\uEE7E", "Passkey"),
            ("\uE890", "View"),
            ("\uED1A", "Hide"),
            ("\uE9A8", "Password Show"),
            ("\uE9A9", "Password Hide"),
            ("\uF0EF", "Application Guard"),
            ("\uF540", "Safe"),
        ]),
        ("Navigation & Windows", [
            ("\uE72A", "Forward"),
            ("\uE72B", "Back"),
            ("\uE70E", "Chevron Up"),
            ("\uE70D", "Chevron Down"),
            ("\uE76C", "Chevron Right"),
            ("\uE76B", "Chevron Left"),
            ("\uE74A", "Up"),
            ("\uE74B", "Down"),
            ("\uE8AD", "Go"),
            ("\uE700", "Menu"),
            ("\uE712", "More"),
            ("\uE71D", "All Apps"),
            ("\uE8FC", "Go to Start"),
            ("\uE89F", "Close Pane"),
            ("\uE8A0", "Open Pane"),
            ("\uE73F", "Back to Window"),
            ("\uE740", "Full Screen"),
            ("\uE97A", "Reply"),
            ("\uE8A9", "View All"),
            ("\uE89A", "Two Page"),
            ("\uE7C2", "Move"),
            ("\uE8B0", "Click"),
            ("\uECCD", "Explore Content"),
        ]),
        ("Status & Symbols", [
            ("\uE734", "Star"),
            ("\uE735", "Star Filled"),
            ("\uE73E", "Checkmark"),
            ("\uEC61", "Completed"),
            ("\uE8FB", "Accept"),
            ("\uE7BA", "Warning"),
            ("\uE814", "Incident Triangle"),
            ("\uE783", "Error"),
            ("\uE945", "Lightning"),
            ("\uE840", "Pinned"),
            ("\uE718", "Pin"),
            ("\uE77A", "Unpin"),
            ("\uE7C1", "Flag"),
            ("\uE946", "Info"),
            ("\uE897", "Help"),
            ("\uEB51", "Heart"),
            ("\uEB52", "Heart Fill"),
            ("\uEA92", "Heart Broken"),
            ("\uE8E1", "Like"),
            ("\uE8E0", "Dislike"),
            ("\uEA80", "Lightbulb"),
            ("\uE91C", "Action Center"),
            ("\uEA8F", "Ringer"),
            ("\uE754", "Flashlight"),
            ("\uE781", "LED Light"),
            ("\uE789", "Megaphone"),
            ("\uE8BE", "Leaf"),
            ("\uF1E8", "Leaf Two"),
            ("\uEC0A", "Sustainable"),
            ("\uEB42", "Drop"),
            ("\uE9CE", "Unknown"),
            ("\uEB44", "Radar"),
            ("\uE877", "Vibrate"),
            ("\uE915", "Radio Bullet"),
            ("\uE9D2", "Area Chart"),
            ("\uEB05", "Pie Chart"),
            ("\uF003", "Relationship"),
            ("\uEA24", "Beta"),
            ("\uE95E", "Health"),
        ]),
    ];

    // ═══════════════════════════════════════════════════════════
    //  Icon Data — Emoji
    // ═══════════════════════════════════════════════════════════

    private static readonly (string Category, (string Emoji, string Name)[] Items)[] EmojiCategories =
    [
        ("Smileys & People", [
            ("😀", "Grinning"),
            ("😃", "Happy"),
            ("😄", "Big Smile"),
            ("😁", "Beaming"),
            ("😆", "Laughing"),
            ("😅", "Sweat Smile"),
            ("🤣", "Rolling"),
            ("😂", "Joy"),
            ("🙂", "Slight Smile"),
            ("�", "Upside Down"),
            ("😉", "Wink"),
            ("😊", "Blush"),
            ("😇", "Halo"),
            ("😎", "Cool"),
            ("🤓", "Nerd"),
            ("🧐", "Monocle"),
            ("🤩", "Star Eyes"),
            ("😍", "Heart Eyes"),
            ("🥰", "Smiling Hearts"),
            ("😘", "Kiss"),
            ("😋", "Yummy"),
            ("😜", "Tongue Wink"),
            ("🤪", "Zany"),
            ("😝", "Tongue Squint"),
            ("🥳", "Party"),
            ("🤗", "Hugging"),
            ("🤭", "Hand Over Mouth"),
            ("🤫", "Shushing"),
            ("🤔", "Thinking"),
            ("🤐", "Zipper Mouth"),
            ("😏", "Smirk"),
            ("😌", "Relieved"),
            ("😔", "Pensive"),
            ("😪", "Sleepy"),
            ("😴", "Sleeping"),
            ("🤤", "Drooling"),
            ("😷", "Mask"),
            ("🤒", "Thermometer"),
            ("🤢", "Nauseated"),
            ("🤮", "Vomiting"),
            ("🥵", "Hot"),
            ("🥶", "Cold"),
            ("😵", "Dizzy"),
            ("🤯", "Exploding Head"),
            ("😱", "Screaming"),
            ("😨", "Fearful"),
            ("😰", "Anxious"),
            ("😢", "Crying"),
            ("😭", "Sobbing"),
            ("😤", "Huffing"),
            ("😠", "Angry"),
            ("😡", "Rage"),
            ("🤬", "Cursing"),
            ("🥺", "Pleading"),
            ("😬", "Grimacing"),
            ("🫠", "Melting"),
            ("🫡", "Saluting"),
            ("🫢", "Peeking"),
            ("🤖", "Robot"),
            ("👻", "Ghost"),
            ("💀", "Skull"),
            ("👽", "Alien"),
            ("🤡", "Clown"),
            ("😈", "Devil"),
            ("👹", "Ogre"),
            ("👺", "Goblin"),
            ("💩", "Poop"),
        ]),
        ("Gestures & Body", [
            ("👋", "Wave"),
            ("👍", "Thumbs Up"),
            ("👎", "Thumbs Down"),
            ("👏", "Clap"),
            ("🙏", "Pray"),
            ("💪", "Muscle"),
            ("🤝", "Handshake"),
            ("✌️", "Peace"),
            ("🤞", "Crossed Fingers"),
            ("🤟", "Love You"),
            ("🤘", "Rock On"),
            ("🤙", "Call Me"),
            ("👀", "Eyes"),
            ("👁️", "Eye"),
            ("🧠", "Brain"),
            ("🫀", "Anatomical Heart"),
            ("🫁", "Lungs"),
            ("🦷", "Tooth"),
            ("🦴", "Bone"),
            ("👤", "Silhouette"),
            ("👥", "Two Silhouettes"),
            ("🙋", "Raising Hand"),
            ("💁", "Tipping Hand"),
            ("🤷", "Shrug"),
            ("🙇", "Bowing"),
            ("🙅", "No Gesture"),
            ("🙆", "OK Gesture"),
            ("✋", "Raised Hand"),
            ("🤚", "Back of Hand"),
            ("🖐️", "Splayed Hand"),
            ("🖖", "Vulcan Salute"),
            ("👆", "Point Up"),
            ("👇", "Point Down"),
            ("👈", "Point Left"),
            ("👉", "Point Right"),
            ("☝️", "Index Up"),
            ("🫵", "Pointing at Viewer"),
            ("✊", "Raised Fist"),
            ("👊", "Fist Bump"),
            ("🤲", "Palms Up"),
            ("🫶", "Heart Hands"),
        ]),
        ("Animals & Nature", [
            ("🐶", "Dog"),
            ("🐱", "Cat"),
            ("🐭", "Mouse"),
            ("🐹", "Hamster"),
            ("🐰", "Rabbit"),
            ("🦊", "Fox"),
            ("🐻", "Bear"),
            ("🐼", "Panda"),
            ("🐨", "Koala"),
            ("🐯", "Tiger"),
            ("🦁", "Lion"),
            ("🐮", "Cow"),
            ("🐷", "Pig"),
            ("🐸", "Frog"),
            ("🐵", "Monkey"),
            ("🐔", "Chicken"),
            ("🐧", "Penguin"),
            ("🐦", "Bird"),
            ("🦅", "Eagle"),
            ("🦆", "Duck"),
            ("🦉", "Owl"),
            ("🦇", "Bat"),
            ("🐺", "Wolf"),
            ("🐗", "Boar"),
            ("🐴", "Horse"),
            ("🦄", "Unicorn"),
            ("🐝", "Bee"),
            ("🐛", "Bug"),
            ("🦋", "Butterfly"),
            ("🐌", "Snail"),
            ("🐞", "Ladybug"),
            ("🐢", "Turtle"),
            ("🐍", "Snake"),
            ("🦎", "Lizard"),
            ("🐙", "Octopus"),
            ("🦑", "Squid"),
            ("🦐", "Shrimp"),
            ("🦀", "Crab"),
            ("🐠", "Tropical Fish"),
            ("🐡", "Blowfish"),
            ("🦈", "Shark"),
            ("🐬", "Dolphin"),
            ("🐳", "Whale"),
            ("🐊", "Crocodile"),
            ("🦕", "Dinosaur"),
            ("🦖", "T-Rex"),
            ("🌲", "Tree"),
            ("🌳", "Deciduous Tree"),
            ("🌴", "Palm Tree"),
            ("🌵", "Cactus"),
            ("🌻", "Sunflower"),
            ("🌹", "Rose"),
            ("🌺", "Hibiscus"),
            ("🌸", "Cherry Blossom"),
            ("💐", "Bouquet"),
            ("🍀", "Clover"),
            ("🍁", "Maple Leaf"),
            ("🍂", "Fallen Leaf"),
            ("🍃", "Leaf in Wind"),
            ("🌈", "Rainbow"),
            ("🌊", "Wave"),
            ("❄️", "Snowflake"),
            ("🌙", "Crescent Moon"),
            ("🌞", "Sun"),
            ("☀️", "Sunny"),
        ]),
        ("Food & Drink", [
            ("🍎", "Apple"),
            ("🍐", "Pear"),
            ("🍊", "Orange"),
            ("🍋", "Lemon"),
            ("🍌", "Banana"),
            ("🍉", "Watermelon"),
            ("🍇", "Grapes"),
            ("🍓", "Strawberry"),
            ("🫐", "Blueberries"),
            ("🍑", "Peach"),
            ("🥝", "Kiwi"),
            ("🍍", "Pineapple"),
            ("🥑", "Avocado"),
            ("🥕", "Carrot"),
            ("🌽", "Corn"),
            ("🌶️", "Pepper"),
            ("🍕", "Pizza"),
            ("🍔", "Burger"),
            ("🍟", "Fries"),
            ("🌮", "Taco"),
            ("🌯", "Burrito"),
            ("🍣", "Sushi"),
            ("🍜", "Steaming Bowl"),
            ("🍝", "Spaghetti"),
            ("🥘", "Paella"),
            ("🍲", "Stew"),
            ("🥗", "Salad"),
            ("🍰", "Cake"),
            ("🎂", "Birthday Cake"),
            ("🍩", "Donut"),
            ("🍦", "Ice Cream"),
            ("🍫", "Chocolate"),
            ("🍬", "Candy"),
            ("🍭", "Lollipop"),
            ("☕", "Coffee"),
            ("🍵", "Tea"),
            ("🍺", "Beer"),
            ("🍻", "Clinking Beer"),
            ("🍷", "Wine"),
            ("🍸", "Cocktail"),
            ("🥂", "Champagne"),
            ("🧃", "Juice Box"),
            ("🥤", "Cup with Straw"),
            ("🧋", "Bubble Tea"),
            ("🍿", "Popcorn"),
            ("🧁", "Cupcake"),
            ("🍪", "Cookie"),
            ("🥐", "Croissant"),
            ("🥖", "Baguette"),
            ("🧀", "Cheese"),
            ("🥚", "Egg"),
            ("🥩", "Steak"),
            ("🍗", "Drumstick"),
        ]),
        ("Activities & Sports", [
            ("⚽", "Soccer"),
            ("🏀", "Basketball"),
            ("🏈", "Football"),
            ("⚾", "Baseball"),
            ("🎾", "Tennis"),
            ("🏐", "Volleyball"),
            ("🏓", "Ping Pong"),
            ("🏸", "Badminton"),
            ("🥊", "Boxing"),
            ("⛳", "Golf"),
            ("🎱", "Billiards"),
            ("🏹", "Archery"),
            ("🎣", "Fishing"),
            ("🥋", "Martial Arts"),
            ("⛷️", "Skiing"),
            ("🏂", "Snowboarding"),
            ("🏄", "Surfing"),
            ("🚴", "Cycling"),
            ("🏊", "Swimming"),
            ("🤸", "Cartwheeling"),
            ("🧗", "Climbing"),
            ("🧘", "Yoga"),
            ("🎮", "Video Game"),
            ("🕹️", "Joystick"),
            ("🎲", "Dice"),
            ("🧩", "Puzzle"),
            ("♟️", "Chess"),
            ("🎯", "Bullseye"),
            ("🏆", "Trophy"),
            ("🥇", "Gold Medal"),
            ("🥈", "Silver Medal"),
            ("🥉", "Bronze Medal"),
            ("🎪", "Circus"),
            ("🎭", "Theater"),
            ("🎨", "Art"),
            ("🖌️", "Paintbrush"),
            ("🎬", "Movie"),
            ("🎤", "Microphone"),
            ("🎧", "Headphones"),
            ("🎵", "Music"),
            ("🎶", "Notes"),
            ("🎸", "Guitar"),
            ("🎹", "Piano"),
            ("🥁", "Drum"),
            ("🎺", "Trumpet"),
            ("🎻", "Violin"),
            ("🎷", "Saxophone"),
        ]),
        ("Travel & Places", [
            ("🚗", "Car"),
            ("🚕", "Taxi"),
            ("🚙", "SUV"),
            ("🚌", "Bus"),
            ("🚎", "Trolleybus"),
            ("🏎️", "Racing Car"),
            ("🚓", "Police Car"),
            ("🚑", "Ambulance"),
            ("🚒", "Fire Truck"),
            ("🏍️", "Motorcycle"),
            ("🛵", "Scooter"),
            ("🚲", "Bicycle"),
            ("🛴", "Kick Scooter"),
            ("🚂", "Locomotive"),
            ("🚆", "Train"),
            ("🚇", "Metro"),
            ("✈️", "Airplane"),
            ("🛩️", "Small Plane"),
            ("🚀", "Rocket"),
            ("🛸", "UFO"),
            ("🚁", "Helicopter"),
            ("⛵", "Sailboat"),
            ("🚢", "Ship"),
            ("🛥️", "Motor Boat"),
            ("⛽", "Fuel Pump"),
            ("🏠", "House"),
            ("🏡", "House Garden"),
            ("🏢", "Office"),
            ("🏬", "Department Store"),
            ("🏥", "Hospital"),
            ("🏫", "School"),
            ("🏛️", "Classical Building"),
            ("⛪", "Church"),
            ("🕌", "Mosque"),
            ("🕍", "Synagogue"),
            ("🏰", "Castle"),
            ("🗼", "Tower"),
            ("🗽", "Statue of Liberty"),
            ("🌍", "Globe Earth"),
            ("🌎", "Globe Americas"),
            ("🌏", "Globe Asia"),
            ("🏔️", "Mountain"),
            ("🏕️", "Camping"),
            ("🏖️", "Beach"),
            ("⛱️", "Beach Umbrella"),
            ("🌋", "Volcano"),
            ("🏜️", "Desert"),
            ("🌄", "Sunrise Mountain"),
            ("🌅", "Sunrise"),
            ("🌆", "Cityscape Dusk"),
            ("🌇", "Sunset"),
            ("🌉", "Bridge at Night"),
        ]),
        ("Objects & Tech", [
            ("💻", "Laptop"),
            ("🖥️", "Desktop"),
            ("⌨️", "Keyboard"),
            ("🖱️", "Mouse"),
            ("🖨️", "Printer"),
            ("📱", "Phone"),
            ("📲", "Mobile Arrow"),
            ("📞", "Telephone"),
            ("📟", "Pager"),
            ("📠", "Fax"),
            ("📧", "Email"),
            ("📷", "Camera"),
            ("📸", "Camera Flash"),
            ("📹", "Video Camera"),
            ("🎥", "Movie Camera"),
            ("📺", "Television"),
            ("📻", "Radio"),
            ("🔧", "Wrench"),
            ("🛠️", "Hammer Wrench"),
            ("🔨", "Hammer"),
            ("🪛", "Screwdriver"),
            ("🔩", "Nut and Bolt"),
            ("⚙️", "Gear"),
            ("🧲", "Magnet"),
            ("🔬", "Microscope"),
            ("🔭", "Telescope"),
            ("🩺", "Stethoscope"),
            ("💊", "Pill"),
            ("🩹", "Bandage"),
            ("💉", "Syringe"),
            ("💡", "Light Bulb"),
            ("🔦", "Flashlight"),
            ("🕯️", "Candle"),
            ("🪫", "Low Battery"),
            ("🔋", "Battery"),
            ("🔌", "Plug"),
            ("📚", "Books"),
            ("📖", "Book"),
            ("📰", "Newspaper"),
            ("📝", "Memo"),
            ("📋", "Clipboard"),
            ("📎", "Paperclip"),
            ("🖇️", "Linked Paperclips"),
            ("📐", "Triangular Ruler"),
            ("📏", "Straight Ruler"),
            ("✂️", "Scissors"),
            ("🗂️", "Card Index"),
            ("🗃️", "Card File Box"),
            ("🗄️", "File Cabinet"),
            ("🗑️", "Wastebasket"),
            ("🔑", "Key"),
            ("🗝️", "Old Key"),
            ("🏷️", "Label"),
            ("💾", "Floppy Disk"),
            ("💿", "CD"),
            ("📀", "DVD"),
            ("📦", "Package"),
            ("🎁", "Gift"),
            ("🛒", "Shopping Cart"),
            ("🪴", "Potted Plant"),
            ("🧸", "Teddy Bear"),
            ("🪞", "Mirror"),
            ("🪟", "Window"),
            ("🛏️", "Bed"),
            ("🪑", "Chair"),
            ("🚿", "Shower"),
            ("🛁", "Bathtub"),
            ("🧹", "Broom"),
            ("🧺", "Basket"),
            ("👓", "Glasses"),
            ("🕶️", "Sunglasses"),
            ("👔", "Necktie"),
            ("👕", "T-Shirt"),
            ("👗", "Dress"),
            ("👑", "Crown"),
            ("🎒", "Backpack"),
            ("👜", "Handbag"),
            ("💼", "Briefcase"),
            ("🧳", "Luggage"),
            ("☂️", "Umbrella"),
        ]),
        ("Symbols & Shapes", [
            ("❤️", "Red Heart"),
            ("🧡", "Orange Heart"),
            ("💛", "Yellow Heart"),
            ("💚", "Green Heart"),
            ("💙", "Blue Heart"),
            ("💜", "Purple Heart"),
            ("🖤", "Black Heart"),
            ("🤍", "White Heart"),
            ("🤎", "Brown Heart"),
            ("💔", "Broken Heart"),
            ("❣️", "Heart Exclamation"),
            ("💕", "Two Hearts"),
            ("💞", "Revolving Hearts"),
            ("💓", "Beating Heart"),
            ("💗", "Growing Heart"),
            ("💖", "Sparkling Heart"),
            ("💘", "Heart Arrow"),
            ("💝", "Heart Ribbon"),
            ("💯", "Hundred"),
            ("⭐", "Star"),
            ("🌟", "Glowing Star"),
            ("✨", "Sparkles"),
            ("⚡", "Lightning"),
            ("🔥", "Fire"),
            ("💧", "Water"),
            ("🌀", "Cyclone"),
            ("☁️", "Cloud"),
            ("🌤️", "Sun Behind Cloud"),
            ("⛈️", "Thunderstorm"),
            ("🌧️", "Rain"),
            ("🌨️", "Snow"),
            ("🌪️", "Tornado"),
            ("🔒", "Lock"),
            ("🔓", "Unlock"),
            ("🔐", "Lock with Key"),
            ("⏰", "Alarm Clock"),
            ("⏱️", "Stopwatch"),
            ("⏳", "Hourglass"),
            ("🔔", "Bell"),
            ("🔕", "Bell Slash"),
            ("📌", "Pin"),
            ("📍", "Round Pin"),
            ("✅", "Check"),
            ("❌", "Cross"),
            ("❓", "Question"),
            ("❗", "Exclamation"),
            ("⚠️", "Warning"),
            ("🚫", "Prohibited"),
            ("⛔", "No Entry"),
            ("♻️", "Recycling"),
            ("💎", "Gem"),
            ("💰", "Money Bag"),
            ("💵", "Dollar"),
            ("💳", "Credit Card"),
            ("🏧", "ATM"),
            ("🔴", "Red Circle"),
            ("🟠", "Orange Circle"),
            ("🟡", "Yellow Circle"),
            ("🟢", "Green Circle"),
            ("🔵", "Blue Circle"),
            ("🟣", "Purple Circle"),
            ("⚫", "Black Circle"),
            ("⚪", "White Circle"),
            ("🟤", "Brown Circle"),
            ("🔺", "Red Triangle Up"),
            ("🔻", "Red Triangle Down"),
            ("🔶", "Large Orange Diamond"),
            ("🔷", "Large Blue Diamond"),
            ("▶️", "Play"),
            ("⏸️", "Pause"),
            ("⏹️", "Stop"),
            ("⏭️", "Next Track"),
            ("🔀", "Shuffle"),
            ("🔁", "Repeat"),
            ("🔃", "Clockwise Arrows"),
            ("➕", "Plus"),
            ("➖", "Minus"),
            ("✖️", "Multiply"),
            ("➗", "Divide"),
            ("♾️", "Infinity"),
            ("💬", "Speech Bubble"),
            ("💭", "Thought Bubble"),
            ("🗯️", "Anger Bubble"),
            ("ℹ️", "Info"),
            ("🆕", "New"),
            ("🆗", "OK"),
            ("🆘", "SOS"),
            ("🏳️", "White Flag"),
            ("🏴", "Black Flag"),
            ("🚩", "Red Flag"),
            ("🏁", "Checkered Flag"),
        ]),
    ];
}
