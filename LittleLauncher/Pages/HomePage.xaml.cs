using LittleLauncher.Classes.Settings;
using LittleLauncher.ViewModels;
using NLog;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LittleLauncher.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal UserSettings Settings => SettingsManager.Current;

    public HomePage()
    {
        InitializeComponent();
        VersionTextBlock.Text = SettingsManager.Current.LastKnownVersion;
        TrayIconSwitch.IsOn = !SettingsManager.Current.NIconHide;
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