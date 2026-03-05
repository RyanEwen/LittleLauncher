using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Models;
using SelfHostedHelper.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Serialization;
using Wpf.Ui.Controls;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SelfHostedHelper.Pages;

public partial class SyncPage : Page
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private enum PendingAction { None, Test, Upload, Download }
    private PendingAction _pendingAction = PendingAction.None;

    public SyncPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
    }

    // ── Button handlers ─────────────────────────────────────────────

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select SSH Private Key",
            Filter = "All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            SettingsManager.Current.SftpPrivateKeyPath = dialog.FileName;
        }
    }

    private void ExportSshConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export SSH Connection Profile",
            Filter = "XML Files (*.xml)|*.xml",
            FileName = "ssh-connection.xml"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var profile = SshConnectionProfile.FromCurrentSettings();
            var serializer = new XmlSerializer(typeof(SshConnectionProfile));
            using var writer = new StreamWriter(dialog.FileName);
            serializer.Serialize(writer, profile);
            ShowStatus($"Connection profile exported to {Path.GetFileName(dialog.FileName)}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to export SSH connection profile");
            ShowStatus($"Export failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void ImportSshConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import SSH Connection Profile",
            Filter = "XML Files (*.xml)|*.xml",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var serializer = new XmlSerializer(typeof(SshConnectionProfile));
            using var reader = new StreamReader(dialog.FileName);
            if (serializer.Deserialize(reader) is SshConnectionProfile profile)
            {
                profile.ApplyToCurrentSettings();
                SettingsManager.SaveSettings();
                ShowStatus("Connection profile imported.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import SSH connection profile");
            ShowStatus($"Import failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.SaveSettings();

        if (NeedsPassword())
        {
            _pendingAction = PendingAction.Test;
            PasswordCard.Visibility = Visibility.Visible;
            return;
        }

        await RunTestAsync(null);
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.SaveSettings();

        if (NeedsPassword())
        {
            _pendingAction = PendingAction.Upload;
            PasswordCard.Visibility = Visibility.Visible;
            return;
        }

        await RunUploadAsync(null);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.SaveSettings();

        if (NeedsPassword())
        {
            _pendingAction = PendingAction.Download;
            PasswordCard.Visibility = Visibility.Visible;
            return;
        }

        await RunDownloadAsync(null);
    }

    private async void PasswordOk_Click(object sender, RoutedEventArgs e)
    {
        string password = PasswordBox.Password;
        PasswordCard.Visibility = Visibility.Collapsed;
        PasswordBox.Clear();

        switch (_pendingAction)
        {
            case PendingAction.Test:
                await RunTestAsync(password);
                break;
            case PendingAction.Upload:
                await RunUploadAsync(password);
                break;
            case PendingAction.Download:
                await RunDownloadAsync(password);
                break;
        }

        _pendingAction = PendingAction.None;
    }

    // ── Async operations ────────────────────────────────────────────

    private async Task RunTestAsync(string? password)
    {
        ShowStatus("Testing connection...", InfoBarSeverity.Informational);
        var (success, message) = await SftpSyncService.TestConnectionAsync(password);
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task RunUploadAsync(string? password)
    {
        ShowStatus("Uploading settings...", InfoBarSeverity.Informational);
        var (success, message) = await SftpSyncService.UploadSettingsAsync(password);
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task RunDownloadAsync(string? password)
    {
        ShowStatus("Downloading settings...", InfoBarSeverity.Informational);
        var (success, message) = await SftpSyncService.DownloadSettingsAsync(password);
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private bool NeedsPassword()
    {
        // If an SSH key is explicitly configured or can be auto-detected, no password needed
        string? keyPath = SettingsManager.Current.SftpPrivateKeyPath;
        if (!string.IsNullOrWhiteSpace(keyPath) && System.IO.File.Exists(keyPath))
            return false;

        // Check auto-detection from ~/.ssh/
        string sshDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (System.IO.Directory.Exists(sshDir))
        {
            foreach (var name in new[] { "id_ed25519", "id_rsa", "id_ecdsa", "id_dsa" })
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(sshDir, name)))
                    return false;
            }
        }

        return true;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
