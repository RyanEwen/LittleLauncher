using CommunityToolkit.Mvvm.ComponentModel;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

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

    /// <summary>Animate launcher flyouts when they open and close.</summary>
    [ObservableProperty]
    public partial bool FlyoutAnimationsEnabled { get; set; }

    // NIconHide, TrayIconMode, CustomTrayIconPath are kept here as legacy XML migration fields only.
    // They are copied into the first Launcher during CompleteInitialization() and then cleared.
    // Per-launcher icon settings now live on each Launcher object in the Launchers collection.

    /// <summary>[Migration] Legacy hide-tray-icon flag. Migrated to first Launcher.NIconHide on load.</summary>
    public bool NIconHide { get; set; }

    /// <summary>[Migration] Legacy tray icon style. Migrated to first Launcher.TrayIconMode on load.</summary>
    public int TrayIconMode { get; set; }

    /// <summary>[Migration] Legacy custom icon path. Migrated to first Launcher.CustomTrayIconPath on load.</summary>
    public string CustomTrayIconPath { get; set; } = "";

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

    [JsonIgnore]
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

    // ── Launchers ─────────────────────────────────────────────────────

    /// <summary>
    /// The named launchers. Each launcher has its own items, tray icon, and identity.
    /// Replaces the legacy flat LauncherItems collection.
    /// </summary>
    public ObservableCollection<Launcher> Launchers { get; set; } = [];

    /// <summary>
    /// [Migration] Legacy flat launcher items list. Present in old settings files.
    /// On load, migrated into the first Launcher's Items and cleared. Not used in new code.
    /// </summary>
    [JsonIgnore]
    public ObservableCollection<LauncherItem> LauncherItems { get; set; } = [];

    // ── Upgrade tracking ────────────────────────────────────────────

    /// <summary>
    /// App version that last wrote this settings file, used to detect upgrades. Empty on a
    /// fresh install and on files written before this was introduced.
    /// </summary>
    public string LastRunVersion { get; set; } = "";

    /// <summary>
    /// Show the one-time "item editing has moved to the flyout" notice on the Launchers page.
    /// </summary>
    /// <remarks>
    /// Only set for installs upgrading from a build that had the in-settings item editor — a
    /// fresh install has never seen it, so the notice would refer to something the user has
    /// no memory of. Cleared permanently once dismissed.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowEditingMovedNotice { get; set; }

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

    [JsonIgnore]
    private bool _initializing = true;

    // ── Settings Window State ────────────────────────────────────────

    /// <summary>Saved settings window X position (physical pixels).</summary>
    public int SettingsWindowX { get; set; }

    /// <summary>Saved settings window Y position (physical pixels).</summary>
    public int SettingsWindowY { get; set; }

    /// <summary>Saved settings window width (physical pixels).</summary>
    public int SettingsWindowWidth { get; set; }

    /// <summary>Saved settings window height (physical pixels).</summary>
    public int SettingsWindowHeight { get; set; }

    /// <summary>Whether the settings window was maximized.</summary>
    public bool SettingsWindowMaximized { get; set; }

    // ── Constructor (defaults) ──────────────────────────────────────

    public UserSettings()
    {
        AppTheme = 0;
        Startup = false;
        FlyoutAnimationsEnabled = true;
        NIconHide = false;
        TrayIconMode = 0;
        CustomTrayIconPath = "";
        LastKnownVersion = "";

        // Do NOT populate defaults here — the JSON deserializer calls this constructor
        // then overwrites with deserialized values.
        Launchers = [];

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
        // ── Launcher migration ───────────────────────────────────────
        // Migrate from the old flat LauncherItems / global icon settings to a Launcher-based model.
        if (Launchers.Count == 0)
        {
            var defaultLauncher = new Launcher
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Default",
                // Carry over global icon settings from the legacy fields
                TrayIconMode = TrayIconModes.FromLegacyInt(TrayIconMode),
                CustomTrayIconPath = CustomTrayIconPath,
                NIconHide = NIconHide,
            };

            if (LauncherItems.Count > 0)
            {
                // Migrate existing items into the default launcher
                foreach (var item in LauncherItems)
                    defaultLauncher.Items.Add(item);
            }
            else
            {
                // No legacy items — seed with sample shortcuts
                defaultLauncher.Items.Add(new LauncherItem("Google", "https://www.google.com", "\uE774", isWebsite: true));
                defaultLauncher.Items.Add(new LauncherItem("Explorer", "explorer.exe", "Folder24"));
                defaultLauncher.Items.Add(new LauncherItem("Notepad", "notepad.exe", "Notepad24"));
            }

            Launchers.Add(defaultLauncher);

            // Clear legacy fields — they are now represented inside the launcher
            LauncherItems.Clear();
        }

        _initializing = false;
    }

    // ── Change handlers ─────────────────────────────────────────────

    partial void OnAppThemeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        LittleLauncher.Classes.ThemeManager.ApplyAndSaveTheme(newValue);
    }
}
