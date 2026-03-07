using LittleLauncher.Classes.Settings;
using LittleLauncher.Services;
using LittleLauncher.ViewModels;
using NLog;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LittleLauncher.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal UserSettings Settings => SettingsManager.Current;

    private UpdateService.UpdateCheckResult? _updateResult;

    public HomePage()
    {
        InitializeComponent();
        LoadAppIcon();
        VersionTextBlock.Text = SettingsManager.Current.LastKnownVersion;
        TrayIconSwitch.IsOn = !SettingsManager.Current.NIconHide;
        _ = CheckForUpdateAsync();
    }

    private void LoadAppIcon()
    {
        string icoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher", "app-icon.ico");
        if (File.Exists(icoPath))
        {
            var bmp = new BitmapImage();
            // Decode at full resolution so high-DPI displays get a crisp downscale
            bmp.DecodePixelWidth = 256;
            bmp.DecodePixelHeight = 256;
            bmp.UriSource = new Uri(icoPath);
            AppIcon.Source = bmp;
        }
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var result = await UpdateService.CheckForUpdateAsync();
            if (result is { UpdateAvailable: true })
            {
                _updateResult = result;
                UpdateInfoBar.Message = $"A new version ({result.LatestVersion}) is available. You are running {result.CurrentVersion}.";
                UpdateInfoBar.IsOpen = true;

                if (string.IsNullOrEmpty(result.MsiDownloadUrl))
                {
                    UpdateActionButton.Content = "View Release";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Update check failed on HomePage");
        }
    }

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult == null) return;

        if (!string.IsNullOrEmpty(_updateResult.MsiDownloadUrl))
        {
            UpdateActionButton.IsEnabled = false;
            UpdateActionButton.Content = "Downloading...";

            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                UpdateActionButton.Content = pct < 100 ? $"Downloading ({pct}%)..." : "Installing...";
            });

            var (success, message) = await UpdateService.DownloadAndInstallAsync(
                _updateResult.MsiDownloadUrl, progress);

            if (success)
            {
                UpdateInfoBar.Message = "Installer will launch after the app closes.";
                UpdateInfoBar.Severity = InfoBarSeverity.Success;
                await Task.Delay(1000);
                Environment.Exit(0);
            }
            else
            {
                UpdateActionButton.Content = "Download & Install";
                UpdateActionButton.IsEnabled = true;
                UpdateInfoBar.Message = message;
                UpdateInfoBar.Severity = InfoBarSeverity.Error;
            }
        }
        else if (!string.IsNullOrEmpty(_updateResult.ReleaseUrl))
        {
            Process.Start(new ProcessStartInfo(_updateResult.ReleaseUrl) { UseShellExecute = true });
        }
    }

    private void LauncherItems_Click(object sender, PointerRoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(LauncherItemsPage));
    }

    private void SyncSettings_Click(object sender, PointerRoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(SyncPage));
    }

    private void TrayIconSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        bool show = TrayIconSwitch.IsOn;
        SettingsManager.Current.NIconHide = !show;

        if ((Application.Current as App)?.m_window is MainWindow mainWindow)
            mainWindow.UpdateTrayIconVisibility(show);
    }

    private async void PinToTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (HasPackageIdentity())
        {
            try
            {
                var taskbarManager = global::Windows.UI.Shell.TaskbarManager.GetDefault();
                if (taskbarManager.IsSupported && taskbarManager.IsPinningAllowed)
                {
                    if (await taskbarManager.IsCurrentAppPinnedAsync())
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Already Pinned",
                            Content = "This app is already pinned to the taskbar.",
                            CloseButtonText = "OK",
                            XamlRoot = this.XamlRoot
                        };
                        await dialog.ShowAsync();
                        return;
                    }

                    if (await taskbarManager.RequestPinCurrentAppAsync())
                        return;
                }
            }
            catch (Exception ex)
            {
                Logger.Info(ex, "TaskbarManager pinning failed, falling back to companion exe");
            }
        }

        LaunchCompanionPinMode();
    }

    private static bool HasPackageIdentity()
    {
        try { return global::Windows.ApplicationModel.Package.Current != null; }
        catch { return false; }
    }

    private async void LaunchCompanionPinMode()
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var flyoutExe = Path.Combine(appDir, "LittleLauncherFlyout.exe");

            if (!File.Exists(flyoutExe))
            {
                var dialog = new ContentDialog
                {
                    Title = "File Not Found",
                    Content = "LittleLauncherFlyout.exe was not found in the application directory.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = flyoutExe,
                Arguments = "--pin",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error launching flyout pin helper");
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = $"Failed to pin: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}