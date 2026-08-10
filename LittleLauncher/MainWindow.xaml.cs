using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Windows;
using LittleLauncher.Services;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Automation;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher;

/// <summary>
/// MainWindow — the invisible host window for the Little Launcher (WinUI 3).
///
/// Architecture notes:
///   - This window is never actually displayed.
///   - Its sole purpose is to own the tray icon.
///   - A Mutex ("LittleLauncher") prevents multiple instances.
///   - A registered window message lets a second instance or LauncherShortcut
///     signal the first to show the flyout via PostMessage.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>The running MainWindow instance. Set in the constructor; null before construction.</summary>
    public static new MainWindow? Current { get; private set; }

    /// <summary>
    /// True when running as an MSIX-packaged app (has package identity).
    /// Cached once at startup — never changes during the process lifetime.
    /// </summary>
    internal static bool IsPackaged { get; } = DetectPackagedContext();

    private static bool DetectPackagedContext()
    {
        try
        {
            // Accessing Package.Current throws InvalidOperationException in unpackaged apps.
            _ = global::Windows.ApplicationModel.Package.Current.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the Start Menu Programs folder path.
    /// In MSIX this is VFS-redirected, but the companion exe's relaunch
    /// properties handle taskbar pinning identity — the shortcuts here are
    /// just for the shell's AUMID index (best-effort).
    /// </summary>
    internal static string GetStartMenuProgramsDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
    }

    internal static string GetLegacySharedStartMenuProgramsDir()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");
    }

    /// <summary>
    /// Returns the physical (non-VFS) path for the app's data directory.
    /// In MSIX, writes to %AppData%\LittleLauncher are VFS-redirected to a
    /// package-specific folder that external processes (shell, companion exe)
    /// cannot see. This method returns the real redirect target path so that
    /// external references (.lnk IconLocation, etc.) point to existing files.
    /// </summary>
    internal static string GetPhysicalAppDataDir()
    {
        if (IsPackaged)
        {
            return Path.Combine(
                global::Windows.Storage.ApplicationData.Current.RoamingFolder.Path,
                "LittleLauncher");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher");
    }

    /// <summary>
    /// Returns the real, shared %AppData% path used by unpackaged builds.
    /// In packaged builds this bypasses MSIX redirection so we can keep old
    /// unpackaged launcher pins pointing at a current companion exe.
    /// </summary>
    internal static string GetLegacySharedAppDataDir()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "AppData", "Roaming", "LittleLauncher");
    }



    /// <summary>Holds the Win32 HICON state for one launcher's tray icon.</summary>
    private sealed class TrayIconEntry
    {
        public uint Uid;
        public Guid Guid;
        public System.Drawing.Icon? Icon;
        public bool IsAdded; // false when NIconHide=true (NIM_DELETE called)
    }

    private static readonly Mutex Singleton = new(true, "LittleLauncher");

    /// <summary>Per-launcher registered window message IDs (key = Launcher.Id).</summary>
    private readonly Dictionary<string, int> _wmShowFlyoutPerLauncher = new();
    private static int _wmShowSettings;
    private int _wmTrayCallback;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;

    /// <summary>Per-launcher native tray icons (key = Launcher.Id).</summary>
    private readonly Dictionary<string, TrayIconEntry> _trayIcons = new();
    private uint _nextIconId = 1;
    private readonly global::Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _lastDarkTheme;

    /// <summary>Daily timer that refreshes stale auto-fetched item icons while the app runs.</summary>
    private System.Threading.Timer? _iconRefreshTimer;

    public MainWindow()
    {
        // ── Singleton check ─────────────────────────────────────────
        if (!Singleton.WaitOne(TimeSpan.Zero, true))
        {
            // Another instance is running — signal it and exit.
            IntPtr target = FindWindow(null, "Little Launcher Host");

            string[] args = Environment.GetCommandLineArgs();
            bool isSilent = args.Length > 1 &&
                args[1].Equals("--silent", StringComparison.OrdinalIgnoreCase);

            if (!isSilent)
            {
                // Default: show settings (Start Menu, double-click exe, etc.).
                // The companion exe handles flyout signaling itself via
                // PostMessage and never launches a second LittleLauncher.exe
                // when the app is already running.
                int msg = RegisterWindowMessage("LittleLauncher_ShowSettings");
                if (target != IntPtr.Zero && msg != 0)
                    PostMessage(target, msg, IntPtr.Zero, IntPtr.Zero);
            }
            // --silent second instance: just exit quietly

            Environment.Exit(0);
        }

        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        Current = this;

        Logger.Info("Starting Little Launcher MainWindow");

        // Register the window message for cross-process flyout signaling.
        // Per-launcher show-flyout messages are registered in CreateTrayIconForLauncher().
        _wmShowSettings = RegisterWindowMessage("LittleLauncher_ShowSettings");

        // Also register the legacy (no-launcher) message for backward compat with old pinned shortcuts,
        // and store it under an empty key so WndProc can route it to the first launcher.
        int legacyMsg = RegisterWindowMessage("LittleLauncher_ShowFlyout");
        _wmShowFlyoutPerLauncher[string.Empty] = legacyMsg;

        // Hide from Alt-Tab and make invisible
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        // Move off-screen
        var appWindow = GetAppWindow();
        appWindow.Move(new global::Windows.Graphics.PointInt32(-9999, -9999));
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(1, 1));

        // Prevent WinUI 3 from closing the host window (and terminating the app)
        // when other windows close. Exit only happens via Environment.Exit(0) in
        // the tray icon's Exit command.
        appWindow.Closing += (s, e) => e.Cancel = true;

        // Hook WndProc for cross-process PostMessage IPC
        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 0, 0);

        // Deferred init: apply theme, tray icon
        ApplyDeferredInit();
        EnsureStartMenuShortcuts();
        EnsureFlyoutShortcut();
        CleanUpStaleIconFiles();
        LauncherPanels.WarmUp(this, SettingsManager.Current.Launchers);
        _ = StartAutoSyncAsync();
        _ = FetchMissingIconsOnStartupAsync();

        // The app stays resident in the tray for weeks, so a launch-only refresh
        // isn't enough. Check daily; downloads only happen once a cached icon
        // is older than FaviconService.IconMaxAge. Marshal to the UI thread so
        // IconPath change notifications are safe for bindings.
        _iconRefreshTimer = new System.Threading.Timer(
            _ => App.MainDispatcherQueue.TryEnqueue(() => _ = RefreshStaleIconsPeriodicAsync()),
            null,
            TimeSpan.FromHours(24),
            TimeSpan.FromHours(24));

        RegisterForNotifications();
        ShowUpgradeNoticeToast();
        _ = CheckForUpdateOnStartupAsync();

        // Tell Windows to include --silent when auto-restarting the app
        // (e.g. "Restart apps" after sign-in). Without this, Windows
        // relaunches the exe with no args, which opens the Settings window.
        RegisterApplicationRestart("--silent", 0);

        // Listen for OS theme changes to refresh icons
        _lastDarkTheme = Classes.ThemeManager.IsDarkTheme();
        _uiSettings.ColorValuesChanged += OnSystemThemeChanged;

        if (!IsSilentLaunch())
            SettingsWindow.ShowInstance(this);
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    /// <summary>
    /// Registers for Windows notifications and routes a click back into the app.
    /// </summary>
    /// <remarks>
    /// Packaged builds used to skip this entirely. That was never a platform limitation: a
    /// packaged app needs a COM activator declared in its manifest so a clicked toast has
    /// somewhere to go, and without one <c>Register()</c> can throw. The MSIX manifest now
    /// declares one (<c>windows.toastNotificationActivation</c> plus its <c>com:ExeServer</c>),
    /// so both install types register identically. The CLSID there must stay stable — change it
    /// and toasts already sitting in the Action Center lose their activator.
    /// </remarks>
    private void RegisterForNotifications()
    {
        try
        {
            var manager = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Toast notification registration failed");
        }
    }

    /// <summary>
    /// Opens the app when a notification is clicked.
    /// </summary>
    /// <remarks>
    /// A toast raised by a web launcher's page carries the launcher it came from and is routed
    /// back to that flyout, which is the only place the click means anything —
    /// <see cref="WebFlyoutWindow.HandleNotificationActivation"/> also tells the page it was
    /// clicked, so its <c>onclick</c> handler runs as it would in a browser.
    /// <para>Everything else lands on the Home page, deliberately unrouted: the app's own notices
    /// are also showing there as a banner, so the click arrives at the same action the toast was
    /// offering with no extra plumbing to keep in sync.</para>
    /// </remarks>
    private void OnNotificationInvoked(
        Microsoft.Windows.AppNotifications.AppNotificationManager sender,
        Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs args)
    {
        var arguments = args.Arguments;

        // Whatever the user typed into the toast's reply box, if it had one. Read here rather than
        // in the flyout: the args do not outlive this callback.
        string reply = args.UserInput.TryGetValue("llReply", out string? typed) ? typed ?? "" : "";

        DispatcherQueue.TryEnqueue(() =>
        {
            if (WebFlyoutWindow.HandleNotificationActivation(arguments, reply)) return;
            SettingsWindow.ShowInstance(this);
        });
    }

    /// <summary>
    /// Opens a launcher's panel from a context with no pointer position — a clicked toast.
    /// </summary>
    /// <remarks>
    /// Anchored the same way a pinned shortcut's signal is: on the taskbar button if the launcher
    /// has one, otherwise on the cursor, which on a toast click is the corner the tray lives in
    /// anyway. Web launchers with an anchor or a remembered position ignore both and place
    /// themselves — see <c>CalculatePlacement</c>.
    /// </remarks>
    internal void OpenLauncherPanel(string launcherId)
    {
        GetCursorPos(out var pt);
        TryResolveLauncherAnchorPoint(launcherId, pt.X, pt.Y, out int anchorX, out int anchorY);
        DispatcherQueue.TryEnqueue(() => LauncherPanels.Toggle(this, anchorX, anchorY, launcherId));
    }

    /// <summary>
    /// Announces a just-detected upgrade as a toast, once.
    /// </summary>
    /// <remarks>
    /// The Home page banner only reaches someone who opens the app, and Store updates install
    /// silently — so for Store users this is the only notice that arrives on its own. Gated on
    /// the transient <c>UpgradeNoticesJustRaised</c>, not the persisted flag, which stays set
    /// until the banner is dismissed and would otherwise re-toast on every launch.
    /// </remarks>
    private void ShowUpgradeNoticeToast()
    {
        if (!SettingsManager.Current.UpgradeNoticesJustRaised) return;

        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText("New: web launchers")
                .AddText("A launcher can now open a web page — a dashboard, webmail, or any site — straight from its tray icon.");
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Upgrade notice toast failed");
        }
    }

    /// <summary>
    /// Finish initialization that needs the window to be set up.
    /// </summary>
    private void ApplyDeferredInit()
    {
        // Apply theme
        ThemeManager.ApplySavedTheme(this);

        // Clear stale paths left by a different install type (e.g. MSIX → WiX).
        // MSIX VFS-redirects %AppData% into the package's RoamingState folder;
        // those paths become dead after uninstalling the MSIX and switching to WiX.
        MigrateStaleIconPaths();

        // ── Tray icons (one per launcher) ──────────────────────────
        SetupTrayIcons();

        var asm = typeof(MainWindow).Assembly.GetName();
        SettingsManager.Current.LastKnownVersion = $"v{asm.Version!.Major}.{asm.Version.Minor}.{asm.Version.Build}";

        // Ensure existing Windows startup registry entry includes --silent
        MigrateStartupRegistryEntry();
    }

    private void SetupTrayIcons()
    {
        _wmTrayCallback = RegisterWindowMessage("LittleLauncher_TrayCallback");
        foreach (var launcher in SettingsManager.Current.Launchers)
            CreateTrayIconForLauncher(launcher);
        SaveSettingsIconToAppData();
    }

    private void CreateTrayIconForLauncher(Launcher launcher)
    {
        // Register per-launcher window message for flyout signaling (from companion exe)
        int wmFlyout = RegisterWindowMessage($"LittleLauncher_ShowFlyout_{launcher.Id}");
        _wmShowFlyoutPerLauncher[launcher.Id] = wmFlyout;

        uint uid = _nextIconId++;
        Guid trayGuid = GetTrayIconGuid(launcher.Id);
        var icon = ResolveTrayIcon(launcher);
        SaveResolvedIconToAppData(launcher);

        var entry = new TrayIconEntry { Uid = uid, Guid = trayGuid, Icon = icon };
        _trayIcons[launcher.Id] = entry;

        if (!launcher.NIconHide)
            AddNativeIcon(entry, launcher.Name);

        // Subscribe to launcher property changes to live-update the icon
        launcher.PropertyChanged += (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Launcher.TrayIconMode):
                case nameof(Launcher.CustomTrayIconPath):
                    DispatcherQueue.TryEnqueue(() => UpdateTrayIcon(launcher));
                    break;
                case nameof(Launcher.NIconHide):
                    DispatcherQueue.TryEnqueue(() => UpdateTrayIconVisibility(launcher));
                    break;
                case nameof(Launcher.Name):
                    DispatcherQueue.TryEnqueue(() => UpdateTrayIconTooltip(launcher));
                    break;
            }
        };
    }

    private void AddNativeIcon(TrayIconEntry entry, string? tooltip)
    {
        var tip = (tooltip ?? "");
        if (tip.Length > 127) tip = tip[..127];
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = entry.Uid,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID,
            uCallbackMessage = (uint)_wmTrayCallback,
            hIcon = entry.Icon?.Handle ?? IntPtr.Zero,
            szTip = tip,
            szInfo = "",
            szInfoTitle = "",
            guidItem = entry.Guid,
        };
        Shell_NotifyIcon(NIM_ADD, ref data);
        entry.IsAdded = true;
    }

    private void RemoveNativeIcon(TrayIconEntry entry)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = entry.Uid,
            uFlags = NIF_GUID,
            szTip = "",
            szInfo = "",
            szInfoTitle = "",
            guidItem = entry.Guid,
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        entry.IsAdded = false;
    }

    private void CleanupTrayIcons()
    {
        foreach (var entry in _trayIcons.Values)
        {
            if (entry.IsAdded)
                RemoveNativeIcon(entry);
            entry.Icon?.Dispose();
        }
        _trayIcons.Clear();
    }

    private void ShowContextMenu(int x, int y, string? launcherId = null)
    {
        var popup = new H.NotifyIcon.Core.PopupMenu();

        if (launcherId != null)
        {
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            if (launcher != null)
            {
                var editSettings = new H.NotifyIcon.Core.PopupMenuItem { Text = "Edit Launcher Settings" };
                editSettings.Click += (s, e) =>
                {
                    // Opens directly rather than routing through the settings window.
                    DispatcherQueue.TryEnqueue(() => _ = Windows.LauncherSettingsWindow.ShowAsync(launcher));
                };
                popup.Items.Add(editSettings);

                popup.Items.Add(new H.NotifyIcon.Core.PopupMenuSeparator());
            }
        }

        var settingsItem = new H.NotifyIcon.Core.PopupMenuItem { Text = "App Settings" };
        settingsItem.Click += (s, e) => SettingsWindow.ShowInstance(this);
        popup.Items.Add(settingsItem);
        popup.Items.Add(new H.NotifyIcon.Core.PopupMenuSeparator());
        var exitItem = new H.NotifyIcon.Core.PopupMenuItem { Text = "Exit" };
        exitItem.Click += (s, e) =>
        {
            SettingsManager.SaveSettings();
            try { Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Unregister(); } catch { }
            CleanupTrayIcons();
            Environment.Exit(0);
        };
        popup.Items.Add(exitItem);
        popup.Show(_hwnd, x, y);
    }

    /// <summary>
    /// Rebuilds tray icons for all launchers. Removes icons for deleted launchers,
    /// re-creates icons for new launchers, and re-subscribes PropertyChanged handlers
    /// in case launcher objects were replaced (e.g. by SFTP sync).
    /// </summary>
    public void RefreshTrayIcons()
    {
        var currentIds = SettingsManager.Current.Launchers.Select(l => l.Id).ToHashSet();

        // Remove icons for deleted launchers
        foreach (var id in _trayIcons.Keys.ToList())
        {
            if (!currentIds.Contains(id))
            {
                var entry = _trayIcons[id];
                if (entry.IsAdded) RemoveNativeIcon(entry);
                entry.Icon?.Dispose();
                _trayIcons.Remove(id);
                _wmShowFlyoutPerLauncher.Remove(id);
            }
        }

        // Create icons for new launchers, update existing ones.
        // Use RefreshLauncherIcon (not UpdateTrayIcon) to avoid per-launcher
        // SaveSettingsIconToAppData/CleanUpStaleIconFiles — we do those once below.
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            if (!_trayIcons.ContainsKey(launcher.Id))
                CreateTrayIconForLauncher(launcher);
            else
                RefreshLauncherIcon(launcher);
        }

        SaveSettingsIconToAppData();
        SettingsWindow.GetCurrent()?.RefreshIcon();

        // A launcher that has been switched between kinds still owns the panel it used to use.
        foreach (var launcher in SettingsManager.Current.Launchers)
            LauncherPanels.SyncKind(launcher);

        LauncherPanels.WarmUp(this, SettingsManager.Current.Launchers);
    }

    /// <summary>
    /// Resolves the tray icon for a specific launcher based on its TrayIconMode setting.
    /// </summary>
    private static System.Drawing.Icon? ResolveTrayIcon(Launcher launcher)
    {
        try
        {
            using var bitmap = ResolveBaseIconBitmap(launcher);
            if (bitmap != null)
                return BitmapToIcon(bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to resolve tray icon for launcher {Name}, falling back to Blue", launcher.Name);
        }

        // Last-resort fallback: load the embedded exe icon
        string fallbackIco = Path.Combine(AppContext.BaseDirectory, "Resources", "LittleLauncher.ico");
        return File.Exists(fallbackIco) ? new System.Drawing.Icon(fallbackIco, 64, 64) : null;
    }

    /// <summary>
    /// Preset icon PNG filenames (in Resources/AppIcons/), keyed by TrayIconMode string.
    /// </summary>
    private static readonly Dictionary<string, string> PresetIcons = new()
    {
        { TrayIconModes.Blue, "Blue" },
        { TrayIconModes.Green, "Green" },
        { TrayIconModes.Teal, "Teal" },
        { TrayIconModes.Red, "Red" },
        { TrayIconModes.Orange, "Orange" },
        { TrayIconModes.Purple, "Purple" },
    };

    /// <summary>
    /// Glyph preset icon characters (Segoe Fluent Icons), keyed by TrayIconMode string.
    /// </summary>
    private static readonly Dictionary<string, (char Glyph, string Label)> GlyphPresets = new()
    {
        { TrayIconModes.Pin, ('\uE840', "Pin") },
        { TrayIconModes.Star, ('\uE734', "Star") },
        { TrayIconModes.Heart, ('\uEB51', "Heart") },
        { TrayIconModes.Lightning, ('\uE945', "Lightning") },
        { TrayIconModes.Search, ('\uE721', "Search") },
        { TrayIconModes.Globe, ('\uE774', "Globe") },
    };

    /// <summary>
    /// Single source of truth for rendering a launcher's icon as a 256×256 bitmap.
    /// All icon surfaces (tray, shortcuts, settings window) derive from this.
    /// </summary>
    private static System.Drawing.Bitmap? ResolveBaseIconBitmap(Launcher launcher)
    {
        string mode = launcher.TrayIconMode;

        try
        {
            // Composite mode — 2×2 grid of first 4 item icons
            if (mode == TrayIconModes.Composite)
                return RenderCompositeIconBitmap(launcher);

            // Custom user image
            if (mode == TrayIconModes.Custom)
            {
                string path = launcher.CustomTrayIconPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using var ico = new System.Drawing.Icon(path, 256, 256);
                        return new System.Drawing.Bitmap(ico.ToBitmap(), 256, 256);
                    }
                    using var original = new System.Drawing.Bitmap(path);
                    return TrimAndResizeTo256(original);
                }
            }

            // Glyph presets
            if (GlyphPresets.TryGetValue(mode, out var preset))
            {
                bool dark = ThemeManager.IsDarkTheme();
                var fg = dark ? System.Drawing.Color.White : System.Drawing.Color.Black;
                return RenderGlyphBitmap(preset.Glyph, fg);
            }

            // Gallery-chosen glyph/emoji (stored as "Glyph:X" or "Glyph:#RRGGBB:X" in TrayIconMode)
            if (TrayIconModes.IsGlyphMode(mode))
            {
                string? glyphStr = TrayIconModes.GetGlyphCharacter(mode);
                if (!string.IsNullOrEmpty(glyphStr) && glyphStr.Length > 0)
                {
                    bool dark = ThemeManager.IsDarkTheme();
                    string? colorHex = TrayIconModes.GetGlyphColor(mode);
                    var fg = !string.IsNullOrEmpty(colorHex)
                        ? ParseHexColor(colorHex, dark ? System.Drawing.Color.White : System.Drawing.Color.Black)
                        : (dark ? System.Drawing.Color.White : System.Drawing.Color.Black);
                    string fontName = Classes.IconGallery.IsFluentGlyph(glyphStr)
                        ? "Segoe Fluent Icons"
                        : "Segoe UI Emoji";
                    return RenderGlyphBitmap(glyphStr[0], fg, 256, 240f, fontName);
                }
            }

            // Color presets (fallback to Blue if unknown)
            if (!PresetIcons.TryGetValue(mode, out var name))
                name = PresetIcons[TrayIconModes.Blue];
            string pngPath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", $"{name}.png");
            if (File.Exists(pngPath))
            {
                using var original = new System.Drawing.Bitmap(pngPath);
                return TrimAndResizeTo256(original);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to resolve base icon bitmap for mode {Mode}", mode);
        }
        return null;
    }

    /// <summary>
    /// Renders a Segoe Fluent Icons glyph as a 256×256 bitmap.
    /// </summary>
    private static System.Drawing.Bitmap RenderGlyphBitmap(char glyph, System.Drawing.Color fg)
    {
        return RenderGlyphBitmap(glyph, fg, 256, 240f);
    }

    /// <summary>Parses "#RRGGBB" to a System.Drawing.Color, returning fallback on failure.</summary>
    private static System.Drawing.Color ParseHexColor(string hex, System.Drawing.Color fallback)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                int r = Convert.ToByte(hex[..2], 16);
                int g = Convert.ToByte(hex[2..4], 16);
                int b = Convert.ToByte(hex[4..6], 16);
                return System.Drawing.Color.FromArgb(255, r, g, b);
            }
        }
        catch { /* fall through */ }
        return fallback;
    }

    /// <summary>
    /// Renders a Segoe Fluent Icons glyph at a specified size.
    /// </summary>
    private static System.Drawing.Bitmap RenderGlyphBitmap(char glyph, System.Drawing.Color fg, int size, float fontPx, string fontName = "Segoe Fluent Icons")
    {
        var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var font = new System.Drawing.Font(fontName, fontPx, System.Drawing.GraphicsUnit.Pixel);
            using var brush = new System.Drawing.SolidBrush(fg);
            using var fmt = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic);
            fmt.Alignment = System.Drawing.StringAlignment.Center;
            fmt.LineAlignment = System.Drawing.StringAlignment.Center;
            g.DrawString(glyph.ToString(), font, brush, new System.Drawing.RectangleF(0, 0, size, size), fmt);
        }
        return bitmap;
    }

    /// <summary>
    /// Renders a 2×2 composite icon from the first 4 launchable items, similar to
    /// how Windows 11 Start menu shows folder/group previews.
    /// </summary>
    private static System.Drawing.Bitmap? RenderCompositeIconBitmap(Launcher launcher)
    {
        const int canvasSize = 256;
        const int cellSize = 124;  // each sub-icon cell
        const int gap = 4;         // gap between cells

        // Collect first 4 launchable items (flatten groups)
        var items = new List<LauncherItem>();
        CollectLaunchableItems(launcher.Items, items, 4);
        if (items.Count == 0) return null;

        // 2×2 grid positions, centered on canvas
        int gridWidth = cellSize * 2 + gap;
        int offsetX = (canvasSize - gridWidth) / 2;
        int offsetY = (canvasSize - gridWidth) / 2;
        var positions = new (int X, int Y)[]
        {
            (offsetX, offsetY),
            (offsetX + cellSize + gap, offsetY),
            (offsetX, offsetY + cellSize + gap),
            (offsetX + cellSize + gap, offsetY + cellSize + gap),
        };

        bool dark = ThemeManager.IsDarkTheme();
        var bitmap = new System.Drawing.Bitmap(canvasSize, canvasSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        for (int i = 0; i < Math.Min(items.Count, 4); i++)
        {
            var item = items[i];
            var pos = positions[i];

            // Try to load the item's icon
            System.Drawing.Bitmap? subIcon = null;
            try
            {
                if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
                {
                    if (item.IconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using var ico = new System.Drawing.Icon(item.IconPath, 64, 64);
                        subIcon = new System.Drawing.Bitmap(ico.ToBitmap(), cellSize, cellSize);
                    }
                    else
                    {
                        using var orig = new System.Drawing.Bitmap(item.IconPath);
                        subIcon = new System.Drawing.Bitmap(orig, cellSize, cellSize);
                    }
                }
                else if (!string.IsNullOrEmpty(item.IconGlyph) && item.IconGlyph.Length > 0)
                {
                    var fg = !string.IsNullOrEmpty(item.IconColor)
                        ? ParseHexColor(item.IconColor, dark ? System.Drawing.Color.White : System.Drawing.Color.Black)
                        : (dark ? System.Drawing.Color.White : System.Drawing.Color.Black);
                    string fontName = Classes.IconGallery.IsFluentGlyph(item.IconGlyph)
                        ? "Segoe Fluent Icons"
                        : "Segoe UI Emoji";
                    subIcon = RenderGlyphBitmap(item.IconGlyph[0], fg, cellSize, cellSize * 0.8f, fontName);
                }
            }
            catch { /* best-effort */ }

            if (subIcon == null) continue;

            using (subIcon)
            {
                g.DrawImage(subIcon,
                    new System.Drawing.Rectangle(pos.X, pos.Y, cellSize, cellSize),
                    new System.Drawing.Rectangle(0, 0, subIcon.Width, subIcon.Height),
                    System.Drawing.GraphicsUnit.Pixel);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Collects the first N launchable (non-group, non-heading, non-column-break) items,
    /// flattening groups.
    /// </summary>
    private static void CollectLaunchableItems(IEnumerable<LauncherItem> source, List<LauncherItem> result, int max)
    {
        foreach (var item in source)
        {
            if (result.Count >= max) return;
            if (item.IsColumnBreak) continue;
            if (item.IsGroup)
            {
                CollectLaunchableItems(item.Children, result, max);
                continue;
            }
            // Headings have no Path and aren't launchable
            if (string.IsNullOrEmpty(item.Path)) continue;
            result.Add(item);
        }
    }

    /// <summary>Creates a GraphicsPath for a rounded rectangle.</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(int x, int y, int w, int h, int r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Trims transparent padding and centers the content on a 256×256 canvas,
    /// preserving the original aspect ratio.
    /// </summary>
    private static System.Drawing.Bitmap TrimAndResizeTo256(System.Drawing.Bitmap original)
    {
        var bounds = GetOpaqueContentBounds(original);
        int contentSize = Math.Max(bounds.Width, bounds.Height);
        if (contentSize <= 0) contentSize = original.Width;

        // Fill 100% of the canvas so icons appear full-size in tray/taskbar
        float scale = 256f / contentSize;
        int drawW = (int)(bounds.Width * scale);
        int drawH = (int)(bounds.Height * scale);
        int offsetX = (256 - drawW) / 2;
        int offsetY = (256 - drawH) / 2;

        const int iconSize = 256;
        var resized = new System.Drawing.Bitmap(iconSize, iconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(resized))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.Clear(System.Drawing.Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            // Draw only the trimmed content region, preserving aspect ratio
            var dest = new System.Drawing.Rectangle(offsetX, offsetY, drawW, drawH);
            g.DrawImage(original, dest, bounds, System.Drawing.GraphicsUnit.Pixel);
        }
        return resized;
    }

    /// <summary>
    /// Returns the bounding rectangle of non-transparent pixels in a bitmap.
    /// </summary>
    private static System.Drawing.Rectangle GetOpaqueContentBounds(System.Drawing.Bitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A > 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < minX) return new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        return new System.Drawing.Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Converts a Bitmap to an Icon via an in-memory multi-resolution ICO stream.
    /// Produces entries at 16, 24, 32, 48, 64, and 256 pixels so the system tray
    /// can pick the correct size for the current DPI.
    /// </summary>
    private static System.Drawing.Icon BitmapToIcon(System.Drawing.Bitmap bitmap)
    {
        var icoBytes = BitmapToIcoBytes(bitmap);
        using var ms = new MemoryStream(icoBytes);
        return new System.Drawing.Icon(ms);
    }

    /// <summary>
    /// Renders a Bitmap as a multi-resolution ICO byte array (16–256 px, PNG-compressed).
    /// Used for both in-memory Icon creation and direct file writes.
    /// Writing these bytes directly to disk avoids System.Drawing.Icon.Save() which
    /// is known to lose multi-resolution data on .NET.
    /// </summary>
    private static byte[] BitmapToIcoBytes(System.Drawing.Bitmap bitmap)
    {
        int[] sizes = [16, 24, 32, 48, 64, 256];
        byte[][] pngEntries = new byte[sizes.Length][];

        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            using var resized = new System.Drawing.Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(resized))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(bitmap, 0, 0, s, s);
            }
            using var pngStream = new MemoryStream();
            resized.Save(pngStream, ImageFormat.Png);
            pngEntries[i] = pngStream.ToArray();
        }

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ICO header
            bw.Write((short)0);                     // reserved
            bw.Write((short)1);                     // type: icon
            bw.Write((short)sizes.Length);           // image count

            // Directory entries — offset starts after header + all directory entries
            int dataOffset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));  // width
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));  // height
                bw.Write((byte)0);            // color palette
                bw.Write((byte)0);            // reserved
                bw.Write((short)1);           // color planes
                bw.Write((short)32);          // bits per pixel
                bw.Write(pngEntries[i].Length); // image data size
                bw.Write(dataOffset);         // offset to image data

                dataOffset += pngEntries[i].Length;
            }

            // PNG payloads
            for (int i = 0; i < sizes.Length; i++)
                bw.Write(pngEntries[i]);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Ensures the per-launcher .ico file exists in AppData. Called before
    /// pinning to the taskbar so the companion exe and Start Menu shortcut
    /// can reference the correct icon.
    /// </summary>
    internal static void EnsureLauncherIconSaved(Launcher launcher) =>
        SaveResolvedIconToAppData(launcher);

    /// <summary>
    /// Persists a launcher's resolved icon as an .ico file in AppData so that
    /// shortcuts (Start Menu, pinned taskbar) can reference it.
    /// Returns the path to the saved .ico, or null on failure.
    /// The first launcher's icon is also saved as the canonical app-icon.ico.
    /// </summary>
    private static string? SaveResolvedIconToAppData(Launcher launcher)
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            Directory.CreateDirectory(appDataDir);

            // Per-launcher icon file
            string launcherIcoPath = Path.Combine(appDataDir, $"app-icon-{launcher.Id}.ico");

            using var bitmap = ResolveBaseIconBitmap(launcher);
            if (bitmap == null) return null;

            var icoBytes = BitmapToIcoBytes(bitmap);

            File.WriteAllBytes(launcherIcoPath, icoBytes);

            // Also update the canonical app-icon.ico if this is the first (or only) launcher
            var firstLauncher = SettingsManager.Current.Launchers.FirstOrDefault();
            if (firstLauncher != null && firstLauncher.Id == launcher.Id)
            {
                string canonicalPath = Path.Combine(appDataDir, "app-icon.ico");
                File.Copy(launcherIcoPath, canonicalPath, overwrite: true);
            }

            return launcherIcoPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save resolved icon to AppData for launcher {Name}", launcher.Name);
        }
        return null;
    }

    /// <summary>
    /// Generates a settings-specific icon by compositing the app's blue rocket icon
    /// with a small gear overlay in the bottom-right corner.
    /// Saved as settings-icon.ico alongside app-icon.ico.
    /// </summary>
    internal static string? SaveSettingsIconToAppData()
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            Directory.CreateDirectory(appDataDir);
            string icoPath = Path.Combine(appDataDir, "settings-icon.ico");

            // Always use the blue rocket (app identity) as the base icon
            string bluePng = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", "Blue.png");
            if (!File.Exists(bluePng)) return null;
            using var original = new System.Drawing.Bitmap(bluePng);
            using var baseBitmap = TrimAndResizeTo256(original);
            if (baseBitmap == null) return null;

            // Draw gear overlay in the bottom-right corner
            const int overlaySize = 112; // ~44% of 256
            const int padding = 4;
            using (var g = System.Drawing.Graphics.FromImage(baseBitmap))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                // Semi-transparent dark circle background for contrast
                int cx = 256 - overlaySize / 2 - padding;
                int cy = 256 - overlaySize / 2 - padding;
                using var bgBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 30, 30, 30));
                g.FillEllipse(bgBrush, cx - overlaySize / 2, cy - overlaySize / 2, overlaySize, overlaySize);

                // Gear glyph (\uE713 = Settings in Segoe Fluent Icons)
                using var font = new System.Drawing.Font("Segoe Fluent Icons", overlaySize * 0.7f, System.Drawing.GraphicsUnit.Pixel);
                using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                using var fmt = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic);
                fmt.Alignment = System.Drawing.StringAlignment.Center;
                fmt.LineAlignment = System.Drawing.StringAlignment.Center;
                var rect = new System.Drawing.RectangleF(
                    cx - overlaySize / 2f, cy - overlaySize / 2f,
                    overlaySize, overlaySize);
                g.DrawString("\uE713", font, brush, rect, fmt);
            }

            File.WriteAllBytes(icoPath, BitmapToIcoBytes(baseBitmap));
            return icoPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save settings icon to AppData");
        }
        return null;
    }

    /// <summary>
    /// Updates the tray icon for a specific launcher and refreshes associated shortcuts.
    /// Called from the launcher's PropertyChanged handler for single-launcher changes.
    /// For batch updates (sync, startup), use <see cref="RefreshLauncherIcon"/> and
    /// call <see cref="SaveSettingsIconToAppData"/> once at the end.
    /// </summary>
    internal void UpdateTrayIcon(Launcher launcher)
    {
        RefreshLauncherIcon(launcher);
        SaveSettingsIconToAppData();
        CleanUpStaleIconFiles();
        SettingsWindow.GetCurrent()?.RefreshIcon();
    }

    /// <summary>
    /// Refreshes a single launcher's tray icon and persists its .ico to disk.
    /// Does NOT update settings-icon.ico or clean up stale files — callers
    /// that process multiple launchers should do that once at the end.
    /// </summary>
    private void RefreshLauncherIcon(Launcher launcher)
    {
        if (!_trayIcons.TryGetValue(launcher.Id, out var entry)) return;
        entry.Icon?.Dispose();
        entry.Icon = ResolveTrayIcon(launcher);
        if (entry.IsAdded)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = entry.Uid,
                uFlags = NIF_ICON | NIF_GUID,
                hIcon = entry.Icon?.Handle ?? IntPtr.Zero,
                szTip = "",
                szInfo = "",
                szInfoTitle = "",
                guidItem = entry.Guid,
            };
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        SaveResolvedIconToAppData(launcher);
    }

    /// <summary>
    /// Cleans up stale versioned icon files from AppData.
    /// Windows 11's taskbar does not re-read pinned icon bitmaps at runtime,
    /// so we no longer attempt to update pinned .lnk icons or restart Explorer.
    /// To change a pinned icon, users must unpin and re-pin the launcher.
    /// </summary>
    private static void CleanUpStaleIconFiles()
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            // Remove versioned icon copies (app-icon-{id}-v*.ico)
            foreach (string f in Directory.GetFiles(appDataDir, "app-icon-*-v*.ico"))
                try { File.Delete(f); } catch { }

            // For pin copies (app-icon-{id}-pin*.ico), keep the most recent per launcher.
            // RelaunchIconResource points to the timestamped pin copy, so deleting ALL
            // of them would make existing pinned icons go blank. Windows also caches
            // icon bitmaps per file path, so we need unique filenames per pin attempt.
            var pinFiles = Directory.GetFiles(appDataDir, "app-icon-*-pin*.ico");
            var pinGroups = new Dictionary<string, List<string>>();
            foreach (string f in pinFiles)
            {
                // Extract launcher ID: "app-icon-{id}-pin{tick}.ico"
                string name = Path.GetFileNameWithoutExtension(f);
                int pinIdx = name.LastIndexOf("-pin", StringComparison.Ordinal);
                if (pinIdx > 0)
                {
                    string prefix = name[..pinIdx]; // "app-icon-{id}"
                    if (!pinGroups.TryGetValue(prefix, out var list))
                        pinGroups[prefix] = list = [];
                    list.Add(f);
                }
            }
            foreach (var group in pinGroups.Values)
            {
                if (group.Count <= 1) continue;
                // Keep the newest (highest LastWriteTime), delete the rest
                group.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                for (int i = 1; i < group.Count; i++)
                    try { File.Delete(group[i]); } catch { }
            }

            // Legacy single-icon versioned files
            foreach (string f in Directory.GetFiles(appDataDir, "app-icon-v*.ico"))
                try { File.Delete(f); } catch { }
            string altPath = Path.Combine(appDataDir, "app-icon-alt.ico");
            if (File.Exists(altPath))
                try { File.Delete(altPath); } catch { }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to clean up stale icon files");
        }
    }

    internal void UpdateTrayIconVisibility(Launcher launcher)
    {
        if (!_trayIcons.TryGetValue(launcher.Id, out var entry)) return;
        if (launcher.NIconHide && entry.IsAdded)
            RemoveNativeIcon(entry);
        else if (!launcher.NIconHide && !entry.IsAdded)
            AddNativeIcon(entry, launcher.Name);
    }

    private void UpdateTrayIconTooltip(Launcher launcher)
    {
        if (!_trayIcons.TryGetValue(launcher.Id, out var entry) || !entry.IsAdded) return;
        var tip = (launcher.Name ?? "");
        if (tip.Length > 127) tip = tip[..127];
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = entry.Uid,
            uFlags = NIF_TIP | NIF_GUID,
            szTip = tip,
            szInfo = "",
            szInfoTitle = "",
            guidItem = entry.Guid,
        };
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private static Guid GetTrayIconGuid(string launcherId)
    {
        if (Guid.TryParse(launcherId, out var parsedGuid))
            return parsedGuid;

        byte[] hash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes("LittleLauncher.Tray." + launcherId));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    private void OnSystemThemeChanged(global::Windows.UI.ViewManagement.UISettings sender, object args)
    {
        bool dark = Classes.ThemeManager.IsDarkTheme();
        if (dark == _lastDarkTheme) return;
        _lastDarkTheme = dark;

        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var launcher in SettingsManager.Current.Launchers)
                RefreshLauncherIcon(launcher);
            SaveSettingsIconToAppData();
            SettingsWindow.GetCurrent()?.RefreshIcon();
        });
    }

    private async Task StartAutoSyncAsync()
    {
        await AutoSyncService.SyncOnStartupAsync();
        AutoSyncService.Start();
    }

    /// <summary>
    /// Scans launcher items on startup, fetches any missing icons, and refreshes
    /// stale auto-fetched icons (favicons, app icons, PWA icons older than
    /// <see cref="FaviconService.IconMaxAge"/>). Custom icons are never touched.
    /// Covers settings-import-then-restart and machine-migration scenarios.
    /// </summary>
    private async Task FetchMissingIconsOnStartupAsync()
    {
        try
        {
            // Check across all launchers' items for missing icons
            bool anyMissing = false;
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                var allItems = launcher.Items.SelectMany(i => i.IsGroup ? new[] { i }.Concat(i.Children) : [i]);
                if (allItems.Any(i =>
                    !i.IsGroup &&
                    !string.IsNullOrWhiteSpace(i.Path) &&
                    (string.IsNullOrEmpty(i.IconPath) || !File.Exists(i.IconPath))))
                {
                    anyMissing = true;
                    break;
                }
            }

            if (anyMissing)
            {
                foreach (var launcher in SettingsManager.Current.Launchers)
                    await FaviconService.FetchMissingItemIconsAsync(launcher.Items);
            }

            bool anyRefreshed = await RefreshStaleIconsCoreAsync();

            if (!anyMissing && !anyRefreshed) return;

            ApplyItemIconChanges();
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Startup icon fetch failed");
        }
    }

    /// <summary>
    /// Runs the stale-icon refresh across all launchers with one shared
    /// visited-path set so items sharing a favicon aren't fetched twice.
    /// </summary>
    private static async Task<bool> RefreshStaleIconsCoreAsync()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var launcher in SettingsManager.Current.Launchers)
            changed |= await FaviconService.RefreshStaleItemIconsAsync(launcher.Items, visited);
        return changed;
    }

    /// <summary>
    /// Persists item icon changes and pushes them to every surface: flyouts,
    /// tray icons (Composite mode reads item icons), and the Settings window icon.
    /// </summary>
    private void ApplyItemIconChanges()
    {
        SettingsManager.SaveSettings();
        FlyoutWindow.InvalidateAllItems();

        // Re-save tray icons now that item icons have changed. This matters for
        // Composite mode, whose bitmap is built from the first 4 item icons.
        // Use RefreshLauncherIcon (batch-friendly) and SaveSettingsIconToAppData once.
        foreach (var launcher in SettingsManager.Current.Launchers)
            RefreshLauncherIcon(launcher);
        SaveSettingsIconToAppData();
        SettingsWindow.GetCurrent()?.RefreshIcon();
    }

    /// <summary>
    /// Periodic stale-icon refresh while the app stays resident in the tray.
    /// The daily cadence only checks file timestamps; downloads happen when a
    /// cached icon crosses <see cref="FaviconService.IconMaxAge"/>.
    /// </summary>
    private async Task RefreshStaleIconsPeriodicAsync()
    {
        try
        {
            if (!await RefreshStaleIconsCoreAsync()) return;
            ApplyItemIconChanges();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Periodic icon refresh failed");
        }
    }

    /// <summary>
    /// Checks for app updates on startup. In unpackaged builds this also shows
    /// a toast notification; in packaged builds it only caches the result for UI use.
    /// The result is cached in
    /// <see cref="UpdateService.LatestResult"/> for the HomePage to consume.
    /// </summary>
    private async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            var result = await UpdateService.CheckForUpdateAsync();
            if (result is not { UpdateAvailable: true }) return;
            if (IsPackaged) return;

            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText("Update Available")
                .AddText($"Little Launcher {result.LatestVersion} is available. You are running {result.CurrentVersion}.");
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Startup update check failed");
        }
    }

    // ── WndProc — handle cross-process PostMessage IPC ────────────

    /// <summary>
    /// Returns true when the app should start silently (tray only, no Settings window).
    /// Silent launch happens on Windows startup and when the companion exe cold-starts the app.
    /// </summary>
    private static bool IsSilentLaunch()
    {
        // Command-line flag used by unpackaged startup (registry Run key)
        // and companion exe cold-start.
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--silent", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // MSIX StartupTask activation. The manifest declares a StartupTask
        // with no way to pass args, so detect it via the AppLifecycle API.
        // In non-MSIX builds, GetActivatedEventArgs() throws — the catch
        // handles that gracefully.
        try
        {
            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask)
                return true;
        }
        catch { /* Not available or not packaged — treat as normal launch */ }

        return false;
    }

    /// <summary>
    /// Ensures the Windows startup registry entry includes --silent so the app
    /// starts silently on login. Migrates existing entries that lack the flag.
    /// Runs unconditionally so entries created by old versions (before --silent
    /// <summary>
    /// Clears icon paths that point to non-existent files, typically caused by
    /// switching install types (MSIX → WiX or vice versa). MSIX VFS-redirects
    /// %AppData% into the package's RoamingState folder; those paths become dead
    /// after uninstalling the MSIX. Clearing them lets FetchMissingIconsOnStartupAsync
    /// re-fetch into the current cache directory, and lets ResolveBaseIconBitmap
    /// fall back to color presets instead of returning null.
    /// </summary>
    private static void MigrateStaleIconPaths()
    {
        bool changed = false;
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            // Custom tray icon path
            if (!string.IsNullOrEmpty(launcher.CustomTrayIconPath) && !File.Exists(launcher.CustomTrayIconPath))
            {
                launcher.CustomTrayIconPath = "";
                changed = true;
            }

            // Item icon paths (recursive into groups)
            changed |= ClearStaleItemIconPaths(launcher.Items);
        }
        if (changed)
            SettingsManager.SaveSettings();
    }

    private static bool ClearStaleItemIconPaths(IEnumerable<LauncherItem> items)
    {
        bool changed = false;
        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                changed |= ClearStaleItemIconPaths(item.Children);
                continue;
            }
            if (!string.IsNullOrEmpty(item.IconPath) && !File.Exists(item.IconPath))
            {
                item.IconPath = "";
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Ensures existing Windows startup registry entry includes --silent.
    /// Old versions registered LittleLauncher.exe without the flag, which caused
    /// the Settings window to appear on login. This ensures entries from before v1.7
    /// (when --silent was added) are fixed regardless of the in-app Startup setting.
    /// </summary>
    private static void MigrateStartupRegistryEntry()
    {
        try
        {
            if (IsPackaged)
            {
                RemoveStartupRegistryEntry();
                return;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            const string appName = "Little Launcher";
            var currentValue = key.GetValue(appName) as string;
            if (currentValue != null && !currentValue.Contains("--silent", StringComparison.OrdinalIgnoreCase))
            {
                var executablePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
                    key.SetValue(appName, $"\"{executablePath}\" --silent");
            }
        }
        catch { /* best-effort migration */ }
    }

    internal static void RemoveStartupRegistryEntry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key?.GetValue("Little Launcher") != null)
                key.DeleteValue("Little Launcher", false);
        }
        catch { /* best-effort cleanup */ }
    }

    private bool TryResolveLauncherAnchorPoint(string launcherId, int fallbackX, int fallbackY, out int anchorX, out int anchorY)
    {
        anchorX = fallbackX;
        anchorY = fallbackY;

        var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
        if (launcher == null)
            return false;

        try
        {
            var element = FindTaskbarLauncherElement(launcher.Name);
            if (element == null)
                return false;

            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                return false;

            anchorX = (int)Math.Round(bounds.Left + (bounds.Width / 2.0));
            anchorY = (int)Math.Round(bounds.Top + (bounds.Height / 2.0));
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static AutomationElement? FindTaskbarLauncherElement(string launcherName)
    {
        string expectedName = $"Little Launcher - {launcherName}";

        var focusedMatch = MatchLauncherElementOrAncestor(AutomationElement.FocusedElement, expectedName, launcherName);
        if (focusedMatch != null)
            return focusedMatch;

        var root = AutomationElement.RootElement;
        if (root == null)
            return null;

        foreach (string taskbarClass in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var taskbar = root.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, taskbarClass));
            if (taskbar == null)
                continue;

            var exactMatch = taskbar.FindFirst(
                TreeScope.Descendants,
                BuildLauncherButtonCondition(expectedName));
            if (exactMatch != null)
                return exactMatch;

            var partialMatch = FindLauncherButtonBySubstring(taskbar, launcherName);
            if (partialMatch != null)
                return partialMatch;
        }

        return null;
    }

    private static AutomationElement? MatchLauncherElementOrAncestor(AutomationElement? element, string expectedName, string launcherName)
    {
        while (element != null)
        {
            if (IsLauncherButtonMatch(element, expectedName, launcherName))
                return element;

            try
            {
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        return null;
    }

    private static Condition BuildLauncherButtonCondition(string buttonName)
    {
        return new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, buttonName));
    }

    private static AutomationElement? FindLauncherButtonBySubstring(AutomationElement taskbar, string launcherName)
    {
        var buttons = taskbar.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            if (IsLauncherButtonMatch(button, null, launcherName))
                return button;
        }

        return null;
    }

    private static bool IsLauncherButtonMatch(AutomationElement element, string? expectedName, string launcherName)
    {
        string name;
        try
        {
            name = element.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!string.IsNullOrEmpty(expectedName) &&
            string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Contains(launcherName, StringComparison.OrdinalIgnoreCase) &&
               name.Contains("Little Launcher", StringComparison.OrdinalIgnoreCase);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        // Handle tray icon left/right click callbacks from Shell_NotifyIcon
        if ((int)msg == _wmTrayCallback && _wmTrayCallback != 0)
        {
            uint uid = (uint)(wParam.ToInt64() & 0xFFFFFFFF);
            int notification = (int)(lParam.ToInt64() & 0xFFFF);

            string? targetLauncherId = null;
            foreach (var (lid, entry) in _trayIcons)
            {
                if (entry.Uid == uid) { targetLauncherId = lid; break; }
            }

            if (targetLauncherId != null)
            {
                if (notification == WM_LBUTTONUP)
                {
                    GetCursorPos(out var pt);
                    string lid = targetLauncherId;
                    DispatcherQueue.TryEnqueue(() => LauncherPanels.Toggle(this, pt.X, pt.Y, lid));
                }
                else if (notification == WM_RBUTTONUP || notification == WM_CONTEXTMENU)
                {
                    GetCursorPos(out var pt);
                    int cx = pt.X, cy = pt.Y;
                    string lid = targetLauncherId;
                    DispatcherQueue.TryEnqueue(() => ShowContextMenu(cx, cy, lid));
                }
            }
            return IntPtr.Zero;
        }

        // Iterate per-launcher window messages (from companion exe / LauncherShortcut)
        foreach (var (launcherId, wmShowFlyout) in _wmShowFlyoutPerLauncher)
        {
            if ((int)msg == wmShowFlyout && wmShowFlyout != 0)
            {
                // Resolve which launcher to show: use named launcherId, fall back to first launcher
                var targetId = launcherId;
                if (string.IsNullOrEmpty(targetId))
                    targetId = SettingsManager.Current.Launchers.FirstOrDefault()?.Id ?? string.Empty;
                int anchorX = (int)wParam;
                int anchorY = (int)lParam;
                TryResolveLauncherAnchorPoint(targetId, anchorX, anchorY, out anchorX, out anchorY);
                DispatcherQueue.TryEnqueue(() => LauncherPanels.Toggle(this, anchorX, anchorY, targetId));
                return IntPtr.Zero;
            }
        }

        if ((int)msg == _wmShowSettings && _wmShowSettings != 0)
        {
            DispatcherQueue.TryEnqueue(() => SettingsWindow.ShowInstance(this));
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    // ── Shortcut management ──────────────────────────────────────────

    /// <summary>
    /// Returns the icon location string for shortcuts.
    /// Uses the saved AppData icon if custom, otherwise the app exe icon.
    /// </summary>
    private static string GetShortcutIconLocation(string fallback)
    {
        string appDataIcon = Path.Combine(
            GetPhysicalAppDataDir(), "app-icon.ico");
        return File.Exists(appDataIcon) ? $"{appDataIcon},0" : fallback;
    }

    /// <summary>
    /// Removes stale per-launcher flyout shortcuts from the Start Menu.
    /// Previous versions created these for taskbar pin identity; pinning is
    /// now handled entirely by the companion exe's relaunch properties.
    /// </summary>
    private static void CleanUpStaleFlyoutShortcuts()
    {
        CleanUpStaleFlyoutShortcuts(GetStartMenuProgramsDir());

        if (IsPackaged)
            CleanUpStaleFlyoutShortcuts(GetLegacySharedStartMenuProgramsDir());
    }

    private static void CleanUpStaleFlyoutShortcuts(string startMenuDir)
    {
        try
        {
            if (!Directory.Exists(startMenuDir)) return;
            foreach (var pattern in new[] { "Little Launcher - *.lnk", "Little Launcher Flyout - *.lnk", "Little Launcher Flyout.lnk" })
            {
                foreach (var lnk in Directory.GetFiles(startMenuDir, pattern))
                {
                    try { File.Delete(lnk); } catch { }
                }
            }
        }
        catch { /* best-effort */ }
    }

    private static void EnsureStartMenuShortcuts()
    {
        // Clean up per-launcher flyout shortcuts from previous versions.
        // Pinning is now handled entirely by the companion exe's relaunch
        // properties — no Start Menu flyout shortcuts needed.
        CleanUpStaleFlyoutShortcuts();

        // MSIX packages register their own Start Menu entry via the manifest.
        // Remove any stale unpackaged shortcut so Windows only sees the
        // packaged entry, then stop before creating a duplicate .lnk.
        if (IsPackaged)
        {
            CleanUpLegacyUnpackagedMainShortcut();
            return;
        }

        try
        {
            string startMenuDir = GetStartMenuProgramsDir();

            string exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return;

            Directory.CreateDirectory(startMenuDir);

            // Remove old-named shortcuts (renamed to "Little Launcher")
            foreach (var old in new[] { "LittleLauncher Settings.lnk", "LittleLauncher.lnk" })
            {
                string oldLnk = Path.Combine(startMenuDir, old);
                if (File.Exists(oldLnk))
                    File.Delete(oldLnk);
            }

            // Remove MSI-era shortcut that lived in a "Little Launcher" subfolder
            string msiSubfolder = Path.Combine(startMenuDir, "Little Launcher");
            if (Directory.Exists(msiSubfolder))
            {
                try { Directory.Delete(msiSubfolder, true); }
                catch { /* best-effort */ }
            }

            // Single Start Menu shortcut — always opens settings when clicked.
            // Always use the exe's embedded icon (blue rocket) for the app identity.
            CreateOrUpdateShortcut(
                Path.Combine(startMenuDir, "Little Launcher.lnk"),
                exePath,
                "--settings",
                "Little Launcher",
                $"{exePath},0");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create Start Menu shortcuts");
        }
    }

    private static void CleanUpLegacyUnpackagedMainShortcut()
    {
        try
        {
            string startMenuDir = GetLegacySharedStartMenuProgramsDir();
            if (!Directory.Exists(startMenuDir)) return;

            foreach (string shortcutName in new[] { "Little Launcher.lnk", "LittleLauncher.lnk", "LittleLauncher Settings.lnk" })
            {
                string shortcutPath = Path.Combine(startMenuDir, shortcutName);
                if (File.Exists(shortcutPath))
                    File.Delete(shortcutPath);
            }

            string msiSubfolder = Path.Combine(startMenuDir, "Little Launcher");
            if (Directory.Exists(msiSubfolder))
            {
                try { Directory.Delete(msiSubfolder, true); }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Copies the companion flyout exe to %AppData%\LittleLauncher\ so it has
    /// a consistent, non-packaged location for all build types (WiX, MSIX,
    /// unpackaged). In packaged builds, also mirrors the helper into the legacy
    /// shared Roaming path so old unpackaged launcher pins stop cold-starting a
    /// stale debug/WiX build. Also writes a main-exe-path.txt breadcrumb and
    /// cleans up legacy Start Menu shortcuts from previous versions.
    /// </summary>
    private static void EnsureFlyoutShortcut()
    {
        try
        {
            // Source: the companion exe next to the main exe
            string sourceExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LittleLauncherFlyout.exe");
            if (!File.Exists(sourceExe))
                return;
            string mainExePath = Environment.ProcessPath ?? "";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            foreach (string targetDir in GetFlyoutCompanionTargetDirs())
            {
                Directory.CreateDirectory(targetDir);

                string destExe = Path.Combine(targetDir, "LittleLauncherFlyout.exe");
                File.Copy(sourceExe, destExe, overwrite: true);

                // In debug (framework-dependent) builds, the companion exe also
                // needs its .dll, .deps.json, and .runtimeconfig.json to run.
                // Release builds are Native AOT single-file and don't need these.
                foreach (var ext in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                {
                    string src = Path.Combine(baseDir, "LittleLauncherFlyout" + ext);
                    if (File.Exists(src))
                        File.Copy(src, Path.Combine(targetDir, "LittleLauncherFlyout" + ext), overwrite: true);
                }

                // Write a breadcrumb so the companion exe can launch the main app
                // if it isn't running (FindWindow returns null).
                if (!string.IsNullOrEmpty(mainExePath))
                    File.WriteAllText(Path.Combine(targetDir, "main-exe-path.txt"), mainExePath);
            }

            // Remove the old Start Menu shortcut (no longer created — the
            // pin-to-taskbar button in Settings handles pinning directly).
            string oldLnk = Path.Combine(
                GetStartMenuProgramsDir(),
                "Little Launcher Flyout.lnk");
            if (File.Exists(oldLnk))
                File.Delete(oldLnk);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create flyout shortcut");
        }
    }

    private static IEnumerable<string> GetFlyoutCompanionTargetDirs()
    {
        yield return GetPhysicalAppDataDir();

        if (IsPackaged)
        {
            string legacyDir = GetLegacySharedAppDataDir();
            if (!string.Equals(legacyDir, GetPhysicalAppDataDir(), StringComparison.OrdinalIgnoreCase))
                yield return legacyDir;
        }
    }

    /// <summary>
    /// Updates the icon on the main Start Menu shortcut (non-MSIX only).
    private static void CreateOrUpdateShortcut(
        string shortcutPath, string exePath, string? arguments,
        string description, string iconLocation)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return;

        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = exePath;
                if (arguments != null)
                    shortcut.Arguments = arguments;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = description;
                shortcut.IconLocation = iconLocation;
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}
