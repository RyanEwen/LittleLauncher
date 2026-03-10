using LittleLauncher.Classes.Settings;
using NLog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
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
        LoadIconPreviews();
        UpdateCustomIconCardVisibility();
        UpdateCustomIconPathText();
    }

    private void LoadIconPreviews()
    {
        string iconsDir = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons");
        var previews = new (Image Control, string Name)[]
        {
            (IconPreviewBlue, "Blue"), (IconPreviewGreen, "Green"), (IconPreviewTeal, "Teal"),
            (IconPreviewRed, "Red"), (IconPreviewOrange, "Orange"), (IconPreviewPurple, "Purple"),
        };
        foreach (var (control, name) in previews)
        {
            string path = Path.Combine(iconsDir, $"{name}.png");
            if (File.Exists(path))
                control.Source = new BitmapImage(new Uri(path));
        }
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
            const string appName = "Little Launcher";
            var executablePath = Environment.ProcessPath;

            if (enable)
            {
                if (File.Exists(executablePath))
                    key.SetValue(appName, $"\"{executablePath}\" --silent");
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
        picker.SuggestedFileName = $"Little Launcher Settings {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
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

    private void TrayIconModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomIconCardVisibility();
    }

    private void UpdateCustomIconCardVisibility()
    {
        if (CustomIconCard != null)
            CustomIconCard.Visibility = SettingsManager.Current.TrayIconMode == 12
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void UpdateCustomIconPathText()
    {
        if (CustomIconPathText == null) return;
        string path = SettingsManager.Current.CustomTrayIconPath;
        CustomIconPathText.Text = string.IsNullOrEmpty(path)
            ? "No file selected"
            : Path.GetFileName(path);
    }

    private async void BrowseCustomIcon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".ico");
        picker.FileTypeFilter.Add(".png");
        InitializePicker(picker);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            // Copy the icon to AppData so the path survives across sessions
            string appDataDir = MainWindow.GetPhysicalAppDataDir();
            Directory.CreateDirectory(appDataDir);

            string ext = Path.GetExtension(file.Path);
            string destPath = Path.Combine(appDataDir, $"custom-tray-icon{ext}");
            File.Copy(file.Path, destPath, overwrite: true);

            SettingsManager.Current.CustomTrayIconPath = destPath;
            UpdateCustomIconPathText();
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