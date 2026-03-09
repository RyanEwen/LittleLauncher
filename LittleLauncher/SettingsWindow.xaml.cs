using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private IntPtr _hIconSmall;
    private IntPtr _hIconBig;


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

        // Set the window icon (titlebar, taskbar, Alt-Tab)
        var hwnd = WindowNative.GetWindowHandle(this);

        // Give this window its own AppUserModelID so the taskbar treats it
        // independently from the main (invisible) window. Without this, the
        // taskbar always uses the exe's embedded icon (WindowsAppSDK#2730).
        SetWindowAppUserModelId(hwnd, "LittleLauncher.Settings");

        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(wndId);
        string settingsIcon = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher", "settings-icon.ico");
        if (!File.Exists(settingsIcon))
            MainWindow.SaveSettingsIconToAppData();
        string iconPath = File.Exists(settingsIcon)
            ? settingsIcon
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "LittleLauncher.ico");
        ApplyWindowIcon(hwnd, iconPath);
        SetAppWindowIcon(appWindow, iconPath);
        LoadTitleBarIcon(iconPath);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;
        appWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(900 * scale), (int)(700 * scale)));

        // Navigate to home
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage));

        // Apply saved theme to this window
        Classes.ThemeManager.ApplySavedTheme(this);

        // Re-apply icon after WinUI finishes initializing (it can override WM_SETICON)
        Activated += (s, e) =>
        {
            if (_hIconBig != IntPtr.Zero)
            {
                var h = WindowNative.GetWindowHandle(this);
                SendMessage(h, WM_SETICON, ICON_SMALL, _hIconSmall);
                SendMessage(h, WM_SETICON, ICON_BIG, _hIconBig);
            }
        };

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
    /// Re-reads the settings icon (app icon + gear overlay) and applies it to this window.
    /// Called when the tray icon mode or OS theme changes.
    /// </summary>
    internal void RefreshIcon()
    {
        string settingsIcon = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher", "settings-icon.ico");
        if (!File.Exists(settingsIcon)) return;
        var hwnd = WindowNative.GetWindowHandle(this);
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(wndId);
        ApplyWindowIcon(hwnd, settingsIcon);
        SetAppWindowIcon(appWindow, settingsIcon);
        LoadTitleBarIcon(settingsIcon);
    }

    /// <summary>
    /// Sets the AppWindow icon via the HICON → IconId interop path.
    /// This is the documented way to update titlebar + taskbar + Alt-Tab.
    /// </summary>
    private static void SetAppWindowIcon(AppWindow appWindow, string icoPath)
    {
        // Load at native size (0,0) so the OS picks the best resolution
        var hIcon = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
        if (hIcon == IntPtr.Zero) return;
        try
        {
            var iconId = Microsoft.UI.Win32Interop.GetIconIdFromIcon(hIcon);
            appWindow.SetIcon(iconId);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Sets both ICON_SMALL and ICON_BIG on the HWND via WM_SETICON.
    /// Keeps HICON handles alive as instance fields so the taskbar retains them.
    /// </summary>
    private void ApplyWindowIcon(IntPtr hwnd, string icoPath)
    {
        // Clean up previous handles
        if (_hIconSmall != IntPtr.Zero) { DestroyIcon(_hIconSmall); _hIconSmall = IntPtr.Zero; }
        if (_hIconBig != IntPtr.Zero) { DestroyIcon(_hIconBig); _hIconBig = IntPtr.Zero; }

        _hIconSmall = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        _hIconBig = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);

        if (_hIconSmall != IntPtr.Zero)
            SendMessage(hwnd, WM_SETICON, ICON_SMALL, _hIconSmall);
        if (_hIconBig != IntPtr.Zero)
            SendMessage(hwnd, WM_SETICON, ICON_BIG, _hIconBig);
    }

    /// <summary>
    /// Loads the icon into the custom titlebar Image element.
    /// </summary>
    private void LoadTitleBarIcon(string icoPath)
    {
        try
        {
            TitleBarIcon.Source = new BitmapImage(new Uri(icoPath));
        }
        catch { /* fallback: leave empty */ }
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