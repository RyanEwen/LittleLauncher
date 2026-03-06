using LittleLauncher.Classes.Settings;
using NLog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using global::Windows.Storage.Pickers;
using WinRT.Interop;

namespace LittleLauncher.Pages;

public partial class SystemPage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public SystemPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
    }

    private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        SetStartup(StartupSwitch.IsOn);
    }

    private void SetStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            const string appName = "LittleLauncher";
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

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("XML Files", new List<string> { ".xml" });
        picker.SuggestedFileName = $"LittleLauncher_Settings_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            try
            {
                SettingsManager.SaveSettings(file.Path);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error exporting settings");
            }
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".xml");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            try
            {
                SettingsManager.RestoreSettings(file.Path);
                SettingsManager.SaveSettings();

                // Restart to apply imported settings
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                    System.Diagnostics.Process.Start(exePath);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error importing settings");
            }
        }
    }

    private static void InitializePicker(object picker)
    {
        var window = SettingsWindow.GetCurrent();
        if (window == null) return;
        var hwnd = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, hwnd);
    }
}