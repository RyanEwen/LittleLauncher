namespace LittleLauncher.Services;

/// <summary>
/// A single remote file, reached through a cloud provider's own API rather than through a synced
/// folder on disk.
/// </summary>
/// <remarks>
/// <para>Deliberately narrow: the global sync needs exactly one small file per account, so the
/// interface is read it, write it, ask when it changed. Nothing here exposes folders, listings or
/// sharing, because nothing needs them — and a wider interface would invite a provider to depend
/// on a capability the other one does not have.</para>
/// <para>What this buys over a synced folder is the reason the API path exists at all: the
/// upload is confirmed by the service rather than handed to a background client, the file is
/// reachable on machines where the vendor's sync app is not installed, there is no placeholder to
/// hydrate, and <see cref="GetRemoteModifiedAsync"/> answers "did the other machine change this?"
/// — which a folder cannot.</para>
/// </remarks>
public interface ICloudFileStore
{
    /// <summary>Provider name for logs, status messages and the token store's filename.</summary>
    string ProviderName { get; }

    /// <summary>True when this build carries an OAuth registration for the provider.</summary>
    bool IsAvailable { get; }

    /// <summary>True when tokens exist locally. Does not prove they are still accepted.</summary>
    bool IsSignedIn { get; }

    /// <summary>The signed-in account, for the settings UI. Empty when signed out.</summary>
    string AccountName { get; }

    /// <summary>Run the interactive sign-in. Returns false if cancelled or refused.</summary>
    Task<(bool Success, string Message)> SignInAsync(CancellationToken ct = default);

    /// <summary>Forget the local tokens.</summary>
    void SignOut();

    /// <summary>The file's contents, or null when it does not exist yet.</summary>
    Task<byte[]?> DownloadAsync(CancellationToken ct = default);

    /// <summary>Create or replace the file.</summary>
    Task UploadAsync(byte[] content, CancellationToken ct = default);

    /// <summary>When the remote file last changed, or null when it does not exist.</summary>
    Task<DateTimeOffset?> GetRemoteModifiedAsync(CancellationToken ct = default);
}
