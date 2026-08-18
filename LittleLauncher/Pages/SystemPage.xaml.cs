using LittleLauncher.Classes.Settings;
using NLog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using global::Windows.ApplicationModel;
using global::Windows.Storage.Pickers;
using WinRT.Interop;

namespace LittleLauncher.Pages;

public partial class SystemPage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const string StartupTaskId = "LittleLauncherStartup";
    private bool _startupStateInitialized;
    private bool _updatingStartupSwitch;

    private bool _updatingShortcutsSwitch;

    public SystemPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;

        // Shown in the positive, stored in the negative. The inversion lives here rather than in
        // the model because a bool defaulting to true cannot be turned off under
        // WhenWritingDefault — see user-settings.md.
        _updatingShortcutsSwitch = true;
        WebShortcutsToggle.IsOn = !SettingsManager.Current.DisableWebLauncherShortcuts;
        _updatingShortcutsSwitch = false;

        _ = RefreshStartupStateAsync();
        _ = RefreshProfileCleanupAsync();
    }

    // ── Unused browser profiles ────────────────────────────────────

    private List<Services.WebProfileCleanupService.Reclaimable> _reclaimableProfiles = [];

    private static string NewLine => System.Environment.NewLine;

    /// <summary>
    /// Counts what could be reclaimed and offers it, or says there is nothing to do.
    /// </summary>
    /// <remarks>
    /// Off the UI thread: sizing a profile folder walks a Chromium cache of tens of thousands of
    /// files, which is not something to do while the page is trying to appear.
    /// </remarks>
    private async Task RefreshProfileCleanupAsync()
    {
        var found = await Task.Run(Services.WebProfileCleanupService.Scan);
        _reclaimableProfiles = found;

        long bytes = found.Sum(f => f.Bytes);
        bool any = found.Count > 0;

        ProfileCleanupButton.IsEnabled = any;
        ProfileCleanupSubtitle.Text = any
            ? $"{found.Count} left behind by deleted launchers, or by launchers moved to the shared profile "
              + $"— {Services.WebProfileCleanupService.FormatSize(bytes)}"
            : "Nothing to clean up. Sign-ins for launchers you still have are never touched";
    }

    /// <summary>
    /// Deletes the unused profiles, after saying exactly which sign-ins are going.
    /// </summary>
    /// <remarks>
    /// Named rather than counted in the confirmation: "3 profiles" is not something anyone can
    /// agree to, and the only question that matters is whether one of them is a launcher they still
    /// use — which the list answers at a glance.
    /// </remarks>
    private async void ProfileCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reclaimableProfiles.Count == 0) return;

        var lines = _reclaimableProfiles
            .Select(f => $"• {f.Description} ({Services.WebProfileCleanupService.FormatSize(f.Bytes)})");

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete unused browser profiles",
            Content = "These hold cookies and sign-ins nothing is using any more:" + NewLine + NewLine
                    + string.Join(NewLine, lines)
                    + NewLine + NewLine
                    + "Launchers you still have keep their own sign-ins. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        ProfileCleanupButton.IsEnabled = false;
        ProfileCleanupButton.Content = "Deleting…";

        var (deleted, bytes) = await Services.WebProfileCleanupService.DeleteAsync(_reclaimableProfiles);

        ProfileCleanupButton.Content = "Delete";
        await RefreshProfileCleanupAsync();

        // Rescanned above, so anything a locked file kept back is still listed and still offered.
        if (deleted > 0)
            ProfileCleanupSubtitle.Text = $"Freed {Services.WebProfileCleanupService.FormatSize(bytes)}. "
                                        + ProfileCleanupSubtitle.Text;
    }

    private void WebShortcutsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // Setting IsOn from code raises Toggled exactly as a click does, so the initial sync above
        // would otherwise write the setting back over itself.
        if (_updatingShortcutsSwitch) return;

        SettingsManager.Current.DisableWebLauncherShortcuts = !WebShortcutsToggle.IsOn;
        SettingsManager.SaveSettings();
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingStartupSwitch || !_startupStateInitialized)
            return;

        await SetStartupAsync(StartupSwitch.IsOn);
    }

    private async Task RefreshStartupStateAsync()
    {
        try
        {
            bool isEnabled = await IsStartupEnabledAsync();
            ApplyStartupSwitchState(isEnabled);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read startup state");
        }
        finally
        {
            _startupStateInitialized = true;
        }
    }

    private async Task SetStartupAsync(bool enable)
    {
        try
        {
            bool isEnabled;
            if (MainWindow.IsPackaged)
            {
                isEnabled = await SetPackagedStartupAsync(enable);
                MainWindow.RemoveStartupRegistryEntry();
            }
            else
            {
                SetUnpackagedStartup(enable);
                isEnabled = await IsStartupEnabledAsync();
            }

            ApplyStartupSwitchState(isEnabled);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set startup");
            ApplyStartupSwitchState(await IsStartupEnabledAsync());
        }
    }

    private void ApplyStartupSwitchState(bool isEnabled)
    {
        try
        {
            _updatingStartupSwitch = true;
            StartupSwitch.IsOn = isEnabled;
            SettingsManager.Current.Startup = isEnabled;
        }
        finally
        {
            _updatingStartupSwitch = false;
        }
    }

    private static async Task<bool> IsStartupEnabledAsync()
    {
        if (MainWindow.IsPackaged)
        {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            return startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue("Little Launcher") is string currentValue && !string.IsNullOrWhiteSpace(currentValue);
    }

    private static async Task<bool> SetPackagedStartupAsync(bool enable)
    {
        var startupTask = await StartupTask.GetAsync(StartupTaskId);

        if (!enable)
        {
            startupTask.Disable();
            return false;
        }

        if (startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
            return true;

        StartupTaskState result = await startupTask.RequestEnableAsync();
        return result is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    private static void SetUnpackagedStartup(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;

        const string appName = "Little Launcher";
        string executablePath = Environment.ProcessPath ?? string.Empty;

        if (enable)
        {
            if (File.Exists(executablePath))
                key.SetValue(appName, $"\"{executablePath}\" --silent");
            else
                throw new FileNotFoundException("Application executable not found.");
        }
        else if (key.GetValue(appName) != null)
        {
            key.DeleteValue(appName, false);
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ExportButtonClickAsync();
    }

    private async Task ExportButtonClickAsync()
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

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ImportButtonClickAsync();
    }

    private async Task ImportButtonClickAsync()
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