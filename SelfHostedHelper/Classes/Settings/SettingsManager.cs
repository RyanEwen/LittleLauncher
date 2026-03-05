// Copyright © 2024-2026 The SelfHostedHelper Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using SelfHostedHelper.ViewModels;
using System.IO;
using System.Xml.Serialization;

namespace SelfHostedHelper.Classes.Settings;

/// <summary>
/// Manages the application settings and saves them to a file in \AppData\SelfHostedHelper.
/// </summary>
public class SettingsManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SelfHostedHelper",
        "settings.xml"
    );

    private static UserSettings _current = new();

    /// <summary>
    /// The current user settings stored in the app.
    /// </summary>
    /// <returns>The current user settings.</returns>
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
    /// Checks whether the app has updated, and restores the settings from the previous version if necessary. Only updates in release mode.
    /// </summary>
    /// <summary>
    /// Restores the settings `SettingsManager.Current` from the settings file.
    /// </summary>
    /// <returns>The restored settings.</returns>
    public UserSettings RestoreSettings(string? filePath = null)
    {
        filePath ??= SettingsFilePath;

        try
        {
            if (File.Exists(filePath))
            {
                using (StreamReader reader = new StreamReader(filePath))
                {
                    XmlSerializer xmlSerializer = new XmlSerializer(typeof(UserSettings));
                    if (xmlSerializer.Deserialize(reader) is UserSettings deserialized)
                    {
                        _current = deserialized;
                        _current.CompleteInitialization();

                        Logger.Info("Settings successfully restored");
                        return _current;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error(ex, "No permission to write in settings file");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error restoring settings");
        }

        // if the settings file not found or cannot be read
        Logger.Warn("Settings file not found or cannot be read, loading default settings");
        _current = new UserSettings();
        _current.CompleteInitialization();
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

            using (StreamWriter writer = new StreamWriter(filePath))
            {
                XmlSerializer xmlSerializer = new XmlSerializer(typeof(UserSettings));
                xmlSerializer.Serialize(writer, _current);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // if the app doesn't have permission to write to the settings file
            Logger.Error(ex, "No permission to write in settings file");
        }
        catch (Exception ex)
        {
            // if the settings file cannot be saved
            Logger.Error(ex, "Error saving settings");
        }
    }
}
