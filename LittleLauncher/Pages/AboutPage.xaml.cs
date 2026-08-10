using LittleLauncher.Services;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace LittleLauncher.Pages;

public partial class AboutPage : Page
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private UpdateService.UpdateCheckResult? _updateResult;

    public AboutPage()
    {
        InitializeComponent();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Checking...";
        UpdateStatusText.Text = "Checking for updates...";

        try
        {
            var result = await UpdateService.CheckForUpdateAsync();
            if (result == null)
            {
                UpdateStatusText.Text = "Unable to check for updates. Check your internet connection.";
            }
            else if (result.UpdateAvailable)
            {
                _updateResult = result;
                // The Store path can know an update exists without knowing its version number.
                UpdateStatusText.Text = (result.IsStoreManaged, string.IsNullOrEmpty(result.LatestVersion)) switch
                {
                    (true, true) => $"A new version is available in the Microsoft Store (you have {result.CurrentVersion})",
                    (true, false) => $"Version {result.LatestVersion} is available in the Microsoft Store (you have {result.CurrentVersion})",
                    _ => $"Version {result.LatestVersion} is available (you have {result.CurrentVersion})",
                };
                CheckUpdateButton.Content = !result.IsStoreManaged && string.IsNullOrEmpty(result.MsiDownloadUrl)
                    ? "View Release"
                    : "Download & Install";
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Click -= CheckForUpdates_Click;
                CheckUpdateButton.Click += DownloadUpdate_Click;
                return;
            }
            else if (result.IsStoreManaged)
            {
                // "Nothing to download" and "already staged, waiting for us to exit" look
                // identical from the Store APIs, and Little Launcher lives in the tray and never
                // exits on its own — so it is the app most likely to be sitting on a staged
                // update indefinitely. Offer the restart rather than claiming everything is
                // settled.
                UpdateStatusText.Text =
                    $"You're up to date ({result.CurrentVersion}). If the Store downloaded an "
                    + "update in the background, restart to finish installing it.";
                CheckUpdateButton.Content = "Restart Now";
                CheckUpdateButton.Click -= CheckForUpdates_Click;
                CheckUpdateButton.Click += RestartForUpdate_Click;
                CheckUpdateButton.IsEnabled = true;
                return;
            }
            else
            {
                UpdateStatusText.Text = $"You're up to date ({result.CurrentVersion})";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Manual update check failed");
            UpdateStatusText.Text = "Update check failed. Try again later.";
        }

        CheckUpdateButton.Content = "Check for Updates";
        CheckUpdateButton.IsEnabled = true;
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult == null) return;

        if (_updateResult.IsStoreManaged || !string.IsNullOrEmpty(_updateResult.MsiDownloadUrl))
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = "Downloading...";

            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                CheckUpdateButton.Content = pct < 100 ? $"Downloading ({pct}%)..." : "Installing...";
            });

            var (success, message) = await UpdateService.DownloadAndInstallAsync(
                _updateResult,
                GetOwnerWindowHandle(),
                progress);

            if (success)
            {
                UpdateStatusText.Text = message;
                await Task.Delay(1000);
                Environment.Exit(0);
            }
            else
            {
                UpdateStatusText.Text = message;
                CheckUpdateButton.Content = "Retry";
                CheckUpdateButton.IsEnabled = true;
            }
        }
        else if (!string.IsNullOrEmpty(_updateResult.ReleaseUrl))
        {
            Process.Start(new ProcessStartInfo(_updateResult.ReleaseUrl) { UseShellExecute = true });
        }
    }

    private async void RestartForUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Restarting...";
        CheckUpdateButton.IsEnabled = false;
        UpdateService.RestartToApplyPackagedUpdate();
        await Task.Delay(500);
        Environment.Exit(0);
    }

    private static nint GetOwnerWindowHandle()
    {
        Window? owner = SettingsWindow.GetCurrent();
        owner ??= MainWindow.Current;
        return owner == null ? 0 : WindowNative.GetWindowHandle(owner);
    }
}