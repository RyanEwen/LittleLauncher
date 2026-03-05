using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Models;
using SelfHostedHelper.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;

namespace SelfHostedHelper.Pages;

public partial class LauncherItemsPage : Page
{
    public LauncherItemsPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        RefreshList();
    }

    private void RefreshList()
    {
        ItemsList.ItemsSource = null;
        ItemsList.ItemsSource = SettingsManager.Current.LauncherItems;
    }

    private async void ShowAddDialog_Click(object sender, RoutedEventArgs e)
    {
        await ShowItemDialog(null);
    }

    private async void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem item)
            await ShowItemDialog(item);
    }

    private async Task ShowItemDialog(LauncherItem? existingItem)
    {
        bool isEdit = existingItem != null;

        // Track state for this dialog session
        string fetchedIconPath = existingItem?.IconPath ?? "";
        bool isWebsite = existingItem?.IsWebsite ?? true; // default to website

        // ── 1. Type selector ────────────────────────────────────────
        var typeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        typeCombo.Items.Add(new ComboBoxItem { Content = "Website or Web App" });
        typeCombo.Items.Add(new ComboBoxItem { Content = "Application" });
        typeCombo.SelectedIndex = isEdit ? (existingItem!.IsWebsite ? 0 : 1) : 0;

        // ── 2. URL / Path ───────────────────────────────────────────
        // State variables declared early so combo event handlers can reference them
        DispatcherTimer? debounceTimer = null;
        string lastFetchedPath = "";
        bool populating = false;

        var pathLabel = Label("URL");
        var pathBox = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "https://example.com",
            ClearButtonEnabled = true,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // App-mode: ComboBox with installed apps + Browse
        var appPathCombo = new ComboBox
        {
            IsEditable = false,
            Margin = new Thickness(0, 0, 0, 0),
            DisplayMemberPath = "DisplayName"
        };
        foreach (var app in GetInstalledApplications())
            appPathCombo.Items.Add(app);
        appPathCombo.Items.Add(new InstalledApp("Browse\u2026", "__browse__"));

        var browseButton = new Wpf.Ui.Controls.Button
        {
            Content = "Browse",
            Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.FolderOpen24),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
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

        // ── 3. Arguments (Application only) ─────────────────────────
        var argsLabel = Label("Arguments");
        var argsBox = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "(optional)",
            ClearButtonEnabled = true,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // ── 4. Name ─────────────────────────────────────────────────
        var nameBox = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Auto-detected",
            ClearButtonEnabled = true,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // ── 5. Icon ─────────────────────────────────────────────────
        var iconPreview = new Image
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconStatus = new System.Windows.Controls.TextBlock
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

        var refreshButton = new Wpf.Ui.Controls.Button
        {
            Content = "Retry",
            Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowSync24),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12
        };
        iconRow.Children.Add(refreshButton);

        // ── Type toggle logic ───────────────────────────────────────
        void UpdateTypeUI()
        {
            bool wasWebsite = isWebsite;
            isWebsite = typeCombo.SelectedIndex == 0;
            pathLabel.Text = isWebsite ? "URL" : "Application";
            pathBox.PlaceholderText = isWebsite ? "https://example.com" : "e.g. notepad.exe or C:\\Program Files\\...";
            pathBox.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            appPathRow.Visibility = isWebsite ? Visibility.Collapsed : Visibility.Visible;
            argsLabel.Visibility = isWebsite ? Visibility.Collapsed : Visibility.Visible;
            argsBox.Visibility = isWebsite ? Visibility.Collapsed : Visibility.Visible;

            // Clear form when switching type (but not on initial setup)
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
                populating = false;
            }
        }

        typeCombo.SelectionChanged += (s, ev) => UpdateTypeUI();

        // ── Auto-populate on path change (debounced) ────────────────
        // (populating, debounceTimer, lastFetchedPath declared earlier)

        // Core fetch logic — called by debounce and refresh button.
        // When force is true, re-fetches even if the path hasn't changed
        // and overwrites the name field.
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
            debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            debounceTimer.Tick += async (s, ev) =>
            {
                debounceTimer.Stop();
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

        // ── Wire up app-path combo events (after ScheduleFetch exists) ──
        void BrowseForApp()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Select an application"
            };
            if (dlg.ShowDialog() == true)
            {
                populating = true;
                pathBox.Text = dlg.FileName;
                populating = false;
                ScheduleFetch();
            }
        }

        browseButton.Click += (s, ev) => BrowseForApp();
        appPathCombo.SelectionChanged += (s, ev) =>
        {
            if (populating) return;
            if (appPathCombo.SelectedItem is InstalledApp app)
            {
                if (app.ExePath == "__browse__")
                {
                    appPathCombo.SelectedIndex = -1;
                    BrowseForApp();
                    return;
                }
                populating = true;
                pathBox.Text = app.ExePath;
                populating = false;
                ScheduleFetch();
            }
        };

        // ── Populate for edit mode ──────────────────────────────────
        if (isEdit)
        {
            populating = true;
            pathBox.Text = existingItem!.Path;
            // Try to select the matching installed app in the combo
            for (int i = 0; i < appPathCombo.Items.Count; i++)
            {
                if (appPathCombo.Items[i] is InstalledApp ia &&
                    string.Equals(ia.ExePath, existingItem.Path, StringComparison.OrdinalIgnoreCase))
                {
                    appPathCombo.SelectedIndex = i;
                    break;
                }
            }
            argsBox.Text = existingItem.Arguments;
            nameBox.Text = existingItem.Name;
            UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, isWebsite);
            populating = false;
        }

        // ── Build form ──────────────────────────────────────────────
        var form = new StackPanel();
        form.Children.Add(Label("Type"));
        form.Children.Add(typeCombo);
        form.Children.Add(pathLabel);
        form.Children.Add(pathBox);
        form.Children.Add(appPathRow);
        form.Children.Add(argsLabel);
        form.Children.Add(argsBox);
        form.Children.Add(Label("Name"));
        form.Children.Add(nameBox);
        form.Children.Add(Label("Icon"));
        form.Children.Add(iconRow);

        var validationHint = new System.Windows.Controls.TextBlock
        {
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.OrangeRed),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        form.Children.Add(validationHint);

        UpdateTypeUI();

        var dialog = new ContentDialog()
        {
            DialogHostEx = (Wpf.Ui.Controls.ContentDialogHost)Window.GetWindow(this).FindName("DialogHost"),
            Title = isEdit ? "Edit Item" : "Add Item",
            Content = form,
            PrimaryButtonText = isEdit ? "Save" : "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        // Real-time validation: disable Save/Add when required fields are empty
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
        }
        else
        {
            SettingsManager.Current.LauncherItems.Add(
                new LauncherItem(name, finalPath, glyph, isWebsite, args, fetchedIconPath));
        }

        RefreshList();
        SaveAndUpdateTaskbar();
    }

    private static void UpdateIconPreview(Image preview, System.Windows.Controls.TextBlock status, string iconPath, bool isWebsite = true)
    {
        string iconLabel = isWebsite ? "favicon" : "icon";

        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 32;
                bitmap.EndInit();
                bitmap.Freeze();
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

    private static System.Windows.Controls.TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Medium,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem item)
        {
            var items = SettingsManager.Current.LauncherItems;
            int index = items.IndexOf(item);
            if (index > 0)
            {
                items.Move(index, index - 1);
                RefreshList();
                SaveAndUpdateTaskbar();
            }
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem item)
        {
            var items = SettingsManager.Current.LauncherItems;
            int index = items.IndexOf(item);
            if (index >= 0 && index < items.Count - 1)
            {
                items.Move(index, index + 1);
                RefreshList();
                SaveAndUpdateTaskbar();
            }
        }
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
    /// and the Windows Registry uninstall keys, filtering out non-launchable
    /// entries like SDKs, runtimes, drivers, and libraries.
    /// </summary>
    private static List<InstalledApp> GetInstalledApplications()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        // ── 1. Start Menu shortcuts (most reliable for "launchable" apps) ──
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
                    catch { /* skip unreadable shortcuts */ }
                }
            }
            catch { /* access denied */ }
        }

        // ── 2. Registry uninstall keys (fills gaps Start Menu misses) ──
        var uninstallKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var keyPath in uninstallKeys)
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
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

                            // Skip system components and updates
                            var systemComponent = sub.GetValue("SystemComponent");
                            if (systemComponent is int sc && sc == 1) continue;
                            var parentName = sub.GetValue("ParentDisplayName") as string;
                            if (!string.IsNullOrEmpty(parentName)) continue;
                            var releaseType = sub.GetValue("ReleaseType") as string;
                            if (!string.IsNullOrEmpty(releaseType)) continue; // updates, hotfixes, etc.

                            var displayName = sub.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName)) continue;
                            if (IsNonAppName(displayName)) continue;

                            var exePath = ResolveAppExePath(sub);
                            if (string.IsNullOrEmpty(exePath)) continue;

                            // Only add if we don't already have this exe from Start Menu
                            if (!apps.ContainsKey(exePath))
                                apps[exePath] = new InstalledApp(displayName, exePath);
                        }
                        catch { /* skip unreadable entries */ }
                    }
                }
                catch { /* skip inaccessible hives */ }
            }
        }

        return apps.Values
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Filters out SDKs, runtimes, drivers, libraries, and other non-app entries.</summary>
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

        // Filter names that are just GUIDs or hex codes
        if (name.StartsWith('{') || name.StartsWith("KB"))
            return true;

        return false;
    }

    /// <summary>Resolves a .lnk shortcut to its target path using Shell32 COM.</summary>
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

    private static string? ResolveAppExePath(RegistryKey sub)
    {
        // DisplayIcon often points to the main .exe
        var displayIcon = sub.GetValue("DisplayIcon") as string;
        if (!string.IsNullOrEmpty(displayIcon))
        {
            // Strip icon index like ",0" or ",1"
            var iconPath = displayIcon.Split(',')[0].Trim('"', ' ');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
                return iconPath;
        }

        // InstallLocation + look for a main exe
        var installLoc = sub.GetValue("InstallLocation") as string;
        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            var displayName = sub.GetValue("DisplayName") as string ?? "";
            // Try exe matching the display name first
            foreach (var exe in Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var fn = Path.GetFileNameWithoutExtension(exe);
                if (displayName.Contains(fn, StringComparison.OrdinalIgnoreCase)
                    || fn.Contains(displayName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return exe;
            }
            // Fall back to first exe in directory
            var firstExe = Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (firstExe != null) return firstExe;
        }

        return null;
    }

    private void SaveAndUpdateTaskbar()
    {
        SettingsManager.SaveSettings();

        if (Application.Current?.MainWindow is MainWindow mainWindow)
        {
            mainWindow.UpdateTaskbar();
        }
    }
}
