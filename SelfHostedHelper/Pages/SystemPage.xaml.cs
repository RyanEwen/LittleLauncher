using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Classes.Utils;
using Microsoft.Win32;
using NLog;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SelfHostedHelper.Pages;

public partial class SystemPage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public SystemPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        UpdateMonitorList();
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        SetStartup(StartupSwitch.IsChecked ?? false);
    }

    private void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            const string appName = "TaskbarLauncher";
            var executablePath = Environment.ProcessPath;

            if (enable)
            {
                if (File.Exists(executablePath))
                    key.SetValue(appName, executablePath);
                else
                    throw new FileNotFoundException("Application executable not found.");
            }
            else
            {
                if (key.GetValue(appName) != null)
                    key.DeleteValue(appName, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set startup");
        }
    }

    private void UpdateMonitorList()
    {
        MonitorUtil.UpdateMonitorList(
            WidgetMonitorComboBox,
            () => SettingsManager.Current.TaskbarWidgetSelectedMonitor,
            value => SettingsManager.Current.TaskbarWidgetSelectedMonitor = value);
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"TaskbarLauncher_Settings_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}",
            DefaultExt = ".xml",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                SettingsManager.SaveSettings(dialog.FileName);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error exporting settings");
            }
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            DefaultExt = ".xml",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var manager = new SettingsManager();
                manager.RestoreSettings(dialog.FileName);
                SettingsManager.SaveSettings();

                // Restart to apply imported settings
                Application.Current.Shutdown();
                System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error importing settings");
            }
        }
    }
}
