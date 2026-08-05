namespace LittleLauncher.Models;

/// <summary>
/// Where the global launcher sync reads and writes <c>launchers.json</c>.
/// Stored as an int in <see cref="ViewModels.UserSettings.SyncProvider"/>.
/// </summary>
/// <remarks>
/// <para><see cref="Sftp"/> is 0 deliberately. <c>SettingsManager.JsonOptions</c> uses
/// <c>WhenWritingDefault</c>, so the CLR default is the value that is written by omission —
/// which must stay the transport every existing settings file was configured for.</para>
/// <para>There are three kinds of transport here, not two: SFTP, the API-backed cloud accounts
/// (<see cref="Services.CloudSyncService"/>), and plain folders
/// (<see cref="Services.FolderSyncService"/>). OneDrive and Google Drive sign in and talk to
/// Microsoft Graph and Drive v3 directly rather than writing into a synced folder — the vendor
/// confirms the upload, no sync client needs to be installed, and the remote's modified time can
/// actually be read.</para>
/// <para>Other clouds — Dropbox, Seafile, iCloud, Syncthing — and OneDrive for Business, which
/// cannot use the app-folder permission, are served by <see cref="Folder"/> pointed at their
/// synced folder.</para>
/// </remarks>
public static class SyncProviders
{
    /// <summary>SSH/SFTP server (the original transport, and the default).</summary>
    public const int Sftp = 0;

    /// <summary>OneDrive via Microsoft Graph, in the app folder. Personal accounts only.</summary>
    public const int OneDrive = 1;

    /// <summary>Google Drive via Drive v3, in the hidden app-data folder.</summary>
    public const int GoogleDrive = 2;

    /// <summary>A UNC path on a network file share.</summary>
    public const int NetworkShare = 3;

    /// <summary>Any other folder — another sync client, a removable drive, a local path.</summary>
    public const int Folder = 4;

    /// <summary>
    /// Any WebDAV server — Nextcloud, ownCloud, Fastmail Files, a NAS. No app registration,
    /// consent screen or vendor review, because it is a standard rather than a product.
    /// </summary>
    public const int WebDav = 5;

    /// <summary>
    /// True when the provider syncs through a directory on this machine rather than a
    /// network connection the app opens itself.
    /// </summary>
    /// <remarks>
    /// Tests for membership rather than <c>!= Sftp</c> so an unrecognised value — a settings file
    /// from a newer build, or the <c>-1</c> a <see cref="Microsoft.UI.Xaml.Controls.ComboBox"/>
    /// writes back when its bound index is out of range — falls back to SFTP. The alternative
    /// resolves to a folder provider with no folder, which is silently inert.
    /// </remarks>
    public static bool IsFolderBased(int provider) =>
        provider is NetworkShare or Folder;

    /// <summary>
    /// True when the provider signs in to a cloud account and uses that vendor's API, rather
    /// than touching the filesystem at all.
    /// </summary>
    public static bool IsCloudAccount(int provider) =>
        provider is OneDrive or GoogleDrive or WebDav;

    /// <summary>
    /// True when the provider's credentials are typed into the app rather than obtained in a
    /// browser, so the UI shows a form instead of a sign-in button.
    /// </summary>
    public static bool UsesTypedCredentials(int provider) => provider is WebDav;

    /// <summary>
    /// The destinations offered in the UI, in display order.
    /// </summary>
    /// <remarks>
    /// <see cref="NetworkShare"/> is absent on purpose. It and <see cref="Folder"/> are the same
    /// transport reading the same <c>SyncFolderPath</c> setting, so once several destinations can
    /// be enabled at once, offering both would let a user switch on two that silently fight over
    /// one path. <see cref="Normalize"/> folds the old constant into <see cref="Folder"/>.
    /// </remarks>
    public static readonly int[] Selectable =
    [
        Sftp, OneDrive, GoogleDrive, WebDav, Folder,
    ];

    /// <summary>
    /// Collapse constants that name the same destination, so the enabled set cannot hold two
    /// entries that mean one thing.
    /// </summary>
    public static int Normalize(int provider) => provider == NetworkShare ? Folder : provider;

    /// <summary>Human-readable name, used in status messages.</summary>
    public static string DisplayName(int provider) => provider switch
    {
        OneDrive => "OneDrive",
        GoogleDrive => "Google Drive",
        NetworkShare => "network share",
        Folder => "folder",
        WebDav => "WebDAV",
        _ => "SFTP",
    };
}
