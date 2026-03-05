using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.ViewModels;
using Renci.SshNet;
using System.IO;
using System.Xml.Serialization;

namespace SelfHostedHelper.Services;

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

    private static readonly string LocalSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SelfHostedHelper",
        "settings.xml"
    );

    /// <summary>
    /// Upload the current local settings file to the remote SFTP server.
    /// </summary>
    public static async Task<(bool Success, string Message)> UploadSettingsAsync(string? password = null)
    {
        try
        {
            // Ensure local settings are saved first
            SettingsManager.SaveSettings();

            if (!File.Exists(LocalSettingsPath))
                return (false, "Local settings file not found.");

            using var client = CreateSftpClient(password);
            await Task.Run(() => client.Connect());

            string remotePath = GetRemoteFilePath(client);
            string remoteDir = GetRemoteDirectory(client);

            // Ensure remote directory exists
            await Task.Run(() => EnsureRemoteDirectory(client, remoteDir));

            // Upload a sanitized copy without SSH connection settings
            using var stream = SerializeWithoutSshSettings(SettingsManager.Current);
            await Task.Run(() => client.UploadFile(stream, remotePath, canOverride: true));

            client.Disconnect();

            Logger.Info($"Settings uploaded to {remotePath}");
            return (true, $"Settings uploaded to {SettingsManager.Current.SftpHost}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to upload settings via SFTP");
            return (false, $"Upload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Download settings from the remote SFTP server and overwrite local settings.
    /// Returns true if settings were successfully downloaded and applied.
    /// </summary>
    public static async Task<(bool Success, string Message)> DownloadSettingsAsync(string? password = null)
    {
        try
        {
            using var client = CreateSftpClient(password);
            await Task.Run(() => client.Connect());

            string remotePath = GetRemoteFilePath(client);

            if (!await Task.Run(() => client.Exists(remotePath)))
            {
                client.Disconnect();
                return (false, "No settings file found on the remote server.");
            }

            // Download to a temp file first, then swap
            string tempPath = LocalSettingsPath + ".download";
            string directory = Path.GetDirectoryName(LocalSettingsPath)!;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (var fileStream = File.Create(tempPath))
            {
                await Task.Run(() => client.DownloadFile(remotePath, fileStream));
            }

            client.Disconnect();

            // Replace local settings
            File.Copy(tempPath, LocalSettingsPath, overwrite: true);
            File.Delete(tempPath);

            // Reload settings — preserve local SSH/connection settings
            var localSettings = SettingsManager.Current;
            string savedHost = localSettings.SftpHost;
            int savedPort = localSettings.SftpPort;
            string savedUsername = localSettings.SftpUsername;
            string savedKeyPath = localSettings.SftpPrivateKeyPath;
            string savedRemotePath = localSettings.SftpRemotePath;
            bool savedAutoSync = localSettings.SftpAutoSync;
            bool savedAutoSyncOnClose = localSettings.SftpAutoSyncOnClose;

            var manager = new SettingsManager();
            manager.RestoreSettings();

            // Restore local SSH settings (these are machine-specific, not synced)
            var current = SettingsManager.Current;
            current.SftpHost = savedHost;
            current.SftpPort = savedPort;
            current.SftpUsername = savedUsername;
            current.SftpPrivateKeyPath = savedKeyPath;
            current.SftpRemotePath = savedRemotePath;
            current.SftpAutoSync = savedAutoSync;
            current.SftpAutoSyncOnClose = savedAutoSyncOnClose;
            SettingsManager.SaveSettings();

            Logger.Info($"Settings downloaded from {remotePath}");
            return (true, $"Settings downloaded from {SettingsManager.Current.SftpHost}. Restart for full effect.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download settings via SFTP");
            return (false, $"Download failed: {ex.Message}");
        }
    }

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

    private static string GetRemoteFilePath(SftpClient client)
    {
        string remoteDir = ResolveRemotePath(client, SettingsManager.Current.SftpRemotePath).TrimEnd('/');
        return $"{remoteDir}/settings.xml";
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
    /// Serialize settings to a MemoryStream with SSH connection fields blanked out.
    /// These are machine-specific and shouldn't be synced between devices.
    /// </summary>
    private static MemoryStream SerializeWithoutSshSettings(UserSettings settings)
    {
        // Temporarily blank SSH fields, serialize, then restore
        string host = settings.SftpHost;
        int port = settings.SftpPort;
        string user = settings.SftpUsername;
        string key = settings.SftpPrivateKeyPath;
        string remote = settings.SftpRemotePath;
        bool autoSync = settings.SftpAutoSync;
        bool autoSyncOnClose = settings.SftpAutoSyncOnClose;

        try
        {
            settings.SftpHost = "";
            settings.SftpPort = 22;
            settings.SftpUsername = "";
            settings.SftpPrivateKeyPath = "";
            settings.SftpRemotePath = "~/.config/TaskbarLauncher/";
            settings.SftpAutoSync = false;
            settings.SftpAutoSyncOnClose = false;

            var stream = new MemoryStream();
            var serializer = new XmlSerializer(typeof(UserSettings));
            serializer.Serialize(stream, settings);
            stream.Position = 0;
            return stream;
        }
        finally
        {
            settings.SftpHost = host;
            settings.SftpPort = port;
            settings.SftpUsername = user;
            settings.SftpPrivateKeyPath = key;
            settings.SftpRemotePath = remote;
            settings.SftpAutoSync = autoSync;
            settings.SftpAutoSyncOnClose = autoSyncOnClose;
        }
    }
}
