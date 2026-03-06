using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Windows;
using LittleLauncher.Services;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
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
        FlyoutWindow.WarmUp(this);
        _ = StartAutoSyncAsync();
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

        // Use icon from Resources
        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "LittleLauncher.ico");
        if (File.Exists(iconPath))
            nIcon.Icon = new System.Drawing.Icon(iconPath);

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

    // ── Start Menu shortcut ─────────────────────────────────────────

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

            string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

            CreateShortcutIfMissing(
                Path.Combine(startMenuDir, "LittleLauncher.lnk"),
                exePath,
                arguments: null,
                "LittleLauncher",
                $"{system32}\\shell32.dll,14");

            CreateShortcutIfMissing(
                Path.Combine(startMenuDir, "LittleLauncher Settings.lnk"),
                exePath,
                "--settings",
                "Open LittleLauncher Settings",
                $"{system32}\\imageres.dll,109");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create Start Menu shortcuts");
        }
    }

    private static void CreateShortcutIfMissing(
        string shortcutPath, string exePath, string? arguments,
        string description, string iconLocation)
    {
        if (File.Exists(shortcutPath))
            return;

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