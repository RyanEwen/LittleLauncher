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

    /// <summary>Fallback height, used only if the form cannot be measured.</summary>
    private const int FallbackHeightDips = 560;

    /// <summary>Chrome around the form: title bar, body padding and the button row.</summary>
    private const int ChromeHeightDips = 130;

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly IntPtr _hwnd;
    private readonly Launcher _launcher;
    private TextBox? _nameBox;
    private TextBox? _urlBox;
    private Button? _doneButton;
    private bool _isNewLauncher;


    /// <summary>
    /// Opens launcher settings. Completes when the window closes.
    /// <paramref name="ownerHwnd"/> keeps it above an always-on-top flyout.
    /// </summary>
    /// <param name="isNewLauncher">
    /// Changes the accept button from "Done" to wording that signals a further step, since a
    /// launcher created this way goes straight on to item editing.
    /// </param>
    public static Task<bool> ShowAsync(
        Launcher launcher,
        IntPtr ownerHwnd = default,
        Action<Window>? onCreated = null,
        bool isNewLauncher = false)
    {
        var window = new LauncherSettingsWindow(launcher, ownerHwnd, isNewLauncher);
        onCreated?.Invoke(window);
        window.Activate();
        return window._completion.Task;
    }

    private LauncherSettingsWindow(Launcher launcher, IntPtr ownerHwnd, bool isNewLauncher)
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
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        doneButton.Click += (_, _) => Close();

        _isNewLauncher = isNewLauncher;
        _doneButton = doneButton;
        UpdateAcceptButton();

        var body = new Grid { Padding = new Thickness(24, 8, 24, 24) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var form = BuildForm(launcher);
        var scroller = new ScrollViewer
        {
            Content = form,
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

        // Real sizing happens once layout has produced an ActualHeight.
        form.Loaded += (_, _) =>
        {
            ResizeToContent(form);

            // A brand-new web launcher needs exactly one thing: an address. Put the caret there
            // rather than making the user find the field the window was opened for.
            if (isNewLauncher && launcher.IsWebLauncher)
                _urlBox?.Focus(FocusState.Programmatic);
        };

        // Hidden: WinUI otherwise pops an "Esc" accelerator tooltip over the window.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; Close(); };
        root.KeyboardAccelerators.Add(escape);

        Closed += (_, _) =>
        {
            CommitName();
            CommitWebUrl();
            _completion.TrySetResult(true);
        };
    }

    /// <summary>
    /// Labels the accept button for what actually happens next.
    /// </summary>
    /// <remarks>
    /// A new shortcut launcher goes straight on to item editing, so "Done" would be a lie — the
    /// user has one more step. A web launcher has no items and nothing follows, so the same
    /// wording is the opposite lie: it promises a step that does not exist, and
    /// <c>LaunchersPage</c> deliberately does not open edit mode for it. Re-run whenever the type
    /// changes, since that decides which of the two this is.
    /// </remarks>
    private void UpdateAcceptButton()
    {
        if (_doneButton == null) return;
        _doneButton.Content = _isNewLauncher && !_launcher.IsWebLauncher ? "Next: Add Items" : "Done";
    }

    /// <summary>The two text fields are the deferred edits; everything else applies as it changes.</summary>
    private void CommitName()
    {
        if (_nameBox == null) return;
        string name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name == _launcher.Name) return;

        _launcher.Name = name;
        SettingsManager.SaveSettings();
        MainWindow.Current?.RefreshTrayIcons();
        FlyoutWindow.InvalidateItems(_launcher.Id);
        WebFlyoutWindow.ApplyLauncherChanges(_launcher.Id);
    }

    /// <summary>
    /// Commits the web address and, unless the user has chosen an icon, adopts the site's icon —
    /// a composite is built from item icons, so a web launcher would otherwise sit in the tray as
    /// the generic app icon.
    /// </summary>
    /// <remarks>
    /// This is the *provisional* icon: an unauthenticated favicon fetch, which gives the launcher
    /// something immediately but comes back empty-handed for a page behind a login. The real one
    /// is whatever the page declares once it loads in the flyout
    /// (<see cref="WebFlyoutWindow.AdoptPageIconAsync"/>). Both write the same managed path, so
    /// the page icon can replace this one — and neither touches an icon the user picked.
    /// </remarks>
    private void CommitWebUrl()
    {
        if (_urlBox == null) return;
        string url = _urlBox.Text.Trim();
        if (url == _launcher.WebUrl) return;

        _launcher.WebUrl = url;
        SettingsManager.SaveSettings();
        WebFlyoutWindow.ApplyLauncherChanges(_launcher.Id);

        if (WebFlyoutWindow.MayAdoptPageIcon(_launcher) && !string.IsNullOrEmpty(url))
            _ = AdoptSiteIconAsync(_launcher, WebFlyoutWindow.NormalizeUrl(url));
    }

    private static async Task AdoptSiteIconAsync(Launcher launcher, string url)
    {
        try
        {
            string? iconPath = await Services.FaviconService.FetchAndCacheAsync(url);
            if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath)) return;

            // Copied out of the favicon cache (which is pruned on the item-icon schedule) to the
            // managed page-icon path — the distinct name is what lets the flyout tell an icon we
            // adopted from one the user chose, and so upgrade it later.
            string destPath = WebFlyoutWindow.GetPageIconPath(launcher.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(iconPath, destPath, overwrite: true);

            launcher.CustomTrayIconPath = destPath;
            launcher.TrayIconMode = TrayIconModes.Custom;
            SettingsManager.SaveSettings();
            MainWindow.Current?.UpdateTrayIcon(launcher);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Could not adopt the site icon for {Name}", launcher.Name);
        }
    }

    /// <summary>
    /// Resizes to the form's real height once layout has run.
    /// </summary>
    /// <remarks>
    /// Must happen on <c>Loaded</c>, not during construction. Before the window is shown the
    /// form has never been laid out, so its <c>DesiredSize</c> is meaningless — feeding that
    /// into the work-area clamp produced a full-height window. <c>ActualHeight</c> after layout
    /// is the real number. The form's height varies (the "Icons Per Row" row only exists in
    /// icon mode), which is why a single constant does not do.
    /// </remarks>
    private void ResizeToContent(FrameworkElement form)
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (appWindow == null) return;

        double contentHeight = form.ActualHeight > 0 ? form.ActualHeight : form.DesiredSize.Height;
        if (contentHeight <= 0) return;   // keep the fallback size

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        double heightDips = contentHeight + ChromeHeightDips;

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (area != null)
            heightDips = Math.Min(heightDips, (area.WorkArea.Height / scale) - 40);

        int width = (int)(WindowWidthDips * scale);
        int height = (int)(heightDips * scale);
        appWindow.Resize(new SizeInt32(width, height));

        if (area != null)
        {
            appWindow.Move(new PointInt32(
                area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
                area.WorkArea.Y + ((area.WorkArea.Height - height) / 2)));
        }
    }

    /// <summary>Initial size, replaced by <see cref="ResizeToContent"/> once laid out.</summary>
    private void SizeAndCentre()
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (appWindow == null) return;

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        int width = (int)(WindowWidthDips * scale);
        int height = (int)(FallbackHeightDips * scale);
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

        // ── Web launcher rows ───────────────────────────────────
        var (webRows, refreshWebRows) = BuildWebRows(launcher);

        // ── Type ─────────────────────────────────────────────────
        // Last to be built, first to be shown: it decides which of the two sets of rows above
        // is relevant, so it needs both of them to exist.
        var typeCombo = new ComboBox { MinWidth = 160 };
        var kinds = new[]
        {
            (Label: "Shortcuts", Value: LauncherKinds.Items),
            (Label: "Web page", Value: LauncherKinds.Web),
        };
        foreach (var kind in kinds)
            typeCombo.Items.Add(new ComboBoxItem { Content = kind.Label, Tag = kind.Value });
        typeCombo.SelectedIndex = Array.FindIndex(kinds, k => k.Value == LauncherKinds.Normalize(launcher.Kind));

        var itemRows = new[] { viewModeRow, iconsPerRowRow, showTitleRow };

        void UpdateKindVisibility()
        {
            bool isWeb = launcher.IsWebLauncher;
            foreach (var row in itemRows)
                row.Visibility = isWeb ? Visibility.Collapsed : Visibility.Visible;
            if (!isWeb)
                UpdateIconModeControls();   // the icons-per-row row has its own condition
            foreach (var row in webRows)
                row.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
            refreshWebRows();
            UpdateAcceptButton();
        }

        typeCombo.SelectionChanged += (s, e) =>
        {
            if (typeCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int kind)
                return;
            if (kind == LauncherKinds.Normalize(launcher.Kind))
                return;

            launcher.Kind = kind;
            SettingsManager.SaveSettings();
            // Releases the panel the launcher no longer uses and warms up the one it now does.
            MainWindow.Current?.RefreshTrayIcons();
            UpdateKindVisibility();
        };

        var typeRow = BuildRow("Type", "What the tray icon opens", typeCombo);

        UpdateKindVisibility();

        // ── Build dialog content ────────────────────────────────
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameRow);
        panel.Children.Add(typeRow);
        panel.Children.Add(iconRow);
        panel.Children.Add(customIconRow);
        panel.Children.Add(viewModeRow);
        panel.Children.Add(iconsPerRowRow);
        panel.Children.Add(showTitleRow);
        foreach (var row in webRows)
            panel.Children.Add(row);
        panel.Children.Add(hideRow);
        panel.Children.Add(taskbarRow);
        return panel;
    }

    /// <summary>
    /// Builds a settings row with the control on its own line, full width, under the label.
    /// </summary>
    /// <remarks>
    /// For controls whose content is long and open-ended — a URL, a path. The side-by-side
    /// <see cref="BuildRow"/> sizes its control column to <c>Auto</c>, so a wide control starves
    /// the label column: a long address squeezed "Web Address" into a three-character ribbon of
    /// wrapped text and stretched the box down the full height of it. Giving the control the whole
    /// width sidesteps the competition rather than trying to balance it.
    /// </remarks>
    private static FrameworkElement BuildStackedRow(string title, string subtitle, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>Builds one label/control settings row in the same shape as the rows above.</summary>
    private static Grid BuildRow(string title, string subtitle, FrameworkElement control)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        label.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        label.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        });

        // Centred, never stretched. A control with the default vertical alignment grows to the
        // row's height, and the row is as tall as its label — so a subtitle that wraps to two
        // lines silently inflates the input beside it. That is what made the "Unload After"
        // number box look oversized while the identical box on the (single-line) row above
        // looked normal.
        control.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(label, 0);
        Grid.SetColumn(control, 1);
        row.Children.Add(label);
        row.Children.Add(control);
        return row;
    }

    /// <summary>
    /// Builds the rows that only apply to a web launcher.
    /// </summary>
    /// <remarks>
    /// Only the two settings a web launcher cannot work without — its address and its size — are
    /// shown outright. Everything else (zoom, what the browser does when hidden, reload, pin,
    /// browsing data) is tuning for a launcher that already works, so it sits in a collapsed
    /// <c>Advanced</c> expander rather than making the common case read as an eight-field form.
    /// Pin is doubly safe to demote: it also has a button in the flyout's own header.
    /// </remarks>
    /// <returns>
    /// The rows, plus a callback that re-reads the launcher into the controls.
    /// </returns>
    private (IReadOnlyList<FrameworkElement> Rows, Action Refresh) BuildWebRows(Launcher launcher)
    {
        // ── Address ─────────────────────────────────────────────
        var urlBox = new TextBox
        {
            PlaceholderText = "https://homeassistant.local:8123/lovelace/cameras",
            Text = launcher.WebUrl,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Without this the box stretches to whatever height the row ends up being.
            VerticalAlignment = VerticalAlignment.Center,
        };
        _urlBox = urlBox;

        // Typing a dashboard URL from memory is the worst way to enter one; it is already a
        // bookmark in the browser the user set it up in.
        var bookmarkButton = new Button
        {
            Content = "Bookmark…",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        bookmarkButton.Click += async (_, _) =>
        {
            var picked = await Pages.BookmarkPicker.PickAsync(Content.XamlRoot);
            if (picked == null) return;

            urlBox.Text = picked.Url;

            // Committed immediately rather than on close: the name is worth adopting too, and
            // doing that silently at close would overwrite a name the user had just typed.
            CommitWebUrl();
            if (_nameBox != null && string.IsNullOrWhiteSpace(_nameBox.Text))
                _nameBox.Text = picked.Name;
        };

        var urlControls = new Grid();
        urlControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(urlBox, 0);
        Grid.SetColumn(bookmarkButton, 1);
        urlControls.Children.Add(urlBox);
        urlControls.Children.Add(bookmarkButton);

        var urlRow = BuildStackedRow("Web Address", "The page this launcher opens", urlControls);

        // ── Panel size ──────────────────────────────────────────
        var widthBox = new NumberBox
        {
            Value = launcher.ResolvedWebFlyoutWidth,
            Minimum = Launcher.MinWebFlyoutWidth,
            Maximum = Launcher.MaxWebFlyoutDimension,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
        };
        var heightBox = new NumberBox
        {
            Value = launcher.ResolvedWebFlyoutHeight,
            Minimum = Launcher.MinWebFlyoutHeight,
            Maximum = Launcher.MaxWebFlyoutDimension,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
        };

        var sizeControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        sizeControls.Children.Add(widthBox);
        sizeControls.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 });
        sizeControls.Children.Add(heightBox);

        widthBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(widthBox.Value)) return;
            launcher.WebFlyoutWidth = (int)widthBox.Value;
            SettingsManager.SaveSettings();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        heightBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(heightBox.Value)) return;
            launcher.WebFlyoutHeight = (int)heightBox.Value;
            SettingsManager.SaveSettings();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };

        var sizeRow = BuildRow("Flyout Size", "Width × height of the web flyout, in pixels", sizeControls);

        // ── Zoom ────────────────────────────────────────────────
        var zoomCombo = new ComboBox { MinWidth = 100 };
        int[] zoomLevels = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200];
        foreach (int zoom in zoomLevels)
            zoomCombo.Items.Add(new ComboBoxItem { Content = $"{zoom}%", Tag = zoom });
        int currentZoom = (int)Math.Round(launcher.ResolvedWebZoomFactor * 100);
        zoomCombo.SelectedIndex = Math.Max(0, Array.IndexOf(zoomLevels, currentZoom));
        zoomCombo.SelectionChanged += (_, _) =>
        {
            if (zoomCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int zoom) return;
            launcher.WebZoomPercent = zoom;
            SettingsManager.SaveSettings();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        var zoomRow = BuildRow("Zoom", "Page zoom inside the flyout", zoomCombo);

        // ── Hidden policy ───────────────────────────────────────
        var policyCombo = new ComboBox { MinWidth = 200 };
        var policies = new[]
        {
            (Label: "Unload when idle", Value: WebHiddenPolicies.UnloadWhenIdle),
            (Label: "Stay loaded (suspended)", Value: WebHiddenPolicies.Suspend),
            (Label: "Keep running", Value: WebHiddenPolicies.KeepRunning),
        };
        foreach (var policy in policies)
            policyCombo.Items.Add(new ComboBoxItem { Content = policy.Label, Tag = policy.Value });
        policyCombo.SelectedIndex = Array.FindIndex(policies, p => p.Value == WebHiddenPolicies.Normalize(launcher.WebHiddenPolicy));

        var policyRow = BuildRow(
            "When Hidden",
            "Unloading frees all memory and stops video and polling; staying loaded reopens instantly",
            policyCombo);

        // ── Idle delay ──────────────────────────────────────────
        var idleBox = new NumberBox
        {
            Value = launcher.ResolvedWebIdleUnloadMinutes,
            Minimum = 1,
            Maximum = 720,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
        };
        idleBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(idleBox.Value)) return;
            launcher.WebIdleUnloadMinutes = (int)idleBox.Value;
            SettingsManager.SaveSettings();
        };
        var idleRow = BuildRow("Unload After", "Minutes the flyout may sit closed before the page is dropped", idleBox);

        // Gated on the kind as well as the policy: the caller shows every web row at once, and
        // this row is the one exception that must stay hidden even then.
        void UpdateIdleVisibility()
        {
            idleRow.Visibility = launcher.IsWebLauncher &&
                WebHiddenPolicies.Normalize(launcher.WebHiddenPolicy) == WebHiddenPolicies.UnloadWhenIdle
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        policyCombo.SelectionChanged += (_, _) =>
        {
            if (policyCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int policy) return;
            launcher.WebHiddenPolicy = policy;
            SettingsManager.SaveSettings();
            UpdateIdleVisibility();
        };
        UpdateIdleVisibility();

        // ── Reload on show ──────────────────────────────────────
        var reloadToggle = new ToggleSwitch { IsOn = launcher.WebReloadOnShow, OnContent = "", OffContent = "", MinWidth = 0 };
        reloadToggle.Toggled += (_, _) =>
        {
            launcher.WebReloadOnShow = reloadToggle.IsOn;
            SettingsManager.SaveSettings();
        };
        var reloadRow = BuildRow("Reload On Open", "Fetch the page again each time, instead of showing it as you left it", reloadToggle);

        // ── Keep open on focus loss ─────────────────────────────
        var pinToggle = new ToggleSwitch { IsOn = launcher.WebPinFlyout, OnContent = "", OffContent = "", MinWidth = 0 };
        pinToggle.Toggled += (_, _) =>
        {
            launcher.WebPinFlyout = pinToggle.IsOn;
            SettingsManager.SaveSettings();
        };
        var pinRow = BuildRow("Pin Open", "Stay on screen when you click elsewhere, instead of dismissing like a flyout", pinToggle);

        // ── Sign-out / clear data ───────────────────────────────
        var clearButton = new Button { Content = "Clear" };
        clearButton.Click += async (_, _) =>
        {
            clearButton.IsEnabled = false;
            try
            {
                await WebFlyoutWindow.ClearBrowsingDataAsync(launcher);
                clearButton.Content = "Cleared";
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Clearing web launcher data failed");
                await ShowErrorAsync("Could not clear this launcher's browsing data. Close its panel and try again.");
                clearButton.Content = "Clear";
            }
            finally
            {
                clearButton.IsEnabled = true;
            }
        };
        var clearRow = BuildRow("Browsing Data", "Signs out of the page and clears its cookies and cache", clearButton);

        // ── Advanced ────────────────────────────────────────────
        var advancedPanel = new StackPanel { Spacing = 12 };
        foreach (var row in new[] { zoomRow, policyRow, idleRow, reloadRow, pinRow, clearRow })
            advancedPanel.Children.Add(row);

        var advanced = new Expander
        {
            Header = "Advanced",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = advancedPanel,
        };

        // Deliberately *not* resizing the window as this expands. Growing the window to fit
        // read as a jolt — the window jumped a beat after the expander had already animated —
        // and the form is already inside a ScrollViewer with the button row pinned below it,
        // so revealing the section just scrolls. Nothing to chase, nothing to animate.

        void Refresh()
        {
            urlBox.Text = launcher.WebUrl;
            widthBox.Value = launcher.ResolvedWebFlyoutWidth;
            heightBox.Value = launcher.ResolvedWebFlyoutHeight;
            UpdateIdleVisibility();
        }

        return ([urlRow, sizeRow, advanced], Refresh);
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
