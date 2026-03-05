using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Windows;
using SelfHostedHelper.Services;
using SelfHostedHelper.ViewModels;
using System.Windows;
using static SelfHostedHelper.Classes.NativeMethods;

namespace SelfHostedHelper;

/// <summary>
/// MainWindow — the invisible host window for the Taskbar Launcher.
///
/// Architecture notes:
///   - This window is never actually displayed (Width/Height = 0, Visibility = Hidden).
///   - Its sole purpose is to own the tray NotifyIcon and the TaskbarWindow.
///   - A Mutex ("TaskbarLauncher") prevents multiple instances.
///   - An EventWaitHandle lets a second instance signal the first to open settings.
///   - TaskbarWindow is a child of the real Windows taskbar (Shell_TrayWnd) via SetParent.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly Mutex Singleton = new(true, "TaskbarLauncher");

    internal TaskbarWindow? taskbarWindow;

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
            // Another instance is running — signal it to show the flyout, then exit.
            Task.Run(() =>
            {
                try
                {
                    using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, "TaskbarLauncher_ShowFlyout");
                    evt.Set();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to signal existing instance");
                }
            });

            Environment.Exit(0);
        }

        InitializeComponent();
        DataContext = SettingsManager.Current;

        Logger.Info("Starting TaskbarLauncher MainWindow");

        // Listen for flyout signals (second instance launch or companion exe)
        Task.Run(() =>
        {
            try
            {
                using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, "TaskbarLauncher_ShowFlyout");
                while (true)
                {
                    evt.WaitOne();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        GetCursorPos(out var pt);
                        FlyoutWindow.Toggle(new Point(pt.X, pt.Y));
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Flyout event listener error");
            }
        });

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

        // Now that Application.Current.MainWindow is set, finish init
        ApplyDeferredInit();

        // Create the taskbar widget
        UpdateTaskbar();

        // Auto-sync from SFTP if enabled
        if (SettingsManager.Current.SftpAutoSync)
        {
            try
            {
                var (success, message) = await SftpSyncService.DownloadSettingsAsync();
                if (success) Logger.Info("Auto-synced settings from SFTP");
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

    // ── Tray icon event handlers ────────────────────────────────────

    private void nIcon_LeftClick(object sender, RoutedEventArgs e)
    {
        NativeMethods.GetCursorPos(out var pt);
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
}
