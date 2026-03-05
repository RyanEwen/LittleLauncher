using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using static SelfHostedHelper.Classes.NativeMethods;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace SelfHostedHelper.Windows;

/// <summary>
/// A flyout popup window that displays launcher items.
/// Shown from the taskbar widget icon or the tray icon.
/// Positioned above the taskbar and dismissed on focus loss or Escape.
/// </summary>
public partial class FlyoutWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly object BoundsFileLock = new();
    private static readonly ConcurrentDictionary<string, WindowBounds> CachedBounds = new(StringComparer.OrdinalIgnoreCase);

    private static FlyoutWindow? _instance;
    private DateTime _lastDismissed = DateTime.MinValue;
    private bool _toolWindowStyleApplied;
    private int _lastItemsHash;

    private FlyoutWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Toggle the flyout at the given screen-coordinate anchor point.
    /// If already visible, hides it. If recently dismissed (within 300 ms), ignores
    /// the request to prevent the toggle-on-deactivate race condition.
    /// </summary>
    public static void Toggle(Point screenAnchor)
    {
        if (_instance != null && _instance.IsVisible)
        {
            _instance.Hide();
            return;
        }

        // Prevent re-opening immediately after dismiss (toggle race with Deactivated)
        if (_instance != null && (DateTime.UtcNow - _instance._lastDismissed).TotalMilliseconds < 300)
            return;

        _instance ??= new FlyoutWindow();
        _instance.RebuildItemsIfNeeded();
        _instance.ApplyTheme();

        // Show off-screen first so layout runs, then reposition
        _instance.Left = -9999;
        _instance.Top = -9999;
        _instance.Show();

        // Hide from Alt-Tab (only needs to be done once per window handle)
        if (!_instance._toolWindowStyleApplied)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_instance).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            _instance._toolWindowStyleApplied = true;
        }

        _instance.PositionAt(screenAnchor);
        _instance.Activate();
    }

    /// <summary>
    /// Hide the flyout if it is currently visible.
    /// </summary>
    public static void DismissIfOpen()
    {
        if (_instance is { IsVisible: true })
            _instance.Hide();
    }

    /// <summary>
    /// Pre-create the singleton so the first Toggle is fast.
    /// </summary>
    public static void WarmUp()
    {
        if (_instance == null)
        {
            _instance = new FlyoutWindow();
            _instance.RebuildItems();
            _instance.ApplyTheme();
        }
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
        if (currentHash == _lastItemsHash && ItemsPanel.Children.Count > 0)
            return;

        _lastItemsHash = currentHash;
        RebuildItems();
    }

    private void RebuildItems()
    {
        ItemsPanel.Children.Clear();

        var items = SettingsManager.Current.LauncherItems;
        if (items == null || items.Count == 0)
        {
            var placeholder = new TextBlock
            {
                Text = "No launcher items configured",
                Foreground = new SolidColorBrush(Colors.Gray),
                Margin = new Thickness(12, 8, 12, 8),
                FontSize = 13
            };
            ItemsPanel.Children.Add(placeholder);
            return;
        }

        foreach (var item in items)
        {
            if (item.IsCategory)
                ItemsPanel.Children.Add(CreateCategoryHeading(item));
            else
                ItemsPanel.Children.Add(CreateItemRow(item));
        }
    }

    private UIElement CreateCategoryHeading(LauncherItem item)
    {
        bool isDark = IsDarkTheme();

        var heading = new TextBlock
        {
            Text = item.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(isDark
                ? Color.FromArgb(180, 255, 255, 255)
                : Color.FromArgb(180, 0, 0, 0)),
            Margin = new Thickness(14, 8, 14, 4),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        return heading;
    }

    private Border CreateItemRow(LauncherItem item)
    {
        bool isDark = IsDarkTheme();

        UIElement icon;

        // Prefer favicon image if available, fall back to symbol glyph
        if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(item.IconPath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 24;
            bitmap.EndInit();
            bitmap.Freeze();

            icon = new Image
            {
                Source = bitmap,
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
        }
        else
        {
            SymbolRegular symbol = SymbolRegular.Open24;
            if (!string.IsNullOrEmpty(item.IconGlyph) && Enum.TryParse<SymbolRegular>(item.IconGlyph, out var parsed))
                symbol = parsed;

            icon = new SymbolIcon
            {
                Symbol = symbol,
                FontSize = 18,
                Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
        }

        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 0, 12, 0)
        };
        stack.Children.Add(icon);
        stack.Children.Add(name);

        var border = new Border
        {
            Height = 40,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(1, 128, 128, 128)),
            Child = stack,
            Cursor = Cursors.Hand,
            Tag = item,
            Margin = new Thickness(2)
        };

        border.MouseEnter += ItemRow_MouseEnter;
        border.MouseLeave += ItemRow_MouseLeave;
        border.MouseLeftButtonUp += ItemRow_Click;

        var editMenuItem = new MenuItem { Header = "Edit" };
        editMenuItem.Click += (s, e) => EditItem(item);
        border.ContextMenu = new ContextMenu { Items = { editMenuItem } };

        return border;
    }

    // ── Theme ───────────────────────────────────────────────────────

    private void ApplyTheme()
    {
        bool isDark = IsDarkTheme();
        RootBorder.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(245, 32, 32, 32)
            : Color.FromArgb(245, 243, 243, 243));
    }

    private static bool IsDarkTheme()
    {
        var appTheme = ApplicationThemeManager.GetAppTheme();
        if (appTheme == ApplicationTheme.Dark) return true;
        if (appTheme == ApplicationTheme.Light) return false;
        return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark;
    }

    // ── Positioning ─────────────────────────────────────────────────

    private void PositionAt(Point screenAnchor)
    {
        var pt = new POINT { X = (int)screenAnchor.X, Y = (int)screenAnchor.Y };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        UpdateLayout();

        // Flyout size in physical pixels
        double flyoutWidth = ActualWidth * scale;
        double flyoutHeight = ActualHeight * scale;

        // Bottom of flyout at bottom of work area (just above the taskbar)
        double left = screenAnchor.X - flyoutWidth / 2.0;
        double top = workArea.Bottom - flyoutHeight;

        // Clamp to work area
        if (left < workArea.Left) left = workArea.Left;
        if (left + flyoutWidth > workArea.Right) left = workArea.Right - flyoutWidth;
        if (top < workArea.Top) top = workArea.Top;

        // Convert physical pixels to WPF device-independent units
        Left = left / scale;
        Top = top / scale;
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Hide();
        _lastDismissed = DateTime.UtcNow;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            _lastDismissed = DateTime.UtcNow;
        }
    }

    private void ItemRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not LauncherItem item) return;

        Hide();
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

    private static void LaunchWebsite(LauncherItem item)
    {
        if (!item.OpenInAppWindow)
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            return;
        }

        if (TryLaunchInAppWindow(item.Path, item.AppWindowBrowser, item.AppWindowBrowserProfile))
            return;

        // Fallback: open via default browser if app-mode launch is unavailable.
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
        if (name is "firefox" or "zen" or "waterfox" or "librewolf" or "floorp" or "mercury" or "firedragon")
            return BrowserEngine.Gecko;

        return BrowserEngine.Chromium;
    }

    private static bool TryLaunchInAppWindow(string url, string browserPath, string browserProfile)
    {
        string profileId = GetAppWindowProfileId(url);

        string browserExe = ResolveBrowserExe(browserPath);
        if (browserExe == "")
            return false;

        var engine = DetectEngine(browserExe);

        // Snapshot existing browser windows before launch.
        var existingWindows = GetBrowserWindows(engine);

        try
        {
            string args = engine == BrowserEngine.Gecko
                ? BuildGeckoArgs(url, profileId)
                : BuildChromiumArgs(url, browserProfile, profileId);

            Process.Start(new ProcessStartInfo
            {
                FileName = browserExe,
                Arguments = args,
                UseShellExecute = false
            });

            _ = RestoreAndTrackWindowBoundsAsync(existingWindows, profileId, engine);
            return true;
        }
        catch
        {
            return false;
        }
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
        // Gecko's userChrome.css is profile-wide (affects all windows), so app-window
        // mode always uses an isolated sandbox profile with --no-remote.
        string appProfileDir = GetAppWindowProfileDirectory(profileId);
        Directory.CreateDirectory(appProfileDir);
        EnsureGeckoAppWindowProfile(appProfileDir);
        return $"--new-window \"{url}\" --profile \"{appProfileDir}\" --no-remote";
    }

    /// <summary>
    /// Ensures a Gecko sandbox profile has userChrome.css to hide browser UI
    /// and user.js prefs to enable it and suppress first-run dialogs.
    /// </summary>
    private static void EnsureGeckoAppWindowProfile(string profileDir)
    {
        string chromeDir = Path.Combine(profileDir, "chrome");
        Directory.CreateDirectory(chromeDir);

        string userChromePath = Path.Combine(chromeDir, "userChrome.css");
        if (!File.Exists(userChromePath))
        {
            File.WriteAllText(userChromePath,
                """@namespace url("http://www.mozilla.org/keymaster/gatekeeper/there.is.only.xul"); #navigator-toolbox { visibility: collapse !important; }""");
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

        // Use the OS default browser
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
                if (File.Exists(exePath))
                    return exePath;
            }
        }
        catch { }

        return null;
    }

    private static string GetAppWindowProfileId(string url)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string GetAppWindowProfileDirectory(string profileId)
    {
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SelfHostedHelper",
            "AppWindowProfiles");

        return Path.Combine(baseDir, profileId);
    }

    private static string GetBoundsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SelfHostedHelper",
            "edge-window-bounds.json");
    }

    private static async Task RestoreAndTrackWindowBoundsAsync(HashSet<IntPtr> existingWindows, string profileId, BrowserEngine engine)
    {
        IntPtr hwnd = await WaitForNewBrowserWindowAsync(existingWindows, engine, TimeSpan.FromSeconds(10));
        if (hwnd == IntPtr.Zero)
            return;

        if (TryGetSavedBounds(profileId, out var savedBounds))
        {
            SetWindowPos(
                hwnd,
                0,
                savedBounds.Left,
                savedBounds.Top,
                savedBounds.Width,
                savedBounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            if (savedBounds.IsMaximized)
                ShowWindow(hwnd, SW_MAXIMIZE);
        }

        InstallBoundsTrackingHooks(hwnd, profileId);
    }

    /// <summary>
    /// Prevents GC of WinEventProc delegates while their hooks are active.
    /// </summary>
    private static readonly HashSet<WinEventProc> ActiveHookDelegates = [];

    private static void InstallBoundsTrackingHooks(IntPtr hwnd, string profileId)
    {
        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);

        WindowBounds? lastBounds = null;
        IntPtr hookLocation = IntPtr.Zero;
        IntPtr hookDestroy = IntPtr.Zero;
        WinEventProc? handler = null;

        handler = (hHook, eventType, eventHwnd, idObject, idChild, eventThread, time) =>
        {
            if (eventHwnd != hwnd || idObject != OBJID_WINDOW)
                return;

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
                if (lastBounds is not null)
                    SaveBounds(profileId, lastBounds);

                if (hookLocation != IntPtr.Zero) UnhookWinEvent(hookLocation);
                if (hookDestroy != IntPtr.Zero) UnhookWinEvent(hookDestroy);

                lock (ActiveHookDelegates)
                    ActiveHookDelegates.Remove(handler!);
            }
        };

        lock (ActiveHookDelegates)
            ActiveHookDelegates.Add(handler);

        hookLocation = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
        hookDestroy = SetWinEventHook(
            EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
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
                if (existingWindows.Contains(hwnd))
                    continue;

                // Verify it's a real visible window with reasonable size.
                if (!GetWindowRect(hwnd, out RECT rect))
                    continue;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width >= 200 && height >= 120)
                    return hwnd;
            }

            await Task.Delay(200);
        }

        return IntPtr.Zero;
    }

    private static readonly string[] ChromiumWindowClasses = ["Chrome_WidgetWin_1"];
    private static readonly string[] GeckoWindowClasses = ["MozillaWindowClass", "MozillaDialogClass"];

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
                if (cls == target)
                {
                    windows.Add(hWnd);
                    break;
                }
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
            if (CachedBounds.TryGetValue(profileId, out var cachedBounds))
            {
                bounds = cachedBounds;
                return true;
            }

            var all = LoadAllBounds();
            foreach (var kv in all)
                CachedBounds[kv.Key] = kv.Value;

            if (CachedBounds.TryGetValue(profileId, out var loadedBounds))
            {
                bounds = loadedBounds;
                return true;
            }

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
        if (!File.Exists(filePath))
            return new Dictionary<string, WindowBounds>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Dictionary<string, WindowBounds>>(json)
                ?? new Dictionary<string, WindowBounds>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, WindowBounds>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record WindowBounds(int Left, int Top, int Width, int Height, bool IsMaximized = false);

    private void EditItem(LauncherItem item)
    {
        Hide();
        _lastDismissed = DateTime.UtcNow;

        SettingsWindow.ShowInstance();
        SettingsWindow.NavigateToEditItem(item);
    }

    private void ItemRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;

        bool isDark = IsDarkTheme();
        var targetColor = isDark
            ? Color.FromArgb(40, 255, 255, 255)
            : Color.FromArgb(60, 0, 0, 0);

        var animation = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (border.Background is not SolidColorBrush)
            border.Background = new SolidColorBrush(Color.FromArgb(1, 128, 128, 128));

        border.Background.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void ItemRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;

        var animation = new ColorAnimation
        {
            To = Color.FromArgb(1, 128, 128, 128),
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        border.Background?.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
}
