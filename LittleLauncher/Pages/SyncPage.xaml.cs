using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using System.Xml.Serialization;
using global::Windows.Storage.Pickers;
using WinRT.Interop;

namespace LittleLauncher.Pages;

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

    // -- Button handlers --

    private async void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            SettingsManager.Current.SftpPrivateKeyPath = file.Path;
        }
    }

    private async void ExportSshConfig_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("XML Files", new List<string> { ".xml" });
        picker.SuggestedFileName = "ssh-connection";
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var profile = SshConnectionProfile.FromCurrentSettings();
            var serializer = new XmlSerializer(typeof(SshConnectionProfile));
            using var writer = new StreamWriter(file.Path);
            serializer.Serialize(writer, profile);
            ShowStatus($"Connection profile exported to {Path.GetFileName(file.Path)}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to export SSH connection profile");
            ShowStatus($"Export failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ImportSshConfig_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".xml");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            var serializer = new XmlSerializer(typeof(SshConnectionProfile));
            using var reader = new StreamReader(file.Path);
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
        PasswordBox.Password = "";

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

    // -- Async operations --

    private async Task RunTestAsync(string? password)
    {
        ShowStatus("Testing connection...", InfoBarSeverity.Informational);
        var (success, message) = await SftpSyncService.TestConnectionAsync(password);
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task RunUploadAsync(string? password)
    {
        ShowStatus("Uploading launcher items...", InfoBarSeverity.Informational);
        var (success, message) = await SftpSyncService.UploadLauncherItemsAsync(password);
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task RunDownloadAsync(string? password)
    {
        ShowStatus("Downloading launcher items...", InfoBarSeverity.Informational);
        var (success, message) = await SftpSyncService.DownloadLauncherItemsAsync(password);
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    // -- Helpers --

    private bool NeedsPassword()
    {
        string? keyPath = SettingsManager.Current.SftpPrivateKeyPath;
        if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
            return false;

        string sshDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(sshDir))
        {
            foreach (var name in new[] { "id_ed25519", "id_rsa", "id_ecdsa", "id_dsa" })
            {
                if (File.Exists(Path.Combine(sshDir, name)))
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

    private static void InitializePicker(object picker)
    {
        var window = SettingsWindow.GetCurrent();
        if (window == null) return;
        var hwnd = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, hwnd);
    }
}