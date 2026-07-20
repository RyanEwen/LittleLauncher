using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using global::Windows.Graphics;
using WinRT.Interop;
using Image = Microsoft.UI.Xaml.Controls.Image;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// Per-launcher settings (name, tray icon, view mode, density, title, taskbar pin).
/// </summary>
/// <remarks>
/// <para>A standalone window rather than a <c>ContentDialog</c>, for the same reason as
/// <see cref="ItemEditorWindow"/>: it is opened from the flyout, and a dialog cannot
/// overflow the flyout's HWND. The Launchers page opens this same window, so there is one
/// implementation rather than a dialog and a window drifting apart.</para>
/// <para>Settings apply immediately as they are changed; the name is committed on close.</para>
/// </remarks>
public sealed class LauncherSettingsWindow : Window
{
    private const int WindowWidthDips = 560;
    private const int WindowHeightDips = 620;

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly IntPtr _hwnd;
    private readonly Launcher _launcher;
    private TextBox? _nameBox;

    /// <summary>
    /// Opens launcher settings. Completes when the window closes.
    /// <paramref name="ownerHwnd"/> keeps it above an always-on-top flyout.
    /// </summary>
    public static Task<bool> ShowAsync(Launcher launcher, IntPtr ownerHwnd = default, Action<Window>? onCreated = null)
    {
        var window = new LauncherSettingsWindow(launcher, ownerHwnd);
        onCreated?.Invoke(window);
        window.Activate();
        return window._completion.Task;
    }

    private LauncherSettingsWindow(Launcher launcher, IntPtr ownerHwnd)
    {
        _launcher = launcher;
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Launcher Settings";
        SystemBackdrop = new MicaBackdrop();

        if (ownerHwnd != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, ownerHwnd);

        var titleBar = WindowChrome.BuildTitleBar(Title);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);

