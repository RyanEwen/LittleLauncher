using SelfHostedHelper.Classes.Settings;
using NLog;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Windows.UI.Shell;

namespace SelfHostedHelper.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public HomePage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        VersionTextBlock.Text = SettingsManager.Current.LastKnownVersion;

        // Show tray icon toggle is inverted: checked = visible (NIconHide = false)
        TrayIconSwitch.IsChecked = !SettingsManager.Current.NIconHide;
    }

    private void LauncherItems_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is SettingsWindow sw)
            sw.NavigateTo(typeof(LauncherItemsPage));
    }

    private void SyncSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is SettingsWindow sw)
            sw.NavigateTo(typeof(SyncPage));
    }

    private void TrayIconSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool show = TrayIconSwitch.IsChecked ?? false;
        SettingsManager.Current.NIconHide = !show;

        if (Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.nIcon.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PinToTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (HasPackageIdentity())
        {
            try
            {
                var taskbarManager = TaskbarManager.GetDefault();
                if (taskbarManager.IsSupported && taskbarManager.IsPinningAllowed)
                {
                    if (await taskbarManager.IsCurrentAppPinnedAsync())
                    {
                        MessageBox.Show(Window.GetWindow(this),
                            "This app is already pinned to the taskbar.",
                            "Already Pinned", MessageBoxButton.OK, MessageBoxImage.Information);
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
        try
        {
            return global::Windows.ApplicationModel.Package.Current != null;
        }
        catch
        {
            return false;
        }
    }

    private void LaunchCompanionPinMode()
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var flyoutExe = Path.Combine(appDir, "TaskbarLauncherFlyout.exe");

            if (!File.Exists(flyoutExe))
            {
                MessageBox.Show("TaskbarLauncherFlyout.exe was not found in the application directory.",
                    "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show($"Failed to pin: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
