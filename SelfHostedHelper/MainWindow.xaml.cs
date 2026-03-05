using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Windows;
using SelfHostedHelper.Services;
using SelfHostedHelper.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static SelfHostedHelper.Classes.NativeMethods;

namespace SelfHostedHelper;

/// <summary>
/// MainWindow — the invisible host window for the Taskbar Launcher.
///
/// Architecture notes:
///   - This window is never actually displayed (Width/Height = 0, Visibility = Hidden).
///   - Its sole purpose is to own the tray NotifyIcon and the TaskbarWindow.
///   - A Mutex ("TaskbarLauncher") prevents multiple instances.
///   - A registered window message lets a second instance or LauncherShortcut
///     signal the first to show the flyout via PostMessage.
///   - TaskbarWindow is a child of the real Windows taskbar (Shell_TrayWnd) via SetParent.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly Mutex Singleton = new(true, "TaskbarLauncher");

    internal TaskbarWindow? taskbarWindow;
    private static int _wmShowFlyout;
    private static int _wmShowSettings;

    /// <summary>
    /// Set by TaskbarWindow when Explorer is restarting so we can avoid
    /// touching stale handles.
    /// </summary>
    internal static volatile bool ExplorerRestarting = false;

    public MainWindow()
    {
        // ── Singleton check ─────────────────────────────────────────
        if (!Singleton.WaitOne(TimeSpan.Zero, true))
        {
            // Another instance is running — signal it and exit.
            IntPtr target = FindWindow(null, "TaskbarLauncher Host");

            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && args[1] == "--settings")
            {
                int msg = RegisterWindowMessage("TaskbarLauncher_ShowSettings");
                if (target != IntPtr.Zero && msg != 0)
                    PostMessage(target, msg, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                GetCursorPos(out var pt);
                int msg = RegisterWindowMessage("TaskbarLauncher_ShowFlyout");
                if (target != IntPtr.Zero && msg != 0)
                    PostMessage(target, msg, (IntPtr)pt.X, (IntPtr)pt.Y);
            }

            Environment.Exit(0);
        }

        InitializeComponent();
        DataContext = SettingsManager.Current;

        Logger.Info("Starting TaskbarLauncher MainWindow");

        // Register the window message for cross-process flyout signaling
        _wmShowFlyout = RegisterWindowMessage("TaskbarLauncher_ShowFlyout");
        _wmShowSettings = RegisterWindowMessage("TaskbarLauncher_ShowSettings");

        // ── Restore settings ────────────────────────────────────────
        // Settings are restored in App.OnStartup before MainWindow is created.

    }

    /// <summary>
    /// Deferred initialisation that needs Application.Current.MainWindow to be set.
    /// StartupUri doesn't assign MainWindow until after the constructor returns,
    /// so theme application and tray-icon updates must live here.
    /// </summary>
    private void ApplyDeferredInit()
    {
        // Apply theme (needs Application.Current.MainWindow)
        ThemeManager.ApplySavedTheme();

        // ── Tray icon visibility ────────────────────────────────────
        nIcon.Visibility = SettingsManager.Current.NIconHide
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Show settings on first run
        if (string.IsNullOrEmpty(SettingsManager.Current.LastKnownVersion))
        {
            SettingsWindow.ShowInstance();
        }

        SettingsManager.Current.LastKnownVersion = "v1.0.0";
    }

    // ── Window loaded — create the taskbar widget ───────────────────

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Move off-screen so it's truly invisible
        Left = -9999;
        Top = -9999;

        // Hide from Alt-Tab
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        // Hook WndProc for cross-process PostMessage IPC
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        // Now that Application.Current.MainWindow is set, finish init
        ApplyDeferredInit();

        // Ensure Start Menu shortcuts exist
        EnsureStartMenuShortcuts();

        // Create the taskbar widget
        UpdateTaskbar();

        // Pre-create the flyout so the first toggle is instant
        FlyoutWindow.WarmUp();

        // Auto-sync from SFTP if enabled
        if (SettingsManager.Current.SftpAutoSync)
        {
            try
            {
                var (success, message) = await SftpSyncService.DownloadSettingsAsync();
                if (success)
                {
                    Logger.Info("Auto-synced settings from SFTP");
                    // RestoreSettings replaces the singleton — rebind and re-apply
                    DataContext = SettingsManager.Current;
                    ApplyDeferredInit();
                    UpdateTaskbar();
                }
                else Logger.Warn($"Auto-sync skipped: {message}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Auto-sync failed");
            }
        }
    }

    // ── Public method to (re)create the taskbar widget ──────────────

    public void UpdateTaskbar()
    {
        if (ExplorerRestarting) return;

        if (SettingsManager.Current.TaskbarWidgetEnabled)
        {
            if (taskbarWindow == null)
            {
                taskbarWindow = new TaskbarWindow();
            }
            else
            {
                taskbarWindow.RefreshLauncher();
            }
        }
        else
        {
            CloseTaskbarWindow();
        }
    }

    private void CloseTaskbarWindow()
    {
        if (taskbarWindow != null)
        {
            taskbarWindow.StopTimer();
            taskbarWindow.Close();
            taskbarWindow = null;
        }
    }

    /// <summary>
    /// Called from TaskbarWindow when Explorer restarts and the widget needs recreation.
    /// </summary>
    internal void RecreateTaskbarWindow()
    {
        CloseTaskbarWindow();
        taskbarWindow = new TaskbarWindow();
    }

    // ── WndProc — handle cross-process PostMessage IPC ────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _wmShowFlyout && _wmShowFlyout != 0)
        {
            var anchor = new Point((int)wParam, (int)lParam);
            Dispatcher.BeginInvoke(() => FlyoutWindow.Toggle(anchor));
            handled = true;
        }
        else if (msg == _wmShowSettings && _wmShowSettings != 0)
        {
            Dispatcher.BeginInvoke(() => SettingsWindow.ShowInstance());
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ── Tray icon event handlers ────────────────────────────────────

    private void nIcon_LeftClick(object sender, RoutedEventArgs e)
    {
        GetCursorPos(out var pt);
        FlyoutWindow.Toggle(new Point(pt.X, pt.Y));
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow.ShowInstance();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.SaveSettings();
        taskbarWindow?.Close();
        nIcon.Dispose();
        Application.Current.Shutdown();
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

            // Main app shortcut — globe icon (shell32.dll index 14)
            CreateShortcutIfMissing(
                Path.Combine(startMenuDir, "SelfHostedHelper.lnk"),
                exePath,
                arguments: null,
                "SelfHostedHelper",
                $"{system32}\\shell32.dll,14");

            // Settings shortcut — cog icon (imageres.dll index 109)
            CreateShortcutIfMissing(
                Path.Combine(startMenuDir, "SelfHostedHelper Settings.lnk"),
                exePath,
                "--settings",
                "Open SelfHostedHelper Settings",
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
