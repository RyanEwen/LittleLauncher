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

    public MainWindow()
    {
        // ── Singleton check ─────────────────────────────────────────
        if (!Singleton.WaitOne(TimeSpan.Zero, true))
        {
            // Another instance is running — signal it and exit.
            IntPtr target = FindWindow(null, "LittleLauncher Host");

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

        Logger.Info("Starting LittleLauncher MainWindow");

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

        SettingsManager.Current.LastKnownVersion = "v1.0.0";
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
    /// Mode: 0 = App Icon, 1 = Light, 2 = Dark, 3 = Auto, 4-9 = Presets, 10 = Custom.
    /// </summary>
    private static System.Drawing.Icon? ResolveTrayIcon()
    {
        int mode = SettingsManager.Current.TrayIconMode;
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        try
        {
            return mode switch
            {
                1 => LoadIconFromImage(Path.Combine(baseDir, "Resources", "TrayIcons", "TrayWhite.png")),
                2 => LoadIconFromImage(Path.Combine(baseDir, "Resources", "TrayIcons", "TrayBlack.png")),
                3 => LoadIconFromImage(Path.Combine(baseDir, "Resources", "TrayIcons",
                        Classes.ThemeManager.IsDarkTheme() ? "TrayWhite.png" : "TrayBlack.png")),
                >= 4 and <= 9 => RenderPresetIcon(mode),
                10 => LoadCustomIcon(SettingsManager.Current.CustomTrayIconPath),
                _ => LoadDefaultIcon(baseDir),
            };
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to load tray icon for mode {Mode}, falling back to default", mode);
            return LoadDefaultIcon(baseDir);
        }
    }

    /// <summary>
    /// Preset icon glyph characters (Segoe Fluent Icons).
    /// </summary>
    private static readonly Dictionary<int, (char Glyph, string Name)> PresetIcons = new()
    {
        { 4, ('\uE840', "Pin") },          // Pin
        { 5, ('\uE734', "Star") },        // FavoriteStar
        { 6, ('\uEB51', "Heart") },       // Heart
        { 7, ('\uE945', "Lightning") },   // Lightning
        { 8, ('\uE721', "Shield") },      // Shield
        { 9, ('\uE774', "Globe") },       // Globe
    };

    /// <summary>
    /// Renders a Segoe Fluent Icons glyph as a tray icon.
    /// Uses white foreground for dark themes, black for light themes.
    /// </summary>
    private static System.Drawing.Icon? RenderPresetIcon(int mode)
    {
        if (!PresetIcons.TryGetValue(mode, out var preset))
            return LoadDefaultIcon(AppDomain.CurrentDomain.BaseDirectory);

        bool dark = Classes.ThemeManager.IsDarkTheme();
        var fg = dark ? System.Drawing.Color.White : System.Drawing.Color.Black;

        const int size = 256;
        using var bitmap = new System.Drawing.Bitmap(size, size);
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
            var rect = new System.Drawing.RectangleF(0, 0, size, size);
            g.DrawString(preset.Glyph.ToString(), font, brush, rect, fmt);
        }

        return BitmapToIcon(bitmap);
    }

    private static System.Drawing.Icon? LoadDefaultIcon(string baseDir)
    {
        string iconPath = Path.Combine(baseDir, "Resources", "LittleLauncher.ico");
        if (!File.Exists(iconPath)) return null;

        // Extract the largest frame and run through the trim-and-resize pipeline
        // so it matches the preset icon proportions in the tray.
        using var ico = new System.Drawing.Icon(iconPath, 256, 256);
        using var bmp = ico.ToBitmap();
        return TrimAndResizeToBitmapIcon(bmp);
    }

    private static System.Drawing.Icon? LoadCustomIcon(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return LoadDefaultIcon(AppDomain.CurrentDomain.BaseDirectory);

        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            return new System.Drawing.Icon(path, 64, 64);

        return LoadIconFromImage(path);
    }

    private static System.Drawing.Icon? LoadIconFromImage(string path)
    {
        if (!File.Exists(path)) return null;
        using var original = new System.Drawing.Bitmap(path);
        return TrimAndResizeToBitmapIcon(original);
    }

    /// <summary>
    /// Trims transparent borders from a bitmap and redraws it centered into a
    /// 256×256 canvas, then converts to a multi-resolution ICO.
    /// </summary>
    private static System.Drawing.Icon TrimAndResizeToBitmapIcon(System.Drawing.Bitmap original)
    {
        var bounds = GetOpaqueContentBounds(original);
        int contentSize = Math.Max(bounds.Width, bounds.Height);
        if (contentSize <= 0) contentSize = original.Width;

        // Fill ~93.75% of the canvas (matching preset glyphs at 240/256)
        float scale = 256f * 0.9375f / contentSize;
        int drawSize = (int)(contentSize * scale);
        int offset = (256 - drawSize) / 2;

        const int iconSize = 256;
        using var resized = new System.Drawing.Bitmap(iconSize, iconSize);
        using (var g = System.Drawing.Graphics.FromImage(resized))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            // Draw only the trimmed content region, not the full bitmap with padding
            var dest = new System.Drawing.Rectangle(offset, offset, drawSize, drawSize);
            g.DrawImage(original, dest, bounds, System.Drawing.GraphicsUnit.Pixel);
        }
        return BitmapToIcon(resized);
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
            using var resized = new System.Drawing.Bitmap(s, s);
            using (var g = System.Drawing.Graphics.FromImage(resized))
            {
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

            int mode = SettingsManager.Current.TrayIconMode;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // For mode 0 (Default), remove any override so shortcuts fall back to the exe icon.
            if (mode == 0)
            {
                if (File.Exists(icoPath))
                    File.Delete(icoPath);
                return null;
            }

            // For mode 10 (Custom) with an .ico file, just copy it directly.
            if (mode == 10)
            {
                string customPath = SettingsManager.Current.CustomTrayIconPath;
                if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
                {
                    if (customPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(customPath, icoPath, overwrite: true);
                        return icoPath;
                    }
                    SavePngAsIco(customPath, icoPath);
                    return icoPath;
                }
            }

            // For modes 1-3 (Light/Dark/Auto), convert the PNG to .ico.
            // For modes 4-9 (Presets), render the glyph to .ico.
            if (mode >= 4 && mode <= 9)
            {
                var icon = RenderPresetIcon(mode);
                if (icon != null)
                {
                    using var stream = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
                    icon.Save(stream);
                    icon.Dispose();
                    return icoPath;
                }
            }

            string pngPath = mode switch
            {
                1 => Path.Combine(baseDir, "Resources", "TrayIcons", "TrayWhite.png"),
                2 => Path.Combine(baseDir, "Resources", "TrayIcons", "TrayBlack.png"),
                3 => Path.Combine(baseDir, "Resources", "TrayIcons",
                        Classes.ThemeManager.IsDarkTheme() ? "TrayWhite.png" : "TrayBlack.png"),
                _ => ""
            };

            if (!string.IsNullOrEmpty(pngPath) && File.Exists(pngPath))
            {
                SavePngAsIco(pngPath, icoPath);
                return icoPath;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save resolved icon to AppData");
        }
        return null;
    }

    /// <summary>
    /// Converts a PNG (or other image) to a single-image .ico file.
    /// </summary>
    private static void SavePngAsIco(string imagePath, string icoPath)
    {
        using var bitmap = new System.Drawing.Bitmap(imagePath);
        using var stream = new FileStream(icoPath, FileMode.Create, FileAccess.Write);

        // Write ICO header
        using var bw = new BinaryWriter(stream);
        bw.Write((short)0);           // reserved
        bw.Write((short)1);           // type: icon
        bw.Write((short)1);           // image count

        // Write ICO directory entry
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, ImageFormat.Png);
        byte[] pngBytes = pngStream.ToArray();

        bw.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
        bw.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
        bw.Write((byte)0);            // color palette
        bw.Write((byte)0);            // reserved
        bw.Write((short)1);           // color planes
        bw.Write((short)32);          // bits per pixel
        bw.Write(pngBytes.Length);    // image data size
        bw.Write(22);                 // offset to image data (6 header + 16 entry)

        // Write PNG data
        bw.Write(pngBytes);
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
    }

    internal void UpdateTrayIconVisibility(bool visible)
    {
        if (nIcon != null)
            nIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
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

            // Remove the old separate Settings shortcut (replaced by single shortcut)
            string oldSettingsLnk = Path.Combine(startMenuDir, "LittleLauncher Settings.lnk");
            if (File.Exists(oldSettingsLnk))
                File.Delete(oldSettingsLnk);

            // Single Start Menu shortcut — always opens settings when clicked
            CreateOrUpdateShortcut(
                Path.Combine(startMenuDir, "LittleLauncher.lnk"),
                exePath,
                "--settings",
                "LittleLauncher",
                $"{exePath},0");
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

            // Only update flyout-targeting shortcuts (not the main app shortcut)
            var shortcuts = new List<string>();

            // Start Menu flyout shortcut (LittleLauncher.lnk targets the main exe
            // which also serves as the flyout entry point)
            // We only customize the pinned taskbar flyout shortcut, not Start Menu app shortcuts.

            // Pinned taskbar shortcuts — only update ones that target the flyout companion exe
            string taskbarPinDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch",
                "User Pinned", "TaskBar");
            if (Directory.Exists(taskbarPinDir))
            {
                foreach (string lnk in Directory.GetFiles(taskbarPinDir, "*.lnk"))
                {
                    if (IsFlyoutShortcut(lnk))
                        shortcuts.Add(lnk);
                }
            }

            if (shortcuts.Count == 0) return;

            // Determine new icon location
            string newIconLocation = iconPath != null
                ? $"{iconPath},0"
                : GetFlyoutExeIconFallback();

            foreach (string lnk in shortcuts)
                UpdateShortcutIconLocation(lnk, newIconLocation);

            // Notify the shell that shortcuts changed so it refreshes cached icons
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to update shortcut icons");
        }
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