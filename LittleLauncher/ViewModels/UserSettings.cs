using CommunityToolkit.Mvvm.ComponentModel;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using System.Collections.ObjectModel;
using System.Xml.Serialization;

namespace LittleLauncher.ViewModels;

/// <summary>
/// User settings data model for the launcher application.
/// All [ObservableProperty] fields generate INotifyPropertyChanged automatically
/// via CommunityToolkit.Mvvm source generators.
/// </summary>
public partial class UserSettings : ObservableObject
{
    // ── Appearance & Behaviour ──────────────────────────────────────

    /// <summary>App theme. 0 = System default, 1 = Light, 2 = Dark.</summary>
    [ObservableProperty]
    public partial int AppTheme { get; set; }

    /// <summary>Start minimized to tray when Windows starts.</summary>
    [ObservableProperty]
    public partial bool Startup { get; set; }

    /// <summary>Hide the tray icon completely.</summary>
    [ObservableProperty]
    public partial bool NIconHide { get; set; }

    /// <summary>Tray icon style. 0 = Default, 1 = Light, 2 = Dark, 3 = Auto, 4 = Custom.</summary>
    [ObservableProperty]
    public partial int TrayIconMode { get; set; }

    /// <summary>Path to a custom tray icon file (.ico or .png) when TrayIconMode is Custom.</summary>
    [ObservableProperty]
    public partial string CustomTrayIconPath { get; set; }

    /// <summary>Last known app version string.</summary>
    [ObservableProperty]
    public partial string LastKnownVersion { get; set; }

    // ── Taskbar Widget ──────────────────────────────────────────────

    /// <summary>Whether the little launcher widget is enabled.</summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetEnabled { get; set; }

    /// <summary>Target monitor for the widget.</summary>
    [ObservableProperty]
    public partial int TaskbarWidgetSelectedMonitor { get; set; }

    /// <summary>Widget position: 0 = Left, 1 = Center, 2 = Right.</summary>
    [ObservableProperty]
    public partial int TaskbarWidgetPosition { get; set; }

    /// <summary>Apply automatic padding for the native Windows Widgets button.</summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetPadding { get; set; }

    /// <summary>Manual pixel offset applied to the widget.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetManualPaddingText))]
    public partial int TaskbarWidgetManualPadding { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetManualPaddingText
    {
        get => TaskbarWidgetManualPadding.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetManualPadding = result switch
                {
                    > 9999 => 9999,
                    < -9999 => -9999,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetManualPadding = 0;
            }
            OnPropertyChanged();
        }
    }

    // ── Launcher Items ──────────────────────────────────────────────

    /// <summary>Application / website shortcuts shown in the taskbar widget.</summary>
    public ObservableCollection<LauncherItem> LauncherItems { get; set; }

    // ── SFTP Sync ───────────────────────────────────────────────────

    /// <summary>SSH/SFTP hostname or IP address.</summary>
    [ObservableProperty]
    public partial string SftpHost { get; set; }

    /// <summary>SSH port (default 22).</summary>
    [ObservableProperty]
    public partial int SftpPort { get; set; }

    /// <summary>SSH username.</summary>
    [ObservableProperty]
    public partial string SftpUsername { get; set; }

    /// <summary>Path to SSH private key file (optional, alternative to password).</summary>
    [ObservableProperty]
    public partial string SftpPrivateKeyPath { get; set; }

    /// <summary>Remote directory where settings are stored.</summary>
    [ObservableProperty]
    public partial string SftpRemotePath { get; set; }

    /// <summary>Auto-sync launcher items on startup and periodically.</summary>
    [ObservableProperty]
    public partial bool SftpAutoSync { get; set; }

    /// <summary>Interval in minutes between periodic sync downloads (default 5).</summary>
    [ObservableProperty]
    public partial int SftpAutoSyncInterval { get; set; }

    // ── Initialisation flag ─────────────────────────────────────────

    [XmlIgnore]
    private bool _initializing = true;

    // ── Constructor (defaults) ──────────────────────────────────────

    public UserSettings()
    {
        AppTheme = 0;
        Startup = false;
        NIconHide = false;
        TrayIconMode = 0;
        CustomTrayIconPath = "";
        LastKnownVersion = "";

        // Do NOT populate defaults here — XmlSerializer calls this constructor
        // then appends deserialized items, which would double the list.
        LauncherItems = [];

        SftpHost = "";
        SftpPort = 22;
        SftpUsername = "";
        SftpPrivateKeyPath = "";
        SftpRemotePath = "~/.config/LittleLauncher/";
        SftpAutoSync = false;
        SftpAutoSyncInterval = 5;
    }

    /// <summary>Called after XML deserialization to finalize initialization.</summary>
    internal void CompleteInitialization()
    {
        // Populate default launcher items only if none were deserialized
        if (LauncherItems.Count == 0)
        {
            LauncherItems.Add(new LauncherItem("Google", "https://www.google.com", "Globe24", isWebsite: true));
            LauncherItems.Add(new LauncherItem("Explorer", "explorer.exe", "Folder24"));
            LauncherItems.Add(new LauncherItem("Notepad", "notepad.exe", "Notepad24"));
        }

        _initializing = false;
    }

    // ── Change handlers ─────────────────────────────────────────────

    partial void OnAppThemeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        LittleLauncher.Classes.ThemeManager.ApplyAndSaveTheme(newValue);
    }

    partial void OnTrayIconModeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        ApplyTrayIconChange();
    }

    partial void OnCustomTrayIconPathChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue || _initializing) return;
        if (TrayIconMode == 10) // Custom
            ApplyTrayIconChange();
    }

    private void ApplyTrayIconChange()
    {
        if ((Microsoft.UI.Xaml.Application.Current as App)?.m_window is MainWindow mw)
            mw.UpdateTrayIcon();
        LittleLauncher.Classes.Settings.SettingsManager.SaveSettings();
    }
}