        var doneButton = new Button
        {
            Content = "Done",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        doneButton.Click += (_, _) => Close();

        var body = new Grid { Padding = new Thickness(24, 8, 24, 24) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroller = new ScrollViewer
        {
            Content = BuildForm(launcher),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroller, 0);
        Grid.SetRow(doneButton, 1);
        body.Children.Add(scroller);
        body.Children.Add(doneButton);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(titleBar);
        root.Children.Add(body);

        Content = root;
        ThemeManager.ApplySavedTheme(this);
        WindowChrome.ApplyIcon(_hwnd);

        SizeAndCentre();

        // Hidden: WinUI otherwise pops an "Esc" accelerator tooltip over the window.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; Close(); };
        root.KeyboardAccelerators.Add(escape);

        Closed += (_, _) =>
        {
            CommitName();
            _completion.TrySetResult(true);
        };
    }

    /// <summary>The name is the only deferred edit; everything else applies as it changes.</summary>
    private void CommitName()
    {
        if (_nameBox == null) return;
        string name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name == _launcher.Name) return;

        _launcher.Name = name;
        SettingsManager.SaveSettings();
        MainWindow.Current?.RefreshTrayIcons();
        FlyoutWindow.InvalidateItems(_launcher.Id);
    }

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

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private FrameworkElement BuildForm(Launcher launcher)
    {
        // ── Name row ────────────────────────────────────────────────
        var nameBox = new TextBox
        {
            PlaceholderText = "Launcher name",
            Text = launcher.Name,
            MinWidth = 160,
            MaxWidth = 280,
        };
        _nameBox = nameBox;

        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameLabel.Children.Add(new TextBlock { Text = "Name", FontSize = 14 });
        nameLabel.Children.Add(new TextBlock { Text = "Display name in tray icon tooltip", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(nameLabel, 0);
        Grid.SetColumn(nameBox, 1);
        nameRow.Children.Add(nameLabel);
        nameRow.Children.Add(nameBox);

        // ── Icon chooser ─────────────────────────────────────────
        var (iconButton, customIconRow) = BuildIconChooser(launcher);

        var iconRow = new Grid();
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        iconLabel.Children.Add(new TextBlock { Text = "Icon", FontSize = 14 });
        iconLabel.Children.Add(new TextBlock { Text = "Icon style for this launcher", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(iconLabel, 0);
        Grid.SetColumn(iconButton, 1);
        iconRow.Children.Add(iconLabel);
        iconRow.Children.Add(iconButton);

        // ── View mode combo ──────────────────────────────────────
        var viewModeCombo = new ComboBox { MinWidth = 160 };
        var viewModes = new[]
        {
            (Label: "Icons", Value: LauncherViewModes.Icon),
            (Label: "Small Icons", Value: LauncherViewModes.SmallIcon),
            (Label: "List", Value: LauncherViewModes.List),
        };

        foreach (var viewMode in viewModes)
        {
            viewModeCombo.Items.Add(new ComboBoxItem
            {
                Content = viewMode.Label,
                Tag = viewMode.Value,
            });
        }

        int selectedViewMode = LauncherViewModes.Normalize(launcher.ViewMode);
        viewModeCombo.SelectedIndex = Array.FindIndex(viewModes, vm => vm.Value == selectedViewMode);
        if (viewModeCombo.SelectedIndex < 0)
            viewModeCombo.SelectedIndex = 0;

        var viewModeRow = new Grid();
        viewModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        viewModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var viewModeLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        viewModeLabel.Children.Add(new TextBlock { Text = "View Mode", FontSize = 14 });
        viewModeLabel.Children.Add(new TextBlock { Text = "How items appear in the flyout popup", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(viewModeLabel, 0);
        Grid.SetColumn(viewModeCombo, 1);
        viewModeRow.Children.Add(viewModeLabel);
        viewModeRow.Children.Add(viewModeCombo);

        // ── Icon mode density ───────────────────────────────────
        var iconsPerRowCombo = new ComboBox { MinWidth = 100 };
        for (int iconsPerRow = Launcher.MinIconModeIconsPerRow; iconsPerRow <= Launcher.MaxIconModeIconsPerRow; iconsPerRow++)
            iconsPerRowCombo.Items.Add(iconsPerRow.ToString());
        iconsPerRowCombo.SelectedItem = Launcher.ClampIconModeIconsPerRow(launcher.IconModeIconsPerRow).ToString();
        iconsPerRowCombo.SelectionChanged += (s, e) =>
        {
            if (iconsPerRowCombo.SelectedItem is string selected && int.TryParse(selected, out int iconsPerRow))
            {
                launcher.IconModeIconsPerRow = Launcher.ClampIconModeIconsPerRow(iconsPerRow);
                SettingsManager.SaveSettings();
                FlyoutWindow.InvalidateItems(launcher.Id);
            }
        };

        var iconsPerRowRow = new Grid();
        iconsPerRowRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        iconsPerRowRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconsPerRowLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        iconsPerRowLabel.Children.Add(new TextBlock { Text = "Icons Per Row", FontSize = 14 });
        iconsPerRowLabel.Children.Add(new TextBlock { Text = "How many icons fit across each icon-mode column", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(iconsPerRowLabel, 0);
        Grid.SetColumn(iconsPerRowCombo, 1);
        iconsPerRowRow.Children.Add(iconsPerRowLabel);
        iconsPerRowRow.Children.Add(iconsPerRowCombo);

        void UpdateIconModeControls()
        {
            int selectedMode = viewModeCombo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is int value
                ? value
                : LauncherViewModes.Icon;
            iconsPerRowRow.Visibility = LauncherViewModes.IsIconMode(selectedMode) ? Visibility.Visible : Visibility.Collapsed;
        }

        viewModeCombo.SelectionChanged += (s, e) =>
        {
            if (viewModeCombo.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not int viewMode)
                return;

            launcher.ViewMode = viewMode;
            SettingsManager.SaveSettings();
            FlyoutWindow.InvalidateItems(launcher.Id);
            UpdateIconModeControls();
        };

        UpdateIconModeControls();

        // ── Show title toggle ────────────────────────────────────
        var showTitleToggle = new ToggleSwitch
        {
            IsOn = launcher.ShowTitle,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        showTitleToggle.Toggled += (s, e) =>
        {
            launcher.ShowTitle = showTitleToggle.IsOn;
            SettingsManager.SaveSettings();
            FlyoutWindow.InvalidateItems(launcher.Id);
        };

        var showTitleRow = new Grid();
        showTitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        showTitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var showTitleLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        showTitleLabel.Children.Add(new TextBlock { Text = "Show Title", FontSize = 14 });
        showTitleLabel.Children.Add(new TextBlock { Text = "Show the launcher name at the top of the flyout", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(showTitleLabel, 0);
        Grid.SetColumn(showTitleToggle, 1);
        showTitleRow.Children.Add(showTitleLabel);
        showTitleRow.Children.Add(showTitleToggle);

        // ── Show in tray toggle ──────────────────────────────────
        var showToggle = new ToggleSwitch
        {
            IsOn = !launcher.NIconHide,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        showToggle.Toggled += (s, e) =>
        {
            launcher.NIconHide = !showToggle.IsOn;
            SettingsManager.SaveSettings();
        };

        var hideRow = new Grid();
        hideRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hideRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hideLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hideLabel.Children.Add(new TextBlock { Text = "Show in Tray", FontSize = 14 });
        hideLabel.Children.Add(new TextBlock { Text = "Show this launcher's icon in the system tray", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(hideLabel, 0);
        Grid.SetColumn(showToggle, 1);
        hideRow.Children.Add(hideLabel);
        hideRow.Children.Add(showToggle);

        // ── Pin to taskbar row ──────────────────────────────────
        var pinBtn = new Button
        {
            Content = "Pin to Taskbar",
            Tag = launcher,
        };
        pinBtn.Click += PinToTaskbar_Click;

        var taskbarRow = new Grid();
        taskbarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        taskbarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var taskbarLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        taskbarLabel.Children.Add(new TextBlock { Text = "Show in Taskbar", FontSize = 14 });
        taskbarLabel.Children.Add(new TextBlock { Text = "Pin a shortcut to the taskbar for quick access", FontSize = 12, Opacity = 0.5 });
        taskbarLabel.Children.Add(new TextBlock { Text = "Changing the icon requires unpinning and re-pinning", FontSize = 11, Opacity = 0.4, FontStyle = global::Windows.UI.Text.FontStyle.Italic });
        Grid.SetColumn(taskbarLabel, 0);
        Grid.SetColumn(pinBtn, 1);
        taskbarRow.Children.Add(taskbarLabel);
        taskbarRow.Children.Add(pinBtn);

        // ── Build dialog content ────────────────────────────────
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameRow);
        panel.Children.Add(iconRow);
        panel.Children.Add(customIconRow);
        panel.Children.Add(viewModeRow);
        panel.Children.Add(iconsPerRowRow);
        panel.Children.Add(showTitleRow);
        panel.Children.Add(hideRow);
        panel.Children.Add(taskbarRow);
        return panel;
    }

    private (Button Button, Grid CustomRow) BuildIconChooser(Launcher launcher)
    {
        // ── Preview elements for the button content ──
        var previewIcon = new FontIcon { FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        var previewImage = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var previewEmoji = new TextBlock { FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        var previewLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var chevron = new FontIcon { Glyph = "", FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

        var buttonContent = new StackPanel { Orientation = Orientation.Horizontal };
        buttonContent.Children.Add(previewIcon);
        buttonContent.Children.Add(previewImage);
        buttonContent.Children.Add(previewEmoji);
        buttonContent.Children.Add(previewLabel);
        buttonContent.Children.Add(chevron);

        // ── Custom icon path row ──
        var customIconRow = BuildCustomIconRow(launcher);
        customIconRow.Visibility = launcher.TrayIconMode == TrayIconModes.Custom ? Visibility.Visible : Visibility.Collapsed;

        void UpdatePreview()
        {
            string mode = launcher.TrayIconMode;
            previewIcon.Visibility = Visibility.Collapsed;
            previewImage.Visibility = Visibility.Collapsed;
            previewEmoji.Visibility = Visibility.Collapsed;
            // Clear any custom color from a previous glyph selection
            previewIcon.ClearValue(FontIcon.ForegroundProperty);
            previewEmoji.ClearValue(TextBlock.ForegroundProperty);

            if (mode == TrayIconModes.Composite)
            {
                previewIcon.Glyph = "\uF0E2";
                previewIcon.Visibility = Visibility.Visible;
                previewLabel.Text = "Composite";
            }
            else if (mode == TrayIconModes.Custom)
            {
                previewIcon.Glyph = "\uE8B9";
                previewIcon.Visibility = Visibility.Visible;
                previewLabel.Text = "Custom";
            }
            else if (TrayIconModes.IsGlyphMode(mode))
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
                    previewIcon.Glyph = glyph;
                    if (colorBrush != null)
                        previewIcon.Foreground = colorBrush;
                    else
                        previewIcon.ClearValue(FontIcon.ForegroundProperty);
                    previewIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    previewEmoji.Text = glyph;
                    if (colorBrush != null)
                        previewEmoji.Foreground = colorBrush;
                    else
                        previewEmoji.ClearValue(TextBlock.ForegroundProperty);
                    previewEmoji.Visibility = Visibility.Visible;
                }
                previewLabel.Text = "";
            }
            else
            {
                // Known preset: color or glyph
                string iconsDir = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons");
                string[] colorNames = ["Blue", "Green", "Teal", "Red", "Orange", "Purple"];
                if (colorNames.Contains(mode))
                {
                    string pngPath = Path.Combine(iconsDir, $"{mode}.png");
                    if (File.Exists(pngPath))
                        previewImage.Source = new BitmapImage(new Uri(pngPath));
                    previewImage.Visibility = Visibility.Visible;
                    previewLabel.Text = mode;
                }
                else
                {
                    // Glyph preset (Pin, Star, Heart, etc.)
                    (string glyph, string label)[] glyphs = [
                        ("\uE840", "Pin"), ("\uE734", "Star"), ("\uEB51", "Heart"),
                        ("\uE945", "Lightning"), ("\uE721", "Search"), ("\uE774", "Globe"),
                    ];
                    var match = glyphs.FirstOrDefault(g => g.label == mode);
                    if (match.glyph != null)
                    {
                        previewIcon.Glyph = match.glyph;
                        previewIcon.Visibility = Visibility.Visible;
                        previewLabel.Text = match.label;
                    }
                    else
                    {
                        previewIcon.Glyph = "\uE774";
                        previewIcon.Visibility = Visibility.Visible;
                        previewLabel.Text = mode;
                    }
                }
            }

            customIconRow.Visibility = mode == TrayIconModes.Custom
                ? Visibility.Visible : Visibility.Collapsed;
        }

        var button = new Button { Content = buttonContent, Padding = new Thickness(10, 6, 10, 6) };

        // ── Build the gallery flyout ──
        var flyout = IconGallery.CreateLauncherIconFlyout(
            currentMode: launcher.TrayIconMode,
            onSelected: result =>
            {
                if (result.Glyph != null)
                {
                    launcher.TrayIconMode = TrayIconModes.ToGlyphMode(result.Glyph, result.Color);
                    launcher.CustomTrayIconPath = "";
                }
                else if (result.ImagePath != null)
                {
                    // Copy to AppData as custom tray icon
                    string destPath = Path.Combine(MainWindow.GetPhysicalAppDataDir(),
                        $"custom-tray-icon-{launcher.Id}{Path.GetExtension(result.ImagePath)}");
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(result.ImagePath, destPath, overwrite: true);
                    launcher.TrayIconMode = TrayIconModes.Custom;
                    launcher.CustomTrayIconPath = destPath;
                }
                else if (result.PresetMode != null)
                {
                    launcher.TrayIconMode = result.PresetMode;
                    launcher.CustomTrayIconPath = "";
                }
                SettingsManager.SaveSettings();
                UpdatePreview();
            },
            onBrowseRequested: async () =>
            {
                var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".ico");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    _hwnd);
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    string destPath = Path.Combine(MainWindow.GetPhysicalAppDataDir(),
                        $"custom-tray-icon-{launcher.Id}{Path.GetExtension(file.Path)}");
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(file.Path, destPath, overwrite: true);
                    launcher.TrayIconMode = TrayIconModes.Custom;
                    launcher.CustomTrayIconPath = destPath;
                    SettingsManager.SaveSettings();
                    UpdatePreview();
                }
            }
        );

        button.Flyout = flyout;
        UpdatePreview();

        return (button, customIconRow);
    }

    private Grid BuildCustomIconRow(Launcher launcher)
    {
        var pathText = new TextBlock
        {
            Text = string.IsNullOrEmpty(launcher.CustomTrayIconPath)
                ? "No file selected"
                : System.IO.Path.GetFileName(launcher.CustomTrayIconPath),
            FontSize = 12,
            Opacity = 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
        };

        var browseBtn = new Button { Content = "Browse..." };
        browseBtn.Click += async (s, e) =>
        {
            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".ico");
            picker.FileTypeFilter.Add(".png");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                string destPath = Path.Combine(MainWindow.GetPhysicalAppDataDir(), $"custom-tray-icon-{launcher.Id}{Path.GetExtension(file.Path)}");
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file.Path, destPath, overwrite: true);
                launcher.CustomTrayIconPath = destPath;
                pathText.Text = Path.GetFileName(destPath);
                SettingsManager.SaveSettings();
            }
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = "Custom Icon", FontSize = 14 });
        label.Children.Add(pathText);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(browseBtn, 1);
        row.Children.Add(label);
        row.Children.Add(browseBtn);
        return row;
    }

    private async void PinToTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;

        // Ensure the per-launcher icon .ico exists so the companion exe's
        // RelaunchIconResource can reference it.
        MainWindow.EnsureLauncherIconSaved(launcher);

        string appDataDir = MainWindow.GetPhysicalAppDataDir();
        string baseIco = Path.Combine(appDataDir, $"app-icon-{launcher.Id}.ico");
        string? pinnedIconPath = null;
        if (File.Exists(baseIco))
        {
            pinnedIconPath = Path.Combine(appDataDir, $"app-icon-{launcher.Id}-pin{Environment.TickCount64}.ico");
            File.Copy(baseIco, pinnedIconPath, overwrite: true);
        }

        // Launch the companion exe in --pin mode with the launcher's ID
        string flyoutExe = Path.Combine(appDataDir, "LittleLauncherFlyout.exe");
        if (!File.Exists(flyoutExe))
            flyoutExe = Path.Combine(AppContext.BaseDirectory, "LittleLauncherFlyout.exe");

        if (!File.Exists(flyoutExe))
        {
            await ShowErrorAsync("The companion flyout exe was not found. Build the project first.");
            return;
        }

        string iconArg = pinnedIconPath != null ? $" --icon \"{pinnedIconPath}\"" : "";

        // Minimize the settings window while the companion exe is running.
        // WinUI 3's ContentDialog aggressively reclaims focus, which can
        // dismiss the taskbar right-click context menu the user needs to
        // select "Pin to taskbar".
        
        IntPtr settingsHwnd = _hwnd;
        if (settingsHwnd != IntPtr.Zero)
            NativeMethods.ShowWindow(settingsHwnd, 6 /* SW_MINIMIZE */);

        // Capture the dispatcher before leaving the UI thread
        var dispatcher = DispatcherQueue;

        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = flyoutExe,
            Arguments = $"--pin --launcher {launcher.Id} --name \"{launcher.Name}\"{iconArg}",
            UseShellExecute = false,
        });

        // Restore the settings window after the companion exe exits (user clicked OK).
        if (proc != null && settingsHwnd != IntPtr.Zero)
        {
            _ = Task.Run(async () =>
            {
                await proc.WaitForExitAsync();
                dispatcher.TryEnqueue(() =>
                {
                    NativeMethods.ShowWindow(settingsHwnd, 9 /* SW_RESTORE */);
                    NativeMethods.SetForegroundWindow(settingsHwnd);
                });
            });
        }
    }
}
