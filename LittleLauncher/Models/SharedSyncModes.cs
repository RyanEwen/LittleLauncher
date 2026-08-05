namespace LittleLauncher.Models;

/// <summary>
/// How one shared launcher's items travel between the people sharing it.
/// Stored as an int in <see cref="Launcher.SharedSyncMode"/>.
/// </summary>
/// <remarks>
/// <para>Independent of <see cref="SyncProviders"/>, which decides where <i>your own</i> launchers
/// sync. The two answer different questions — "where do I keep my config" versus "where do we
/// both reach this one launcher" — and a shared launcher must keep working when the owner and the
/// subscriber sync their own settings to completely different places.</para>
/// <para><b>The requirement here is different, and stricter: the location has to be reachable by
/// someone else.</b> That rules out the private per-app cloud storage the global sync uses —
/// OneDrive's app folder and Google's app-data folder are per-user and cannot be granted to
/// anyone — which is why cloud sharing needs the wider scopes and a share link, while these
/// three need nothing extra.</para>
/// </remarks>
public static class SharedSyncModes
{
    /// <summary>A local or UNC path, including a folder some other client syncs.</summary>
    public const int File = 0;

    /// <summary>A path on an SSH/SFTP server, with per-launcher connection settings.</summary>
    public const int Sftp = 1;

    /// <summary>
    /// A URL on a WebDAV server — Nextcloud, ownCloud, a NAS.
    /// </summary>
    /// <remarks>
    /// The best fit of the three for genuine person-to-person sharing: the location is already a
    /// real shared address, each participant authenticates as themselves, and both can write, so
    /// 2-way sharing works without any link-granting machinery.
    /// </remarks>
    public const int WebDav = 2;

    /// <summary>
    /// A file in the owner's OneDrive, reached by everyone else through a share link.
    /// </summary>
    /// <remarks>
    /// <para>Not the app folder the global sync uses — that is private per-user storage with no
    /// way to grant anyone access. This writes to an ordinary Drive folder and mints an editable
    /// anonymous link, which is why it needs the wider
    /// <see cref="Services.OneDriveFileStore.SharingScope"/> and asks for it only when someone
    /// first shares this way.</para>
    /// <para>The subscriber holds the link, not a path: they resolve it against their own account
    /// and never need to know where in the owner's drive the file actually lives.</para>
    /// </remarks>
    public const int OneDrive = 3;

    /// <summary>Display name, used in dialogs and status messages.</summary>
    public static string DisplayName(int mode) => mode switch
    {
        Sftp => "SFTP",
        WebDav => "WebDAV",
        OneDrive => "OneDrive",
        _ => "File",
    };
}
