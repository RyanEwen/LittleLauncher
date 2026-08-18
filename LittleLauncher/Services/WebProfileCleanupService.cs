// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Services;

/// <summary>
/// Finds and removes WebView2 profile folders nothing points at any more.
/// </summary>
/// <remarks>
/// <para>A web launcher's profile is a Chromium user-data folder, and they are not small — a chat
/// app that has been open for months runs to hundreds of megabytes. Two things leave them behind,
/// and neither used to clean up after itself:</para>
/// <list type="bullet">
///   <item><b>A deleted launcher.</b> Removing a launcher disposed its panel and dropped it from
///   settings, and left <c>WebProfiles\{id}</c> on disk for good — with no launcher left to name
///   it, nothing could ever find it again.</item>
///   <item><b>A launcher moved to the shared profile.</b> Its private folder is deliberately kept:
///   moving across is reversible precisely because nothing deletes it, so the session is still
///   there if the user moves back. That is worth having by default and not worth keeping forever.</item>
/// </list>
/// <para><b>Never the shared folder, and never a folder in use.</b> Everything reported here
/// belongs to a launcher that is on the shared profile, or to no launcher at all, so no live
/// browser has it open. That is what makes deleting safe without tearing down any panels.</para>
/// </remarks>
internal static class WebProfileCleanupService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>One profile folder that could be removed, and why it is going.</summary>
    /// <param name="Path">Full path to the folder.</param>
    /// <param name="Description">What it belonged to, for the confirmation the user is shown.</param>
    /// <param name="Bytes">Size on disk, so "free 740 MB" can be an honest number.</param>
    internal record Reclaimable(string Path, string Description, long Bytes);

    /// <summary>Profile folders no launcher is using. Never touches the shared one.</summary>
    internal static List<Reclaimable> Scan()
    {
        var found = new List<Reclaimable>();

        string root = System.IO.Path.Combine(MainWindow.GetPhysicalAppDataDir(), "WebProfiles");
        if (!Directory.Exists(root)) return found;

        // By id, because the folder name *is* the launcher id — that is the only thing tying a
        // folder to a launcher, and it is why a deleted launcher's folder is unfindable by hand.
        var byId = SettingsManager.Current.Launchers.ToDictionary(l => l.Id, l => l);

        foreach (string folder in SafeDirectories(root))
        {
            string name = System.IO.Path.GetFileName(folder);

            // The shared profile is in use by every launcher set to share, and its name is not a
            // launcher id — so it would otherwise look exactly like an orphan.
            if (string.Equals(name, "Shared", StringComparison.OrdinalIgnoreCase)) continue;

            string? description = Describe(name, byId);
            if (description == null) continue;

            found.Add(new Reclaimable(folder, description, DirectorySize(folder)));
        }

        return [.. found.OrderByDescending(f => f.Bytes)];
    }

    /// <summary>Why this folder is reclaimable, or null when it is still someone's profile.</summary>
    private static string? Describe(string folderName, Dictionary<string, Launcher> byId)
    {
        if (!byId.TryGetValue(folderName, out var launcher))
            return "a launcher that no longer exists";

        // Still private: this is its live profile and its sign-ins.
        if (!launcher.WebSharedProfile) return null;

        return $"{launcher.Name}, which now uses the shared profile";
    }

    /// <summary>Deletes the folders handed to it, and reports how much actually went.</summary>
    /// <remarks>
    /// Per folder rather than all-or-nothing: one profile with a file still locked must not stop
    /// the other nine being reclaimed, and the caller is told the total that succeeded rather than
    /// the total it asked for.
    /// </remarks>
    internal static async Task<(int Deleted, long Bytes)> DeleteAsync(IEnumerable<Reclaimable> folders)
    {
        var list = folders.ToList();

        return await Task.Run(() =>
        {
            int deleted = 0;
            long bytes = 0;

            foreach (var folder in list)
            {
                try
                {
                    if (!Directory.Exists(folder.Path)) continue;

                    Directory.Delete(folder.Path, recursive: true);
                    deleted++;
                    bytes += folder.Bytes;
                    Logger.Info("Removed unused web profile {Path} ({Description})", folder.Path, folder.Description);
                }
                catch (Exception ex)
                {
                    // Usually a file still mapped by a browser process that has not fully exited.
                    Logger.Warn(ex, "Could not remove web profile {Path}", folder.Path);
                }
            }

            return (deleted, bytes);
        });
    }

    /// <summary>
    /// Removes one launcher's own profile folder, for a launcher being deleted.
    /// </summary>
    /// <remarks>
    /// Always the <em>private</em> folder — <c>GetUserDataFolder(Launcher)</c> would answer with the
    /// shared one for a launcher set to share, and deleting that would sign out every other launcher
    /// on it. A launcher that has been on the shared profile can still have a stale private folder
    /// from before the switch, which is exactly what this is for.
    /// </remarks>
    internal static void DeleteFor(Launcher launcher)
    {
        try
        {
            string folder = WebFlyoutWindow.GetUserDataFolder(launcher.Id);
            if (!Directory.Exists(folder)) return;

            Directory.Delete(folder, recursive: true);
            Logger.Info("Removed the web profile of deleted launcher {Name}", launcher.Name);
        }
        catch (Exception ex)
        {
            // Not worth failing the delete over: the launcher is gone either way, and what is left
            // behind is what the cleanup in settings exists to sweep up.
            Logger.Warn(ex, "Could not remove the web profile of deleted launcher {Name}", launcher.Name);
        }
    }

    /// <summary>Human-readable size, for a button that has to say what it is about to free.</summary>
    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not list web profiles in {Root}", root);
            return [];
        }
    }

    private static long DirectorySize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex)
        {
            // A profile mid-write, or a path over MAX_PATH inside a Chromium cache. Reporting zero
            // is better than refusing to offer the folder for deletion.
            Logger.Debug(ex, "Could not size web profile {Folder}", folder);
            return 0;
        }
    }
}
