using LittleLauncher.Classes.Settings;

namespace LittleLauncher.Services;

/// <summary>
/// Manages automatic SFTP sync of launchers.
/// Handles: startup download, debounced upload on item changes, and periodic download.
/// All triggers are gated by the SftpAutoSync toggle.
/// </summary>
public static class AutoSyncService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static System.Threading.Timer? _periodicTimer;
    private static System.Threading.Timer? _debounceTimer;
    private static bool _running;
    private static bool _hasPendingLocalItemChanges;

    /// <summary>
    /// When true, the next call to <see cref="NotifyItemsChanged"/> is ignored.
    /// Used to prevent feedback loops when downloading shared items triggers
    /// a re-upload.
    /// </summary>
    internal static bool SuppressNextChange { get; set; }

    /// <summary>
    /// Start periodic sync timer. Call once at app startup (after startup sync completes).
    /// </summary>
    public static void Start()
    {
        if (_running) return;
        _running = true;
        RestartPeriodicTimer();
    }

    /// <summary>
    /// Stop all sync timers. Call at app shutdown.
    /// </summary>
    public static void Stop()
    {
        _running = false;
        _periodicTimer?.Dispose();
        _periodicTimer = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    /// <summary>
    /// Restart the periodic timer (e.g. when interval setting changes).
    /// </summary>
    public static void RestartPeriodicTimer()
    {
        _periodicTimer?.Dispose();
        _periodicTimer = null;

        if (!_running || !SettingsManager.Current.SftpAutoSync)
            return;

        int minutes = SettingsManager.Current.SftpAutoSyncInterval;
        if (minutes <= 0) minutes = 5;
        var interval = TimeSpan.FromMinutes(minutes);

        _periodicTimer = new System.Threading.Timer(
            _ => _ = DownloadSilentAsync("periodic"),
            null,
            interval,
            interval);
    }

    /// <summary>
    /// <summary>
    /// True while the user is in the middle of editing launchers, and a download would pull the
    /// rug out from under them.
    /// </summary>
    /// <remarks>
    /// The pending-changes flag alone is not enough. It is only raised once something has been
    /// saved, so the window between "started creating a launcher" and "finished" was unguarded —
    /// and a download in that window deletes anything the server has never seen, which is exactly
    /// what a half-made launcher is. Creating one repeatedly failed for that reason: it vanished
    /// mid-edit.
    /// </remarks>
    private static bool IsUserEditingLaunchers()
    {
        try
        {
            if (SettingsWindow.GetCurrent() != null) return true;
            if (Windows.LauncherSettingsWindow.OpenCount > 0) return true;
            if (Windows.FlyoutWindow.IsAnyInEditMode()) return true;
        }
        catch (Exception ex)
        {
            // Never let a UI probe stop the sync service; assume not editing.
            Logger.Debug(ex, "Could not determine editing state");
        }
        return false;
    }

    /// <summary>
    /// Notify that launchers changed — items, or the launchers themselves (added, removed,
    /// renamed, reconfigured). Triggers a debounced upload and blocks downloads until it lands.
    /// </summary>
    public static void NotifyLaunchersChanged() => NotifyItemsChanged();

    /// Notify that launcher items changed. Triggers a debounced upload (3 seconds).
    /// </summary>
    public static void NotifyItemsChanged()
    {
        if (SettingsManager.Current.SftpAutoSync
            && !string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
        {
            _hasPendingLocalItemChanges = true;

            // Persisted alongside the in-memory flag, so a restart before the debounced upload
            // does not forget that local is ahead of the server.
            SettingsManager.Current.LaunchersModifiedUtc = DateTime.UtcNow;
            SettingsManager.SaveSettings();
        }

        if (SuppressNextChange)
        {
            SuppressNextChange = false;
            return;
        }

        if (!SettingsManager.Current.SftpAutoSync
            || string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
            return;

        // Reset the debounce timer — upload 3 seconds after last change
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(
            _ => _ = UploadAndPushSharedAsync("debounced item change"),
            null,
            TimeSpan.FromSeconds(3),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Cancel any pending debounce and upload immediately (fire-and-forget).
    /// Call when the user is about to leave (e.g. settings window close) to
    /// ensure changes reach the server before the app might be killed.
    /// </summary>
    public static void FlushPendingUpload()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        if (!SettingsManager.Current.SftpAutoSync
            || string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
            return;

        _ = UploadAndPushSharedAsync("settings close flush");
    }

    internal static void ClearPendingLocalItemChanges()
    {
        _hasPendingLocalItemChanges = false;
        if (SettingsManager.Current.LaunchersModifiedUtc != default)
        {
            SettingsManager.Current.LaunchersModifiedUtc = default;
            SettingsManager.SaveSettings();
        }
    }

    /// <summary>
    /// Run the startup sync: download launchers from the server, then sync shared launchers.
    /// </summary>
    public static async Task SyncOnStartupAsync()
    {
        if (!SettingsManager.Current.SftpAutoSync
            || string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
            return;

        try
        {
            var (success, message) = await SftpSyncService.DownloadLaunchersAsync();
            if (success)
            {
                ClearPendingLocalItemChanges();
                Logger.Info("Auto-sync startup: downloaded launchers");
                Windows.FlyoutWindow.InvalidateItems();
                MainWindow.Current?.RefreshTrayIcons();
            }
            else
                Logger.Warn($"Auto-sync startup skipped: {message}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Auto-sync startup failed");
        }

        await SyncSharedLaunchersSilentAsync("startup");
    }

    private static async Task UploadAndPushSharedAsync(string trigger)
    {
        if (!SettingsManager.Current.SftpAutoSync
            || string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
            return;

        try
        {
            var (success, message) = await SftpSyncService.UploadLaunchersAsync();
            if (success)
            {
                ClearPendingLocalItemChanges();
                Logger.Info($"Auto-sync ({trigger}): uploaded launchers");
            }
            else
                Logger.Warn($"Auto-sync ({trigger}) skipped: {message}");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Auto-sync ({trigger}) failed");
        }

        // Also push any shared launchers that this user participates in
        await PushSharedLaunchersSilentAsync(trigger);
    }

    private static async Task PushSharedLaunchersSilentAsync(string trigger)
    {
        try
        {
            await SftpSyncService.PushAllSharedLaunchersAsync();
            Logger.Info($"Shared launcher push ({trigger}): complete");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Shared launcher push ({trigger}) failed");
        }
    }

    private static async Task DownloadSilentAsync(string trigger)
    {
        if (!SettingsManager.Current.SftpAutoSync
            || string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
            return;

        if (_hasPendingLocalItemChanges || SettingsManager.Current.LaunchersModifiedUtc != default)
        {
            Logger.Info($"Auto-sync ({trigger}) download skipped: pending local item changes have not been uploaded yet");
            await SyncSharedLaunchersSilentAsync(trigger);
            return;
        }

        if (IsUserEditingLaunchers())
        {
            Logger.Info($"Auto-sync ({trigger}) download skipped: launchers are being edited");
            await SyncSharedLaunchersSilentAsync(trigger);
            return;
        }

        try
        {
            var (success, message) = await SftpSyncService.DownloadLaunchersAsync();
            if (success)
            {
                ClearPendingLocalItemChanges();
                Logger.Info($"Auto-sync ({trigger}): downloaded launchers");
                App.MainDispatcherQueue.TryEnqueue(() =>
                {
                    // force:false — a periodic download that changed nothing must not rebuild
                    // every flyout's visual tree on a timer (that churn fed the container leak).
                    Windows.FlyoutWindow.InvalidateItems(force: false);
                    MainWindow.Current?.RefreshTrayIcons();
                });
            }
            else
                Logger.Warn($"Auto-sync ({trigger}) skipped: {message}");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Auto-sync ({trigger}) failed");
        }

        await SyncSharedLaunchersSilentAsync(trigger);
    }

    private static async Task SyncSharedLaunchersSilentAsync(string trigger)
    {
        try
        {
            await SftpSyncService.SyncAllSharedLaunchersAsync();
            App.MainDispatcherQueue.TryEnqueue(() =>
            {
                // force:false — this runs on every periodic tick; only rebuild when a shared
                // launcher's items actually changed, not unconditionally (see InvalidateItems).
                Windows.FlyoutWindow.InvalidateItems(force: false);
            });
            Logger.Info($"Shared launcher sync ({trigger}): complete");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Shared launcher sync ({trigger}) failed");
        }
    }
}
