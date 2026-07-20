using LittleLauncher.Classes;
using LittleLauncher.Models;
using LittleLauncher.Services;
using LittleLauncher.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using global::Windows.Graphics;
using global::Windows.Storage.Pickers;
using WinRT.Interop;
using Image = Microsoft.UI.Xaml.Controls.Image;

namespace LittleLauncher.Windows;

/// <summary>Outcome of an <see cref="ItemEditorWindow"/> session.</summary>
public enum ItemEditorResult
{
    /// <summary>Dismissed without changes; the item was not touched.</summary>
    Cancelled,

    /// <summary>The item was created or updated in the target collection.</summary>
    Saved,

    /// <summary>The user asked to delete the item. The caller performs the removal.</summary>
    Deleted,
}

/// <summary>
/// Add/edit dialog for a single <see cref="LauncherItem"/>.
/// </summary>
/// <remarks>
/// This is a standalone window rather than a <c>ContentDialog</c> because a
/// <c>ContentDialog</c> renders inside its host window's content area and cannot overflow
/// the HWND — the form needs ~460&#215;620 and the flyout it is opened from can be as narrow
/// as one 175px column. Callers await <see cref="ShowAsync"/>, which completes with an
/// <see cref="ItemEditorResult"/>.
/// </remarks>
public sealed class ItemEditorWindow : Window
{
    private const int WindowWidthDips = 560;

    // Sized to the taller of the two tabs plus chrome, rather than left oversized — the
    // form is top-aligned above a bottom-anchored button row, so surplus height shows up
    // as a large empty gap in the middle of the window.
    private const int WindowHeightDips = 620;

    private readonly TaskCompletionSource<ItemEditorResult> _completion = new();
    private readonly IntPtr _hwnd;
    private ItemEditorResult _result = ItemEditorResult.Cancelled;

    /// <summary>
    /// Opens the editor. When <paramref name="existingItem"/> is null a new item is appended
    /// to <paramref name="targetList"/>; otherwise the existing item is mutated in place.
    /// The caller is responsible for persisting — and, on
    /// <see cref="ItemEditorResult.Deleted"/>, for removing the item.
    /// </summary>
    public static Task<ItemEditorResult> ShowAsync(
        LauncherItem? existingItem,
        ObservableCollection<LauncherItem> targetList,
        IntPtr ownerHwnd = default,
        Action<Window>? onCreated = null)
    {
        var window = new ItemEditorWindow(existingItem, targetList, ownerHwnd);
        onCreated?.Invoke(window);
        window.Activate();
        return window._completion.Task;
    }

    private ItemEditorWindow(LauncherItem? existingItem, ObservableCollection<LauncherItem> targetList, IntPtr ownerHwnd)
    {
        _hwnd = WindowNative.GetWindowHandle(this);

        bool isEdit = existingItem != null;
        Title = isEdit ? "Edit Item" : "Add Item";
        SystemBackdrop = new MicaBackdrop();

        // Owned windows always sit above their owner — required because the flyout sets
        // IsAlwaysOnTop and would otherwise cover this.
        if (ownerHwnd != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, ownerHwnd);

        // Custom title bar so the caption follows the app theme and Mica runs full height.
        var titleBar = WindowChrome.BuildTitleBar(Title);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var body = BuildContent(existingItem, targetList, isEdit);
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(titleBar);
        root.Children.Add(body);

        Content = root;
        ThemeManager.ApplySavedTheme(this);
        WindowChrome.ApplyIcon(_hwnd);

        SizeAndCentre();
        Closed += (_, _) => _completion.TrySetResult(_result);
    }

