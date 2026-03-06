using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Dispatching;
using Renci.SshNet;
using System.Collections.ObjectModel;
using System.IO;
using System.Xml.Serialization;

namespace LittleLauncher.Services;

/// <summary>
/// Provides SSH/SFTP-based settings synchronization.
/// Uploads or downloads the application's settings.xml to/from a remote server.
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

    // ── Launcher-items-only sync ───────────────────────────────────

    /// <summary>
    /// Upload only the launcher items list to the remote SFTP server.
    /// The synced file contains no other settings — only launcher items.
    /// </summary>
    public static async Task<(bool Success, string Message)> UploadLauncherItemsAsync(string? password = null)
    {
        try
        {
            SettingsManager.SaveSettings();

            using var client = CreateSftpClient(password);
            await Task.Run(() => client.Connect());

            string remoteDir = GetRemoteDirectory(client);
            string remotePath = $"{remoteDir}/launcher-items.xml";

            await Task.Run(() => EnsureRemoteDirectory(client, remoteDir));

            using var stream = SerializeLauncherItems(SettingsManager.Current.LauncherItems);
            await Task.Run(() => client.UploadFile(stream, remotePath, canOverride: true));

            client.Disconnect();

            Logger.Info($"Launcher items uploaded to {remotePath}");
            return (true, $"Launcher items uploaded to {SettingsManager.Current.SftpHost}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to upload launcher items via SFTP");
            return (false, $"Upload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Download launcher items from the remote SFTP server and merge into current settings.
    /// Only replaces the launcher items list — all other settings are preserved.
    /// </summary>
    public static async Task<(bool Success, string Message)> DownloadLauncherItemsAsync(string? password = null)
    {
        try
        {
            using var client = CreateSftpClient(password);
            await Task.Run(() => client.Connect());

            string remoteDir = GetRemoteDirectory(client);
            string remotePath = $"{remoteDir}/launcher-items.xml";

            if (!await Task.Run(() => client.Exists(remotePath)))
            {
                client.Disconnect();
                return (false, "No launcher items file found on the remote server.");
            }

            using var stream = new MemoryStream();
            await Task.Run(() => client.DownloadFile(remotePath, stream));
            stream.Position = 0;

            client.Disconnect();

            var items = DeserializeLauncherItems(stream);
            if (items == null)
                return (false, "Failed to parse launcher items from server.");

            // Replace the launcher items collection on the UI thread
            await ApplyLauncherItemsAsync(items);

            SettingsManager.SaveSettings();

            // Fetch missing web icons in the background (fire-and-forget)
            _ = FetchMissingIconsAsync();

            Logger.Info($"Launcher items downloaded from {remotePath}");
            return (true, $"Launcher items downloaded from {SettingsManager.Current.SftpHost}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download launcher items via SFTP");
            return (false, $"Download failed: {ex.Message}");
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
    /// Iterates launcher items that are websites and fetches any missing favicons.
    /// Called after downloading settings so synced items get their icons on this machine.
    /// </summary>
    private static async Task FetchMissingIconsAsync()
    {
        var items = SettingsManager.Current.LauncherItems;
        bool changed = false;

        foreach (var item in items)
        {
            if (!item.IsWebsite || string.IsNullOrWhiteSpace(item.Path))
                continue;

            // Already has a valid local icon
            if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
                continue;

            try
            {
                var cached = FaviconService.GetCachedPath(item.Path);
                if (cached != null)
                {
                    item.IconPath = cached;
                    changed = true;
                    continue;
                }

                var iconPath = await FaviconService.FetchAndCacheAsync(item.Path);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    item.IconPath = iconPath;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Failed to fetch icon for {item.Path}");
            }
        }

        if (changed)
            SettingsManager.SaveSettings();
    }

    /// <summary>
    /// Replace the LauncherItems ObservableCollection contents on the UI thread.
    /// The collection is bound to WinUI controls, so mutations must happen on the dispatcher thread.
    /// </summary>
    private static async Task ApplyLauncherItemsAsync(List<LauncherItem> items)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher != null)
        {
            // Already on UI thread
            ReplaceItems(items);
        }
        else
        {
            // Marshal to UI thread
            var tcs = new TaskCompletionSource();
            App.MainDispatcherQueue.TryEnqueue(() =>
            {
                ReplaceItems(items);
                tcs.SetResult();
            });
            await tcs.Task;
        }

        static void ReplaceItems(List<LauncherItem> items)
        {
            var current = SettingsManager.Current.LauncherItems;
            current.Clear();
            foreach (var item in items)
                current.Add(item);
        }
    }

    /// <summary>
    /// Serialize only the launcher items list to a MemoryStream.
    /// </summary>
    private static MemoryStream SerializeLauncherItems(ObservableCollection<LauncherItem> items)
    {
        var list = new List<LauncherItem>(items);
        var stream = new MemoryStream();
        var serializer = new XmlSerializer(typeof(List<LauncherItem>));
        serializer.Serialize(stream, list);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Deserialize a launcher items list from a stream.
    /// </summary>
    private static List<LauncherItem>? DeserializeLauncherItems(MemoryStream stream)
    {
        var serializer = new XmlSerializer(typeof(List<LauncherItem>));
        return serializer.Deserialize(stream) as List<LauncherItem>;
    }
}
