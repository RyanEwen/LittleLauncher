// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Models;
using LittleLauncher.ViewModels;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace LittleLauncher.Classes.Settings;

/// <summary>
/// Manages the application settings and saves them to a file in \AppData\LittleLauncher.
/// On first load, migrates from settings.xml (XmlSerializer) to settings.json (System.Text.Json).
/// </summary>
public static class SettingsManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LittleLauncher");

    private static string SettingsFilePath => Path.Combine(SettingsDir, "settings.json");

    /// <summary>Legacy XML settings path — used for one-time migration only.</summary>
    private static string LegacyXmlPath => Path.Combine(SettingsDir, "settings.xml");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = null, // PascalCase to match property names
    };

    private static UserSettings _current = new();

    /// <summary>
    /// The current user settings stored in the app.
    /// </summary>
    public static UserSettings Current
    {
        get
        {
            if (_current == null)
            {
                _current = new UserSettings();
            }
            return _current;
        }
        set => _current = value;
    }

    /// <summary>
    /// Flat enumeration of all <see cref="LauncherItem"/> objects across every launcher's Items
    /// collection (including group children). Use when a search must span all layouts.
    /// </summary>
    public static IEnumerable<LauncherItem> AllItems =>
        _current?.Launchers
            .SelectMany(l => l.Items.SelectMany(i => i.IsGroup ? new[] { i }.Concat(i.Children) : [i]))
        ?? Enumerable.Empty<LauncherItem>();

    /// <summary>Version of the running assembly, or null if it cannot be determined.</summary>
    private static Version? AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>The version that introduced web launchers, and so the one-time notice about them.</summary>
    private static readonly Version WebLauncherVersion = new(1, 25, 0);

    /// <summary>
    /// Raises any one-time notices this upgrade has earned. Call **before**
    /// <see cref="StampVersion"/>, which overwrites the evidence.
    /// </summary>
    /// <remarks>
    /// Only ever called from the two load paths that found an existing settings file. A fresh
    /// install has never seen the old behaviour, so announcing a change to it is pure noise —
    /// which is why the defaults path deliberately does not call this.
    /// </remarks>
    private static void RaiseUpgradeNotices(string? previousVersion)
    {
        if (!IsOlderThan(previousVersion, WebLauncherVersion)) return;

        _current.ShowWebLauncherNotice = true;
        _current.UpgradeNoticesJustRaised = true;
    }

    /// <summary>
    /// True when a recorded version predates <paramref name="threshold"/>.
    /// </summary>
    /// <remarks>
    /// A missing or unparseable value counts as older: settings files written before
    /// <see cref="UserSettings.LastRunVersion"/> existed carry nothing, and those are upgraders
    /// too — arguably the oldest ones.
    /// </remarks>
    private static bool IsOlderThan(string? version, Version threshold)
    {
        if (string.IsNullOrWhiteSpace(version)) return true;
        return !Version.TryParse(version, out var parsed) || parsed < threshold;
    }

    /// <summary>
    /// Records the running version in the settings file, so an upgrade can be detected later.
    /// </summary>
    /// <remarks>
    /// A settings file with no recorded version predates this field, which is what identifies
    /// an install upgrading from before it existed. Any future one-time upgrade notice should
    /// compare <c>LastRunVersion</c> here and only fire when a settings file already existed —
    /// a fresh install has never seen whatever is being announced.
    /// </remarks>
    private static void StampVersion()
    {
        var current = AppVersion;
        if (current == null) return;

        string stamped = current.ToString(3);
        if (_current.LastRunVersion != stamped)
        {
            _current.LastRunVersion = stamped;
            SaveSettings();
        }
    }

    /// <summary>
    /// Restores the settings <see cref="Current"/> from the settings file.
    /// Migrates from legacy XML format if the JSON file doesn't exist.
    /// </summary>
    public static UserSettings RestoreSettings(string? filePath = null)
    {
        filePath ??= SettingsFilePath;

        try
        {
            // ── Try JSON first ──────────────────────────────────────────
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                var deserialized = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                if (deserialized != null)
                {
                    _current = deserialized;
                    _current.CompleteInitialization();
                    NormalizeAllGlyphs();
                    // Before StampVersion, which replaces the value being compared against.
                    RaiseUpgradeNotices(_current.LastRunVersion);
                    StampVersion();
                    Logger.Info("Settings successfully restored");
                    return _current;
                }
            }

            // ── Migrate from legacy XML ─────────────────────────────────
            if (File.Exists(LegacyXmlPath))
            {
                Logger.Info("Migrating settings from XML to JSON");
                using (StreamReader reader = new StreamReader(LegacyXmlPath))
                {
                    XmlSerializer xmlSerializer = new XmlSerializer(typeof(UserSettings));
                    if (xmlSerializer.Deserialize(reader) is UserSettings xmlSettings)
                    {
                        _current = xmlSettings;
                        _current.CompleteInitialization();
                        NormalizeAllGlyphs();
                        RaiseUpgradeNotices(_current.LastRunVersion);
                        StampVersion();

                        // Save in new JSON format and rename old file
                        SaveSettings();
                        try { File.Move(LegacyXmlPath, LegacyXmlPath + ".bak", overwrite: true); } catch { }

                        Logger.Info("Settings migrated from XML to JSON");
                        return _current;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error(ex, "No permission to read settings file");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error restoring settings");
        }

        // if the settings file not found or cannot be read
        Logger.Warn("Settings file not found or cannot be read, loading default settings");
        _current = new UserSettings();
        _current.CompleteInitialization();
        StampVersion();
        return _current;
    }

    /// <summary>
    /// Saves the app settings to the settings file.
    /// </summary>
    public static void SaveSettings(string? filePath = null)
    {
        filePath ??= SettingsFilePath;

        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(_current, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error(ex, "No permission to write in settings file");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error saving settings");
        }
    }

    /// <summary>Normalize legacy glyph text names across all launchers' items.</summary>
    /// <remarks>
    /// Also the hook for <see cref="Models.Launcher.MigrateWebModel"/>, which brings a web
    /// launcher written before single-address and bookmark-bar launchers merged onto the one
    /// model. Both run at the same two moments — after a JSON load and after the legacy XML
    /// migration — and both are idempotent, so a launcher already current is untouched.
    /// </remarks>
    private static void NormalizeAllGlyphs()
    {
        foreach (var launcher in _current.Launchers)
        {
            launcher.MigrateWebModel();

            foreach (var item in launcher.Items)
            {
                item.NormalizeGlyph();
                if (item.IsGroup)
                    foreach (var child in item.Children)
                        child.NormalizeGlyph();
            }
        }
    }
}
