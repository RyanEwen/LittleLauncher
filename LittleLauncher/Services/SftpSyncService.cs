using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Dispatching;
using Renci.SshNet;
using System.Collections.ObjectModel;
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
/// </summary>
public static class SftpSyncService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

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
            string remotePath = $"{remoteDir}/launchers.json";

            await Task.Run(() => EnsureRemoteDirectory(client, remoteDir));

            var launchers = SettingsManager.Current.Launchers;
            using var stream = SerializeLaunchers(launchers);
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
            string remotePath = $"{remoteDir}/launchers.json";

            if (!await Task.Run(() => client.Exists(remotePath)))
            {
                client.Disconnect();
                return (false, "No launchers file found on the remote server.");
            }

            using var stream = new MemoryStream();
            await Task.Run(() => client.DownloadFile(remotePath, stream));
            stream.Position = 0;

            client.Disconnect();

            var (launchers, remoteTimestamp) = DeserializeLaunchers(stream);
            if (launchers == null)
                return (false, "Failed to parse launchers from server.");

            // If this is a startup sync and we have a remote timestamp,
            // compare with local settings file to avoid overwriting newer local changes.
            // Applies to every automatic download, not just the one at startup. Periodic syncs
            // used to skip this check entirely, so the server overwrote local launchers every few
            // minutes however recently they had been edited — changes vanished well after being
            // saved, which is precisely the failure this guard exists to prevent.
            if (!force)
            {
                var localModified = SettingsManager.Current.LaunchersModifiedUtc;
                if (localModified != default)
                {
                    Logger.Info($"Download skipped: local launcher changes at {localModified:u} have not been uploaded yet");
                    return (false, "Local launcher changes are newer than the server; skipped download.");
                }

                if (remoteTimestamp.HasValue)
                {
                    var localSettingsPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "LittleLauncher", "settings.json");
                    if (File.Exists(localSettingsPath))
                    {
                        var localFileModified = File.GetLastWriteTimeUtc(localSettingsPath);
                        if (localFileModified > remoteTimestamp.Value.UtcDateTime)
                        {
                            Logger.Info($"Download skipped: local settings ({localFileModified:u}) " +
                                        $"are newer than remote ({remoteTimestamp.Value.UtcDateTime:u})");
                            return (false, "Local settings are newer than server; skipped download.");
                        }
                    }
                }
            }

            await ApplyLaunchersAsync(launchers);

            // Normalize legacy glyphs and fetch missing icons
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                foreach (var item in launcher.Items)
                {
                    item.NormalizeGlyph();
                    if (item.IsGroup)
                        foreach (var child in item.Children)
                            child.NormalizeGlyph();
                }
                await FaviconService.FetchMissingItemIconsAsync(launcher.Items);
            }

            SettingsManager.SaveSettings();

            Logger.Info($"Launchers downloaded from {remotePath}");
            return (true, $"Launchers downloaded from {SettingsManager.Current.SftpHost}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download launchers via SFTP");
            return (false, $"Download failed: {ex.Message}");
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

            // File mode always works; SFTP mode requires an auto key
            if (launcher.IsSftpSync && !HasAutoKeyForShared(launcher)) continue;

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
            if (launcher.IsSftpSync && !HasAutoKeyForShared(launcher)) continue;

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

            var items = new List<LauncherItem>(launcher.Items);
            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions);

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

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            List<LauncherItem>? items;
            try
            {
                items = await JsonSerializer.DeserializeAsync<List<LauncherItem>>(stream, JsonOptions);
            }
            catch
            {
                return (false, "Failed to parse shared launcher file.");
            }

            if (items == null)
                return (false, "Shared launcher file was empty.");

            await ApplySharedItemsAsync(launcher, items);
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

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var items = await JsonSerializer.DeserializeAsync<List<LauncherItem>>(stream, JsonOptions);
            if (items == null)
                return (false, 0, "File exists but could not be parsed.");

            return (true, items.Count, "");
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

            var items = new List<LauncherItem>(launcher.Items);
            using var stream = new MemoryStream();
            JsonSerializer.Serialize(stream, items, JsonOptions);
            stream.Position = 0;
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

            List<LauncherItem>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<LauncherItem>>(ms, JsonOptions);
            }
            catch
            {
                return (false, "Failed to parse shared launcher file.");
            }

            if (items == null)
                return (false, "Shared launcher file was empty.");

            await ApplySharedItemsAsync(launcher, items);
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

            var items = JsonSerializer.Deserialize<List<LauncherItem>>(ms, JsonOptions);
            if (items == null)
                return (false, 0, "File exists but could not be parsed.");

            return (true, items.Count, "");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Apply downloaded shared items to a launcher on the UI thread,
    /// fetch missing icons, and save settings.
    /// Suppresses the auto-sync upload trigger to prevent feedback loops.
    /// </summary>
    private static async Task ApplySharedItemsAsync(Launcher launcher, List<LauncherItem> items)
    {
        AutoSyncService.SuppressNextChange = true;
        var tcs = new TaskCompletionSource();
        App.MainDispatcherQueue.TryEnqueue(() =>
        {
            launcher.Items.Clear();
            foreach (var item in items)
            {
                item.NormalizeGlyph();
                launcher.Items.Add(item);
            }
            tcs.SetResult();
        });
        await tcs.Task;

        await FaviconService.FetchMissingItemIconsAsync(launcher.Items);
        SettingsManager.SaveSettings();
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
    /// Merge downloaded launchers into the existing Launchers collection on the UI thread.
    /// Existing launchers are updated in-place (preserving object references for PropertyChanged
    /// subscriptions and FlyoutWindow instances). New launchers are added; missing ones removed.
    /// </summary>
    private static async Task ApplyLaunchersAsync(List<Launcher> launchers)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher != null)
        {
            MergeLaunchers(launchers);
        }
        else
        {
            var tcs = new TaskCompletionSource();
            App.MainDispatcherQueue.TryEnqueue(() =>
            {
                MergeLaunchers(launchers);
                tcs.SetResult();
            });
            await tcs.Task;
        }

        static void MergeLaunchers(List<Launcher> launchers)
        {
            var current = SettingsManager.Current.Launchers;
            var downloadedById = launchers.ToDictionary(l => l.Id);

            // Remove launchers that no longer exist on the server
            for (int i = current.Count - 1; i >= 0; i--)
            {
                if (!downloadedById.ContainsKey(current[i].Id))
                {
                    Windows.LauncherPanels.Dispose(current[i].Id);
                    current.RemoveAt(i);
                }
            }

            // Update existing launchers in-place; add new ones
            foreach (var downloaded in launchers)
            {
                var existing = current.FirstOrDefault(l => l.Id == downloaded.Id);
                if (existing != null)
                {
                    existing.Name = downloaded.Name;
                    existing.TrayIconMode = downloaded.TrayIconMode;
                    existing.CustomTrayIconPath = downloaded.CustomTrayIconPath;
                    existing.NIconHide = downloaded.NIconHide;

                    // A web launcher carries no items, so without these it would arrive on the
                    // other machine as an empty shortcut launcher.
                    existing.Kind = downloaded.Kind;
                    existing.WebUrl = downloaded.WebUrl;
                    existing.WebFlyoutWidth = downloaded.WebFlyoutWidth;
                    existing.WebFlyoutHeight = downloaded.WebFlyoutHeight;
                    existing.WebZoomPercent = downloaded.WebZoomPercent;
                    existing.WebHiddenPolicy = downloaded.WebHiddenPolicy;
                    existing.WebIdleUnloadMinutes = downloaded.WebIdleUnloadMinutes;
                    existing.WebReloadOnShow = downloaded.WebReloadOnShow;
                    existing.WebPinFlyout = downloaded.WebPinFlyout;

                    existing.Items.Clear();
                    foreach (var item in downloaded.Items)
                        existing.Items.Add(item);
                }
                else
                {
                    current.Add(downloaded);
                }
            }
        }
    }

    /// <summary>
    /// Envelope for the remote launchers.json file. Includes a UTC timestamp
    /// so startup downloads can skip overwriting newer local data.
    /// </summary>
    private sealed class LaunchersEnvelope
    {
        public DateTimeOffset LastModified { get; set; }
        public List<Launcher> Launchers { get; set; } = [];
    }

    /// <summary>
    /// Serialize launchers to a MemoryStream as JSON, wrapped in a timestamped envelope.
    /// </summary>
    private static MemoryStream SerializeLaunchers(ObservableCollection<Launcher> launchers)
    {
        var envelope = new LaunchersEnvelope
        {
            LastModified = DateTimeOffset.UtcNow,
            Launchers = new List<Launcher>(launchers)
        };
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, envelope, JsonOptions);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Deserialize launchers from a JSON stream.
    /// Supports both the new envelope format and the legacy plain array format.
    /// Returns the launcher list and an optional timestamp (null for legacy data).
    /// </summary>
    private static (List<Launcher>? Launchers, DateTimeOffset? LastModified) DeserializeLaunchers(MemoryStream stream)
    {
        try
        {
            // Try envelope format first
            var envelope = JsonSerializer.Deserialize<LaunchersEnvelope>(stream, JsonOptions);
            if (envelope?.Launchers != null && envelope.Launchers.Count > 0)
                return (envelope.Launchers, envelope.LastModified);
        }
        catch { }

        // Fall back to legacy plain array
        stream.Position = 0;
        try
        {
            var list = JsonSerializer.Deserialize<List<Launcher>>(stream, JsonOptions);
            return (list, null);
        }
        catch
        {
            return (null, null);
        }
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
