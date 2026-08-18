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
    /// <remarks>
    /// <para><c>JsonIgnoreCondition.Never</c> is load-bearing, not decoration. This is the one
    /// setting here that defaults to <c>true</c>, and <see cref="SettingsManager.JsonOptions"/>
    /// serialises with <c>DefaultIgnoreCondition = WhenWritingDefault</c> — which drops any
    /// property holding the *CLR* default. Turning animations off therefore wrote <c>false</c>…
    /// by omitting the key entirely, and the constructor put <c>true</c> back on the next load.
    /// The setting could not be turned off at all: verified by toggling it off, closing the
    /// settings window, and finding no <c>FlyoutAnimationsEnabled</c> key in settings.json.</para>
    /// <para>Any future setting whose default is <c>true</c> (or any non-zero number) needs this
    /// attribute, or the phrasing inverted so <c>false</c> is the default behaviour — the approach
    /// the launcher's <c>Web*</c> properties take. See
    /// <see href="../../.claude/docs/user-settings.md">user-settings.md</see>.</para>
    /// </remarks>
    [ObservableProperty]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public partial bool FlyoutAnimationsEnabled { get; set; }

    /// <summary>
    /// Stop creating a Start Menu shortcut for each web launcher.
    /// </summary>
    /// <remarks>
    /// <para>Stored in the negative and shown in the positive ("Start Menu Shortcuts", on by
    /// default) — the same bargain <c>Launcher.WebLockSize</c> makes, and for the same reason: a
    /// bool defaulting to <c>true</c> cannot be turned off under <c>WhenWritingDefault</c>. Invert
    /// it in the one line that builds the toggle, never in the model.</para>
    /// <para>Shortcuts are what let a web launcher be opened from Start search, PowerToys Command
    /// Palette and anything else that indexes the Start Menu. Turning this off deletes the group
    /// rather than leaving it stale — see <see cref="Services.StartMenuShortcutService"/>.</para>
    /// </remarks>
    [ObservableProperty]
    public partial bool DisableWebLauncherShortcuts { get; set; }

    partial void OnDisableWebLauncherShortcutsChanged(bool value)
    {
        if (_initializing) return;
        Services.StartMenuShortcutService.Sync(Launchers);
    }

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

    /// <summary>
    /// Shows the one-time "web launchers" notice on the Home page. Set by
    /// <see cref="SettingsManager"/> when an existing install is upgraded past the version that
    /// introduced them; cleared when the user dismisses it.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c> and is switched *on* by the upgrade check, which is what makes it
    /// safe under this file's <c>WhenWritingDefault</c> policy: dismissing writes <c>false</c>,
    /// the key is dropped, and the notice stays gone. A flag phrased the other way round could
    /// never be dismissed. See the docs on defaults in user-settings.md.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowWebLauncherNotice { get; set; }

    /// <summary>
    /// True only in the session where an upgrade was actually detected. Not persisted.
    /// </summary>
    /// <remarks>
    /// The toast is keyed off this rather than <see cref="ShowWebLauncherNotice"/>, which stays
    /// true until the banner is dismissed in-app — toasting on that would re-notify on every
    /// launch until the user happened to open the app and close a banner they never saw.
    /// </remarks>
    [JsonIgnore]
    public bool UpgradeNoticesJustRaised { get; set; }

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
    /// When local launcher changes were last made without having been uploaded. Default means
    /// nothing is pending.
    /// </summary>
    /// <remarks>
    /// Persisted, unlike the in-memory flag it backs. That flag was lost on restart, so quitting
    /// between a change and its debounced upload left the next startup download free to erase
    /// work that had been saved minutes earlier. Cleared once an upload succeeds.
    /// </remarks>
    public DateTime LaunchersModifiedUtc { get; set; }

    // ── Sync destination ────────────────────────────────────────────

    /// <summary>
    /// Where launchers sync to — see <see cref="SyncProviders"/>. Selects which of the
    /// transports below is used; the other one's settings are kept, not cleared, so switching
    /// back does not mean re-entering them.
    /// </summary>
    /// <remarks>
    /// <see cref="SyncProviders.Sftp"/> is 0 on purpose: <c>WhenWritingDefault</c> drops the key
    /// when the value is the CLR default, so 0 has to mean the behaviour every settings file
    /// written before this setting existed was configured for. See
    /// <see href="../../.claude/docs/user-settings.md">user-settings.md</see>.
    /// </remarks>
    /// <summary>
    /// Which destination the Cloud Sync page is currently *editing*. UI state only — it does
    /// not decide what syncs; <see cref="EnabledSyncProviders"/> does.
    /// </summary>
    /// <remarks>
    /// It used to be the exclusive choice of transport, and is kept under the same name so
    /// existing settings files migrate cleanly: on load it seeds
    /// <see cref="EnabledSyncProviders"/> when that list is absent.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderSync))]
    public partial int SyncProvider { get; set; }

    /// <summary>
    /// Every destination launchers are synced to. Uploads go to all of them; a download takes
    /// whichever reports the newest copy.
    /// </summary>
    /// <remarks>
    /// <para>A list rather than one value because a single destination is a single point of
    /// failure for the thing that holds a user's whole configuration — and because "stop syncing
    /// to X" should not mean "reconfigure Y from scratch".</para>
    /// <para>Absent in files written before this existed, which is exactly what
    /// <see cref="CompleteInitialization"/> keys the migration off: null means "seed me from the
    /// old exclusive <see cref="SyncProvider"/>", empty means "the user turned everything off".
    /// The two must stay distinguishable, so this is deliberately <c>null</c> by default rather
    /// than an empty list.</para>
    /// </remarks>
    public List<int>? EnabledSyncProviders { get; set; }

    /// <summary>
    /// Profiles where web launchers should not offer to save logins or fill them in.
    /// </summary>
    /// <remarks>
    /// <para>For when a password manager extension is doing the job: two of them competing means
    /// the built-in one keeps proposing its own older saved logins over the manager's, which is
    /// what installing Bitwarden and still being offered the old passwords looks like.</para>
    /// <para><b>Keyed by profile</b> — <c>"Shared"</c> or a launcher id, matching the folder names
    /// under <c>WebProfiles</c>. Saved logins belong to a profile, so the setting governing them has
    /// to as well: every launcher sharing a profile shares one answer, and a launcher with a private
    /// profile gets its own. The platform scopes it neither way
    /// (<c>IsPasswordAutosaveEnabled</c> is per browser instance), so this is what decides.</para>
    /// <para>A set of the profiles where it is <em>off</em>, rather than a flag per profile, so the
    /// default — on, as it has always been — is the absent case and stays absent under
    /// <c>WhenWritingDefault</c>.</para>
    /// <para>Read when a browser is created, so it takes effect for a launcher the next time its
    /// browser starts rather than retroactively for one already running.</para>
    /// </remarks>
    public List<string>? ProfilesWithoutPasswordManager { get; set; }

    /// <summary>Extensions promoted to a button in the flyout header.</summary>
    /// <remarks>
    /// Keyed by store id, or by name for one added from a folder — the same fallback the rest of
    /// the feature uses. Absent means none pinned, which is the default and stays absent under
    /// <c>WhenWritingDefault</c>.
    /// </remarks>
    public List<string>? PinnedBrowserExtensions { get; set; }

    /// <summary>Browser extensions loaded into every web launcher's profile.</summary>
    /// <remarks>
    /// App-wide rather than per launcher, because extensions belong to a <em>profile</em> and most
    /// launchers share one — an app-wide list installed onto whichever profile is starting is the
    /// arrangement that matches how WebView2 actually scopes them. Nullable so an absent key stays
    /// absent under <c>WhenWritingDefault</c>; <c>BrowserExtensionService.Installed</c> creates it
    /// on first use.
    /// <para>Only the id and the name of each are synced — see <see cref="Models.BrowserExtension"/>.
    /// </para>
    /// </remarks>
    public List<Models.BrowserExtension>? BrowserExtensions { get; set; }

    /// <summary>
    /// Legacy: unpacked extension folders, before an extension was more than a path.
    /// </summary>
    /// <remarks>
    /// Read once by <c>BrowserExtensionService.MigrateFolders</c>, which turns each into a
    /// <see cref="Models.BrowserExtension"/> and clears this. Those entries become local-only —
    /// a bare folder carries no store id, so there is nothing another machine could fetch by.
    /// </remarks>
    public List<string>? BrowserExtensionFolders { get; set; }

    /// <summary>True when the given destination is switched on.</summary>
    public bool IsSyncProviderEnabled(int provider) =>
        EnabledSyncProviders?.Contains(SyncProviders.Normalize(provider)) == true;

    /// <summary>Switch a destination on or off, keeping its settings either way.</summary>
    public void SetSyncProviderEnabled(int provider, bool enabled)
    {
        provider = SyncProviders.Normalize(provider);
        var list = EnabledSyncProviders ??= [];

        if (enabled)
        {
            if (!list.Contains(provider)) list.Add(provider);
        }
        else
        {
            list.Remove(provider);
        }

        OnPropertyChanged(nameof(EnabledSyncProviders));
    }

    /// <summary>
    /// The folder launchers sync through when <see cref="SyncProvider"/> is folder-based —
    /// a OneDrive or Google Drive path, a UNC share, or any other directory.
    /// </summary>
    [ObservableProperty]
    public partial string SyncFolderPath { get; set; }

    /// <summary>True when the selected provider syncs through a folder rather than SFTP.</summary>
    [JsonIgnore]
    public bool IsFolderSync => SyncProviders.IsFolderBased(SyncProvider);

    /// <summary>
    /// The WebDAV collection URL holding <c>launchers.json</c>, e.g.
    /// <c>https://cloud.example.com/remote.php/dav/files/me/LittleLauncher/</c>.
    /// </summary>
    [ObservableProperty]
    public partial string WebDavUrl { get; set; }

    /// <summary>The WebDAV username.</summary>
    /// <remarks>
    /// The password deliberately is <b>not</b> here — it lives in <see cref="Services.ProtectedStore"/>,
    /// DPAPI-encrypted. This file is exported, backed up and uploaded by the sync feature itself,
    /// so a password in it would be copied to every machine and to the sync destination.
    /// </remarks>
    [ObservableProperty]
    public partial string WebDavUsername { get; set; }

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

        SyncProvider = SyncProviders.Sftp;
        SyncFolderPath = "";
        WebDavUrl = "";
        WebDavUsername = "";

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

        // ── Sync destination migration ───────────────────────────────
        // Null means the file predates multiple destinations, so carry the single exclusive
        // choice forward. Empty is a real state — everything deliberately switched off — and must
        // not be treated as "migrate me again" on the next load.
        EnabledSyncProviders ??= [SyncProviders.Normalize(SyncProvider)];

        _initializing = false;
    }

    // ── Change handlers ─────────────────────────────────────────────

    partial void OnAppThemeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        LittleLauncher.Classes.ThemeManager.ApplyAndSaveTheme(newValue);
    }

    /// <summary>
    /// Rebuild the periodic sync timer when auto-sync is switched on or off.
    /// </summary>
    /// <remarks>
    /// <see cref="Services.AutoSyncService.RestartPeriodicTimer"/> was previously reached only
    /// from <c>Start()</c> at app startup, so this toggle did nothing until the next launch:
    /// turning auto-sync on left no timer running, and turning it off left the old one ticking.
    /// </remarks>
    partial void OnSftpAutoSyncChanged(bool value)
    {
        if (_initializing) return;
        Services.AutoSyncService.RestartPeriodicTimer();
    }

    /// <summary>Rebuild the periodic sync timer so a new interval takes effect immediately.</summary>
    partial void OnSftpAutoSyncIntervalChanged(int value)
    {
        if (_initializing) return;
        Services.AutoSyncService.RestartPeriodicTimer();
    }
}
