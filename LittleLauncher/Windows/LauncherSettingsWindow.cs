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

    /// <summary>
    /// How many of these windows are open. The sync service reads it: a download while a launcher
    /// is being configured erases whatever the server has not seen yet, which for a launcher
    /// being created is all of it.
    /// </summary>
    internal static int OpenCount { get; private set; }

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly IntPtr _hwnd;
    private readonly Launcher _launcher;
    private TextBox? _nameBox;
    private TextBox? _urlBox;
    private Button? _doneButton;
    private bool _isNewLauncher;

    /// <summary>Re-reads the launcher into the icon button, so an icon that arrives is shown.</summary>
    private Action? _refreshIconPreview;

    /// <summary>
    /// The site-icon fetch started by the last address change, if it has not finished.
    /// Pinning waits on it — see <see cref="PinToTaskbar_Click"/>.
    /// </summary>
    private Task _iconAdoption = Task.CompletedTask;

    /// <summary>How long pinning will wait for a site icon before going ahead without it.</summary>
    private static readonly TimeSpan IconAdoptionWait = TimeSpan.FromSeconds(8);


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
        OpenCount++;
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
            OpenCount = Math.Max(0, OpenCount - 1);
            CommitName();
            CommitWebUrl();

            // Whatever changed in here is a launcher change, so the next periodic sync must
            // upload rather than download over it.
            Services.AutoSyncService.NotifyLaunchersChanged();
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
        Services.AutoSyncService.NotifyLaunchersChanged();
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
    /// <para>This is the *provisional* icon: an unauthenticated favicon fetch, which gives the
    /// launcher something immediately but comes back empty-handed for a page behind a login. The
    /// real one is whatever the page declares once it loads in the flyout
    /// (<see cref="WebFlyoutWindow.AdoptPageIconAsync"/>). Both write the same managed path, so
    /// the page icon can replace this one — and neither touches an icon the user picked.</para>
    /// <para>Called as soon as the address is entered — on Enter or when the field is left — and
    /// not only when the window closes. The icon is derived from the address, so anything the user
    /// might do next in this window (look at the icon, pin to the taskbar) is wrong until the
    /// fetch has run.</para>
    /// </remarks>
    private void CommitWebUrl()
    {
        if (_urlBox == null) return;
        CommitWebAddress(_urlBox.Text.Trim());
    }

    /// <summary>
    /// Points the launcher at an address, which means writing its <em>first</em> bookmark.
    /// </summary>
    /// <remarks>
    /// A web launcher is a list of bookmarks whose first entry is what it opens, so "the address"
    /// is a position rather than a field of its own. An empty list gets one; a list that already
    /// has entries has its first re-addressed, which is what the row above the list is editing.
    /// </remarks>
    private void CommitWebAddress(string url)
    {
        url = (url ?? "").Trim();
        if (url == _launcher.WebAddress) return;

        if (_launcher.WebBookmarks.Count == 0)
        {
            if (string.IsNullOrEmpty(url)) return;
            _launcher.WebBookmarks.Add(new WebBookmark(HostOf(url), url));
        }
        else
        {
            var first = _launcher.WebBookmarks[0];
            bool namedAfterItsHost = string.Equals(first.Name, HostOf(first.Url), StringComparison.OrdinalIgnoreCase);

            first.Url = url;
            first.IconPath = "";
            if (namedAfterItsHost) first.Name = HostOf(url);
        }

        SettingsManager.SaveSettings();
        WebFlyoutWindow.ApplyLauncherChanges(_launcher.Id);
        _bookmarksChanged?.Invoke();

        if (string.IsNullOrEmpty(url)) return;

        _ = WebFlyoutWindow.FetchBookmarkIconAsync(_launcher, _launcher.WebBookmarks[0]);

        if (WebFlyoutWindow.MayAdoptPageIcon(_launcher))
            _iconAdoption = AdoptAndShowSiteIconAsync(_launcher, WebFlyoutWindow.NormalizeUrl(url));
    }

    /// <summary>Re-reads the bookmark list into the editor, when one is on screen.</summary>
    private Action? _bookmarksChanged;

    /// <summary>Puts the launcher's address — its first bookmark — back in the address field.</summary>
    private void SyncAddressBox()
    {
        if (_urlBox != null) _urlBox.Text = _launcher.WebAddress;
    }

    /// <summary>Fetches the site icon and shows it in this window's icon row once it lands.</summary>
    private async Task AdoptAndShowSiteIconAsync(Launcher launcher, string url)
    {
        await AdoptSiteIconAsync(launcher, url);

        // The window may have been closed while the fetch was in flight.
        DispatcherQueue?.TryEnqueue(() => _refreshIconPreview?.Invoke());
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
            Services.AutoSyncService.NotifyLaunchersChanged();
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

        // Committed as soon as the field is finished with, like the address below it. Both used to
        // wait for the window to close, which meant every other button in the window — Pin to
        // Taskbar above all, since a pinned name and icon are baked once and never re-read — acted
        // on the name the user had just replaced.
        nameBox.LostFocus += (_, _) => CommitName();
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            CommitName();
        };

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
        var (iconButton, customIconRow, refreshIconPreview) = BuildIconChooser(launcher);
        _refreshIconPreview = refreshIconPreview;

        // ── Back to the page's own icon ──────────────────────────
        // A web launcher adopts its page's icon until someone picks one, and picking one was a
        // one-way door: every route out of Custom leads to another *chosen* icon, so there was no
        // way back to "whatever the page says". This is that way back, and it only appears when
        // there is something to undo.
        var resetIconButton = new Button
        {
            Content = "Use page icon",
            Padding = new Thickness(10, 6, 10, 6),
            Visibility = launcher.IsWebLauncher && !WebFlyoutWindow.MayAdoptPageIcon(launcher)
                ? Visibility.Visible
                : Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(resetIconButton, "Go back to the icon this launcher's page declares");
        resetIconButton.Click += (_, _) =>
        {
            // The adopted state is Custom pointing at the managed page-icon path — that is exactly
            // what MayAdoptPageIcon recognises, and it restores the real icon immediately when one
            // has already been fetched. With nothing fetched yet, Composite is the "never chosen"
            // state, so the next load adopts.
            string adopted = WebFlyoutWindow.GetPageIconPath(launcher.Id);
            if (File.Exists(adopted))
            {
                launcher.TrayIconMode = TrayIconModes.Custom;
                launcher.CustomTrayIconPath = adopted;
            }
            else
            {
                launcher.TrayIconMode = TrayIconModes.Composite;
                launcher.CustomTrayIconPath = "";
            }

            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            resetIconButton.Visibility = Visibility.Collapsed;
            refreshIconPreview();
        };

        var iconRow = new Grid();
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        iconButtons.Children.Add(resetIconButton);
        iconButtons.Children.Add(iconButton);

        var iconLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        iconLabel.Children.Add(new TextBlock { Text = "Icon", FontSize = 14 });
        // Text set by UpdateKindVisibility — a web launcher's icon arrives on its own.
        var iconSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };
        iconLabel.Children.Add(iconSubtitle);
        Grid.SetColumn(iconLabel, 0);
        Grid.SetColumn(iconButtons, 1);
        iconRow.Children.Add(iconLabel);
        iconRow.Children.Add(iconButtons);

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
            Services.AutoSyncService.NotifyLaunchersChanged();
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
            Services.AutoSyncService.NotifyLaunchersChanged();
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
            Services.AutoSyncService.NotifyLaunchersChanged();
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
            Services.AutoSyncService.NotifyLaunchersChanged();
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
        var (webAddressRows, webOptionRows, webAdvanced, refreshWebRows) = BuildWebRows(launcher);

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
            foreach (var row in webAddressRows.Concat(webOptionRows).Append(webAdvanced))
                row.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
            refreshWebRows();
            iconSubtitle.Text = isWeb
                ? "Taken from the page automatically — or pick one here"
                : "Icon style for this launcher";
            refreshIconPreview();
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
            Services.AutoSyncService.NotifyLaunchersChanged();
            // Releases the panel the launcher no longer uses and warms up the one it now does.
            MainWindow.Current?.RefreshTrayIcons();
            UpdateKindVisibility();
        };

        var typeRow = BuildRow("Type", "What the tray icon opens", typeCombo);

        UpdateKindVisibility();

        // ── Build dialog content ────────────────────────────────
        // Ordered as three questions, then the fold: what is this launcher, how does it show its
        // content, and how does it appear in the shell.
        var panel = new StackPanel { Spacing = 12 };

        // ── What it is ──────────────────────────────────────────
        panel.Children.Add(nameRow);
        panel.Children.Add(typeRow);

        // Its content leads, and for a web launcher the address has to come before the icon
        // anyway: the icon is *derived* from it, and asked the other way round the form made the
        // user choose an icon for a page it had not been told about, which reads as a requirement
        // rather than an override.
        foreach (var row in webAddressRows)
            panel.Children.Add(row);

        // ── How it shows that content ───────────────────────────
        panel.Children.Add(viewModeRow);
        panel.Children.Add(iconsPerRowRow);
        panel.Children.Add(showTitleRow);
        foreach (var row in webOptionRows)
            panel.Children.Add(row);

        // ── How it appears in the shell ─────────────────────────
        // The icon sits with the two rows that decide where it is *seen*. It used to sit up beside
        // the launcher's content, where "Icon" read as the icon of the thing being configured
        // rather than as the tray icon — which is the only place it appears.
        panel.Children.Add(iconRow);
        panel.Children.Add(customIconRow);
        panel.Children.Add(hideRow);
        panel.Children.Add(taskbarRow);

        // ── Everything else ─────────────────────────────────────
        // Last in the dialog, not last among the web rows. Advanced is the fold for the settings a
        // working launcher does not need, so anything below it would be something the user had to
        // scroll past a collapsed section to find.
        panel.Children.Add(webAdvanced);
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
    private static Grid BuildRow(string title, string subtitle, FrameworkElement control) =>
        BuildRow(title, new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        }, control);

    /// <summary>
    /// As above, for a row whose subtitle is rewritten later — the caller keeps the
    /// <see cref="TextBlock"/> and sets its text.
    /// </summary>
    private static Grid BuildRow(string title, TextBlock subtitle, FrameworkElement control)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        label.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        label.Children.Add(subtitle);

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
    /// Builds the bookmark editor: the list, reordering, removal, and the two ways to add one.
    /// </summary>
    /// <remarks>
    /// <para>This is the launcher's content, not an extra: the first bookmark is the page the tray
    /// icon opens, and the rest are the bar. That is stated on the first row rather than left for
    /// the user to infer, because reordering the list changes what the launcher opens.</para>
    /// <para>A second bookmark is what makes the bar appear, which the row's subtitle says for the
    /// same reason — it is a visible change the user is one click away from causing.</para>
    /// </remarks>
    private FrameworkElement BuildBookmarksRow(Launcher launcher)
    {
        var list = new StackPanel { Spacing = 4 };

        var urlBox = new TextBox
        {
            PlaceholderText = "https://…",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var addButton = new Button { Content = "Add", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var pickButton = new Button { Content = "From browser…", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        void Persist()
        {
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        }

        void Rebuild()
        {
            list.Children.Clear();

            if (launcher.WebBookmarks.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "No bookmarks yet.",
                    FontSize = 12,
                    Opacity = 0.5,
                });
            }

            foreach (var bookmark in launcher.WebBookmarks.ToList())
            {
                var captured = bookmark;
                bool isAddress = launcher.WebBookmarks.IndexOf(captured) == 0;

                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(captured.Name) ? captured.Url : captured.Name,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                label.Children.Add(new TextBlock
                {
                    Text = captured.Url,
                    FontSize = 11,
                    Opacity = 0.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                // Said on the row rather than in a legend: the only thing that makes this bookmark
                // the launcher's address is being first, so it has to be readable from the order.
                if (isAddress)
                {
                    label.Children.Add(new TextBlock
                    {
                        Text = "Opens with the launcher",
                        FontSize = 11,
                        Opacity = 0.6,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    });
                }

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

                Button Small(string glyph, string tooltip, Action onClick, bool enabled = true)
                {
                    var b = new Button
                    {
                        Content = new FontIcon { Glyph = glyph, FontSize = 11 },
                        Padding = new Thickness(6, 4, 6, 4),
                        MinWidth = 0,
                        MinHeight = 0,
                        IsEnabled = enabled,
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                        BorderThickness = new Thickness(0),
                    };
                    ToolTipService.SetToolTip(b, tooltip);
                    b.Click += (_, _) => onClick();
                    return b;
                }

                int index = launcher.WebBookmarks.IndexOf(captured);

                // A bookmark is named from its host when it is added, which is rarely what anyone
                // would call it — and with an icons-only bar the name becomes the tooltip, so it is
                // the only thing identifying the button.
                buttons.Children.Add(Small("", "Rename", async () =>
                {
                    string? renamed = await TextPromptWindow.ShowAsync(
                        "Rename bookmark", "Name", captured.Name, "Rename", _hwnd);
                    if (renamed == null) return;

                    captured.Name = string.IsNullOrWhiteSpace(renamed) ? HostOf(captured.Url) : renamed.Trim();
                    Persist();
                    Rebuild();
                }));
                buttons.Children.Add(Small("", "Edit address", async () =>
                {
                    string? entered = await TextPromptWindow.ShowAsync(
                        "Edit address", "https://…", captured.Url, "Save", _hwnd);
                    if (entered == null) return;

                    string edited = WebFlyoutWindow.NormalizeUrl(entered);
                    if (string.IsNullOrEmpty(edited) || edited == captured.Url) return;

                    // The address row above edits the same bookmark when this is the first one, so
                    // that path owns it — including the site-icon fetch a new address needs.
                    if (launcher.WebBookmarks.IndexOf(captured) == 0)
                    {
                        CommitWebAddress(edited);
                        return;
                    }

                    bool namedAfterItsHost = string.Equals(captured.Name, HostOf(captured.Url), StringComparison.OrdinalIgnoreCase);
                    captured.Url = edited;
                    captured.IconPath = "";
                    if (namedAfterItsHost) captured.Name = HostOf(edited);

                    Persist();
                    Rebuild();
                    _ = FetchBookmarkIconAsync(launcher, captured);
                }));
                buttons.Children.Add(Small("", "Move up", () =>
                {
                    int i = launcher.WebBookmarks.IndexOf(captured);
                    if (i <= 0) return;
                    launcher.WebBookmarks.Move(i, i - 1);
                    Persist();
                    Rebuild();
                    _bookmarksListChanged?.Invoke();
                    SyncAddressBox();
                }, index > 0));
                buttons.Children.Add(Small("", "Move down", () =>
                {
                    int i = launcher.WebBookmarks.IndexOf(captured);
                    if (i < 0 || i >= launcher.WebBookmarks.Count - 1) return;
                    launcher.WebBookmarks.Move(i, i + 1);
                    Persist();
                    Rebuild();
                    _bookmarksListChanged?.Invoke();
                    SyncAddressBox();
                }, index >= 0 && index < launcher.WebBookmarks.Count - 1));
                buttons.Children.Add(Small("", "Remove", () =>
                {
                    launcher.WebBookmarks.Remove(captured);
                    Persist();
                    Rebuild();

                    // Removing or reordering the first one changes the launcher's address, so the
                    // field above the list is now describing a bookmark that is not there.
                    SyncAddressBox();
                }));

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(label, 0);
                Grid.SetColumn(buttons, 1);
                row.Children.Add(label);
                row.Children.Add(buttons);
                list.Children.Add(row);
            }
        }

        void Add(string name, string url)
        {
            url = WebFlyoutWindow.NormalizeUrl(url);
            if (string.IsNullOrWhiteSpace(url)) return;

            var bookmark = new WebBookmark(string.IsNullOrWhiteSpace(name) ? HostOf(url) : name, url);
            launcher.WebBookmarks.Add(bookmark);
            Persist();
            Rebuild();
            _bookmarksListChanged?.Invoke();
            SyncAddressBox();

            // A provisional icon so the bar is not a row of globes; the real one replaces it the
            // first time the bookmark is opened with a signed-in browser.
            _ = FetchBookmarkIconAsync(launcher, bookmark);
        }

        addButton.Click += (_, _) =>
        {
            Add("", urlBox.Text.Trim());
            urlBox.Text = "";
        };

        pickButton.Click += async (_, _) =>
        {
            var picked = await Pages.BookmarkPicker.PickAsync(Content.XamlRoot);
            if (picked == null) return;
            Add(picked.Name, picked.Url);
        };

        var addRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(urlBox, 0);
        Grid.SetColumn(addButton, 1);
        Grid.SetColumn(pickButton, 2);
        addRow.Children.Add(urlBox);
        addRow.Children.Add(addButton);
        addRow.Children.Add(pickButton);

        // ── Bar appearance ──────────────────────────────────────
        // ── Bar appearance ──────────────────────────────────────
        var iconsOnlyToggle = new ToggleSwitch
        {
            IsOn = launcher.WebBookmarkIconsOnly,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconsOnlyToggle.Toggled += (_, _) =>
        {
            launcher.WebBookmarkIconsOnly = iconsOnlyToggle.IsOn;
            Persist();
        };

        var iconsOnlyRow = BuildRow("Icons Only",
            "Hide the labels in the bar; names still show as tooltips",
            iconsOnlyToggle);
        iconsOnlyRow.Margin = new Thickness(0, 12, 0, 0);

        var body = new StackPanel();
        body.Children.Add(list);
        body.Children.Add(addRow);
        body.Children.Add(iconsOnlyRow);

        // Folded away, like Advanced. A launcher with one page — which is most of them — has a
        // bookmark list of exactly the address already showing in the field above, so laid out flat
        // it was a second copy of the answer taking up most of the dialog.
        var headerLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headerLabel.Children.Add(new TextBlock { Text = "Bookmarks", FontSize = 14 });

        // The subtitle is what has to survive the fold: collapsed, it is the only thing saying that
        // a second page is what produces the bar, and it counts what is inside so the section is
        // worth opening (or worth leaving shut) without opening it.
        var headerSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };
        headerLabel.Children.Add(headerSubtitle);

        void UpdateHeader()
        {
            int count = launcher.WebBookmarks.Count;
            headerSubtitle.Text = count > 1
                ? $"{count} pages, shown as a bar along the bottom of the flyout"
                : "The first is the page this launcher opens. Add another and they show as a bar along the bottom";
        }

        Rebuild();
        UpdateHeader();

        // So the address field above the list follows an edit made down here.
        _bookmarksChanged = () => { Rebuild(); UpdateHeader(); SyncAddressBox(); };
        _bookmarksListChanged = UpdateHeader;

        var bookmarks = new Expander
        {
            Header = headerLabel,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = body,
        };

        ExpanderReveal.Attach(bookmarks);
        return bookmarks;
    }

    /// <summary>Re-reads the bookmark count into the section header, when one is on screen.</summary>
    private Action? _bookmarksListChanged;

    /// <summary>
    /// Host of a URL, used as a bookmark's name until the page offers a better one.
    /// </summary>
    /// <remarks>
    /// Both of these live on <see cref="WebFlyoutWindow"/> because the flyout's own bookmark
    /// editing needs them too, and a bookmark named one way here and another way there would be a
    /// difference with no reason behind it.
    /// </remarks>
    private static string HostOf(string url) => WebFlyoutWindow.HostOf(url);

    /// <inheritdoc cref="WebFlyoutWindow.FetchBookmarkIconAsync"/>
    private static Task FetchBookmarkIconAsync(Launcher launcher, WebBookmark bookmark) =>
        WebFlyoutWindow.FetchBookmarkIconAsync(launcher, bookmark);

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
    /// The rows that say <em>what page</em>, the rows that say <em>how to show it</em>, the
    /// Advanced fold, and a callback that re-reads the launcher into the controls. All four are
    /// returned separately because <c>BuildForm</c> interleaves them with rows of its own —
    /// Advanced in particular goes last in the whole dialog, below even the tray and taskbar rows.
    /// </returns>
    private (IReadOnlyList<FrameworkElement> AddressRows, IReadOnlyList<FrameworkElement> OptionRows,
             FrameworkElement Advanced, Action Refresh)
        BuildWebRows(Launcher launcher)
    {
        // ── Address ─────────────────────────────────────────────
        var urlBox = new TextBox
        {
            PlaceholderText = "https://homeassistant.local:8123/lovelace/cameras",
            Text = launcher.WebAddress,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Without this the box stretches to whatever height the row ends up being.
            VerticalAlignment = VerticalAlignment.Center,
        };
        _urlBox = urlBox;

        // Committed as soon as the address is finished with, rather than only on close: the icon
        // is fetched from it, and until that has happened the launcher is still wearing a globe —
        // which is what a user who pins to the taskbar from this window would get stuck with,
        // since Windows never re-reads a pinned icon.
        urlBox.LostFocus += (_, _) => CommitWebUrl();
        urlBox.KeyDown += (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            CommitWebUrl();
        };

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

        // Still the first field of the form, because it is the one a launcher cannot work without
        // and the one a new launcher is opened to fill in. It edits the first bookmark; the list
        // below shows the same entry marked as the address, so neither is a second source of truth.
        var urlRow = BuildStackedRow("Web Address", "The page this launcher opens", urlControls);
        var bookmarksRow = BuildBookmarksRow(launcher);

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
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        heightBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(heightBox.Value)) return;
            launcher.WebFlyoutHeight = (int)heightBox.Value;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
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
            Services.AutoSyncService.NotifyLaunchersChanged();
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
            Services.AutoSyncService.NotifyLaunchersChanged();
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
            Services.AutoSyncService.NotifyLaunchersChanged();
            UpdateIdleVisibility();
        };
        UpdateIdleVisibility();

        // ── Reload on show ──────────────────────────────────────
        var reloadToggle = new ToggleSwitch { IsOn = launcher.WebReloadOnShow, OnContent = "", OffContent = "", MinWidth = 0 };
        reloadToggle.Toggled += (_, _) =>
        {
            launcher.WebReloadOnShow = reloadToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var reloadRow = BuildRow("Reload On Open", "Fetch the page again each time, instead of showing it as you left it", reloadToggle);

        // ── Links in the real browser ───────────────────────────
        // Its only home. It used to live in the flyout's More menu and nowhere else, which made it
        // unfindable for anyone who had not opened that menu — and it is not a per-moment decision
        // in the way the menu's other items are: how this launcher treats links is a property of
        // the launcher, settled once.
        //
        // Named for what turning it on does rather than for the default it switches off, because
        // "open links in tabs" is already what the launcher does.
        var linksToggle = new ToggleSwitch { IsOn = launcher.WebLinksInBrowser, OnContent = "", OffContent = "", MinWidth = 0 };
        linksToggle.Toggled += (_, _) =>
        {
            launcher.WebLinksInBrowser = linksToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var linksRow = BuildRow("Open Links In Browser",
            "Hand a link that opens a new window to your default browser, instead of opening it in a tab here",
            linksToggle);

        // ── Address bar ─────────────────────────────────────────
        var addressToggle = new ToggleSwitch { IsOn = launcher.WebShowAddressBar, OnContent = "", OffContent = "", MinWidth = 0 };
        addressToggle.Toggled += (_, _) =>
        {
            launcher.WebShowAddressBar = addressToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        // Not in Advanced: it is one of the few things that changes what the flyout *is* — a page
        // versus a small browser — rather than tuning one that already works, and it is the setting
        // the star for bookmarking the current page lives in.
        var addressRow = BuildRow("Address Bar",
            "Show the page address under the header, with a star for bookmarking it",
            addressToggle);

        // ── Tab bar ─────────────────────────────────────────────
        // Beside the address bar, because they are the same question asked twice: which rows of
        // chrome does this launcher keep. It was reachable only from the flyout's "…" menu, which
        // is the wrong place to be the *only* place — the menu is for changing your mind while
        // looking at a launcher, not for finding out that an option exists.
        //
        // **Listed before the address bar**, in both the returned rows and the More menu,
        // because that is the order the two strips appear in on the flyout: header, then tabs,
        // then the address of whichever tab they chose. Two toggles for two adjacent rows read
        // as mislabelled when the list disagrees with what is on screen.
        var tabBarToggle = new ToggleSwitch { IsOn = launcher.WebAlwaysShowTabs, OnContent = "", OffContent = "", MinWidth = 0 };
        tabBarToggle.Toggled += (_, _) =>
        {
            launcher.WebAlwaysShowTabs = tabBarToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        var tabBarRow = BuildRow("Tab Bar",
            "Keep the tab strip on screen with one page open. Off, it appears as soon as there is a second",
            tabBarToggle);

        // ── Keep open on focus loss ─────────────────────────────
        var pinToggle = new ToggleSwitch { IsOn = launcher.WebPinFlyout, OnContent = "", OffContent = "", MinWidth = 0 };
        pinToggle.Toggled += (_, _) =>
        {
            launcher.WebPinFlyout = pinToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var pinRow = BuildRow("Pin Open", "Stay on screen when you click elsewhere, instead of dismissing like a flyout", pinToggle);

        // ── Regular window ──────────────────────────────────────
        var regularToggle = new ToggleSwitch { IsOn = launcher.WebRegularWindow, OnContent = "", OffContent = "", MinWidth = 0 };
        regularToggle.Toggled += (_, _) =>
        {
            launcher.WebRegularWindow = regularToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        // Names the window kind rather than any one symptom of it. "Show in taskbar" would be the
        // obvious label and would be a promise the shell cannot keep: the taskbar button and the
        // Alt-Tab entry are the same switch, so a launcher cannot have one without the other.
        var regularRow = BuildRow("Regular Window",
            "Behave like an ordinary app window: taskbar button, Alt-Tab entry, not always on top, and stays open until closed",
            regularToggle);

        // ── What the taskbar button's click does ────────────────
        var clickCombo = new ComboBox { MinWidth = 200 };
        clickCombo.Items.Add(new ComboBoxItem { Content = "Minimize it", Tag = false });
        clickCombo.Items.Add(new ComboBoxItem { Content = "Close it", Tag = true });
        clickCombo.SelectedIndex = launcher.WebTaskbarClickCloses ? 1 : 0;
        clickCombo.SelectionChanged += (_, _) =>
        {
            if (clickCombo.SelectedItem is not ComboBoxItem { Tag: bool closes }) return;
            launcher.WebTaskbarClickCloses = closes;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var clickRow = BuildRow("Taskbar Click",
            "What clicking this launcher's taskbar button does while it is open",
            clickCombo);

        // ── Close on focus loss, as a window ────────────────────
        var autoHideToggle = new ToggleSwitch { IsOn = launcher.WebWindowAutoHide, OnContent = "", OffContent = "", MinWidth = 0 };
        autoHideToggle.Toggled += (_, _) =>
        {
            launcher.WebWindowAutoHide = autoHideToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var autoHideRow = BuildRow("Close On Focus Loss",
            "Dismiss like a flyout when you click elsewhere, while keeping the taskbar button and Alt-Tab entry",
            autoHideToggle);

        // Both only mean anything with Regular Window on — a flyout has no taskbar button to click,
        // and already dismisses on focus loss unless pinned. They follow that toggle rather than
        // sitting there inert. Shown and hidden rather than disabled: a greyed row still reads as a
        // setting you are missing out on.
        void UpdateWindowRowVisibility()
        {
            var visibility = launcher.WebRegularWindow ? Visibility.Visible : Visibility.Collapsed;
            clickRow.Visibility = visibility;
            autoHideRow.Visibility = visibility;
        }
        regularToggle.Toggled += (_, _) => UpdateWindowRowVisibility();
        UpdateWindowRowVisibility();

        // ── Opening position ────────────────────────────────────
        var anchorCombo = new ComboBox { MinWidth = 200 };
        (string Label, int Value)[] anchors =
        [
            ("Near its tray icon", WebAnchors.Tray),
            // Second, beside the other answer that is not a fixed spot, and named for the gesture
            // that sets it — so the list says how to change it as well as what it does.
            ("Where you last dragged it", WebAnchors.LastPosition),
            ("Top left", WebAnchors.TopLeft),
            ("Top centre", WebAnchors.TopCenter),
            ("Top right", WebAnchors.TopRight),
            ("Left", WebAnchors.Left),
            ("Centre", WebAnchors.Center),
            ("Right", WebAnchors.Right),
            ("Bottom left", WebAnchors.BottomLeft),
            ("Bottom centre", WebAnchors.BottomCenter),
            ("Bottom right", WebAnchors.BottomRight),
        ];
        foreach (var a in anchors)
            anchorCombo.Items.Add(new ComboBoxItem { Content = a.Label, Tag = a.Value });
        anchorCombo.SelectedIndex = Array.FindIndex(anchors, a => a.Value == WebAnchors.Normalize(launcher.WebAnchor));

        var anchorSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };

        void UpdateAnchorText() => anchorSubtitle.Text =
            WebAnchors.Normalize(launcher.WebAnchor) == WebAnchors.LastPosition
                ? "Drag the flyout anywhere and it opens there next time"
                : "Where it opens on the screen holding its tray icon. A move lasts until you close it";

        anchorCombo.SelectionChanged += (_, _) =>
        {
            if (anchorCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int anchor) return;
            if (anchor == WebAnchors.Normalize(launcher.WebAnchor)) return;

            launcher.WebAnchor = anchor;

            // Any answer but "where you last dragged it" forgets where that was — otherwise picking
            // a corner would leave the flyout opening exactly where it did before, and switching
            // back would restore a position the user had stopped using.
            if (anchor != WebAnchors.LastPosition) launcher.WebFlyoutPosition = "";

            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            UpdateAnchorText();
        };

        var anchorRow = BuildRow("Opens At", anchorSubtitle, anchorCombo);

        // ── Remember size ───────────────────────────────────────
        // Stored inverted (WebLockSize) because this one is ON by default, and a bool defaulting
        // to true cannot be turned off under WhenWritingDefault.
        var rememberSizeToggle = new ToggleSwitch { IsOn = !launcher.WebLockSize, OnContent = "", OffContent = "", MinWidth = 0 };
        rememberSizeToggle.Toggled += (_, _) =>
        {
            launcher.WebLockSize = !rememberSizeToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var rememberSizeRow = BuildRow("Remember Size Changes",
            "Keep this flyout at the size you drag it to; otherwise it reopens at the size above",
            rememberSizeToggle);

        // Set once the password row below exists; the profile combo above has to re-read it, and
        // that handler is written before the row it refers to.
        Action? refreshPasswordRow = null;

        // ── Profile ─────────────────────────────────────────────
        // A combo rather than a toggle: "shared with other launchers" is a statement about where
        // the sign-ins live, and naming both ends of it beats an unlabelled switch.
        //
        // Shared leads, being what a new launcher gets. The order is display only — the value is
        // read off each item's Tag, never off the index.
        var profileCombo = new ComboBox { MinWidth = 200 };
        profileCombo.Items.Add(new ComboBoxItem { Content = "Shared with other launchers", Tag = true });
        profileCombo.Items.Add(new ComboBoxItem { Content = "Private to this launcher", Tag = false });
        profileCombo.SelectedIndex = launcher.WebSharedProfile ? 0 : 1;

        var clearSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };

        void UpdateProfileText() => clearSubtitle.Text = launcher.WebSharedProfile
            ? "Signs out of the shared profile — every launcher using it"
            : "Signs out of the page and clears its cookies and cache";

        profileCombo.SelectionChanged += (_, _) =>
        {
            if (profileCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not bool shared) return;
            if (shared == launcher.WebSharedProfile) return;

            launcher.WebSharedProfile = shared;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();

            // The folder is bound when the browser is created, so the running one is still on the
            // old profile until it is dropped.
            WebFlyoutWindow.ReloadProfile(launcher.Id);
            UpdateProfileText();

            // The profile changed, so both the wording and the value belong to a different profile
            // now — a launcher moved onto the shared one adopts whatever the shared one is set to.
            // Through a hook, because that row is built below this handler.
            refreshPasswordRow?.Invoke();
        };

        var profileRow = BuildRow("Sign-ins",
            "Pooled with every launcher set to share, so one sign-in covers them all. Private keeps this "
            + "launcher's cookies to itself — for a second account on a site another launcher already uses",
            profileCombo);

        // ── Saved logins ────────────────────────────────────────
        // Beside Sign-ins deliberately: both are properties of the *profile*, not the launcher, so a
        // launcher on the shared profile is setting this for every launcher on it. Saved passwords
        // live in the profile, so the switch that governs them has to be scoped the same way.
        var passwordToggle = new ToggleSwitch
        {
            IsOn = WebFlyoutWindow.UsesBuiltInPasswordManager(launcher),
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var passwordSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };

        void UpdatePasswordText() => passwordSubtitle.Text = launcher.WebSharedProfile
            ? "Offer to save logins and fill them in, for every launcher on the shared profile. Turn off "
              + "when a password manager extension is doing it, so the two do not compete"
            : "Offer to save logins and fill them in. Turn off when a password manager extension is "
              + "doing it, so the two do not compete";

        passwordToggle.Toggled += (_, _) =>
        {
            WebFlyoutWindow.SetBuiltInPasswordManager(launcher, passwordToggle.IsOn);

            // The setting is read when a browser starts, so a launcher already open keeps the old
            // behaviour until it is reopened. Dropping its browser is what makes that immediate.
            WebFlyoutWindow.ReloadProfile(launcher.Id);
        };

        UpdatePasswordText();
        var passwordRow = BuildRow("Save Logins", passwordSubtitle, passwordToggle);

        refreshPasswordRow = () =>
        {
            UpdatePasswordText();
            passwordToggle.IsOn = WebFlyoutWindow.UsesBuiltInPasswordManager(launcher);
        };

        var forgetLoginsButton = new Button { Content = "Forget" };
        forgetLoginsButton.Click += async (_, _) =>
        {
            forgetLoginsButton.IsEnabled = false;

            // All of them, because WebView2 exposes no way to enumerate saved passwords — only to
            // clear the category. The button is named for what it can actually do.
            bool cleared = await WebFlyoutWindow.ClearSavedPasswordsAsync(launcher);
            forgetLoginsButton.Content = cleared ? "Forgotten" : "Forget";

            if (!cleared)
                await ShowErrorAsync("Open this launcher first — its saved logins can only be cleared while its browser is running.");

            forgetLoginsButton.IsEnabled = true;
        };

        var forgetLoginsRow = BuildRow("Saved Logins",
            "Forget every login and form entry saved for this profile. Logins kept by a password "
            + "manager extension are not affected",
            forgetLoginsButton);

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
        UpdateProfileText();
        var clearRow = BuildRow("Browsing Data", clearSubtitle, clearButton);

        // ── Site permissions ────────────────────────────────────
        // The flyout asks by default, the same as a browser. This is for the launcher whose page
        // asks constantly and is trusted anyway — a dashboard that wants the camera on every load.
        var trustToggle = new ToggleSwitch { IsOn = launcher.WebAllowAllPermissions, OnContent = "", OffContent = "", MinWidth = 0 };
        trustToggle.Toggled += (_, _) =>
        {
            launcher.WebAllowAllPermissions = trustToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();

            // Trusting the site writes its grants into the profile so the page can actually see
            // them, so untrusting it has to take them back out — otherwise the launcher would keep
            // silently allowing everything with the toggle off. See WebFlyoutWindow.Permissions.
            if (!trustToggle.IsOn)
                _ = WebFlyoutWindow.ClearOnTrustDisabledAsync(launcher.Id);
        };
        var trustRow = BuildRow("Trust This Site",
            "Give this launcher's pages the camera, microphone, location and notifications without asking",
            trustToggle);

        // ── Reset stored answers ────────────────────────────────
        // Without this, an accidental Block could only be undone by clearing the whole profile,
        // which also signs the launcher out.
        var permissionsSubtitle = new TextBlock
        {
            Text = "Forget what you have allowed and blocked, so this launcher's pages ask again",
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        };
        var resetPermissionsButton = new Button { Content = "Reset" };
        resetPermissionsButton.Click += async (_, _) =>
        {
            resetPermissionsButton.IsEnabled = false;
            try
            {
                // The stored answers live in the WebView2 profile, which is only reachable through
                // a running browser — so a launcher that is not loaded has this applied when it
                // next opens, and is told so rather than being shown a false "Reset".
                bool applied = await WebFlyoutWindow.ResetSitePermissionsAsync(launcher.Id);
                resetPermissionsButton.Content = "Reset";
                permissionsSubtitle.Text = applied
                    ? "Cleared. This launcher's pages will ask again."
                    : "Will be cleared the next time this launcher opens.";
            }
            finally
            {
                resetPermissionsButton.IsEnabled = true;
            }
        };
        var resetPermissionsRow = BuildRow("Site Permissions", permissionsSubtitle, resetPermissionsButton);

        // ── Advanced ────────────────────────────────────────────
        var advancedPanel = new StackPanel { Spacing = 12 };
        // Opens At / Remember Position / Remember Size are deliberately *not* here — they sit with
        // Flyout Size in the main form. They answer the same question it does ("where and how big
        // does this open?"), they are the ones a user reaches for after dragging a flyout and
        // finding the change did not stick, and Opens At's subtitle describes its interaction with
        // Remember Position, which would read oddly with the two separated by the Advanced fold.
        foreach (var row in new[] { zoomRow, policyRow, idleRow, reloadRow, linksRow, pinRow, regularRow, autoHideRow, clickRow, trustRow, resetPermissionsRow, profileRow, passwordRow, forgetLoginsRow, clearRow })
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
        //
        // The scroll does have to be *made*, though: this section is last in the dialog by
        // design, so expanding it always happens at the bottom of the scroller and everything
        // it reveals lands below the fold. See ExpanderReveal.
        ExpanderReveal.Attach(advanced);

        void Refresh()
        {
            UpdateProfileText();
            UpdateAnchorText();
            urlBox.Text = launcher.WebAddress;
            widthBox.Value = launcher.ResolvedWebFlyoutWidth;
            heightBox.Value = launcher.ResolvedWebFlyoutHeight;
            UpdateIdleVisibility();
        }

        return ([urlRow, bookmarksRow], [tabBarRow, addressRow, sizeRow, anchorRow, rememberSizeRow], advanced, Refresh);
    }

    private (Button Button, Grid CustomRow, Action Refresh) BuildIconChooser(Launcher launcher)
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

            // A web launcher wears the icon of the page it opens until someone picks one instead
            // (WebFlyoutWindow.MayAdoptPageIcon). Neither of the two modes that state describes
            // reads as that: "Composite" composes item icons it hasn't got, and "Custom" names a
            // file the user never chose. Both are shown as what they actually are.
            if (launcher.IsWebLauncher && WebFlyoutWindow.MayAdoptPageIcon(launcher))
            {
                string adopted = WebFlyoutWindow.GetPageIconPath(launcher.Id);
                if (mode == TrayIconModes.Custom && File.Exists(adopted))
                {
                    var bitmap = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache };
                    bitmap.UriSource = new Uri(adopted);
                    previewImage.Source = bitmap;
                    previewImage.Visibility = Visibility.Visible;
                }
                else
                {
                    previewIcon.Glyph = "";   // globe — nothing has loaded yet
                    previewIcon.Visibility = Visibility.Visible;
                }
                previewLabel.Text = "From the page";
                customIconRow.Visibility = Visibility.Collapsed;
                return;
            }

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
            Services.AutoSyncService.NotifyLaunchersChanged();
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
            Services.AutoSyncService.NotifyLaunchersChanged();
                    UpdatePreview();
                }
            }
        );

        button.Flyout = flyout;
        UpdatePreview();

        return (button, customIconRow, UpdatePreview);
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
            Services.AutoSyncService.NotifyLaunchersChanged();
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

    /// <remarks>
    /// A pinned icon is baked once: Windows never re-reads it, so anything not resolved by the
    /// time this runs stays wrong until the user unpins and pins again. The two deferred fields
    /// are therefore committed first, and a site-icon fetch they start is waited for — a web
    /// launcher pinned the moment its address was typed used to pin the generic app icon.
    /// </remarks>
    private async void PinToTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;

        CommitName();
        CommitWebUrl();
        await WaitForIconAdoptionAsync(sender as Button);

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

        // Mint the pin's AUMID here rather than letting the companion do it, and record it on the
        // launcher. It is the identity the taskbar groups the pinned button under, and a window
        // that wants to light that button has to carry the identical string — so something has to
        // remember it, and only this side can.
        //
        // The app used to read it back out of the taskbar's own pin store
        // (HKCU\...\Explorer\Taskband). That works for some pins and not others: measured on a
        // machine with eleven Little Launcher pins, those blobs held the AUMIDs of eight of them,
        // and WhatsApp, Messenger and Web Launcher appeared in neither. Re-pinning did not add
        // them. A window whose AUMID does not match its pin does not fail quietly — it raises a
        // *second* taskbar button beside the pin, which is how that was noticed.
        //
        // Still stamped with a tick, for the reason the companion did it: the AUMID must be unique
        // per pin attempt to bust Windows' per-AUMID icon cache.
        launcher.PinAumid = $"LittleLauncher.Launcher.{launcher.Id}.{Environment.TickCount64}";
        SettingsManager.SaveSettings();
        Services.AutoSyncService.NotifyLaunchersChanged();

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
            Arguments = $"--pin --launcher {launcher.Id} --name \"{launcher.Name}\" --aumid {launcher.PinAumid}{iconArg}",
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

    /// <summary>
    /// Waits out an in-flight site-icon fetch, saying so on the button that is waiting.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="IconAdoptionWait"/>: a host that does not answer must not leave the
    /// button dead. Pinning then goes ahead with whatever icon the launcher has, which is the
    /// behaviour this had before it waited at all.
    /// </remarks>
    private async Task WaitForIconAdoptionAsync(Button? pinButton)
    {
        if (_iconAdoption.IsCompleted) return;

        object? content = pinButton?.Content;
        if (pinButton != null)
        {
            pinButton.IsEnabled = false;
            pinButton.Content = "Fetching icon…";
        }

        try
        {
            await Task.WhenAny(_iconAdoption, Task.Delay(IconAdoptionWait));
        }
        finally
        {
            if (pinButton != null)
            {
                pinButton.Content = content;
                pinButton.IsEnabled = true;
            }
        }
    }
}