    /// <summary>Sizes the window in DIPs and centres it on the monitor it opened on.</summary>
    private void SizeAndCentre()
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (appWindow == null) return;

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        int width = (int)(WindowWidthDips * scale);
        int height = (int)(WindowHeightDips * scale);
        appWindow.Resize(new SizeInt32(width, height));

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (area != null)
        {
            appWindow.Move(new PointInt32(
                area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
                area.WorkArea.Y + ((area.WorkArea.Height - height) / 2)));
        }
    }

    /// <summary>File/icon pickers are COM objects that need an owning HWND in a desktop app.</summary>
    private void InitializePicker(object picker) => InitializeWithWindow.Initialize(picker, _hwnd);

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private FrameworkElement BuildContent(
        LauncherItem? existingItem,
        ObservableCollection<LauncherItem> targetList,
        bool isEdit)
    {
        // Track state for this editing session
        string fetchedIconPath = existingItem?.IconPath ?? "";
        string? customGlyph = isEdit ? existingItem!.IconGlyph : null;
        string customColor = existingItem?.IconColor ?? "";
        bool isWebsite = existingItem?.IsWebsite ?? true;
        bool isPwa = existingItem?.IsPwa ?? false;
        bool openInAppWindow = existingItem?.OpenInAppWindow ?? false;
        string appWindowBrowser = existingItem?.AppWindowBrowser ?? "";
        string appWindowBrowserProfile = existingItem?.AppWindowBrowserProfile ?? "";

        // -- 1. Shared state --
        Microsoft.UI.Dispatching.DispatcherQueueTimer? debounceTimer = null;
        string lastFetchedPath = "";
        bool populating = false;

        // Derived target state, kept in sync by SyncDerived(). The active tab decides the
        // source: "list" = the app/PWA selection, "custom" = the typed path/link.
        string effectiveTarget = existingItem?.Path ?? "";
        string currentTab = "list";

        // Declared early so the validation handlers can reference it before the form is built.
        var validationHint = new TextBlock
        {
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        // Created up front so validation handlers can toggle it before the form exists.
        var saveButton = new Button
        {
            Content = isEdit ? "Save" : "Add",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 90
        };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };

        // -- 2. Unified app + PWA picker (search + icon list) --
        var searchBox = new AutoSuggestBox
        {
            QueryIcon = new SymbolIcon(Symbol.Find),
            PlaceholderText = "Loading apps…",
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var appList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Height = 260,
            Margin = new Thickness(0, 0, 0, 8),
            ItemTemplate = BuildAppItemTemplate()
        };
        ScrollViewer.SetVerticalScrollBarVisibility(appList, ScrollBarVisibility.Auto);

        // Custom path/link inputs (live in the "File or link" pane).
        var pathBox = new TextBox
        {
            PlaceholderText = @"C:\path\to\app.exe  or  https://example.com",
            Margin = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var browseButton = new Button
        {
            Content = "Browse",
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 0, 0)
        };
        var allEntries = new List<AppPickerEntry>();
        AppPickerEntry? pickedEntry = null;
        bool catalogLoaded = false;
        bool pendingEditSelect = false;

        // Enumerating apps (Start Menu + registry + shell:AppsFolder) and PWAs is
        // expensive and apartment-threaded, so build the catalog on a background STA thread.
        static List<AppPickerEntry> BuildCatalog()
        {
            var list = new List<AppPickerEntry>();
            foreach (var app in AppCatalog.GetInstalledApplications())
                list.Add(new AppPickerEntry(app.DisplayName, app.ExePath, false, app.ExePath));
            foreach (var pwa in AppCatalog.GetInstalledPwas())
                list.Add(new AppPickerEntry(pwa.DisplayName, pwa.Aumid, true, $@"shell:AppsFolder\{pwa.Aumid}"));
            return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        void FilterApps(string? query)
        {
            IEnumerable<AppPickerEntry> items = allEntries;
            if (!string.IsNullOrWhiteSpace(query))
                items = items.Where(a => a.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
            var shown = items.ToList();
            populating = true;
            appList.ItemsSource = shown;
            if (pickedEntry != null && shown.Contains(pickedEntry))
            {
                appList.SelectedItem = pickedEntry;
                ScrollSelectionIntoView(pickedEntry);
            }
            populating = false;
        }

        /// <summary>
        /// Brings the selected row into view. Deferred to the dispatcher because the list was
        /// just re-sourced — the containers do not exist yet in this pass, so scrolling
        /// immediately would be a no-op.
        /// </summary>
        void ScrollSelectionIntoView(AppPickerEntry entry) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                try { appList.ScrollIntoView(entry); } catch { /* list re-sourced again */ }
            });

        async Task EnsureCatalogLoadedAsync()
        {
            if (catalogLoaded) return;
            catalogLoaded = true;
            allEntries = await AppPickerService.RunStaAsync(BuildCatalog) ?? new List<AppPickerEntry>();
            searchBox.PlaceholderText = "Search apps and web apps…";
            FilterApps(searchBox.Text);
            AppPickerService.LoadIcons(allEntries, DispatcherQueue);
            if (pendingEditSelect)
            {
                pendingEditSelect = false;
                PreselectEditEntry();
            }
        }

        // -- Target resolution: a list selection wins; otherwise the typed path/link. --
        static bool LooksLikeFilePath(string p) =>
            p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
            (p.Length >= 2 && p[1] == ':') ||
            p.StartsWith(@"\\") ||
            p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

        static bool LooksLikeWebUrl(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
            if (LooksLikeFilePath(p)) return false;
            return p.Contains('.');
        }

        (string target, bool isPwa, bool isWebsite) ResolveTarget()
        {
            if (currentTab == "custom")
            {
                var t = pathBox.Text.Trim();
                return (t, false, LooksLikeWebUrl(t));
            }
            if (appList.SelectedItem is AppPickerEntry e)
                return (e.LaunchPath, e.IsPwa, false);
            return ("", false, false);
        }

        void PreselectEditEntry()
        {
            var match = allEntries.FirstOrDefault(e =>
                e.IsPwa == existingItem!.IsPwa &&
                string.Equals(e.LaunchPath, existingItem.Path, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                pickedEntry = match;
                populating = true;
                if (appList.ItemsSource is IEnumerable<AppPickerEntry> shown && shown.Contains(match))
                {
                    appList.SelectedItem = match;
                    ScrollSelectionIntoView(match);
                }
                populating = false;
                SyncDerived();
                ValidateForm();
            }
            else if (!existingItem!.IsWebsite)
            {
                // Not in the catalog (e.g. a hand-browsed exe) — show it on the File/link tab.
                populating = true;
                pathBox.Text = existingItem.Path;
                populating = false;
                ShowTabPanel("custom");
            }
        }

        static DataTemplate BuildAppItemTemplate() => (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            "<Grid ColumnSpacing=\"12\" Padding=\"0,4\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>" +
            "<Image Grid.Column=\"0\" Source=\"{Binding Icon}\" Width=\"24\" Height=\"24\"/>" +
            "<TextBlock Grid.Column=\"1\" Text=\"{Binding Name}\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\"/>" +
            "</Grid></DataTemplate>");

        // -- 3. Arguments (Application only) --
        var argsLabel = Label("Arguments");
        var argsBox = new TextBox
        {
            PlaceholderText = "(optional)",
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // -- 4. Web app window mode (Website only) --
        var appWindowToggle = new ToggleSwitch
        {
            Header = "Open as app window",
            OffContent = "Use normal browser tab",
            OnContent = "Open in standalone window",
            IsOn = openInAppWindow,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // -- 4a. Browser picker --
        var browserLabel = Label("Browser");
        var browserCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        var installedBrowsers = BrowserCatalog.GetInstalledBrowsers();
        browserCombo.Items.Add(new ComboBoxItem { Content = "Default browser", Tag = "" });
        foreach (var browser in installedBrowsers)
            browserCombo.Items.Add(new ComboBoxItem { Content = browser.DisplayName, Tag = browser.ExePath });
        browserCombo.Items.Add(new ComboBoxItem { Content = "Custom…", Tag = "__custom__" });

        // -- 4b. Profile picker --
        var profileLabel = Label("Profile");
        var profileCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };

        void PopulateProfileCombo()
        {
            profileCombo.Items.Clear();

            BrowserEngine currentEngine;
            if (string.IsNullOrEmpty(appWindowBrowser))
            {
                string? defaultExe = BrowserCatalog.GetDefaultBrowserExePath();
                currentEngine = defaultExe != null ? BrowserCatalog.DetectEngine(defaultExe) : BrowserEngine.Chromium;
            }
            else
            {
                var match = installedBrowsers.FirstOrDefault(b =>
                    string.Equals(b.ExePath, appWindowBrowser, StringComparison.OrdinalIgnoreCase));
                currentEngine = match?.Engine ?? BrowserCatalog.DetectEngine(appWindowBrowser);
            }

            profileCombo.Items.Add(new ComboBoxItem { Content = "App sandbox (isolated)", Tag = "" });

            if (currentEngine != BrowserEngine.Gecko)
            {
                if (string.IsNullOrEmpty(appWindowBrowser))
                {
                    profileCombo.Items.Add(new ComboBoxItem { Content = "Default profile", Tag = "__default__" });
                }
                else
                {
                    var match = installedBrowsers.FirstOrDefault(b =>
                        string.Equals(b.ExePath, appWindowBrowser, StringComparison.OrdinalIgnoreCase));
                    string profileDataDir = match?.ProfileDataDir ?? "";

                    foreach (var profile in BrowserCatalog.GetBrowserProfiles(profileDataDir, currentEngine))
                    {
                        string label = profile.DisplayName == profile.DirectoryName
                            ? profile.DisplayName
                            : $"{profile.DisplayName} ({Path.GetFileName(profile.DirectoryName)})";
                        profileCombo.Items.Add(new ComboBoxItem { Content = label, Tag = profile.DirectoryName });
                    }
                }
            }

            int selectedIndex = 0;
            if (!string.IsNullOrEmpty(appWindowBrowserProfile))
            {
                for (int i = 1; i < profileCombo.Items.Count; i++)
                {
                    if (profileCombo.Items[i] is ComboBoxItem ci &&
                        string.Equals(ci.Tag as string, appWindowBrowserProfile, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }
            profileCombo.SelectedIndex = selectedIndex;
        }

        profileCombo.SelectionChanged += (s, ev) =>
        {
            if (profileCombo.SelectedItem is ComboBoxItem selected)
                appWindowBrowserProfile = selected.Tag as string ?? "";
        };

        // Select existing browser
        int selectedBrowserIndex = 0;
        if (!string.IsNullOrEmpty(appWindowBrowser))
        {
            for (int i = 1; i < browserCombo.Items.Count - 1; i++)
            {
                if (browserCombo.Items[i] is ComboBoxItem ci &&
                    string.Equals(ci.Tag as string, appWindowBrowser, StringComparison.OrdinalIgnoreCase))
                {
                    selectedBrowserIndex = i;
                    break;
                }
            }
            if (selectedBrowserIndex == 0 && appWindowBrowser != "")
            {
                var customItem = new ComboBoxItem
                {
                    Content = Path.GetFileNameWithoutExtension(appWindowBrowser),
                    Tag = appWindowBrowser
                };
                browserCombo.Items.Insert(browserCombo.Items.Count - 1, customItem);
                selectedBrowserIndex = browserCombo.Items.Count - 2;
            }
        }
        browserCombo.SelectedIndex = selectedBrowserIndex;

        browserCombo.SelectionChanged += async (s, ev) =>
        {
            if (browserCombo.SelectedItem is ComboBoxItem selected)
            {
                string tag = selected.Tag as string ?? "";
                if (tag == "__custom__")
                {
                    var picker = new FileOpenPicker();
                    picker.FileTypeFilter.Add(".exe");
                    InitializePicker(picker);
                    var file = await picker.PickSingleFileAsync();
                    if (file != null)
                    {
                        appWindowBrowser = file.Path;
                        var customItem = new ComboBoxItem
                        {
                            Content = Path.GetFileNameWithoutExtension(file.Path),
                            Tag = file.Path
                        };
                        browserCombo.Items.Insert(browserCombo.Items.Count - 1, customItem);
                        browserCombo.SelectedItem = customItem;
                    }
                    else
                    {
                        browserCombo.SelectedIndex = 0;
                        appWindowBrowser = "";
                    }
                }
                else
                {
                    appWindowBrowser = tag;
                }
                PopulateProfileCombo();
            }
        };

        PopulateProfileCombo();

        // -- App window sub-options panel --
        var appWindowOptionsPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        appWindowOptionsPanel.Children.Add(browserLabel);
        appWindowOptionsPanel.Children.Add(browserCombo);
        appWindowOptionsPanel.Children.Add(profileLabel);
        appWindowOptionsPanel.Children.Add(profileCombo);

        void UpdateAppWindowOptionsVisibility()
        {
            appWindowOptionsPanel.Visibility = openInAppWindow && isWebsite
                ? Visibility.Visible : Visibility.Collapsed;
        }

        appWindowToggle.Toggled += (s, ev) =>
        {
            openInAppWindow = appWindowToggle.IsOn;
            UpdateAppWindowOptionsVisibility();
        };

        // -- 5. Name --
        var nameBox = new TextBox
        {
            PlaceholderText = "(optional)",
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // -- 6. Icon --
        var iconPreview = new Image
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconGlyphPreview = new FontIcon
        {
            FontSize = 24,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var iconEmojiPreview = new TextBlock
        {
            FontSize = 24,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var iconStatus = new TextBlock
        {
            Text = "Auto-detected",
            FontSize = 12,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        iconRow.Children.Add(iconPreview);
        iconRow.Children.Add(iconGlyphPreview);
        iconRow.Children.Add(iconEmojiPreview);
        iconRow.Children.Add(iconStatus);

        var refreshButton = new Button
        {
            Content = "Retry",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12
        };
        iconRow.Children.Add(refreshButton);

        // -- Icon gallery flyout --
        void RefreshIconPreview()
        {
            // Hide all previews first
            iconPreview.Source = null;
            iconPreview.Visibility = Visibility.Collapsed;
            iconGlyphPreview.Visibility = Visibility.Collapsed;
            iconEmojiPreview.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(fetchedIconPath) && File.Exists(fetchedIconPath))
            {
                try
                {
                    iconPreview.Source = new BitmapImage { DecodePixelWidth = 32, UriSource = new Uri(fetchedIconPath, UriKind.Absolute) };
                    iconPreview.Visibility = Visibility.Visible;
                    iconStatus.Text = customGlyph != null ? "Custom image" : (isWebsite ? "Auto favicon" : "Auto icon");
                }
                catch
                {
                    iconStatus.Text = "Failed to load icon";
                }
            }
            else if (customGlyph != null)
            {
                SolidColorBrush? colorBrush = null;
                if (!string.IsNullOrEmpty(customColor))
                {
                    try
                    {
                        string h = customColor.TrimStart('#');
                        if (h.Length == 6)
                        {
                            byte r = Convert.ToByte(h[..2], 16);
                            byte g = Convert.ToByte(h[2..4], 16);
                            byte b = Convert.ToByte(h[4..6], 16);
                            colorBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
                        }
                    }
                    catch { /* ignore */ }
                }

                if (IconGallery.IsFluentGlyph(customGlyph))
                {
                    iconGlyphPreview.Glyph = customGlyph;
                    iconGlyphPreview.Foreground = colorBrush ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                    iconGlyphPreview.Visibility = Visibility.Visible;
                }
                else
                {
                    iconEmojiPreview.Text = customGlyph;
                    if (colorBrush != null)
                        iconEmojiPreview.Foreground = colorBrush;
                    else
                        iconEmojiPreview.ClearValue(TextBlock.ForegroundProperty);
                    iconEmojiPreview.Visibility = Visibility.Visible;
                }
                iconStatus.Text = "Custom icon";
            }
            else
            {
                iconStatus.Text = "No icon";
            }
        }

        var chooseIconButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 12 },
                    new TextBlock { Text = "Choose" }
                }
            },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12
        };
        chooseIconButton.Flyout = IconGallery.CreateFlyout(
            onSelected: result =>
            {
                if (result.Glyph != null)
                {
                    customGlyph = result.Glyph;
                    customColor = result.Color ?? "";
                    fetchedIconPath = "";
                }
                else if (result.ImagePath != null)
                {
                    fetchedIconPath = result.ImagePath;
                    // Keep customGlyph as fallback but image takes priority
                }
                RefreshIconPreview();
            },
            onBrowseRequested: async () =>
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".ico");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                InitializePicker(picker);
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    // Copy to AppData cache for persistence
                    string cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "LittleLauncher", "icons");
                    Directory.CreateDirectory(cacheDir);
                    string dest = Path.Combine(cacheDir, $"custom-{Guid.NewGuid():N}{Path.GetExtension(file.Path)}");
                    File.Copy(file.Path, dest, true);
                    fetchedIconPath = dest;
                    RefreshIconPreview();
                }
            },
            onReset: () =>
            {
                customGlyph = null;
                customColor = "";
                fetchedIconPath = "";
                iconPreview.Source = null;
                iconPreview.Visibility = Visibility.Collapsed;
                iconGlyphPreview.Visibility = Visibility.Collapsed;
                iconEmojiPreview.Visibility = Visibility.Collapsed;
                iconStatus.Text = "Auto-detected";
                _ = DoFetch(force: true);
            },
            currentGlyph: customGlyph,
            currentColor: customColor,
            currentImagePath: fetchedIconPath
        );
        iconRow.Children.Add(chooseIconButton);

        // Declared here (after nameBox / appWindowToggle exist) so they can read them.
        void SyncDerived()
        {
            var (t, pwa, web) = ResolveTarget();
            effectiveTarget = t;
            isPwa = pwa;
            isWebsite = web;
            appWindowToggle.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
            UpdateAppWindowOptionsVisibility();
        }

        async Task SelectEntry(AppPickerEntry e)
        {
            pickedEntry = e;
            if (string.IsNullOrWhiteSpace(nameBox.Text)) nameBox.Text = e.Name;
            SyncDerived();
            lastFetchedPath = "";
            await DoFetch(force: false);
        }

        // -- Picker event wiring --
        searchBox.TextChanged += (s, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            FilterApps(searchBox.Text);
        };
        appList.SelectionChanged += async (s, ev) =>
        {
            if (populating) return;
            if (appList.SelectedItem is AppPickerEntry e)
                await SelectEntry(e);
            SyncDerived();
            ValidateForm();
        };

        // -- Auto-populate icon / name for the current target (debounced) --
        async Task DoFetch(bool force)
        {
            SyncDerived();
            var path = effectiveTarget;
            if (string.IsNullOrEmpty(path)) return;
            if (!force && path == lastFetchedPath) return;
            lastFetchedPath = path;

            if (isWebsite)
            {
                var fetchPath = path;
                if (!fetchPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !fetchPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!force && fetchPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return;

                    fetchPath = "https://" + fetchPath;
                    populating = true;
                    pathBox.Text = fetchPath;
                    populating = false;
                    lastFetchedPath = fetchPath;
                }

                iconStatus.Text = "Fetching...";
                refreshButton.IsEnabled = false;
                nameBox.IsEnabled = false;
                nameBox.PlaceholderText = "Fetching name...";
                var titleTask = FaviconService.FetchWebsiteTitleAsync(fetchPath);
                var iconTask = FaviconService.FetchAndCacheAsync(fetchPath);
                await Task.WhenAll(titleTask, iconTask);
                refreshButton.IsEnabled = true;
                nameBox.IsEnabled = true;
                nameBox.PlaceholderText = "(optional)";

                if (force || string.IsNullOrEmpty(nameBox.Text))
                {
                    var title = titleTask.Result;
                    if (!string.IsNullOrEmpty(title))
                        nameBox.Text = title;
                }

                var iconPath = iconTask.Result;
                if (!string.IsNullOrEmpty(iconPath))
                {
                    fetchedIconPath = iconPath;
                    RefreshIconPreview();
                }
                else
                {
                    iconStatus.Text = "Could not fetch icon";
                }
            }
            else if (isPwa)
            {
                // Prefer the PWA's own web icon/manifest asset; fall back to the shell image.
                iconStatus.Text = "Fetching icon...";
                refreshButton.IsEnabled = false;
                var iconPath = await FaviconService.GetBestPwaIconAsync(path);
                refreshButton.IsEnabled = true;
                if (!string.IsNullOrEmpty(iconPath))
                {
                    fetchedIconPath = iconPath;
                    RefreshIconPreview();
                }
                else
                {
                    iconStatus.Text = "No icon available";
                }
            }
            else if (path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            {
                // Store / packaged app — extract icon via shell
                string aumid = path[@"shell:AppsFolder\".Length..];
                iconStatus.Text = "Extracting icon...";
                refreshButton.IsEnabled = false;
                nameBox.IsEnabled = false;
                nameBox.PlaceholderText = "Detecting name...";
                var appIcon = FaviconService.GetPwaIconFromShell(aumid);
                refreshButton.IsEnabled = true;
                nameBox.IsEnabled = true;
                nameBox.PlaceholderText = "(optional)";
                if (!string.IsNullOrEmpty(appIcon))
                {
                    fetchedIconPath = appIcon;
                    RefreshIconPreview();
                }
                else
                {
                    iconStatus.Text = "Could not extract icon";
                }
            }
            else
            {
                if (force || string.IsNullOrEmpty(nameBox.Text))
                {
                    var appName = FaviconService.GetApplicationName(path);
                    if (!string.IsNullOrEmpty(appName))
                        nameBox.Text = appName;
                }

                iconStatus.Text = "Extracting icon...";
                refreshButton.IsEnabled = false;
                nameBox.IsEnabled = false;
                nameBox.PlaceholderText = "Detecting name...";
                var appIcon = FaviconService.GetApplicationIcon(path);
                refreshButton.IsEnabled = true;
                nameBox.IsEnabled = true;
                nameBox.PlaceholderText = "(optional)";
                if (!string.IsNullOrEmpty(appIcon))
                {
                    fetchedIconPath = appIcon;
                    RefreshIconPreview();
                }
                else
                {
                    iconStatus.Text = "Could not extract icon";
                }
            }
        }

        void ScheduleFetch()
        {
            if (populating) return;
            debounceTimer?.Stop();
            debounceTimer = DispatcherQueue.CreateTimer();
            debounceTimer.Interval = TimeSpan.FromMilliseconds(800);
            debounceTimer.IsRepeating = false;
            debounceTimer.Tick += async (s, ev) =>
            {
                await DoFetch(force: false);
            };
            debounceTimer.Start();
        }

        pathBox.TextChanged += (s, ev) =>
        {
            if (populating) return;
            SyncDerived();
            ScheduleFetch();
            ValidateForm();
        };
        refreshButton.Click += async (s, ev) =>
        {
            debounceTimer?.Stop();
            lastFetchedPath = "";
            await DoFetch(force: true);
        };

        // -- Browse for an arbitrary file (custom path) --
        async Task BrowseForApp()
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                populating = true;
                pathBox.Text = file.Path;
                populating = false;
                SyncDerived();
                ScheduleFetch();
                ValidateForm();
            }
        }

        browseButton.Click += async (s, ev) => await BrowseForApp();

        // -- Populate for edit mode --
        if (isEdit)
        {
            populating = true;
            argsBox.Text = existingItem!.Arguments;
            nameBox.Text = existingItem.Name;
            if (existingItem.IsWebsite)
            {
                pathBox.Text = existingItem.Path;
                currentTab = "custom";
            }
            else
            {
                // App or PWA: select the matching row once the catalog finishes loading.
                pendingEditSelect = true;
            }
            RefreshIconPreview();
            populating = false;
            SyncDerived();
        }

        // -- Tab 1: app/PWA picker --
        var listPanel = new StackPanel();
        listPanel.Children.Add(searchBox);
        listPanel.Children.Add(appList);

        // -- Tab 2: file / link --
        var pathRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 4,
            Margin = new Thickness(0, 0, 0, 8)
        };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browseButton, 1);
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browseButton);

        var customPanel = new StackPanel { Visibility = Visibility.Collapsed };
        customPanel.Children.Add(Label("Path or link"));
        customPanel.Children.Add(pathRow);
        customPanel.Children.Add(argsLabel);
        customPanel.Children.Add(argsBox);
        customPanel.Children.Add(appWindowToggle);
        customPanel.Children.Add(appWindowOptionsPanel);

        var tabContent = new Grid();
        tabContent.Children.Add(listPanel);
        tabContent.Children.Add(customPanel);

        // -- Tab strip --
        var listTab = new SelectorBarItem { Text = "Apps & web apps", Tag = "list" };
        var customTab = new SelectorBarItem { Text = "File or link", Tag = "custom" };
        var tabBar = new SelectorBar { Margin = new Thickness(0, 0, 0, 4) };
        tabBar.Items.Add(listTab);
        tabBar.Items.Add(customTab);

        void ShowTabPanel(string tag)
        {
            currentTab = tag;
            listPanel.Visibility = tag == "list" ? Visibility.Visible : Visibility.Collapsed;
            customPanel.Visibility = tag == "custom" ? Visibility.Visible : Visibility.Collapsed;
            var targetItem = tag == "custom" ? customTab : listTab;
            if (!ReferenceEquals(tabBar.SelectedItem, targetItem))
            {
                populating = true;
                tabBar.SelectedItem = targetItem;
                populating = false;
            }
            SyncDerived();
            ValidateForm();
            ScheduleFetch();
        }

        tabBar.SelectionChanged += (s, ev) =>
        {
            if (populating) return;
            ShowTabPanel((tabBar.SelectedItem as SelectorBarItem)?.Tag as string ?? "list");
        };

        // -- Build form --
        var form = new StackPanel { MinWidth = 460 };
        form.Children.Add(tabBar);
        form.Children.Add(tabContent);
        form.Children.Add(Label("Name"));
        form.Children.Add(nameBox);
        form.Children.Add(Label("Icon"));
        form.Children.Add(iconRow);
        form.Children.Add(validationHint);

        ShowTabPanel(currentTab);

        void ValidateForm()
        {
            var (t, _, _) = ResolveTarget();
            // While editing, the existing app/PWA row auto-selects once the catalog
            // finishes loading — treat that pending state as valid so Save isn't disabled.
            if (string.IsNullOrWhiteSpace(t) && !(isEdit && pendingEditSelect))
            {
                validationHint.Text = "Choose an app or web app above, or enter a path or link.";
                validationHint.Visibility = Visibility.Visible;
                saveButton.IsEnabled = false;
            }
            else
            {
                validationHint.Visibility = Visibility.Collapsed;
                saveButton.IsEnabled = true;
            }
        }

        // -- Commit --
        async Task CommitAsync()
        {
            SyncDerived();
            var (finalPath, finalIsPwa, finalIsWebsite) = ResolveTarget();
            finalPath = finalPath.Trim();
            if (string.IsNullOrEmpty(finalPath)) return;

            var name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                if (pickedEntry != null)
                    name = pickedEntry.Name;
                else if (finalIsWebsite)
                    name = await FaviconService.FetchWebsiteTitleAsync(finalPath) ?? finalPath;
                else if (finalPath.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
                    name = Path.GetFileNameWithoutExtension(finalPath);
                else
                    name = FaviconService.GetApplicationName(finalPath) ?? Path.GetFileNameWithoutExtension(finalPath);
            }
            var args = finalIsPwa ? "" : argsBox.Text.Trim();
            var glyph = customGlyph ?? (finalIsWebsite || finalIsPwa ? "" : "");

            if (isEdit)
            {
                existingItem!.Name = name;
                existingItem.Path = finalPath;
                existingItem.Arguments = args;
                existingItem.IconGlyph = glyph;
                existingItem.IconPath = fetchedIconPath;
                existingItem.IconColor = customColor;
                existingItem.IsWebsite = finalIsWebsite;
                existingItem.IsPwa = finalIsPwa;
                existingItem.OpenInAppWindow = finalIsWebsite && openInAppWindow;
                existingItem.AppWindowBrowser = finalIsWebsite && openInAppWindow ? appWindowBrowser : "";
                existingItem.AppWindowBrowserProfile = finalIsWebsite && openInAppWindow ? appWindowBrowserProfile : "";
            }
            else
            {
                var newItem = new LauncherItem(name, finalPath, glyph, finalIsWebsite, args, fetchedIconPath, finalIsWebsite && openInAppWindow);
                newItem.IsPwa = finalIsPwa;
                newItem.IconColor = customColor;
                newItem.AppWindowBrowser = finalIsWebsite && openInAppWindow ? appWindowBrowser : "";
                newItem.AppWindowBrowserProfile = finalIsWebsite && openInAppWindow ? appWindowBrowserProfile : "";
                targetList.Add(newItem);
            }

            _result = ItemEditorResult.Saved;
        }

        saveButton.Click += async (s, ev) =>
        {
            saveButton.IsEnabled = false;
            debounceTimer?.Stop();
            await CommitAsync();
            Close();
        };
        cancelButton.Click += (s, ev) =>
        {
            debounceTimer?.Stop();
            Close();
        };

        // Delete is only meaningful when editing something that already exists.
        var deleteButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new TextBlock { Text = "Delete" },
                },
            },
            Visibility = isEdit ? Visibility.Visible : Visibility.Collapsed,
        };
        deleteButton.Click += async (s, ev) =>
        {
            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Delete item?",
                Content = $"“{(string.IsNullOrWhiteSpace(existingItem?.Name) ? "This item" : existingItem!.Name)}” will be removed from this launcher.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            debounceTimer?.Stop();
            _result = ItemEditorResult.Deleted;
            Close();
        };

        ValidateForm();
        _ = EnsureCatalogLoadedAsync();

        // -- Window chrome: scrolling form above a fixed button row --
        // Delete sits apart on the left, away from Save/Cancel, so it is not hit by accident.
        var buttonRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var confirmButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        confirmButtons.Children.Add(saveButton);
        confirmButtons.Children.Add(cancelButton);

        Grid.SetColumn(deleteButton, 0);
        Grid.SetColumn(confirmButtons, 2);
        buttonRow.Children.Add(deleteButton);
        buttonRow.Children.Add(confirmButtons);

        var root = new Grid { Padding = new Thickness(24, 8, 24, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroller = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroller, 0);
        Grid.SetRow(buttonRow, 1);
        root.Children.Add(scroller);
        root.Children.Add(buttonRow);

        // Escape cancels, matching the ContentDialog behaviour this replaced.
        // Hidden: WinUI otherwise pops an "Esc" accelerator tooltip over the window.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (s, ev) => { ev.Handled = true; Close(); };
        root.KeyboardAccelerators.Add(escape);

        return root;
    }
}
