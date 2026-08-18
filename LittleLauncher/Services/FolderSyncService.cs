using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using System.IO;

namespace LittleLauncher.Services;

/// <summary>
/// Syncs all launchers through a folder on this machine: a UNC path on a network file share, or
/// any other directory — including another cloud client's synced folder.
/// </summary>
/// <remarks>
/// <para>This is the transport for everything without a first-class integration: Dropbox,
/// Seafile, iCloud, Syncthing, a USB stick — and OneDrive for Business, which cannot use the
/// app-folder permission that <see cref="CloudSyncService"/> relies on. OneDrive personal and
/// Google Drive do <b>not</b> come through here; they sign in and use their vendor's API.</para>
/// <para>Where a sync client is involved, it — not this app — moves the bytes, and that bounds
/// what this can promise: writing succeeds as soon as the file lands on disk, and the client
/// uploads it whenever it next runs. A successful upload here means "handed to the sync client",
/// not "visible on your other machine". The API-backed providers exist precisely because they
/// can promise the stronger thing.</para>
/// <para>The file format is identical to the SFTP transport's (see
/// <see cref="LauncherPayload"/>), so machines can use different providers against copies of the
/// same data, and switching provider does not strand anything.</para>
/// </remarks>
public static class FolderSyncService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>True when a sync folder has been configured.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(SettingsManager.Current.SyncFolderPath);

    /// <summary>
    /// Verify the configured folder is reachable and writable, and report whether it already
    /// holds launcher data.
    /// </summary>
    public static async Task<(bool Success, string Message)> TestAsync()
    {
        string folder = SettingsManager.Current.SyncFolderPath;
        if (string.IsNullOrWhiteSpace(folder))
            return (false, "No sync folder is configured.");

        return await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);

                // Creating the directory is not proof of write access to it — a share can grant
                // traversal and refuse writes — so actually write something and take it back.
                string probe = Path.Combine(folder, $".littlelauncher-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);

                string file = Path.Combine(folder, LauncherPayload.FileName);
                return File.Exists(file)
                    ? (true, $"Folder is writable. Existing launcher data found ({DescribeAge(file)}).")
                    : (true, "Folder is writable. No launcher data there yet — upload to create it.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Sync folder test failed for {folder}");
                return (false, $"Folder not usable: {ex.Message}");
            }
        });
    }

    // ── Launcher sync ──────────────────────────────────────────────

    /// <summary>Write all launchers to the configured sync folder.</summary>
    public static async Task<(bool Success, string Message)> UploadLaunchersAsync()
    {
        string folder = SettingsManager.Current.SyncFolderPath;
        if (string.IsNullOrWhiteSpace(folder))
            return (false, "No sync folder is configured.");

        try
        {
            SettingsManager.SaveSettings();

            using var payload = LauncherPayload.Serialize(SettingsManager.Current.Launchers);
            byte[] bytes = payload.ToArray();

            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                LauncherPayload.WriteAtomic(Path.Combine(folder, LauncherPayload.FileName), bytes);
            });

            Logger.Info($"Launchers written to {folder}");
            return (true, $"Launchers saved to {DescribeTarget()}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to write launchers to {folder}");
            return (false, $"Upload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Read all launchers from the configured sync folder and replace the current launchers.
    /// </summary>
    /// <param name="force">
    /// Applies the folder's copy even when local changes are newer. Only for a download the user
    /// explicitly asked for — automatic syncs must never overrule newer local work.
    /// </param>
    public static async Task<(bool Success, string Message)> DownloadLaunchersAsync(bool force = false)
    {
        string folder = SettingsManager.Current.SyncFolderPath;
        if (string.IsNullOrWhiteSpace(folder))
            return (false, "No sync folder is configured.");

        string file = Path.Combine(folder, LauncherPayload.FileName);

        try
        {
            if (!await Task.Run(() => File.Exists(file)))
                return (false, "No launchers file found in the sync folder.");

            // Read into memory before parsing: on OneDrive and Drive for desktop the file may be
            // a placeholder, and touching it blocks while the client hydrates it.
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(file));

            using var stream = new MemoryStream(bytes, writable: false);
            var (launchers, remoteTimestamp, extensions) = LauncherPayload.Deserialize(stream);
            if (launchers == null)
                return (false, "Failed to parse launchers from the sync folder.");

            if (LauncherPayload.ShouldSkipDownload(remoteTimestamp, force, out string reason))
                return (false, reason);

            await LauncherPayload.ApplyAsync(launchers, extensions);

            Logger.Info($"Launchers read from {file}");
            return (true, $"Launchers loaded from {DescribeTarget()}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to read launchers from {file}");
            return (false, $"Download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// When the folder's copy last changed, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Used to pick which destination a download comes from, so it must not throw: an unreachable
    /// share returning null takes it out of the running rather than failing the whole download.
    /// </remarks>
    public static async Task<DateTimeOffset?> GetRemoteModifiedAsync()
    {
        string folder = SettingsManager.Current.SyncFolderPath;
        if (string.IsNullOrWhiteSpace(folder)) return null;

        return await Task.Run<DateTimeOffset?>(() =>
        {
            try
            {
                string file = Path.Combine(folder, LauncherPayload.FileName);
                return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, $"Could not read the timestamp in {folder}");
                return null;
            }
        });
    }

    // ── Helpers ────────────────────────────────────────────────────

    /// <summary>How the configured target reads in a status message.</summary>
    /// <remarks>
    /// The path itself, because the folder providers are now the ones with no vendor name to use
    /// instead — OneDrive and Google Drive sign in through their own APIs. A folder here is a
    /// network share, another sync client's folder, or somewhere the user chose, and the path is
    /// the only thing that identifies it.
    /// </remarks>
    public static string DescribeTarget() => SettingsManager.Current.SyncFolderPath;

    private static string DescribeAge(string file)
    {
        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
            if (age < TimeSpan.FromMinutes(1)) return "just now";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
            if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
            return $"{(int)age.TotalDays} days ago";
        }
        catch
        {
            return "unknown age";
        }
    }
}
