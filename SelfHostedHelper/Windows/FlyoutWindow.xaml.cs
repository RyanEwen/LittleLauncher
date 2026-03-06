using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Models;
using SelfHostedHelper.Services;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
using static SelfHostedHelper.Classes.NativeMethods;

namespace SelfHostedHelper.Windows;

public partial class FlyoutWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly object BoundsFileLock = new();
    private static readonly ConcurrentDictionary<string, WindowBounds> CachedBounds = new(StringComparer.OrdinalIgnoreCase);
    private static FlyoutWindow? _instance;
    private DateTime _lastDismissed = DateTime.MinValue;
    private bool _toolWindowStyleApplied;
    private int _lastItemsHash;
    private MainWindow? _owner;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;
    private bool _isShowing;

    private FlyoutWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Remove titlebar and make borderless
        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        GetAppWindow().SetPresenter(presenter);

        ItemsListControl.ItemsSource = SettingsManager.Current.LauncherItems;

        // Desktop Acrylic blurs whatever is behind the window (including other windows),
        // unlike Mica which only samples the wallpaper.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // OS-level rounded corners (Windows 11) + DWM shadow
        int cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        // Hook WndProc for deactivation detection
        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 2, 0);
        Activated += FlyoutWindow_Activated;
    }

    private void FlyoutWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_isShowing) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _lastDismissed = DateTime.UtcNow;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (msg == 0x0100 && wParam == (IntPtr)0x1B) // WM_KEYDOWN + VK_ESCAPE
        {
            HideFlyout();
            _lastDismissed = DateTime.UtcNow;
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public static void Toggle(MainWindow owner, int screenX, int screenY)
    {
        if (_instance != null && _instance._hwnd != IntPtr.Zero && IsWindow(_instance._hwnd))
        {
            if (GetWindowLong(_instance._hwnd, GWL_STYLE) != 0 &&
                (GetWindowLong(_instance._hwnd, GWL_STYLE) & WS_VISIBLE) != 0)
            {
                _instance.HideFlyout();
                return;
            }
        }

        if (_instance != null && (DateTime.UtcNow - _instance._lastDismissed).TotalMilliseconds < 300)
            return;

        if (_instance == null || _instance._hwnd == IntPtr.Zero)
            _instance = new FlyoutWindow(owner);

        _instance._owner = owner;
        _instance.RebuildItemsIfNeeded();

        // Calculate DPI-aware dimensions
        double dpiScale = GetDpiForWindow(_instance._hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        int flyoutWidthPx = (int)(250 * dpiScale);
        int flyoutHeightPx = (int)Math.Ceiling(_instance.MeasureContentHeight() * dpiScale);

        // Position off-screen first, then show
        _instance._isShowing = true;
        var appWindow = _instance.GetAppWindow();
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(flyoutWidthPx, flyoutHeightPx));
        ShowWindow(_instance._hwnd, SW_SHOWNOACTIVATE);
        SetForegroundWindow(_instance._hwnd);

        if (!_instance._toolWindowStyleApplied)
        {
            int exStyle = GetWindowLong(_instance._hwnd, GWL_EXSTYLE);
            SetWindowLong(_instance._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            _instance._toolWindowStyleApplied = true;
        }

        _instance.PositionAt(screenX, screenY);
        _instance._isShowing = false;
    }

    public static void DismissIfOpen()
    {
        _instance?.HideFlyout();
    }

    public static void WarmUp(MainWindow owner)
    {
        if (_instance == null)
        {
            _instance = new FlyoutWindow(owner);
            _instance._lastItemsHash = ComputeItemsHash();
            // Apply tool window style and hide
            int exStyle = GetWindowLong(_instance._hwnd, GWL_EXSTYLE);
            SetWindowLong(_instance._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            _instance._toolWindowStyleApplied = true;
            ShowWindow(_instance._hwnd, SW_HIDE);
        }
    }

    private void HideFlyout()
    {
        ShowWindow(_hwnd, SW_HIDE);
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    // ── Content ─────────────────────────────────────────────────────

    private static int ComputeItemsHash()
    {
        var items = SettingsManager.Current.LauncherItems;
        if (items == null || items.Count == 0) return 0;
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item.Name);
            hash.Add(item.Path);
            hash.Add(item.IconPath);
            hash.Add(item.IconGlyph);
            hash.Add(item.IsWebsite);
            hash.Add(item.OpenInAppWindow);
            hash.Add(item.AppWindowBrowser);
            hash.Add(item.AppWindowBrowserProfile);
            hash.Add(item.IsCategory);
        }
        return hash.ToHashCode();
    }

    private void RebuildItemsIfNeeded()
    {
        int currentHash = ComputeItemsHash();
        if (currentHash != _lastItemsHash)
        {
            _lastItemsHash = currentHash;
            // Re-bind in case the collection instance was replaced (e.g. settings reload)
            ItemsListControl.ItemsSource = SettingsManager.Current.LauncherItems;
        }
    }



    // ── Positioning ─────────────────────────────────────────────────

    private void PositionAt(int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        int flyoutWidth = (int)(250 * scale);
        int flyoutHeight = (int)Math.Ceiling(MeasureContentHeight() * scale);
        int bottomGap = Math.Max(4, (int)Math.Round(8 * scale));

        int left = screenX - flyoutWidth / 2;
        int top = workArea.Bottom - flyoutHeight - bottomGap;

        if (left < workArea.Left) left = workArea.Left;
        if (left + flyoutWidth > workArea.Right) left = workArea.Right - flyoutWidth;
        if (top < workArea.Top) top = workArea.Top;

        SetWindowPos(_hwnd, 0, left, top, flyoutWidth, flyoutHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void ItemsListControl_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LauncherItem item || item.IsCategory) return;

        HideFlyout();
        _lastDismissed = DateTime.UtcNow;

        try
        {
            if (item.IsWebsite || item.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                               || item.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                LaunchWebsite(item);
            }
            else
            {
                Process.Start(new ProcessStartInfo(item.Path)
                {
                    UseShellExecute = true,
                    Arguments = item.Arguments ?? ""
                });
            }
            Logger.Info($"Launched from flyout: {item.Name} ({item.Path})");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to launch from flyout: {item.Name} ({item.Path})");
        }
    }

    private void ItemsListControl_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // WinUI 3's CanReorderItems reorders its internal items list but does NOT
        // call Move() on the source ObservableCollection. Calling Move() while
        // still bound causes visual duplicates because the ListView reacts to
        // both its own reorder AND the CollectionChanged events.
        // Fix: unbind, rebuild collection order, rebind.
        var settings = SettingsManager.Current.LauncherItems;
        var reordered = ItemsListControl.Items.OfType<LauncherItem>().ToList();

        if (reordered.Count == settings.Count)
        {
            ItemsListControl.ItemsSource = null;
            settings.Clear();
            foreach (var item in reordered)
                settings.Add(item);
            ItemsListControl.ItemsSource = settings;
        }

        SettingsManager.SaveSettings();
        _lastItemsHash = ComputeItemsHash();
        AutoSyncService.NotifyItemsChanged();
    }

    private void ItemsListControl_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe) return;
        var item = fe.DataContext as LauncherItem;
        if (item == null || item.IsCategory) return;

        var flyout = new MenuFlyout();
        var editItem = new MenuFlyoutItem { Text = "Edit" };
        editItem.Click += (_, _) => EditItem(item);
        flyout.Items.Add(editItem);

        flyout.ShowAt(ItemsListControl, e.GetPosition(ItemsListControl));
    }

    private static void LaunchWebsite(LauncherItem item)
    {
        if (!item.OpenInAppWindow)
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            return;
        }

        if (TryLaunchInAppWindow(item.Path, item.AppWindowBrowser, item.AppWindowBrowserProfile))
            return;

        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    private enum BrowserEngine { Chromium, Gecko }

    private static BrowserEngine DetectEngine(string exePath)
    {
        string? dir = Path.GetDirectoryName(exePath);
        if (dir != null && (File.Exists(Path.Combine(dir, "chrome.dll")) ||
                            File.Exists(Path.Combine(dir, "msedge.dll"))))
            return BrowserEngine.Chromium;

        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (name == "firefox" || name == "zen" || name == "waterfox" ||
            name == "librewolf" || name == "floorp" || name == "mercury" || name == "firedragon")
            return BrowserEngine.Gecko;

        return BrowserEngine.Chromium;
    }

    private static bool TryLaunchInAppWindow(string url, string browserPath, string browserProfile)
    {
        string profileId = GetAppWindowProfileId(url);
        string browserExe = ResolveBrowserExe(browserPath);
        if (browserExe == "") return false;

        var engine = DetectEngine(browserExe);
        var existingWindows = GetBrowserWindows(engine);

        try
        {
            string args = engine == BrowserEngine.Gecko
                ? BuildGeckoArgs(url, profileId)
                : BuildChromiumArgs(url, browserProfile, profileId);

            Process.Start(new ProcessStartInfo { FileName = browserExe, Arguments = args, UseShellExecute = false });
            _ = RestoreAndTrackWindowBoundsAsync(existingWindows, profileId, engine);
            return true;
        }
        catch { return false; }
    }

    private static string BuildChromiumArgs(string url, string browserProfile, string profileId)
    {
        string args = $"--app=\"{url}\"";
        if (string.IsNullOrEmpty(browserProfile))
        {
            string appProfileDir = GetAppWindowProfileDirectory(profileId);
            Directory.CreateDirectory(appProfileDir);
            args += $" --user-data-dir=\"{appProfileDir}\"";
        }
        else if (browserProfile != "__default__")
        {
            args += $" --profile-directory=\"{browserProfile}\"";
        }
        return args;
    }

    private static string BuildGeckoArgs(string url, string profileId)
    {
        string appProfileDir = GetAppWindowProfileDirectory(profileId);
        Directory.CreateDirectory(appProfileDir);
        EnsureGeckoAppWindowProfile(appProfileDir);
        return $"--new-window \"{url}\" --profile \"{appProfileDir}\" --no-remote";
    }

    private static void EnsureGeckoAppWindowProfile(string profileDir)
    {
        string chromeDir = Path.Combine(profileDir, "chrome");
        Directory.CreateDirectory(chromeDir);

        string userChromePath = Path.Combine(chromeDir, "userChrome.css");
        if (!File.Exists(userChromePath))
        {
            File.WriteAllText(userChromePath,
                "@namespace url(\"http://www.mozilla.org/keymaster/gatekeeper/there.is.only.xul\"); #navigator-toolbox { visibility: collapse !important; }");
        }

        string userJsPath = Path.Combine(profileDir, "user.js");
        if (!File.Exists(userJsPath))
        {
            File.WriteAllText(userJsPath,
                "user_pref(\"toolkit.legacyUserProfileCustomizations.stylesheets\", true);\n" +
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
                "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
                "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);\n");
        }
    }

    private static string ResolveBrowserExe(string browserPath)
    {
        if (!string.IsNullOrEmpty(browserPath))
            return File.Exists(browserPath) ? browserPath : "";
        return GetDefaultBrowserExePath() ?? "";
    }

    private static string? GetDefaultBrowserExePath()
    {
        try
        {
            int size = 512;
            var sb = new StringBuilder(size);
            int hr = AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, "https", "open", sb, ref size);
            if (hr == 0)
            {
                string exePath = sb.ToString();
                if (File.Exists(exePath)) return exePath;
            }
        }
        catch { }
        return null;
    }

    private static string GetAppWindowProfileId(string url)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string GetAppWindowProfileDirectory(string profileId)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SelfHostedHelper", "AppWindowProfiles", profileId);
    }

    private static string GetBoundsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SelfHostedHelper", "edge-window-bounds.json");
    }

    private static async Task RestoreAndTrackWindowBoundsAsync(HashSet<IntPtr> existingWindows, string profileId, BrowserEngine engine)
    {
        IntPtr hwnd = await WaitForNewBrowserWindowAsync(existingWindows, engine, TimeSpan.FromSeconds(10));
        if (hwnd == IntPtr.Zero) return;

        if (TryGetSavedBounds(profileId, out var savedBounds))
        {
            SetWindowPos(hwnd, 0, savedBounds.Left, savedBounds.Top, savedBounds.Width, savedBounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            if (savedBounds.IsMaximized) ShowWindow(hwnd, SW_MAXIMIZE);
        }

        InstallBoundsTrackingHooks(hwnd, profileId);
    }

    private static readonly HashSet<WinEventProc> ActiveHookDelegates = new();

    private static void InstallBoundsTrackingHooks(IntPtr hwnd, string profileId)
    {
        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
        WindowBounds? lastBounds = null;
        IntPtr hookLocation = IntPtr.Zero;
        IntPtr hookDestroy = IntPtr.Zero;
        WinEventProc? handler = null;

        handler = (hHook, eventType, eventHwnd, idObject, idChild, eventThread, time) =>
        {
            if (eventHwnd != hwnd || idObject != OBJID_WINDOW) return;
            if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
            {
                if (GetWindowRect(hwnd, out RECT rect))
                {
                    bool maximized = IsZoomed(hwnd);
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w >= 320 && h >= 240)
                        lastBounds = new WindowBounds(rect.Left, rect.Top, w, h, maximized);
                }
            }
            else if (eventType == EVENT_OBJECT_DESTROY)
            {
                if (lastBounds is not null) SaveBounds(profileId, lastBounds);
                if (hookLocation != IntPtr.Zero) UnhookWinEvent(hookLocation);
                if (hookDestroy != IntPtr.Zero) UnhookWinEvent(hookDestroy);
                lock (ActiveHookDelegates) ActiveHookDelegates.Remove(handler!);
            }
        };

        lock (ActiveHookDelegates) ActiveHookDelegates.Add(handler);

        hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
        hookDestroy = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
    }

    private static async Task<IntPtr> WaitForNewBrowserWindowAsync(HashSet<IntPtr> existingWindows, BrowserEngine engine, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var currentWindows = GetBrowserWindows(engine);
            foreach (var hwnd in currentWindows)
            {
                if (existingWindows.Contains(hwnd)) continue;
                if (!GetWindowRect(hwnd, out RECT rect)) continue;
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width >= 200 && height >= 120) return hwnd;
            }
            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }

    private static readonly string[] ChromiumWindowClasses = { "Chrome_WidgetWin_1" };
    private static readonly string[] GeckoWindowClasses = { "MozillaWindowClass", "MozillaDialogClass" };

    private static HashSet<IntPtr> GetBrowserWindows(BrowserEngine engine)
    {
        var windowClasses = engine == BrowserEngine.Gecko ? GeckoWindowClasses : ChromiumWindowClasses;
        var windows = new HashSet<IntPtr>();
        var className = new StringBuilder(256);
        EnumWindows((hWnd, _) =>
        {
            className.Clear();
            GetClassName(hWnd, className, className.Capacity);
            string cls = className.ToString();
            foreach (string target in windowClasses)
            {
                if (cls == target) { windows.Add(hWnd); break; }
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool TryGetSavedBounds(string profileId, out WindowBounds bounds)
    {
        bounds = default!;
        lock (BoundsFileLock)
        {
            if (CachedBounds.TryGetValue(profileId, out var cachedBounds)) { bounds = cachedBounds; return true; }
            var all = LoadAllBounds();
            foreach (var kv in all) CachedBounds[kv.Key] = kv.Value;
            if (CachedBounds.TryGetValue(profileId, out var loadedBounds)) { bounds = loadedBounds; return true; }
            return false;
        }
    }

    private static void SaveBounds(string profileId, WindowBounds bounds)
    {
        lock (BoundsFileLock)
        {
            CachedBounds[profileId] = bounds;
            var all = LoadAllBounds();
            all[profileId] = bounds;
            string filePath = GetBoundsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            string json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
    }

    private static Dictionary<string, WindowBounds> LoadAllBounds()
    {
        string filePath = GetBoundsFilePath();
        if (!File.Exists(filePath)) return new Dictionary<string, WindowBounds>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Dictionary<string, WindowBounds>>(json) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private sealed record WindowBounds(int Left, int Top, int Width, int Height, bool IsMaximized = false);

    private void EditItem(LauncherItem item)
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;
        if (_owner != null)
            SettingsWindow.NavigateToEditItem(item, _owner);
    }

    private double _lastMeasuredHeight = 80;

    private double MeasureContentHeight()
    {
        // Calculate height arithmetically instead of calling UpdateLayout()/Measure()
        // on a potentially hidden window. Forcing a XAML layout pass on a window hidden
        // via ShowWindow(SW_HIDE) while another WinUI 3 window is active causes a fatal
        // ExecutionEngineException in Microsoft.WinUI.dll.
        const double itemHeight = 32;      // Content ~20px + Border Padding 6+6
        const double categoryExtra = 0;    // Margin="0,4,0,0" is inside the Border padding, not additive
        const double listPadding = 12;     // ListView Padding="8,6,8,6" → 6+6

        int itemCount = 0;
        int categoryCount = 0;
        var items = SettingsManager.Current.LauncherItems;
        if (items == null) return _lastMeasuredHeight;
        foreach (var item in items)
        {
            if (item.IsCategory) categoryCount++;
            else itemCount++;
        }

        double contentHeight = ((itemCount + categoryCount) * itemHeight)
                             + (categoryCount * categoryExtra)
                             + listPadding;
        _lastMeasuredHeight = Math.Clamp(contentHeight, 80, 560);
        return _lastMeasuredHeight;
    }
}