using LittleLauncher.Models;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// Locates the local sync roots of consumer cloud clients so the user can pick a sync folder
/// instead of typing a path.
/// </summary>
/// <remarks>
/// Everything here is best-effort discovery. Detection returning nothing is a normal outcome —
/// the client may not be installed, or may be installed somewhere this does not know about — so
/// every caller must still allow a folder to be browsed to or typed in by hand.
/// </remarks>
public static class CloudFolderService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>The subfolder created inside a detected root, so sync data is not loose at the top.</summary>
    public const string DefaultSubfolder = "LittleLauncher";

    /// <summary>
    /// Candidate roots for the given provider, most likely first. Empty when nothing was found
    /// (including for <see cref="SyncProviders.NetworkShare"/>, which cannot be detected).
    /// </summary>
    public static IReadOnlyList<string> FindRoots(int provider) => provider switch
    {
        SyncProviders.OneDrive => FindOneDriveRoots(),
        SyncProviders.GoogleDrive => FindGoogleDriveRoots(),
        _ => [],
    };

    /// <summary>
    /// A ready-to-use sync folder for the provider — the first detected root plus
    /// <see cref="DefaultSubfolder"/> — or null when nothing was detected.
    /// </summary>
    public static string? SuggestFolder(int provider)
    {
        var root = FindRoots(provider).FirstOrDefault();
        return root == null ? null : Path.Combine(root, DefaultSubfolder);
    }

    // ── OneDrive ────────────────────────────────────────────────────

    /// <summary>
    /// OneDrive sync roots. Personal and Business accounts both sync to their own folder and a
    /// machine can have several, so all of them are returned.
    /// </summary>
    public static IReadOnlyList<string> FindOneDriveRoots()
    {
        var found = new List<string>();

        // The environment variables OneDrive sets for its own clients. `OneDrive` points at
        // whichever account the user made primary, so it goes first.
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            AddIfDirectory(found, Environment.GetEnvironmentVariable(variable));

        // Accounts registered with the sync client. Covers the case where the app was started
        // from a context that did not inherit OneDrive's environment variables — a scheduled
        // task, or a shell that was already open when OneDrive was installed.
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts != null)
            {
                foreach (var name in accounts.GetSubKeyNames())
                {
                    using var account = accounts.OpenSubKey(name);
                    AddIfDirectory(found, account?.GetValue("UserFolder") as string);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read OneDrive accounts from the registry");
        }

        AddIfDirectory(found, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"));

        return found;
    }

    // ── Google Drive ────────────────────────────────────────────────

    /// <summary>
    /// Google Drive roots. Drive for desktop mounts a virtual drive whose useful contents live
    /// under <c>My Drive</c>, so that subfolder is preferred over the mount root when present —
    /// the root itself also holds read-only <c>Shared drives</c>, which is not somewhere settings
    /// can be written.
    /// </summary>
    public static IReadOnlyList<string> FindGoogleDriveRoots()
    {
        var found = new List<string>();

        // Drive for desktop records its mount point per signed-in account as JSON.
        try
        {
            using var driveFs = Registry.CurrentUser.OpenSubKey(@"Software\Google\DriveFS");
            if (driveFs?.GetValue("PerAccountPreferences") is string json && json.Length > 0)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("per_account_preferences", out var accounts)
                    && accounts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var account in accounts.EnumerateArray())
                    {
                        if (account.TryGetProperty("value", out var value)
                            && value.TryGetProperty("mount_point_path", out var mount)
                            && mount.GetString() is string path)
                        {
                            AddGoogleDriveMount(found, path);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read Google Drive preferences from the registry");
        }

        // Fall back to the mounted volume. Drive for desktop labels it "Google Drive", and the
        // letter is user-configurable so it cannot simply be assumed to be G:.
        foreach (var drive in SafeGetDrives())
        {
            try
            {
                if (drive.IsReady && drive.VolumeLabel.StartsWith("Google Drive", StringComparison.OrdinalIgnoreCase))
                    AddGoogleDriveMount(found, drive.RootDirectory.FullName);
            }
            catch (Exception ex)
            {
                // An unready or permission-denied drive is not an error worth surfacing.
                Logger.Debug(ex, $"Could not inspect drive {drive.Name}");
            }
        }

        // Legacy Backup and Sync put a plain folder in the user profile.
        AddIfDirectory(found, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive"));

        return found;
    }

    /// <summary>Add a Drive mount point, preferring its <c>My Drive</c> subfolder.</summary>
    private static void AddGoogleDriveMount(List<string> found, string mountPath)
    {
        if (string.IsNullOrWhiteSpace(mountPath)) return;

        string myDrive = Path.Combine(mountPath, "My Drive");
        if (Directory.Exists(myDrive))
        {
            AddIfDirectory(found, myDrive);
            return;
        }

        AddIfDirectory(found, mountPath);
    }

    private static DriveInfo[] SafeGetDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not enumerate drives");
            return [];
        }
    }

    // ── Shared ──────────────────────────────────────────────────────

    /// <summary>
    /// Append <paramref name="path"/> when it names a real directory and is not already listed.
    /// Several detection routes legitimately find the same folder, so de-duplication is the norm
    /// rather than the exception.
    /// </summary>
    private static void AddIfDirectory(List<string> found, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!Directory.Exists(path)) return;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Could not probe candidate sync folder: {path}");
            return;
        }

        string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!found.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            found.Add(normalized);
    }
}
