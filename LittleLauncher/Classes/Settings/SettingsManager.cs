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

    /// <summary>Version in which launcher item editing moved from settings into the flyout.</summary>
    private static readonly Version EditingMovedToFlyoutVersion = new(1, 23, 0);

    /// <summary>Version of the running assembly, or null if it cannot be determined.</summary>
    private static Version? AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>
    /// Records the running version in the settings, and raises one-time upgrade notices.
    /// </summary>
    /// <param name="existingInstall">
    /// False for a fresh install (no settings file). Upgrade notices are suppressed in that
    /// case: a new user never saw the feature being described, so the notice is only noise.
    /// </param>
    private static void StampVersion(bool existingInstall)
    {
        var current = AppVersion;
        if (current == null) return;

        Version? previous = null;
        if (!string.IsNullOrWhiteSpace(_current.LastRunVersion))
            Version.TryParse(_current.LastRunVersion, out previous);

        // A settings file with no recorded version predates the field, so it is certainly
        // older than the move.
        if (existingInstall && (previous == null || previous < EditingMovedToFlyoutVersion))
            _current.ShowEditingMovedNotice = true;

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
                    StampVersion(existingInstall: true);
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
                        StampVersion(existingInstall: true);

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
        StampVersion(existingInstall: false);
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
    private static void NormalizeAllGlyphs()
    {
        foreach (var launcher in _current.Launchers)
        {
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
