using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;

namespace LittleLauncher.Services;

/// <summary>
/// One place launchers are synced to, whatever the transport underneath.
/// </summary>
/// <remarks>
/// The uniform shape is what makes several destinations at once tractable: the fan-out in
/// <see cref="LauncherSyncService"/> never asks what kind a destination is, so adding a transport
/// does not mean revisiting the upload, download or status logic.
/// </remarks>
internal interface ISyncDestination
{
    int Provider { get; }
    string DisplayName { get; }

    /// <summary>True when this destination has enough settings to be usable.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// When the remote copy last changed, or null when there is none. Metadata only — this is
    /// called on every enabled destination to decide which one a download should come from, so it
    /// must stay cheap.
    /// </summary>
    Task<DateTimeOffset?> GetRemoteModifiedAsync();

    Task<(bool Success, string Message)> TestAsync(string? password);
    Task<(bool Success, string Message)> UploadAsync(string? password);
    Task<(bool Success, string Message)> DownloadAsync(string? password, bool force);
}

/// <summary>
/// Builds the set of destinations the user has switched on.
/// </summary>
public static class SyncDestinations
{
    /// <summary>Every destination, enabled or not, in display order.</summary>
    internal static IEnumerable<ISyncDestination> All()
    {
        foreach (int provider in SyncProviders.Selectable)
        {
            var destination = For(provider);
            if (destination != null) yield return destination;
        }
    }

    /// <summary>The destinations that are switched on <i>and</i> usable.</summary>
    internal static List<ISyncDestination> Active() =>
        All().Where(d => SettingsManager.Current.IsSyncProviderEnabled(d.Provider) && d.IsConfigured)
             .ToList();

    /// <summary>The destinations that are switched on, whether or not they are configured.</summary>
    internal static List<ISyncDestination> Enabled() =>
        All().Where(d => SettingsManager.Current.IsSyncProviderEnabled(d.Provider)).ToList();

    internal static ISyncDestination? For(int provider) => SyncProviders.Normalize(provider) switch
    {
        SyncProviders.Sftp => new SftpDestination(),
        SyncProviders.Folder => new FolderDestination(),
        int p when CloudSyncService.StoreFor(p) is { } store => new CloudDestination(p, store),
        _ => null,
    };

    // ── Transport adapters ──────────────────────────────────────────

    private sealed class SftpDestination : ISyncDestination
    {
        public int Provider => SyncProviders.Sftp;
        public string DisplayName => "SFTP server";
        public bool IsConfigured => !string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost);

        public Task<DateTimeOffset?> GetRemoteModifiedAsync() => SftpSyncService.GetRemoteModifiedAsync();
        public Task<(bool, string)> TestAsync(string? password) => SftpSyncService.TestConnectionAsync(password);
        public Task<(bool, string)> UploadAsync(string? password) => SftpSyncService.UploadLaunchersAsync(password);
        public Task<(bool, string)> DownloadAsync(string? password, bool force)
            => SftpSyncService.DownloadLaunchersAsync(password, force);
    }

    private sealed class FolderDestination : ISyncDestination
    {
        public int Provider => SyncProviders.Folder;
        public string DisplayName => "Folder or network share";
        public bool IsConfigured => FolderSyncService.IsConfigured;

        public Task<DateTimeOffset?> GetRemoteModifiedAsync() => FolderSyncService.GetRemoteModifiedAsync();
        public Task<(bool, string)> TestAsync(string? password) => FolderSyncService.TestAsync();
        public Task<(bool, string)> UploadAsync(string? password) => FolderSyncService.UploadLaunchersAsync();
        public Task<(bool, string)> DownloadAsync(string? password, bool force)
            => FolderSyncService.DownloadLaunchersAsync(force);
    }

    private sealed class CloudDestination(int provider, ICloudFileStore store) : ISyncDestination
    {
        public int Provider => provider;
        public string DisplayName => store.ProviderName;
        public bool IsConfigured => store.IsAvailable && store.IsSignedIn;

        public async Task<DateTimeOffset?> GetRemoteModifiedAsync()
        {
            try
            {
                return await store.GetRemoteModifiedAsync();
            }
            catch (Exception)
            {
                // A destination that cannot answer must not veto a download from one that can.
                return null;
            }
        }

        public Task<(bool, string)> TestAsync(string? password) => CloudSyncService.TestAsync(provider);
        public Task<(bool, string)> UploadAsync(string? password) => CloudSyncService.UploadLaunchersAsync(provider);
        public Task<(bool, string)> DownloadAsync(string? password, bool force)
            => CloudSyncService.DownloadLaunchersAsync(provider, force);
    }
}
