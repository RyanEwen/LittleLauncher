using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using System.IO;

namespace LittleLauncher.Services;

/// <summary>
/// Global launcher sync over a cloud provider's own API. Wraps an <see cref="ICloudFileStore"/>
/// in the same payload format, download guard and merge every other transport uses.
/// </summary>
/// <remarks>
/// One implementation serves every API-backed provider: OneDrive and Google Drive differ in how
/// a file is addressed, which is entirely inside the store, and not at all in what the file
/// contains or when it is safe to apply. Adding a third provider should mean a new store and a
/// line in <see cref="StoreFor"/>, nothing here.
/// </remarks>
public static class CloudSyncService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly OneDriveFileStore OneDrive = new();
    private static readonly GoogleDriveFileStore GoogleDrive = new();
    private static readonly WebDavFileStore WebDav = new();

    /// <summary>The store for a provider, or null when the provider is not API-backed.</summary>
    public static ICloudFileStore? StoreFor(int provider) => provider switch
    {
        SyncProviders.OneDrive => OneDrive,
        SyncProviders.GoogleDrive => GoogleDrive,
        SyncProviders.WebDav => WebDav,
        _ => null,
    };

    /// <summary>
    /// The store for the destination currently being *edited* on the Cloud Sync page.
    /// </summary>
    /// <remarks>
    /// UI convenience only. Sync operations take an explicit provider, because several
    /// destinations can be enabled at once and "the current one" is then meaningless.
    /// </remarks>
    public static ICloudFileStore? CurrentStore => StoreFor(SettingsManager.Current.SyncProvider);

    // ── Sync operations ─────────────────────────────────────────────

    /// <summary>Confirm the account is reachable and report whether it already holds data.</summary>
    public static async Task<(bool Success, string Message)> TestAsync(int provider)
    {
        var store = StoreFor(provider);
        if (store == null) return (false, "That is not a cloud provider.");
        if (!store.IsAvailable) return (false, CloudSyncCredentials.NotConfiguredMessage(store.ProviderName));
        if (!store.IsSignedIn) return (false, $"Not signed in to {store.ProviderName}.");

        try
        {
            var modified = await store.GetRemoteModifiedAsync();
            return modified.HasValue
                ? (true, $"Connected to {store.ProviderName}. Launcher data last changed {DescribeAge(modified.Value)}.")
                : (true, $"Connected to {store.ProviderName}. No launcher data there yet — upload to create it.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"{store.ProviderName} test failed");
            return (false, ex.Message);
        }
    }

    /// <summary>Push all launchers to the signed-in account.</summary>
    public static async Task<(bool Success, string Message)> UploadLaunchersAsync(int provider)
    {
        var store = StoreFor(provider);
        if (store == null) return (false, "That is not a cloud provider.");
        if (!store.IsSignedIn) return (false, $"Not signed in to {store.ProviderName}.");

        try
        {
            SettingsManager.SaveSettings();

            using var payload = LauncherPayload.Serialize(SettingsManager.Current.Launchers);
            await store.UploadAsync(payload.ToArray());

            Logger.Info($"Launchers uploaded to {store.ProviderName}");
            return (true, $"Launchers uploaded to {store.ProviderName}.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to upload launchers to {store.ProviderName}");
            return (false, $"Upload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pull all launchers from the signed-in account and apply them.
    /// </summary>
    /// <param name="force">
    /// Applies the remote copy even when local changes are newer. Only for a download the user
    /// explicitly asked for — automatic syncs must never overrule newer local work.
    /// </param>
    public static async Task<(bool Success, string Message)> DownloadLaunchersAsync(
        int provider, bool force = false)
    {
        var store = StoreFor(provider);
        if (store == null) return (false, "That is not a cloud provider.");
        if (!store.IsSignedIn) return (false, $"Not signed in to {store.ProviderName}.");

        try
        {
            byte[]? bytes = await store.DownloadAsync();
            if (bytes == null)
                return (false, $"No launchers file found in {store.ProviderName}.");

            using var stream = new MemoryStream(bytes, writable: false);
            var (launchers, remoteTimestamp) = LauncherPayload.Deserialize(stream);
            if (launchers == null)
                return (false, $"Failed to parse launchers from {store.ProviderName}.");

            if (LauncherPayload.ShouldSkipDownload(remoteTimestamp, force, out string reason))
                return (false, reason);

            await LauncherPayload.ApplyAsync(launchers);

            Logger.Info($"Launchers downloaded from {store.ProviderName}");
            return (true, $"Launchers downloaded from {store.ProviderName}.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to download launchers from {store.ProviderName}");
            return (false, $"Download failed: {ex.Message}");
        }
    }

    private static string DescribeAge(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} days ago";
    }
}
