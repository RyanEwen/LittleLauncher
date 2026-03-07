using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher;

/// <summary>
/// SettingsWindow — the main user-facing settings UI (WinUI 3).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static SettingsWindow? instance;
    private readonly MainWindow? _owner;

    public SettingsWindow(MainWindow owner)
    {
        if (instance != null)
        {
            SetForegroundWindow(WindowNative.GetWindowHandle(instance));
            Close();
            return;
        }

        _owner = owner;
        InitializeComponent();
        instance = this;
        Closed += (s, e) => instance = null;

        // Mica backdrop
        SystemBackdrop = new MicaBackdrop();

        // Configure title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set the window icon — use the generated AppData icon (Pin glyph), fallback to bundled .ico
        var hwnd = WindowNative.GetWindowHandle(this);
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(wndId);
        string appDataIcon = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher", "app-icon.ico");
        appWindow.SetIcon(File.Exists(appDataIcon)
            ? appDataIcon
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "LittleLauncher.ico"));
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;
        appWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(900 * scale), (int)(700 * scale)));

        // Navigate to home
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage));

        // Apply saved theme to this window
        Classes.ThemeManager.ApplySavedTheme(this);

        Closed += SettingsWindow_Closed;
    }

    /// <summary>
    /// Show the singleton settings window (create if needed, activate if exists).
    /// </summary>
    public static void ShowInstance(MainWindow owner)
    {
        if (instance == null)
        {
            new SettingsWindow(owner).Activate();
        }
        else
        {
            SetForegroundWindow(WindowNative.GetWindowHandle(instance));
        }
    }

    /// <summary>
    /// Navigate to a specific page type (used from HomePage dashboard cards).
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        ContentFrame.Navigate(pageType);

        // Settings page uses built-in settings button
        if (pageType == typeof(SystemPage))
        {
            RootNavigation.SelectedItem = RootNavigation.SettingsItem;
            return;
        }

        // Update selected nav item
        foreach (var item in RootNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is string tag && GetPageTypeFromTag(tag) == pageType)
            {
                RootNavigation.SelectedItem = item;
                return;
            }
        }
        foreach (var item in RootNavigation.FooterMenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is string tag && GetPageTypeFromTag(tag) == pageType)
            {
                RootNavigation.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>
    /// Open the settings window, navigate to the Launcher Items page, and
    /// immediately open the edit dialog for the given item.
    /// </summary>
    public static void NavigateToEditItem(LauncherItem item, MainWindow owner)
    {
        LauncherItemsPage.PendingEditItem = item;
        if (instance == null)
        {
            var window = new SettingsWindow(owner);
            window.NavigateTo(typeof(LauncherItemsPage));
            window.Activate();
        }
        else
        {
            SetForegroundWindow(WindowNative.GetWindowHandle(instance));
            instance.NavigateTo(typeof(LauncherItemsPage));
        }
    }

    internal MainWindow? GetOwner() => _owner;

    internal static SettingsWindow? GetCurrent() => instance;

    /// <summary>
    /// Re-reads the AppData icon and applies it to this window.
    /// Called when the tray icon mode or OS theme changes.
    /// </summary>
    internal void RefreshIcon()
    {
        string appDataIcon = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher", "app-icon.ico");
        if (!File.Exists(appDataIcon)) return;
        var hwnd = WindowNative.GetWindowHandle(this);
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(wndId).SetIcon(appDataIcon);
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SystemPage));
        }
        else if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            var pageType = GetPageTypeFromTag(tag);
            if (pageType != null)
                ContentFrame.Navigate(pageType);
        }
    }

    private static Type? GetPageTypeFromTag(string tag) => tag switch
    {
        "HomePage" => typeof(HomePage),
        "LauncherItemsPage" => typeof(LauncherItemsPage),
        "SyncPage" => typeof(SyncPage),
        "SystemPage" => typeof(SystemPage),
        "AboutPage" => typeof(AboutPage),
        _ => null
    };

    private void SettingsWindow_Closed(object sender, WindowEventArgs e)
    {
        SettingsManager.SaveSettings();
    }
}