using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Renci.SshNet;
using System.IO;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// Provides SSH/SFTP-based settings synchronization.
/// Uploads or downloads all launchers to/from a remote server as JSON.
///
/// Architecture notes:
///   - Uses SSH.NET (Renci.SshNet) for SFTP operations.
///   - Supports both private-key and password authentication.
///   - The remote path is fully configurable in UserSettings.
///   - Thread-safe: all operations are async and self-contained.
///   - One of two global sync transports; <see cref="FolderSyncService"/> is the other, and
///     <see cref="LauncherSyncService"/> chooses between them. The wire format and the
///     newer-local download guard are shared, in <see cref="LauncherPayload"/>.
///   - Also owns the per-launcher *shared* sync, which is independent of the global transport
///     and has its own File/SFTP setting on each launcher.
/// </summary>
public static class SftpSyncService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions JsonOptions = LauncherPayload.JsonOptions;

    /// <summary>
    /// Test the SFTP connection with current settings.
    /// </summary>
    public static async Task<(bool Success, string Message)> TestConnectionAsync(string? password = null)
    {
        try
        {
            using var client = CreateSftpClient(password);
            await Task.Run(() => client.Connect());
            bool connected = client.IsConnected;
            client.Disconnect();

            return connected
                ? (true, "Connection successful!")
                : (false, "Connection failed — no error but not connected.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SFTP connection test failed");
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    // ── Launcher sync ──────────────────────────────────────────────

    /// <summary>
    /// Upload all launchers to the remote SFTP server as JSON.
    /// </summary>
    public static async Task<(bool Success, string Message)> UploadLaunchersAsync(string? password = null)
    {
        try
        {
            SettingsManager.SaveSettings();

            using var client = CreateSftpClient(password);
            await Task.Run(() => client.Connect());

            string remoteDir = GetRemoteDirectory(client);
            string remotePath = $"{remoteDir}/{LauncherPayload.FileName}";

            await Task.Run(() => EnsureRemoteDirectory(client, remoteDir));

            using var stream = LauncherPayload.Serialize(SettingsManager.Current.Launchers);
            await Task.Run(() => client.UploadFile(stream, remotePath, canOverride: true));

            client.Disconnect();

            Logger.Info($"Launchers uploaded to {remotePath}");
            return (true, $"Launchers uploaded to {SettingsManager.Current.SftpHost}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to upload launchers via SFTP");
            return (false, $"Upload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Download all launchers from the remote SFTP server and replace current launchers.
    /// Falls back to legacy launcher-items.xml if launchers.json doesn't exist.
    /// When called during auto-sync startup, compares remote timestamp with local
    /// settings file to avoid overwriting newer local data.
    /// </summary>
    /// <param name="password">Optional SSH key passphrase.</param>
    /// <param name="force">
    /// Applies the remote copy even when local changes are newer. Only for a download the user
    /// explicitly asked for — automatic syncs must never overrule newer local work.
    /// </param>
    public static async Task<(bool Success, string Message)> DownloadLaunchersAsync(
        string? password = null, bool force = false)
    {
        try
        {
            using var client = CreateSftpClient(password);
            await Task.Run(() => client.Connect());

            string remoteDir = GetRemoteDirectory(client);
            string remotePath = $"{remoteDir}/{LauncherPayload.FileName}";

            if (!await Task.Run(() => client.Exists(remotePath)))
            {
                client.Disconnect();
                return (false, "No launchers file found on the remote server.");
            }

            using var stream = new MemoryStream();
            await Task.Run(() => client.DownloadFile(remotePath, stream));
            stream.Position = 0;

            client.Disconnect();

            var (launchers, remoteTimestamp, extensions) = LauncherPayload.Deserialize(stream);
            if (launchers == null)
                return (false, "Failed to parse launchers from server.");

            if (LauncherPayload.ShouldSkipDownload(remoteTimestamp, force, out string reason))
                return (false, reason);

            await LauncherPayload.ApplyAsync(launchers, extensions);

            Logger.Info($"Launchers downloaded from {remotePath}");
            return (true, $"Launchers downloaded from {SettingsManager.Current.SftpHost}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download launchers via SFTP");
            return (false, $"Download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// When the server's copy last changed, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Used to pick which destination a download comes from, so it must not throw: an unreachable
    /// host returning null takes it out of the running rather than failing the whole download.
    /// Connects only when an SSH key resolves without a passphrase — this runs unattended on the
    /// sync timer and must never sit waiting on a prompt.
    /// </remarks>
    public static async Task<DateTimeOffset?> GetRemoteModifiedAsync()
    {
        if (string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost)) return null;

        try
        {
            using var client = CreateSftpClient(null);
            await Task.Run(() => client.Connect());

            string remotePath = $"{GetRemoteDirectory(client)}/{LauncherPayload.FileName}";
            DateTimeOffset? modified = await Task.Run<DateTimeOffset?>(() =>
                client.Exists(remotePath)
                    ? new DateTimeOffset(client.GetAttributes(remotePath).LastWriteTimeUtc, TimeSpan.Zero)
                    : null);

            client.Disconnect();
            return modified;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read the SFTP timestamp");
            return null;
        }
    }

    // ── Shared launcher sync ──────────────────────────────────────

    /// <summary>
    /// Push a shared launcher's items to its configured location (owner mode).
    /// Dispatches to file or SFTP based on <see cref="Launcher.SharedSyncMode"/>.
    /// </summary>
    public static async Task<(bool Success, string Message)> ShareLauncherAsync(
        Launcher launcher, string? password = null)
    {
        if (launcher.IsOneDriveSync) return await OneDriveSharedStore.PushAsync(launcher);
        if (launcher.IsWebDavSync) return await WebDavSharedStore.PushAsync(launcher);

        return launcher.IsFileSync
            ? await ShareLauncherFileAsync(launcher)
            : await ShareLauncherSftpAsync(launcher, password);
    }

    /// <summary>
    /// Pull a shared launcher's items from its configured location (subscriber mode).
    /// Dispatches to file or SFTP based on <see cref="Launcher.SharedSyncMode"/>.
    /// </summary>
    public static async Task<(bool Success, string Message)> SyncSharedLauncherAsync(
        Launcher launcher, string? password = null)
    {
        if (launcher.IsOneDriveSync) return await OneDriveSharedStore.PullAsync(launcher);
        if (launcher.IsWebDavSync) return await WebDavSharedStore.PullAsync(launcher);

        return launcher.IsFileSync
            ? await SyncSharedLauncherFileAsync(launcher)
            : await SyncSharedLauncherSftpAsync(launcher, password);
    }

    /// <summary>
    /// Verify a shared launcher's location is reachable and contains valid data.
    /// Returns (true, itemCount, "") on success or (false, 0, errorMessage) on failure.
    /// </summary>
    public static async Task<(bool Success, int ItemCount, string Error)> VerifySharedLauncherAsync(
        Launcher launcher, string? password = null)
    {
        if (launcher.IsOneDriveSync) return await OneDriveSharedStore.VerifyAsync(launcher);
        if (launcher.IsWebDavSync) return await WebDavSharedStore.VerifyAsync(launcher);

        return launcher.IsFileSync
            ? await VerifySharedLauncherFileAsync(launcher)
            : await VerifySharedLauncherSftpAsync(launcher, password);
    }

    /// <summary>
    /// Sync all shared launchers silently.
    /// 2-way launchers: pull then push.
    /// 1-way launchers: owners push, subscribers pull.
    /// Skips SFTP launchers without an auto-detectable SSH key.
    /// </summary>
    public static async Task SyncAllSharedLaunchersAsync()
    {
        foreach (var launcher in SettingsManager.Current.Launchers.ToList())
        {
            if (!launcher.IsShared) continue;

            // File mode always works; SFTP needs a passphrase-free key; WebDAV needs its
            // stored password. Anything that would prompt is skipped — this runs on a timer.
            if (!HasAutoKeyForShared(launcher)) continue;

            if (launcher.SharedTwoWay)
            {
                // 2-way: pull first, then push
                var (pullOk, pullMsg) = await SyncSharedLauncherAsync(launcher);
                if (!pullOk) Logger.Warn($"Shared pull failed for '{launcher.Name}': {pullMsg}");

                var (pushOk, pushMsg) = await ShareLauncherAsync(launcher);
                if (!pushOk) Logger.Warn($"Shared push failed for '{launcher.Name}': {pushMsg}");
            }
            else if (launcher.IsSharedOwner)
            {
                var (ok, msg) = await ShareLauncherAsync(launcher);
                if (!ok) Logger.Warn($"Shared outgoing sync failed for '{launcher.Name}': {msg}");
            }
            else
            {
                var (ok, msg) = await SyncSharedLauncherAsync(launcher);
                if (!ok) Logger.Warn($"Shared incoming sync failed for '{launcher.Name}': {msg}");
            }
        }
    }

    /// <summary>
    /// Push shared launchers that this user can write to (2-way participants and 1-way owners).
    /// Used by auto-sync after debounced item changes to propagate edits without pulling first.
    /// Skips SFTP launchers without an auto-detectable SSH key.
    /// </summary>
    public static async Task PushAllSharedLaunchersAsync()
    {
        foreach (var launcher in SettingsManager.Current.Launchers.ToList())
        {
            if (!launcher.IsShared) continue;
            if (!launcher.SharedTwoWay && !launcher.IsSharedOwner) continue;
            if (!HasAutoKeyForShared(launcher)) continue;

            var (ok, msg) = await ShareLauncherAsync(launcher);
            if (!ok) Logger.Warn($"Shared push failed for '{launcher.Name}': {msg}");
        }
    }

    /// <summary>
    /// Returns true if no password prompt is required to sync this shared launcher.
    /// File mode always returns true. SFTP mode checks for an auto-resolvable SSH key.
    /// </summary>
    public static bool HasAutoKeyForShared(Launcher launcher)
    {
        if (launcher.IsFileSync) return true;

        // WebDAV keeps its own password in ProtectedStore, so it never needs a prompt — but it
        // does need that password to actually be there.
        if (launcher.IsWebDavSync) return WebDavSharedStore.HasCredentials(launcher);

        // OneDrive sharing rides the stored account token, so it never prompts — but it does
        // need the wider sharing grant, and an unattended sync must not try to obtain one.
        if (launcher.IsOneDriveSync) return OneDriveSharedStore.HasConsent;

        string? keyPath = ResolvePrivateKeyPath(
            string.IsNullOrWhiteSpace(launcher.SharedSftpPrivateKeyPath) ? null : launcher.SharedSftpPrivateKeyPath);
        return keyPath != null;
    }

    // ── File-based shared sync ──────────────────────────────────────

    private static async Task<(bool Success, string Message)> ShareLauncherFileAsync(Launcher launcher)
    {
        try
        {
            string path = launcher.SharedPath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            byte[] bytes = SharedLauncherPayload.Serialize(launcher);

            // Written atomically because a shared path is very often a cloud-synced folder — the
            // OneDrive or Drive folder is the easiest way to share a launcher with someone. Those
            // clients upload the instant a file changes, so a plain write can be uploaded
            // half-finished and land on a subscriber as truncated JSON.
            await Task.Run(() => LauncherPayload.WriteAtomic(path, bytes));

            Logger.Info($"Shared launcher '{launcher.Name}' written to {path}");
            return (true, $"Saved to {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to write shared launcher '{launcher.Name}' to file");
            return (false, $"Save failed: {ex.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> SyncSharedLauncherFileAsync(Launcher launcher)
    {
        try
        {
            string path = launcher.SharedPath;
            if (!File.Exists(path))
                return (false, "Shared launcher file not found.");

            var file = SharedLauncherPayload.Deserialize(await File.ReadAllBytesAsync(path));
            if (file == null)
                return (false, "Failed to parse shared launcher file.");

            await SharedLauncherPayload.ApplyAsync(launcher, file);
            Logger.Info($"Shared launcher '{launcher.Name}' synced from {path}");
            return (true, "Shared launcher updated.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to read shared launcher '{launcher.Name}' from file");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    private static async Task<(bool Success, int ItemCount, string Error)> VerifySharedLauncherFileAsync(Launcher launcher)
    {
        try
        {
            string path = launcher.SharedPath;
            if (!File.Exists(path))
                return (false, 0, "File not found.");

            var file = SharedLauncherPayload.Deserialize(await File.ReadAllBytesAsync(path));
            if (file == null)
                return (false, 0, "File exists but could not be parsed.");

            return (true, file.Items.Count, "");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    // ── SFTP-based shared sync ──────────────────────────────────────

    private static async Task<(bool Success, string Message)> ShareLauncherSftpAsync(
        Launcher launcher, string? password)
    {
        try
        {
            using var client = CreateSharedSftpClient(launcher, password);
            await Task.Run(() => client.Connect());

            string remotePath = ResolveRemotePath(client, launcher.SharedPath);
            string? remoteDir = RemoteDirOf(remotePath);
            if (!string.IsNullOrEmpty(remoteDir))
                await Task.Run(() => EnsureRemoteDirectory(client, remoteDir));

            using var stream = new MemoryStream(SharedLauncherPayload.Serialize(launcher));
            await Task.Run(() => client.UploadFile(stream, remotePath, canOverride: true));

            client.Disconnect();

            Logger.Info($"Shared launcher '{launcher.Name}' uploaded to {launcher.SharedSftpHost}:{launcher.SharedPath}");
            return (true, $"Synced to {launcher.SharedSftpHost}:{launcher.SharedPath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to share launcher '{launcher.Name}'");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> SyncSharedLauncherSftpAsync(
        Launcher launcher, string? password)
    {
        try
        {
            using var client = CreateSharedSftpClient(launcher, password);
            await Task.Run(() => client.Connect());

            string remotePath = ResolveRemotePath(client, launcher.SharedPath);
            if (!await Task.Run(() => client.Exists(remotePath)))
            {
                client.Disconnect();
                return (false, "Shared launcher file not found on remote server.");
            }

            using var ms = new MemoryStream();
            await Task.Run(() => client.DownloadFile(remotePath, ms));
            ms.Position = 0;
            client.Disconnect();

            var file = SharedLauncherPayload.Deserialize(ms.ToArray());
            if (file == null)
                return (false, "Failed to parse shared launcher file.");

            await SharedLauncherPayload.ApplyAsync(launcher, file);
            Logger.Info($"Shared launcher '{launcher.Name}' synced from {launcher.SharedSftpHost}");
            return (true, "Shared launcher updated.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to sync shared launcher '{launcher.Name}'");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    private static async Task<(bool Success, int ItemCount, string Error)> VerifySharedLauncherSftpAsync(
        Launcher launcher, string? password)
    {
        try
        {
            using var client = CreateSharedSftpClient(launcher, password);
            await Task.Run(() => client.Connect());

            string remotePath = ResolveRemotePath(client, launcher.SharedPath);
            if (!await Task.Run(() => client.Exists(remotePath)))
            {
                client.Disconnect();
                return (false, 0, "File not found on server.");
            }

            using var ms = new MemoryStream();
            await Task.Run(() => client.DownloadFile(remotePath, ms));
            ms.Position = 0;
            client.Disconnect();

            var file = SharedLauncherPayload.Deserialize(ms.ToArray());
            if (file == null)
                return (false, 0, "File exists but could not be parsed.");

            return (true, file.Items.Count, "");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    // ── Private helpers ─────────────────────────────────────────────

    /// <summary>
    /// Well-known SSH private key filenames, checked in order of preference.
    /// </summary>
    private static readonly string[] DefaultKeyNames =
    [
        "id_ed25519",
        "id_rsa",
        "id_ecdsa",
        "id_dsa"
    ];

    private static SftpClient CreateSftpClient(string? password)
    {
        var settings = SettingsManager.Current;

        if (string.IsNullOrWhiteSpace(settings.SftpHost))
            throw new InvalidOperationException("SFTP host is not configured.");

        // Default to Windows username if not specified
        string username = string.IsNullOrWhiteSpace(settings.SftpUsername)
            ? Environment.UserName
            : settings.SftpUsername;

        // Resolve the key path: use explicit setting, or auto-detect from ~/.ssh/
        string? keyPath = ResolvePrivateKeyPath(settings.SftpPrivateKeyPath);

        if (keyPath != null)
        {
            var keyFile = string.IsNullOrEmpty(password)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, password);

            var keyAuth = new PrivateKeyAuthenticationMethod(username, keyFile);
            var connectionInfo = new ConnectionInfo(settings.SftpHost, settings.SftpPort, username, keyAuth);
            Logger.Info($"Using SSH key: {keyPath}");
            return new SftpClient(connectionInfo);
        }

        // Fall back to password authentication
        if (!string.IsNullOrEmpty(password))
        {
            return new SftpClient(settings.SftpHost, settings.SftpPort, username, password);
        }

        throw new InvalidOperationException("No SSH key found and no password provided. Place a key in ~/.ssh/ or specify a path.");
    }

    /// <summary>
    /// Resolves the private key path. If explicitly set, validates it exists.
    /// If empty, auto-detects from %USERPROFILE%\.ssh\.
    /// </summary>
    private static string? ResolvePrivateKeyPath(string? configuredPath)
    {
        // Explicit override — use it if the file exists
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
                return configuredPath;

            Logger.Warn($"Configured SSH key not found: {configuredPath}");
            return null;
        }

        // Auto-detect from ~/.ssh/
        string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (!Directory.Exists(sshDir))
            return null;

        foreach (var name in DefaultKeyNames)
        {
            string candidate = Path.Combine(sshDir, name);
            if (File.Exists(candidate))
            {
                Logger.Info($"Auto-detected SSH key: {candidate}");
                return candidate;
            }
        }

        return null;
    }

    private static string GetRemoteDirectory(SftpClient client)
    {
        return ResolveRemotePath(client, SettingsManager.Current.SftpRemotePath).TrimEnd('/');
    }

    /// <summary>
    /// Expand ~ to the SFTP user's home directory.
    /// </summary>
    private static string ResolveRemotePath(SftpClient client, string path)
    {
        if (path.StartsWith('~'))
        {
            string home = client.WorkingDirectory.TrimEnd('/');
            return home + path[1..];
        }
        return path;
    }

    private static void EnsureRemoteDirectory(SftpClient client, string path)
    {
        // Try creating each segment; ignore failures for segments that already exist
        string current = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            try
            {
                client.CreateDirectory(current);
            }
            catch (Renci.SshNet.Common.SshException)
            {
                // Directory likely already exists — only fail if the final target
                // still doesn't exist after all attempts
            }
        }

        if (!client.Exists(path))
            throw new InvalidOperationException($"Failed to create remote directory: {path}");
    }

    /// <summary>
    /// Create an SFTP client using per-launcher shared connection settings.
    /// </summary>
    private static SftpClient CreateSharedSftpClient(Launcher launcher, string? password)
    {
        if (string.IsNullOrWhiteSpace(launcher.SharedSftpHost))
            throw new InvalidOperationException("SFTP host is not configured for this shared launcher.");

        string username = string.IsNullOrWhiteSpace(launcher.SharedSftpUsername)
            ? Environment.UserName
            : launcher.SharedSftpUsername;

        string? keyPath = ResolvePrivateKeyPath(
            string.IsNullOrWhiteSpace(launcher.SharedSftpPrivateKeyPath) ? null : launcher.SharedSftpPrivateKeyPath);

        if (keyPath != null)
        {
            var keyFile = string.IsNullOrEmpty(password)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, password);
            var keyAuth = new PrivateKeyAuthenticationMethod(username, keyFile);
            var connInfo = new ConnectionInfo(launcher.SharedSftpHost, launcher.SharedSftpPort, username, keyAuth);
            return new SftpClient(connInfo);
        }

        if (!string.IsNullOrEmpty(password))
            return new SftpClient(launcher.SharedSftpHost, launcher.SharedSftpPort, username, password);

        throw new InvalidOperationException(
            "No SSH key found and no password provided. Place a key in ~/.ssh/ or specify a path.");
    }

    /// <summary>Extract the directory portion of a remote path.</summary>
    private static string? RemoteDirOf(string path)
    {
        int idx = path.LastIndexOf('/');
        if (idx <= 0) return null;
        return path[..idx];
    }
}
