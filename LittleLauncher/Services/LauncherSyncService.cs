using LittleLauncher.Classes.Settings;

namespace LittleLauncher.Services;

/// <summary>
/// The single entry point for global launcher sync. Fans out across every destination the user
/// has switched on — SFTP, a signed-in cloud account, WebDAV, a folder — in any combination.
/// </summary>
/// <remarks>
/// <para>Callers outside this folder — <see cref="AutoSyncService"/> and the Cloud Sync page —
/// must go through here rather than naming a transport, so adding or changing one does not mean
/// auditing every trigger for a case that was missed.</para>
/// <para><see cref="SftpSyncService"/> also holds the per-launcher *shared* sync, which is a
/// separate feature with its own per-launcher transport and is not affected by which destinations
/// are enabled here.</para>
/// </remarks>
public static class LauncherSyncService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// True when at least one enabled destination is usable. Every automatic trigger is gated on
    /// this — destinations that are switched on but not yet configured must be inert, not failing.
    /// </summary>
    public static bool IsConfigured => SyncDestinations.Active().Count > 0;

    /// <summary>
    /// True when any enabled destination may need a password typed into the app, so the UI knows
    /// whether to prompt. Only SFTP does: cloud accounts authenticate in the browser, WebDAV
    /// stores its own, and folders are authenticated by Windows.
    /// </summary>
    public static bool UsesCredentials =>
        SyncDestinations.Active().Any(d => d.Provider == Models.SyncProviders.Sftp);

    /// <summary>Verify every enabled destination, reporting each one's result.</summary>
    public static async Task<(bool Success, string Message)> TestAsync(string? password = null)
    {
        var destinations = SyncDestinations.Enabled();
        if (destinations.Count == 0)
            return (false, "No sync destinations are switched on.");

        var lines = new List<string>();
        bool anySucceeded = false;

        foreach (var destination in destinations)
        {
            if (!destination.IsConfigured)
            {
                lines.Add($"{destination.DisplayName}: not configured yet.");
                continue;
            }

            var (ok, message) = await destination.TestAsync(password);
            if (ok) anySucceeded = true;
            lines.Add($"{destination.DisplayName}: {message}");
        }

        return (anySucceeded, string.Join("\n", lines));
    }

    /// <summary>
    /// Push all launchers to every enabled destination.
    /// </summary>
    /// <remarks>
    /// One destination failing must not stop the others — the whole point of enabling several is
    /// that a server being down does not mean the change is lost. Succeeds if any destination
    /// took the copy, and names the ones that did not.
    /// </remarks>
    public static async Task<(bool Success, string Message)> UploadLaunchersAsync(string? password = null)
    {
        var destinations = SyncDestinations.Active();
        if (destinations.Count == 0)
            return (false, "No sync destinations are configured.");

        var succeeded = new List<string>();
        var failed = new List<string>();

        foreach (var destination in destinations)
        {
            try
            {
                var (ok, message) = await destination.UploadAsync(password);
                if (ok) succeeded.Add(destination.DisplayName);
                else failed.Add($"{destination.DisplayName} ({message})");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Upload to {destination.DisplayName} failed");
                failed.Add($"{destination.DisplayName} ({ex.Message})");
            }
        }

        if (succeeded.Count == 0)
            return (false, $"Upload failed: {string.Join("; ", failed)}");

        string summary = $"Launchers uploaded to {string.Join(", ", succeeded)}.";
        if (failed.Count > 0)
            summary += $" Failed: {string.Join("; ", failed)}.";

        return (true, summary);
    }

    /// <summary>
    /// Pull all launchers from whichever enabled destination holds the newest copy.
    /// </summary>
    /// <remarks>
    /// <para>Newest-wins is the only defensible rule once several destinations can hold data: they
    /// are replicas of one thing, so the question is not "which is authoritative" but "which was
    /// written last". Asking each for its timestamp first means one download, not several.</para>
    /// <para>A destination that cannot report a timestamp is skipped rather than assumed empty or
    /// assumed newest — both of those would let an unreachable server decide the outcome.</para>
    /// </remarks>
    /// <param name="force">
    /// Applies the remote copy even when local changes are newer. Only for a download the user
    /// explicitly asked for — automatic syncs must never overrule newer local work.
    /// </param>
    public static async Task<(bool Success, string Message)> DownloadLaunchersAsync(
        string? password = null, bool force = false)
    {
        var destinations = SyncDestinations.Active();
        if (destinations.Count == 0)
            return (false, "No sync destinations are configured.");

        if (destinations.Count == 1)
            return await destinations[0].DownloadAsync(password, force);

        ISyncDestination? newest = null;
        DateTimeOffset newestAt = DateTimeOffset.MinValue;

        foreach (var destination in destinations)
        {
            DateTimeOffset? modified;
            try
            {
                modified = await destination.GetRemoteModifiedAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Could not read the timestamp from {destination.DisplayName}");
                continue;
            }

            if (modified.HasValue && modified.Value > newestAt)
            {
                newestAt = modified.Value;
                newest = destination;
            }
        }

        if (newest == null)
            return (false, "No sync destination holds any launcher data yet.");

        Logger.Info($"Downloading from {newest.DisplayName}, newest at {newestAt:u}");
        var (ok, message) = await newest.DownloadAsync(password, force);

        return (ok, ok ? $"{message} (newest copy was on {newest.DisplayName}.)" : message);
    }
}
