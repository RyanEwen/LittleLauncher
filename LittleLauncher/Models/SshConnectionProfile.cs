using LittleLauncher.Classes.Settings;

namespace LittleLauncher.Models;

/// <summary>
/// A portable SSH connection profile that can be exported/imported
/// to share connection settings between machines.
/// </summary>
public class SshConnectionProfile
{
    public string SftpHost { get; set; } = "";
    public int SftpPort { get; set; } = 22;
    public string SftpUsername { get; set; } = "";
    public string SftpPrivateKeyPath { get; set; } = "";
    public string SftpRemotePath { get; set; } = "~/.config/LittleLauncher/";
    public bool SftpAutoSync { get; set; }
    public int SftpAutoSyncInterval { get; set; } = 5;

    public static SshConnectionProfile FromCurrentSettings()
    {
        var s = SettingsManager.Current;
        return new SshConnectionProfile
        {
            SftpHost = s.SftpHost,
            SftpPort = s.SftpPort,
            SftpUsername = s.SftpUsername,
            SftpPrivateKeyPath = s.SftpPrivateKeyPath,
            SftpRemotePath = s.SftpRemotePath,
            SftpAutoSync = s.SftpAutoSync,
            SftpAutoSyncInterval = s.SftpAutoSyncInterval
        };
    }

    public void ApplyToCurrentSettings()
    {
        var s = SettingsManager.Current;
        s.SftpHost = SftpHost;
        s.SftpPort = SftpPort;
        s.SftpUsername = SftpUsername;
        s.SftpPrivateKeyPath = SftpPrivateKeyPath;
        s.SftpRemotePath = SftpRemotePath;
        s.SftpAutoSync = SftpAutoSync;
        s.SftpAutoSyncInterval = SftpAutoSyncInterval;
    }
}
