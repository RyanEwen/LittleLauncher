using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Services;
using LittleLauncher.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using System.Text.Json;
using global::Windows.Storage.Pickers;
using WinRT.Interop;

namespace LittleLauncher.Pages;

public partial class SyncPage : Page
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private enum PendingAction { None, Test, Upload, Download }
    private PendingAction _pendingAction = PendingAction.None;

    /// <summary>
    /// Suppresses toggle side-effects while the page wires itself up. Setting
    /// <c>ToggleSwitch.IsOn</c> from code raises <c>Toggled</c> exactly as a click does, and
    /// without this the initial refresh would write the settings back over themselves.
    /// </summary>
    private bool _initializing = true;

    public SyncPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;

        // Five sections in one scroller: opening the last one reveals it entirely below the fold.
        Classes.ExpanderReveal.Attach(
            SftpExpander, OneDriveExpander, GoogleExpander, WebDavExpander, FolderExpander);

        RefreshDestinations();
        _initializing = false;
    }

    // -- Destinations --

    /// <summary>
    /// Bring every destination header and panel in line with the current settings.
    /// </summary>
    /// <remarks>
    /// <b>Enabled and expanded are separate.</b> A destination keeps syncing while its expander is
    /// closed, and can be set up while switched off — which is the point of allowing several. The
    /// toggle answers "is this syncing?"; the chevron answers "do I want to see its settings?".
    /// </remarks>
    private void RefreshDestinations()
    {
        bool wasInitializing = _initializing;
        _initializing = true;

        SftpToggle.IsOn = SettingsManager.Current.IsSyncProviderEnabled(SyncProviders.Sftp);
        OneDriveToggle.IsOn = SettingsManager.Current.IsSyncProviderEnabled(SyncProviders.OneDrive);
        GoogleToggle.IsOn = SettingsManager.Current.IsSyncProviderEnabled(SyncProviders.GoogleDrive);
        WebDavToggle.IsOn = SettingsManager.Current.IsSyncProviderEnabled(SyncProviders.WebDav);
        FolderToggle.IsOn = SettingsManager.Current.IsSyncProviderEnabled(SyncProviders.Folder);

        SftpStatus.Text = StatusFor(SyncProviders.Sftp);
        OneDriveStatus.Text = StatusFor(SyncProviders.OneDrive);
        GoogleStatus.Text = StatusFor(SyncProviders.GoogleDrive);
        WebDavStatus.Text = StatusFor(SyncProviders.WebDav);
        FolderStatus.Text = StatusFor(SyncProviders.Folder);

        UpdateAccountUi(SyncProviders.OneDrive, OneDriveHint, OneDriveSignIn, OneDriveSignOut, OneDriveNotice);
        UpdateAccountUi(SyncProviders.GoogleDrive, GoogleHint, GoogleSignIn, GoogleSignOut, GoogleNotice);
        UpdateWebDavUi();

        _initializing = wasInitializing;
    }

    private void Destination_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleSwitch { Tag: string tag } toggle) return;
        if (!int.TryParse(tag, out int provider)) return;

        SettingsManager.Current.SetSyncProviderEnabled(provider, toggle.IsOn);
        SettingsManager.SaveSettings();

        // Open the panel of something just switched on: it almost certainly needs setting up,
        // and a toggle that appears to do nothing is worse than one that shows you the next step.
        if (toggle.IsOn && !IsConfigured(provider))
            ExpanderFor(provider).IsExpanded = true;

        RefreshDestinations();
    }

    private Expander ExpanderFor(int provider) => SyncProviders.Normalize(provider) switch
    {
        SyncProviders.OneDrive => OneDriveExpander,
        SyncProviders.GoogleDrive => GoogleExpander,
        SyncProviders.WebDav => WebDavExpander,
        SyncProviders.Folder => FolderExpander,
        _ => SftpExpander,
    };

    private static bool IsConfigured(int provider) => provider switch
    {
        SyncProviders.Sftp => !string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost),
        SyncProviders.Folder => FolderSyncService.IsConfigured,
        _ => CloudSyncService.StoreFor(provider) is { IsSignedIn: true },
    };

    /// <summary>A one-line answer to "is this one actually going to work?".</summary>
    private static string StatusFor(int provider)
    {
        switch (provider)
        {
            case SyncProviders.Sftp:
                string host = SettingsManager.Current.SftpHost;
                return string.IsNullOrWhiteSpace(host) ? "No host set" : host;

            case SyncProviders.Folder:
                string folder = SettingsManager.Current.SyncFolderPath;
                return string.IsNullOrWhiteSpace(folder) ? "No folder set" : folder;

            default:
                var store = CloudSyncService.StoreFor(provider);
                if (store == null) return "";
                if (!store.IsAvailable) return "Not available in this build";
                if (!store.IsSignedIn)
                    return provider == SyncProviders.WebDav ? "Not connected" : "Not signed in";
                return store.AccountName.Length > 0 ? store.AccountName : "Connected";
        }
    }

    /// <summary>Reflect the signed-in state of an OAuth cloud provider.</summary>
    private static void UpdateAccountUi(
        int provider, TextBlock hint, Button signIn, Button signOut, InfoBar notice)
    {
        var store = CloudSyncService.StoreFor(provider);
        if (store == null) return;

        notice.IsOpen = false;

        // A build with no OAuth registration cannot sign in at all, so say that rather than
        // offering a button that can only fail.
        if (!store.IsAvailable)
        {
            hint.Text = "";
            signIn.IsEnabled = false;
            signIn.Visibility = Visibility.Visible;
            signOut.Visibility = Visibility.Collapsed;

            notice.Severity = InfoBarSeverity.Warning;
            notice.Message = CloudSyncCredentials.NotConfiguredMessage(store.ProviderName);
            notice.IsOpen = true;
            return;
        }

        signIn.IsEnabled = true;

        if (store.IsSignedIn)
        {
            hint.Text = provider == SyncProviders.OneDrive
                ? "Stored in this app's own OneDrive folder. Nothing else in your drive is visible to it."
                : "Stored in this app's hidden Drive folder. Nothing else in your Drive is visible to it.";
            signIn.Visibility = Visibility.Collapsed;
            signOut.Visibility = Visibility.Visible;
        }
        else
        {
            hint.Text = provider == SyncProviders.OneDrive
                ? "Sign in with a personal Microsoft account. Work and school accounts need the \"Folder or network share\" destination."
                : "Sign in with your Google account. Opens in your browser.";
            signIn.Visibility = Visibility.Visible;
            signOut.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Reflect whether a WebDAV password is stored.</summary>
    private void UpdateWebDavUi()
    {
        var store = CloudSyncService.StoreFor(SyncProviders.WebDav);
        bool connected = store?.IsSignedIn == true;

        WebDavNotice.IsOpen = false;

        // The password box is deliberately never repopulated from storage — there is nothing to
        // gain from rendering a stored secret back into the UI, and an empty box beside a
        // "Connected" label reads correctly as "already saved".
        WebDavPasswordBox.Password = "";

        WebDavPasswordHint.Text = connected
            ? "Saved and encrypted on this PC. Enter a new one to replace it."
            : "Use an app password if your server issues them, not your account password.";

        WebDavConnectButton.Content = connected ? "Replace" : "Connect";
        WebDavDisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        if (connected && store != null)
        {
            WebDavNotice.Severity = InfoBarSeverity.Success;
            WebDavNotice.Message = $"Connected as {store.AccountName}.";
            WebDavNotice.IsOpen = true;
        }
    }

    // -- Account handlers --

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button || !int.TryParse(tag, out int provider)) return;

        var store = CloudSyncService.StoreFor(provider);
        if (store == null) return;

        button.IsEnabled = false;
        ShowStatus($"Waiting for {store.ProviderName} sign-in in your browser...", InfoBarSeverity.Informational);

        try
        {
            var (success, message) = await store.SignInAsync();
            ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);

            // Signing in is a strong signal the destination is wanted; switching it on saves a
            // second step, and a sign-in that changed nothing visible would read as a failure.
            if (success) SettingsManager.Current.SetSyncProviderEnabled(provider, true);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"{store.ProviderName} sign-in failed");
            ShowStatus($"Sign-in failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
            SettingsManager.SaveSettings();
            RefreshDestinations();
        }
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int provider)) return;

        var store = CloudSyncService.StoreFor(provider);
        if (store == null) return;

        store.SignOut();

        // Local only, in both cases — but say the right thing about each. An OAuth grant survives
        // in the account until revoked there; a WebDAV password was only ever stored here, so
        // telling someone to "revoke access" would send them looking for a screen that does not
        // exist. Either way, never imply more happened than did.
        ShowStatus(
            provider == SyncProviders.WebDav
                ? "WebDAV password forgotten on this PC. The URL and username are kept."
                : $"Signed out of {store.ProviderName} on this PC. Revoke the app's access in your "
                  + $"{store.ProviderName} account settings to remove it everywhere.",
            InfoBarSeverity.Success);

        RefreshDestinations();
    }

    private async void WebDavConnect_Click(object sender, RoutedEventArgs e)
    {
        string password = WebDavPasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowStatus("Enter the WebDAV password first.", InfoBarSeverity.Warning);
            return;
        }

        SettingsManager.SaveSettings();
        WebDavFileStore.SetPassword(password);
        WebDavPasswordBox.Password = "";

        WebDavConnectButton.IsEnabled = false;
        ShowStatus("Checking the WebDAV server...", InfoBarSeverity.Informational);

        try
        {
            var store = CloudSyncService.StoreFor(SyncProviders.WebDav)!;
            var (success, message) = await store.SignInAsync();

            // Do not leave a password that was just proven wrong sitting in the store — a later
            // background sync would keep replaying it against the server.
            if (!success) store.SignOut();
            else SettingsManager.Current.SetSyncProviderEnabled(SyncProviders.WebDav, true);

            ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            WebDavConnectButton.IsEnabled = true;
            SettingsManager.SaveSettings();
            RefreshDestinations();
        }
    }

    // -- Button handlers --

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            SettingsManager.Current.SyncFolderPath = folder.Path;
            SettingsManager.SaveSettings();
            RefreshDestinations();
        }
    }

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
        picker.FileTypeChoices.Add("JSON Files", new List<string> { ".json" });
        picker.SuggestedFileName = "ssh-connection";
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var profile = SshConnectionProfile.FromCurrentSettings();
            string json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file.Path, json);
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
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".xml");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            string content = await File.ReadAllTextAsync(file.Path);
            var profile = JsonSerializer.Deserialize<SshConnectionProfile>(content);
            if (profile != null)
            {
                profile.ApplyToCurrentSettings();
                SettingsManager.SaveSettings();
                RefreshDestinations();
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
        ShowStatus("Testing destinations...", InfoBarSeverity.Informational);
        var (success, message) = await LauncherSyncService.TestAsync(password);
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        RefreshDestinations();
    }

    private async Task RunUploadAsync(string? password)
    {
        ShowStatus("Uploading launchers...", InfoBarSeverity.Informational);
        var (success, message) = await LauncherSyncService.UploadLaunchersAsync(password);
        if (success)
            AutoSyncService.ClearPendingLocalItemChanges();
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task RunDownloadAsync(string? password)
    {
        ShowStatus("Downloading launchers...", InfoBarSeverity.Informational);
        var (success, message) = await LauncherSyncService.DownloadLaunchersAsync(password, force: true);
        ShowStatus(message, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);

        if (success)
        {
            AutoSyncService.ClearPendingLocalItemChanges();
            FlyoutWindow.InvalidateItems();
            MainWindow.Current?.RefreshTrayIcons();
        }
    }

    // -- Helpers --

    /// <summary>
    /// Whether any enabled destination needs a credential typed in. Only SFTP ever does — cloud
    /// accounts authenticate in the browser, WebDAV stores its own, folders rely on Windows.
    /// </summary>
    private bool NeedsPassword()
    {
        if (!LauncherSyncService.UsesCredentials)
            return false;

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
