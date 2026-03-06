using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Models;
using SelfHostedHelper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using global::Windows.Storage.Pickers;
using WinRT.Interop;
using Image = Microsoft.UI.Xaml.Controls.Image;

namespace SelfHostedHelper.Pages;

/// <summary>
/// Selects the correct DataTemplate based on whether the item is a category or a launchable item.
/// </summary>
public class LauncherItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? LauncherItemTemplate { get; set; }
    public DataTemplate? CategoryItemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is LauncherItem { IsCategory: true })
            return CategoryItemTemplate;
        return LauncherItemTemplate;
    }
}

public partial class LauncherItemsPage : Page
{
    /// <summary>
    /// When set, the edit dialog for this item opens automatically after the page loads.
    /// </summary>
    internal static LauncherItem? PendingEditItem { get; set; }

    public LauncherItemsPage()
    {
        InitializeComponent();
        RefreshList();
        Loaded += LauncherItemsPage_Loaded;
    }

    private async void LauncherItemsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (PendingEditItem is { } item)
        {
            PendingEditItem = null;
            if (item.IsCategory)
                await ShowCategoryDialog(item);
            else
                await ShowItemDialog(item);
        }
    }

    private void RefreshList()
    {
        ItemsList.ItemsSource = null;
        ItemsList.ItemsSource = SettingsManager.Current.LauncherItems;
    }

    private void ItemsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        SaveAndUpdateTaskbar();
    }

    private async void ShowAddDialog_Click(object sender, RoutedEventArgs e)
    {
        await ShowItemDialog(null);
    }

    private async void ShowAddCategoryDialog_Click(object sender, RoutedEventArgs e)
    {
        await ShowCategoryDialog(null);
    }

    private async Task ShowCategoryDialog(LauncherItem? existingItem)
    {
        bool isEdit = existingItem != null;

        var nameBox = new TextBox
        {
            PlaceholderText = "Category name",
            Margin = new Thickness(0, 0, 0, 8)
        };

        if (isEdit)
            nameBox.Text = existingItem!.Name;

        var validationHint = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var form = new StackPanel { MinWidth = 400 };
        form.Children.Add(Label("Name"));
        form.Children.Add(nameBox);
        form.Children.Add(validationHint);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = isEdit ? "Edit Category" : "Add Category",
            Content = form,
            PrimaryButtonText = isEdit ? "Save" : "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        void ValidateForm()
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                validationHint.Text = "Name is required.";
                validationHint.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
            }
            else
            {
                validationHint.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
            }
        }

        nameBox.TextChanged += (s, ev) => ValidateForm();
        ValidateForm();

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();

        if (isEdit)
        {
            existingItem!.Name = name;
        }
        else
        {
            SettingsManager.Current.LauncherItems.Add(LauncherItem.CreateCategory(name));
        }

        RefreshList();
        SaveAndUpdateTaskbar();
    }

    private async void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem item)
        {
            if (item.IsCategory)
                await ShowCategoryDialog(item);
            else
                await ShowItemDialog(item);
        }
    }

    private async Task ShowItemDialog(LauncherItem? existingItem)
    {
        bool isEdit = existingItem != null;

        // Track state for this dialog session
        string fetchedIconPath = existingItem?.IconPath ?? "";
        bool isWebsite = existingItem?.IsWebsite ?? true;
        bool openInAppWindow = existingItem?.OpenInAppWindow ?? false;
        string appWindowBrowser = existingItem?.AppWindowBrowser ?? "";
        string appWindowBrowserProfile = existingItem?.AppWindowBrowserProfile ?? "";

        // -- 1. Type selector --
        var typeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        typeCombo.Items.Add(new ComboBoxItem { Content = "Website or Web App" });
        typeCombo.Items.Add(new ComboBoxItem { Content = "Application" });
        typeCombo.SelectedIndex = isEdit ? (existingItem!.IsWebsite ? 0 : 1) : 0;

        // -- 2. URL / Path --
        Microsoft.UI.Dispatching.DispatcherQueueTimer? debounceTimer = null;
        string lastFetchedPath = "";
        bool populating = false;

        var pathLabel = Label("URL");
        var pathBox = new TextBox
        {
            PlaceholderText = "https://example.com",
            Margin = new Thickness(0, 0, 0, 8)
        };

        // App-mode: ComboBox with installed apps + Browse
        var appPathCombo = new ComboBox
        {
            IsEditable = false,
            Margin = new Thickness(0, 0, 0, 0),
            DisplayMemberPath = "DisplayName"
        };
        bool appCatalogLoaded = false;
        void EnsureAppCatalogLoaded()
        {
            if (appCatalogLoaded) return;
            appPathCombo.Items.Clear();
            foreach (var app in GetInstalledApplications())
                appPathCombo.Items.Add(app);
            appPathCombo.Items.Add(new InstalledApp("Browse\u2026", "__browse__"));
            appCatalogLoaded = true;
        }

        var browseButton = new Button
        {
            Content = "Browse",
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        var appPathRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        appPathCombo.MinWidth = 340;
        appPathRow.Children.Add(appPathCombo);
        appPathRow.Children.Add(browseButton);

        // -- 3. Arguments (Application only) --
        var argsLabel = Label("Arguments");
        var argsBox = new TextBox
        {
            PlaceholderText = "(optional)",
            Margin = new Thickness(0, 0, 0, 8)
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
        var browserCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), MinWidth = 340 };
        var installedBrowsers = GetInstalledBrowsers();
        browserCombo.Items.Add(new ComboBoxItem { Content = "Default browser", Tag = "" });
        foreach (var browser in installedBrowsers)
            browserCombo.Items.Add(new ComboBoxItem { Content = browser.DisplayName, Tag = browser.ExePath });
        browserCombo.Items.Add(new ComboBoxItem { Content = "Custom\u2026", Tag = "__custom__" });

        // -- 4b. Profile picker --
        var profileLabel = Label("Profile");
        var profileCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), MinWidth = 340 };

        void PopulateProfileCombo()
        {
            profileCombo.Items.Clear();

            BrowserEngine currentEngine;
            if (string.IsNullOrEmpty(appWindowBrowser))
            {
                string? defaultExe = GetDefaultBrowserExePath();
                currentEngine = defaultExe != null ? DetectEngine(defaultExe) : BrowserEngine.Chromium;
            }
            else
            {
                var match = installedBrowsers.FirstOrDefault(b =>
                    string.Equals(b.ExePath, appWindowBrowser, StringComparison.OrdinalIgnoreCase));
                currentEngine = match?.Engine ?? DetectEngine(appWindowBrowser);
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

                    foreach (var profile in GetBrowserProfiles(profileDataDir, currentEngine))
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
            PlaceholderText = "Auto-detected",
            Margin = new Thickness(0, 0, 0, 8)
        };

        // -- 6. Icon --
        var iconPreview = new Image
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
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

        // -- Type toggle logic --
        void UpdateTypeUI()
        {
            bool wasWebsite = isWebsite;
            isWebsite = typeCombo.SelectedIndex == 0;
            if (!isWebsite)
            {
                // Registry + app scanning is expensive; only load on-demand.
                EnsureAppCatalogLoaded();
            }
            pathLabel.Text = isWebsite ? "URL" : "Application";
            pathBox.PlaceholderText = isWebsite ? "https://example.com" : @"e.g. notepad.exe or C:\Program Files\...";
            pathBox.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            appPathRow.Visibility = isWebsite ? Visibility.Collapsed : Visibility.Visible;
            argsLabel.Visibility = isWebsite ? Visibility.Collapsed : Visibility.Visible;
            argsBox.Visibility = isWebsite ? Visibility.Collapsed : Visibility.Visible;
            appWindowToggle.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            UpdateAppWindowOptionsVisibility();

            if (wasWebsite != isWebsite && !populating)
            {
                populating = true;
                pathBox.Text = "";
                appPathCombo.SelectedIndex = -1;
                argsBox.Text = "";
                nameBox.Text = "";
                fetchedIconPath = "";
                iconPreview.Source = null;
                iconStatus.Text = "Auto-detected";
                lastFetchedPath = "";
                appWindowToggle.IsOn = false;
                browserCombo.SelectedIndex = 0;
                appWindowBrowser = "";
                profileCombo.SelectedIndex = 0;
                appWindowBrowserProfile = "";
                populating = false;
            }
        }

        typeCombo.SelectionChanged += (s, ev) => UpdateTypeUI();

        // -- Auto-populate on path change (debounced) --
        async Task DoFetch(bool force)
        {
            var path = pathBox.Text.Trim();
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
                nameBox.PlaceholderText = "Auto-detected";

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
                    UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, true);
                }
                else
                {
                    iconStatus.Text = "Could not fetch icon";
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
                nameBox.PlaceholderText = "Auto-detected";
                if (!string.IsNullOrEmpty(appIcon))
                {
                    fetchedIconPath = appIcon;
                    UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, false);
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

        pathBox.TextChanged += (s, ev) => ScheduleFetch();
        refreshButton.Click += async (s, ev) =>
        {
            debounceTimer?.Stop();
            lastFetchedPath = "";
            await DoFetch(force: true);
        };

        // -- Wire up app-path combo events --
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
                ScheduleFetch();
            }
        }

        browseButton.Click += async (s, ev) => await BrowseForApp();
        appPathCombo.SelectionChanged += async (s, ev) =>
        {
            if (populating) return;
            if (appPathCombo.SelectedItem is InstalledApp app)
            {
                if (app.ExePath == "__browse__")
                {
                    appPathCombo.SelectedIndex = -1;
                    await BrowseForApp();
                    return;
                }
                populating = true;
                pathBox.Text = app.ExePath;
                populating = false;
                ScheduleFetch();
            }
        };

        // -- Populate for edit mode --
        if (isEdit)
        {
            populating = true;
            pathBox.Text = existingItem!.Path;
            if (!existingItem.IsWebsite)
            {
                EnsureAppCatalogLoaded();
                for (int i = 0; i < appPathCombo.Items.Count; i++)
                {
                    if (appPathCombo.Items[i] is InstalledApp ia &&
                        string.Equals(ia.ExePath, existingItem.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        appPathCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            argsBox.Text = existingItem.Arguments;
            nameBox.Text = existingItem.Name;
            UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, isWebsite);
            populating = false;
        }

        // -- Build form --
        var form = new StackPanel { MinWidth = 400 };
        form.Children.Add(Label("Type"));
        form.Children.Add(typeCombo);
        form.Children.Add(pathLabel);
        form.Children.Add(pathBox);
        form.Children.Add(appPathRow);
        form.Children.Add(argsLabel);
        form.Children.Add(argsBox);
        form.Children.Add(appWindowToggle);
        form.Children.Add(appWindowOptionsPanel);
        form.Children.Add(Label("Name"));
        form.Children.Add(nameBox);
        form.Children.Add(Label("Icon"));
        form.Children.Add(iconRow);

        var validationHint = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        form.Children.Add(validationHint);

        UpdateTypeUI();

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = isEdit ? "Edit Item" : "Add Item",
            Content = form,
            PrimaryButtonText = isEdit ? "Save" : "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        void ValidateForm()
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(pathBox.Text))
                missing.Add(isWebsite ? "URL" : "Path");
            if (string.IsNullOrWhiteSpace(nameBox.Text))
                missing.Add("Name");

            if (missing.Count > 0)
            {
                validationHint.Text = $"{string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} required.";
                validationHint.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
            }
            else
            {
                validationHint.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
            }
        }

        pathBox.TextChanged += (s, ev) => ValidateForm();
        nameBox.TextChanged += (s, ev) => ValidateForm();
        ValidateForm();

        var result = await dialog.ShowAsync();
        debounceTimer?.Stop();
        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        var finalPath = pathBox.Text.Trim();
        var args = argsBox.Text.Trim();
        var glyph = isWebsite ? "Globe24" : "Open24";

        if (isEdit)
        {
            existingItem!.Name = name;
            existingItem.Path = finalPath;
            existingItem.Arguments = args;
            existingItem.IconGlyph = glyph;
            existingItem.IconPath = fetchedIconPath;
            existingItem.IsWebsite = isWebsite;
            existingItem.OpenInAppWindow = isWebsite && openInAppWindow;
            existingItem.AppWindowBrowser = isWebsite && openInAppWindow ? appWindowBrowser : "";
            existingItem.AppWindowBrowserProfile = isWebsite && openInAppWindow ? appWindowBrowserProfile : "";
        }
        else
        {
            var newItem = new LauncherItem(name, finalPath, glyph, isWebsite, args, fetchedIconPath, isWebsite && openInAppWindow);
            newItem.AppWindowBrowser = isWebsite && openInAppWindow ? appWindowBrowser : "";
            newItem.AppWindowBrowserProfile = isWebsite && openInAppWindow ? appWindowBrowserProfile : "";
            SettingsManager.Current.LauncherItems.Add(newItem);
        }

        RefreshList();
        SaveAndUpdateTaskbar();
    }

    private static void UpdateIconPreview(Image preview, TextBlock status, string iconPath, bool isWebsite = true)
    {
        string iconLabel = isWebsite ? "favicon" : "icon";

        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            try
            {
                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = 32,
                    UriSource = new Uri(iconPath, UriKind.Absolute)
                };
                preview.Source = bitmap;
                status.Text = $"Auto {iconLabel}";
            }
            catch
            {
                preview.Source = null;
                status.Text = "Failed to load icon";
            }
        }
        else
        {
            preview.Source = null;
            status.Text = "No icon";
        }
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static void InitializePicker(object picker)
    {
        var window = SettingsWindow.GetCurrent();
        if (window == null) return;
        var hwnd = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    // -- Browser/app detection helpers --

    private enum BrowserEngine { Chromium, Gecko }

    private record KnownBrowser(string DisplayName, string ExePath, string ProfileDataDir, BrowserEngine Engine);

    private static BrowserEngine DetectEngine(string exePath)
    {
        string? dir = Path.GetDirectoryName(exePath);
        if (dir != null && (File.Exists(Path.Combine(dir, "chrome.dll")) ||
                            File.Exists(Path.Combine(dir, "msedge.dll"))))
            return BrowserEngine.Chromium;

        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (name is "firefox" or "zen" or "waterfox" or "librewolf" or "floorp" or "mercury" or "firedragon")
            return BrowserEngine.Gecko;

        return BrowserEngine.Chromium;
    }

    private static string? GetDefaultBrowserExePath()
    {
        try
        {
            int size = 512;
            var sb = new System.Text.StringBuilder(size);
            int hr = NativeMethods.AssocQueryString(
                NativeMethods.ASSOCF_NONE, NativeMethods.ASSOCSTR_EXECUTABLE,
                "https", "open", sb, ref size);
            if (hr == 0)
            {
                string exePath = sb.ToString();
                if (File.Exists(exePath))
                    return exePath;
            }
        }
        catch { }

        return null;
    }

    private static List<KnownBrowser> GetInstalledBrowsers()
    {
        var browsers = new List<KnownBrowser>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = new (string Name, string[] Paths, string ProfileDataDir, BrowserEngine Engine)[]
        {
            ("Microsoft Edge", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            ], Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), BrowserEngine.Chromium),
            ("Google Chrome", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
            ], Path.Combine(localAppData, "Google", "Chrome", "User Data"), BrowserEngine.Chromium),
            ("Brave", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
            ], Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"), BrowserEngine.Chromium),
            ("Vivaldi", [
                Path.Combine(localAppData, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vivaldi", "Application", "vivaldi.exe")
            ], Path.Combine(localAppData, "Vivaldi", "User Data"), BrowserEngine.Chromium),
            ("Chromium", [
                Path.Combine(localAppData, "Chromium", "Application", "chrome.exe")
            ], Path.Combine(localAppData, "Chromium", "User Data"), BrowserEngine.Chromium),
            ("Firefox", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe")
            ], Path.Combine(appData, "Mozilla", "Firefox"), BrowserEngine.Gecko),
            ("Zen", [
                Path.Combine(localAppData, "Programs", "zen", "zen.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zen Browser", "zen.exe"),
                Path.Combine(localAppData, "zen", "zen.exe"),
            ], Path.Combine(appData, "zen"), BrowserEngine.Gecko),
            ("Waterfox", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Waterfox", "waterfox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Waterfox", "waterfox.exe")
            ], Path.Combine(appData, "Waterfox"), BrowserEngine.Gecko),
            ("LibreWolf", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreWolf", "librewolf.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreWolf", "librewolf.exe")
            ], Path.Combine(appData, "librewolf"), BrowserEngine.Gecko),
        };

        foreach (var (name, paths, profileDataDir, engine) in candidates)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    browsers.Add(new KnownBrowser(name, path, profileDataDir, engine));
                    break;
                }
            }
        }

        return browsers;
    }

    private record BrowserProfile(string DirectoryName, string DisplayName);

    private static List<BrowserProfile> GetBrowserProfiles(string profileDataDir, BrowserEngine engine)
    {
        if (string.IsNullOrEmpty(profileDataDir) || !Directory.Exists(profileDataDir))
            return [];

        return engine == BrowserEngine.Gecko
            ? GetGeckoProfiles(profileDataDir)
            : GetChromiumProfiles(profileDataDir);
    }

    private static List<BrowserProfile> GetChromiumProfiles(string userDataDir)
    {
        var profiles = new List<BrowserProfile>();
        if (string.IsNullOrEmpty(userDataDir) || !Directory.Exists(userDataDir))
            return profiles;

        string localStatePath = Path.Combine(userDataDir, "Local State");
        if (File.Exists(localStatePath))
        {
            try
            {
                string json = File.ReadAllText(localStatePath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("profile", out var profileRoot) &&
                    profileRoot.TryGetProperty("info_cache", out var infoCache))
                {
                    foreach (var entry in infoCache.EnumerateObject())
                    {
                        string dirName = entry.Name;
                        string displayName = dirName;
                        if (entry.Value.TryGetProperty("name", out var nameProp))
                            displayName = nameProp.GetString() ?? dirName;

                        if (Directory.Exists(Path.Combine(userDataDir, dirName)))
                            profiles.Add(new BrowserProfile(dirName, displayName));
                    }
                }
            }
            catch { }
        }

        if (profiles.Count == 0)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(userDataDir))
                {
                    if (File.Exists(Path.Combine(dir, "Preferences")))
                    {
                        string dirName = Path.GetFileName(dir);
                        profiles.Add(new BrowserProfile(dirName, dirName));
                    }
                }
            }
            catch { }
        }

        return profiles.OrderBy(p => p.DirectoryName != "Default")
                       .ThenBy(p => p.DisplayName)
                       .ToList();
    }

    private static List<BrowserProfile> GetGeckoProfiles(string profileDataDir)
    {
        var profiles = new List<BrowserProfile>();
        string iniPath = Path.Combine(profileDataDir, "profiles.ini");
        if (!File.Exists(iniPath))
            return profiles;

        try
        {
            string? currentName = null;
            string? currentPath = null;
            bool isRelative = true;

            foreach (string rawLine in File.ReadLines(iniPath))
            {
                string line = rawLine.Trim();

                if (line.StartsWith('['))
                {
                    if (currentPath != null)
                    {
                        string fullPath = isRelative
                            ? Path.Combine(profileDataDir, currentPath.Replace('/', '\\'))
                            : currentPath;

                        if (Directory.Exists(fullPath))
                            profiles.Add(new BrowserProfile(fullPath, currentName ?? Path.GetFileName(fullPath)));
                    }

                    currentName = null;
                    currentPath = null;
                    isRelative = true;

                    if (!line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase))
                        currentPath = null;

                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();

                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    currentName = value;
                else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                    currentPath = value;
                else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase))
                    isRelative = value == "1";
            }

            if (currentPath != null)
            {
                string fullPath = isRelative
                    ? Path.Combine(profileDataDir, currentPath.Replace('/', '\\'))
                    : currentPath;

                if (Directory.Exists(fullPath))
                    profiles.Add(new BrowserProfile(fullPath, currentName ?? Path.GetFileName(fullPath)));
            }
        }
        catch { }

        return profiles;
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem item)
        {
            SettingsManager.Current.LauncherItems.Remove(item);
            RefreshList();
            SaveAndUpdateTaskbar();
        }
    }

    private record InstalledApp(string DisplayName, string ExePath);

    /// <summary>
    /// Builds a list of installed applications by scanning Start Menu shortcuts
    /// and the Windows Registry uninstall keys.
    /// </summary>
    private static List<InstalledApp> GetInstalledApplications()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        var startMenuDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        foreach (var dir in startMenuDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        var target = ResolveShortcutTarget(lnk);
                        if (string.IsNullOrEmpty(target)) continue;
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(target)) continue;

                        var name = Path.GetFileNameWithoutExtension(lnk);
                        if (IsNonAppName(name)) continue;

                        if (!apps.ContainsKey(target))
                            apps[target] = new InstalledApp(name, target);
                    }
                    catch { }
                }
            }
            catch { }
        }

        var uninstallKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var keyPath in uninstallKeys)
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var key = root.OpenSubKey(keyPath);
                    if (key == null) continue;

                    foreach (var subName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = key.OpenSubKey(subName);
                            if (sub == null) continue;

                            var systemComponent = sub.GetValue("SystemComponent");
                            if (systemComponent is int sc && sc == 1) continue;
                            var parentName = sub.GetValue("ParentDisplayName") as string;
                            if (!string.IsNullOrEmpty(parentName)) continue;
                            var releaseType = sub.GetValue("ReleaseType") as string;
                            if (!string.IsNullOrEmpty(releaseType)) continue;

                            var displayName = sub.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName)) continue;
                            if (IsNonAppName(displayName)) continue;

                            var exePath = ResolveAppExePath(sub);
                            if (string.IsNullOrEmpty(exePath)) continue;

                            if (!apps.ContainsKey(exePath))
                                apps[exePath] = new InstalledApp(displayName, exePath);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        return apps.Values
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsNonAppName(string name)
    {
        string[] filters =
        [
            "SDK", "Runtime", "Redistributable", "Targeting Pack", "Manifest",
            "Toolset", "Template", "Hosting Bundle", "AppHost", "SharedHost",
            "WindowsDesktop", "Host (", "- Debug", "IntelliTrace",
            "DiagnosticsHub", "IntelliSense", "Language Pack",
            "Driver", "Firmware", "BIOS", "Chipset",
            ".NET Framework", "Microsoft .NET", "Microsoft ASP.NET",
            "Microsoft Windows Desktop", "Microsoft Visual C++",
            "Uninstall"
        ];

        foreach (var filter in filters)
        {
            if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (name.StartsWith('{') || name.StartsWith("KB"))
            return true;

        return false;
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string? target = shortcut.TargetPath;
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveAppExePath(Microsoft.Win32.RegistryKey sub)
    {
        var displayIcon = sub.GetValue("DisplayIcon") as string;
        if (!string.IsNullOrEmpty(displayIcon))
        {
            var iconPath = displayIcon.Split(',')[0].Trim('"', ' ');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
                return iconPath;
        }

        var installLoc = sub.GetValue("InstallLocation") as string;
        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            var displayName = sub.GetValue("DisplayName") as string ?? "";
            foreach (var exe in Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var fn = Path.GetFileNameWithoutExtension(exe);
                if (displayName.Contains(fn, StringComparison.OrdinalIgnoreCase)
                    || fn.Contains(displayName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return exe;
            }
            var firstExe = Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (firstExe != null) return firstExe;
        }

        return null;
    }

    private void SaveAndUpdateTaskbar()
    {
        SettingsManager.SaveSettings();
    }
}