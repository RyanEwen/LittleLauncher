using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Windows;
using LittleLauncher.Services;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.Drawing.Imaging;
using System.IO;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;

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

    private static readonly Mutex Singleton = new(true, "LittleLauncher");

    private static int _wmShowFlyout;
    private static int _wmShowSettings;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;

    internal H.NotifyIcon.TaskbarIcon? nIcon;
    private readonly global::Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _lastDarkTheme;

    public MainWindow()
    {
        // ── Singleton check ─────────────────────────────────────────
        if (!Singleton.WaitOne(TimeSpan.Zero, true))
        {
            // Another instance is running — signal it and exit.
            IntPtr target = FindWindow(null, "Little Launcher Host");

            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && args[1] == "--settings")
            {
                int msg = RegisterWindowMessage("LittleLauncher_ShowSettings");
                if (target != IntPtr.Zero && msg != 0)
                    PostMessage(target, msg, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                GetCursorPos(out var pt);
                int msg = RegisterWindowMessage("LittleLauncher_ShowFlyout");
                if (target != IntPtr.Zero && msg != 0)
                    PostMessage(target, msg, (IntPtr)pt.X, (IntPtr)pt.Y);
            }

            Environment.Exit(0);
        }

        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        Logger.Info("Starting Little Launcher MainWindow");

        // Register the window message for cross-process flyout signaling
        _wmShowFlyout = RegisterWindowMessage("LittleLauncher_ShowFlyout");
        _wmShowSettings = RegisterWindowMessage("LittleLauncher_ShowSettings");

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
        UpdateShortcutIcons();
        FlyoutWindow.WarmUp(this);
        _ = StartAutoSyncAsync();

        // Listen for OS theme changes to refresh icons
        _lastDarkTheme = Classes.ThemeManager.IsDarkTheme();
        _uiSettings.ColorValuesChanged += OnSystemThemeChanged;

        // Show settings if launched with --settings (e.g. from Start Menu shortcut)
        string[] cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length > 1 && cmdArgs[1] == "--settings")
        {
            SettingsWindow.ShowInstance(this);
        }
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    /// <summary>
    /// Finish initialization that needs the window to be set up.
    /// </summary>
    private void ApplyDeferredInit()
    {
        // Apply theme
        ThemeManager.ApplySavedTheme(this);

        // ── Tray icon ──────────────────────────────────────────────
        SetupTrayIcon();

        // Show settings on first run
        if (string.IsNullOrEmpty(SettingsManager.Current.LastKnownVersion))
        {
            SettingsWindow.ShowInstance(this);
        }

        var asm = typeof(MainWindow).Assembly.GetName();
        SettingsManager.Current.LastKnownVersion = $"v{asm.Version!.Major}.{asm.Version.Minor}.{asm.Version.Build}";
    }

    private void SetupTrayIcon()
    {
        if (nIcon != null)
        {
            nIcon.Dispose();
            nIcon = null;
        }

        nIcon = new H.NotifyIcon.TaskbarIcon();
        nIcon.NoLeftClickDelay = true;
        nIcon.ToolTipText = "Little Launcher";

        // Use icon based on current settings
        nIcon.Icon = ResolveTrayIcon();
        SaveSettingsIconToAppData();

        // Subscribe to left-click for instant flyout toggle.
        // NoLeftClickDelay skips the double-click wait interval.
        nIcon.LeftClickCommand = new RelayCommand(() =>
        {
            GetCursorPos(out var pt);
            DispatcherQueue.TryEnqueue(() => FlyoutWindow.Toggle(this, pt.X, pt.Y));
        });

        // Context menu via RightClickCommand showing a native popup
        nIcon.RightClickCommand = new RelayCommand(() =>
        {
            GetCursorPos(out var pt);
            var popup = new H.NotifyIcon.Core.PopupMenu();
            var settingsItem = new H.NotifyIcon.Core.PopupMenuItem { Text = "Settings" };
            settingsItem.Click += (s, e) =>
                DispatcherQueue.TryEnqueue(() => SettingsWindow.ShowInstance(this));
            popup.Items.Add(settingsItem);
            popup.Items.Add(new H.NotifyIcon.Core.PopupMenuSeparator());
            var exitItem = new H.NotifyIcon.Core.PopupMenuItem { Text = "Exit" };
            exitItem.Click += (s, e) =>
            {
                SettingsManager.SaveSettings();
                nIcon?.Dispose();
                Environment.Exit(0);
            };
            popup.Items.Add(exitItem);
            popup.Show(_hwnd, pt.X, pt.Y);
        });
        nIcon.MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick;

        nIcon.Visibility = SettingsManager.Current.NIconHide
            ? Visibility.Collapsed
            : Visibility.Visible;

        nIcon.ForceCreate();
    }

    /// <summary>
    /// Resolves the tray icon based on the current TrayIconMode setting.
    /// </summary>
    private static System.Drawing.Icon? ResolveTrayIcon()
    {
        try
        {
            using var bitmap = ResolveBaseIconBitmap();
            if (bitmap != null)
                return BitmapToIcon(bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to resolve tray icon, falling back to Blue");
        }

        // Last-resort fallback: load the embedded exe icon
        string fallbackIco = Path.Combine(AppContext.BaseDirectory, "Resources", "LittleLauncher.ico");
        return File.Exists(fallbackIco) ? new System.Drawing.Icon(fallbackIco, 64, 64) : null;
    }

    /// <summary>
    /// Preset icon PNG filenames (in Resources/AppIcons/).
    /// </summary>
    private static readonly Dictionary<int, string> PresetIcons = new()
    {
        { 0, "Blue" },
        { 1, "Green" },
        { 2, "Teal" },
        { 3, "Red" },
        { 4, "Orange" },
        { 5, "Purple" },
    };

    /// <summary>
    /// Glyph preset icon characters (Segoe Fluent Icons).
    /// </summary>
    private static readonly Dictionary<int, (char Glyph, string Name)> GlyphPresets = new()
    {
        { 6, ('\uE840', "Pin") },
        { 7, ('\uE734', "Star") },
        { 8, ('\uEB51', "Heart") },
        { 9, ('\uE945', "Lightning") },
        { 10, ('\uE721', "Search") },
        { 11, ('\uE774', "Globe") },
    };

    /// <summary>
    /// Single source of truth for rendering the current app icon as a 256×256 bitmap.
    /// All icon surfaces (tray, shortcuts, settings window) derive from this.
    /// </summary>
    private static System.Drawing.Bitmap? ResolveBaseIconBitmap()
    {
        int mode = SettingsManager.Current.TrayIconMode;

        try
        {
            // Custom user image (mode 12)
            if (mode == 12)
            {
                string path = SettingsManager.Current.CustomTrayIconPath;
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

            // Glyph presets (modes 6–11)
            if (mode >= 6 && GlyphPresets.TryGetValue(mode, out var preset))
            {
                bool dark = ThemeManager.IsDarkTheme();
                var fg = dark ? System.Drawing.Color.White : System.Drawing.Color.Black;
                return RenderGlyphBitmap(preset.Glyph, fg);
            }

            // Color presets (modes 0–5)
            if (!PresetIcons.TryGetValue(mode, out var name))
                name = PresetIcons[0];
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
        const int size = 256;
        var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var font = new System.Drawing.Font("Segoe Fluent Icons", 240f, System.Drawing.GraphicsUnit.Pixel);
            using var brush = new System.Drawing.SolidBrush(fg);
            using var fmt = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic);
            fmt.Alignment = System.Drawing.StringAlignment.Center;
            fmt.LineAlignment = System.Drawing.StringAlignment.Center;
            g.DrawString(glyph.ToString(), font, brush, new System.Drawing.RectangleF(0, 0, size, size), fmt);
        }
        return bitmap;
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

        ms.Position = 0;
        return new System.Drawing.Icon(ms);
    }

    /// <summary>
    /// Persists the currently-resolved icon as an .ico file in AppData so that
    /// shortcuts (Start Menu, pinned taskbar) can reference it.
    /// Returns the path to the saved .ico, or null on failure.
    /// </summary>
    private static string? SaveResolvedIconToAppData()
    {
        try
        {
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LittleLauncher");
            Directory.CreateDirectory(appDataDir);
            string icoPath = Path.Combine(appDataDir, "app-icon.ico");

            using var bitmap = ResolveBaseIconBitmap();
            if (bitmap == null) return null;

            using var icon = BitmapToIcon(bitmap);
            using var stream = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
            icon.Save(stream);
            return icoPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save resolved icon to AppData");
        }
        return null;
    }

    /// <summary>
    /// Generates a settings-specific icon by compositing the current app icon
    /// with a small gear overlay in the bottom-right corner.
    /// Saved as settings-icon.ico alongside app-icon.ico.
    /// </summary>
    internal static string? SaveSettingsIconToAppData()
    {
        try
        {
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LittleLauncher");
            Directory.CreateDirectory(appDataDir);
            string icoPath = Path.Combine(appDataDir, "settings-icon.ico");

            using var baseBitmap = ResolveBaseIconBitmap();
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

            using var icon = BitmapToIcon(baseBitmap);
            using var stream = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
            icon.Save(stream);
            return icoPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save settings icon to AppData");
        }
        return null;
    }

    /// <summary>
    /// Updates the tray icon and all shortcuts to reflect the current TrayIconMode.
    /// Called from the OnTrayIconModeChanged handler.
    /// </summary>
    internal void UpdateTrayIcon()
    {
        if (nIcon == null) return;
        nIcon.Icon = ResolveTrayIcon();
        UpdateShortcutIcons();
        SaveSettingsIconToAppData();
        SettingsWindow.GetCurrent()?.RefreshIcon();
    }

    internal void UpdateTrayIconVisibility(bool visible)
    {
        if (nIcon != null)
            nIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSystemThemeChanged(global::Windows.UI.ViewManagement.UISettings sender, object args)
    {
        bool dark = Classes.ThemeManager.IsDarkTheme();
        if (dark == _lastDarkTheme) return;
        _lastDarkTheme = dark;

        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTrayIcon();
        });
    }

    private async Task StartAutoSyncAsync()
    {
        await AutoSyncService.SyncOnStartupAsync();
        AutoSyncService.Start();
    }

    // ── WndProc — handle cross-process PostMessage IPC ────────────

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if ((int)msg == _wmShowFlyout && _wmShowFlyout != 0)
        {
            // Update shortcut icons in case the user just pinned the flyout helper
            UpdateShortcutIcons();
            DispatcherQueue.TryEnqueue(() => FlyoutWindow.Toggle(this, (int)wParam, (int)lParam));
            return IntPtr.Zero;
        }
        else if ((int)msg == _wmShowSettings && _wmShowSettings != 0)
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher", "app-icon.ico");
        return File.Exists(appDataIcon) ? $"{appDataIcon},0" : fallback;
    }

    private static void EnsureStartMenuShortcuts()
    {
        try
        {
            string startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");

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

            // Single Start Menu shortcut — always opens settings when clicked
            CreateOrUpdateShortcut(
                Path.Combine(startMenuDir, "Little Launcher.lnk"),
                exePath,
                "--settings",
                "Little Launcher",
                GetShortcutIconLocation($"{exePath},0"));
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create Start Menu shortcuts");
        }
    }

    /// <summary>
    /// Updates the icon on all known LittleLauncher shortcuts:
    /// Start Menu main shortcut, and any pinned taskbar shortcut.
    /// </summary>
    internal static void UpdateShortcutIcons()
    {
        try
        {
            string? iconPath = SaveResolvedIconToAppData();

            // Tell the shell the .ico file content changed so it invalidates cached copies
            if (iconPath != null)
                NotifyShellItemUpdated(iconPath);

            string newIconLocation = iconPath != null
                ? $"{iconPath},0"
                : $"{Environment.ProcessPath},0";

            // Re-stamp the Start Menu shortcut so the shell picks up the new icon
            string startMenuLnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "Little Launcher.lnk");
            if (File.Exists(startMenuLnk))
            {
                UpdateShortcutIconLocation(startMenuLnk, newIconLocation);
                NotifyShellItemUpdated(startMenuLnk);
            }

            // Pinned taskbar shortcuts — update ones that target the flyout companion exe
            string taskbarPinDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch",
                "User Pinned", "TaskBar");
            if (Directory.Exists(taskbarPinDir))
            {
                string flyoutIconLocation = iconPath != null
                    ? $"{iconPath},0"
                    : GetFlyoutExeIconFallback();

                foreach (string lnk in Directory.GetFiles(taskbarPinDir, "*.lnk"))
                {
                    if (IsFlyoutShortcut(lnk))
                    {
                        UpdateShortcutIconLocation(lnk, flyoutIconLocation);
                        NotifyShellItemUpdated(lnk);
                    }
                }
            }

            // Global broadcast so the shell refreshes any remaining cached icons
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to update shortcut icons");
        }
    }

    /// <summary>
    /// Sends a targeted SHCNE_UPDATEITEM notification for a specific file path so the
    /// shell invalidates its cached icon for that file.
    /// </summary>
    private static void NotifyShellItemUpdated(string path)
    {
        IntPtr pathPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, pathPtr, IntPtr.Zero); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pathPtr); }
    }

    /// <summary>
    /// Returns the default icon location for the flyout companion exe.
    /// </summary>
    private static string GetFlyoutExeIconFallback()
    {
        string flyoutExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LittleLauncherFlyout.exe");
        return File.Exists(flyoutExe) ? $"{flyoutExe},0" : $"{Environment.ProcessPath},0";
    }

    /// <summary>
    /// Checks if a .lnk shortcut targets LittleLauncherFlyout.exe (the companion flyout exe).
    /// </summary>
    private static bool IsFlyoutShortcut(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return false;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return false;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
                string target = shortcut.TargetPath ?? "";
                string fileName = Path.GetFileName(target);
                return fileName.Equals("LittleLauncherFlyout.exe", StringComparison.OrdinalIgnoreCase);
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

    private static void UpdateShortcutIconLocation(string lnkPath, string iconLocation)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
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