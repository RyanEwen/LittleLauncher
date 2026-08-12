// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Services;

/// <summary>
/// Keeps a Start Menu shortcut per web launcher, so they can be launched from anything that reads
/// the Start Menu — Start search itself, PowerToys Command Palette, and every other launcher-style
/// tool.
/// </summary>
/// <remarks>
/// <para>A web launcher is an application in everything but installation: it has a name, an icon
/// and a window. Reaching it only by tray click or a taskbar pin is the odd part, and a shortcut is
/// all the shell needs to index it.</para>
/// <para><b>Per-launcher Start Menu shortcuts existed once and were removed, so this deliberately
/// differs in two ways.</b> The old ones set <c>PKEY_AppUserModel_ID</c> and were the pin identity
/// source, which meant Windows saw two identities for one pin — the shortcut's and the companion's
/// relaunch properties — and produced duplicate "(2)" pins. <b>Nothing here writes an AUMID.</b>
/// These are plain shortcuts that run the same command a pin's relaunch command runs, so they
/// cannot compete with anything. And they live in a subfolder rather than loose in
/// <c>Programs</c>, where <c>MainWindow.CleanUpStaleFlyoutShortcuts</c> still sweeps the old
/// <c>Little Launcher - *.lnk</c> naming on every startup.</para>
/// <para><b>The physical Start Menu path is required, not the framework one.</b> Under MSIX
/// <c>Environment.SpecialFolder.StartMenu</c> is VFS-redirected, so shortcuts written through it
/// land somewhere only the packaged app can see and the shell never indexes them — the feature
/// would appear to do nothing on exactly the build most people run. Same rule as everything else
/// the shell has to read; see the MSIX VFS section of icons.md.</para>
/// </remarks>
internal static class StartMenuShortcutService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>The Start Menu group these shortcuts live in.</summary>
    internal const string FolderName = "Little Launcher";

    /// <summary>
    /// Brings the Start Menu group in line with the current web launchers.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to call often — it is driven from startup and from every launcher
    /// change, and doing nothing is the common case. Pruning is by <em>what should exist</em>
    /// rather than by remembering what was written: any <c>.lnk</c> in the group that no current
    /// web launcher claims is deleted, which is what makes renames, deletions, a launcher switched
    /// away from Web, and files left behind by a crash all resolve themselves.
    /// </remarks>
    internal static void Sync(IEnumerable<Launcher> launchers)
    {
        try
        {
            string dir = GroupDirectory();

            // Turned off, or nothing to list: the group goes away entirely rather than lingering
            // empty, since an empty group is still a visible entry in All apps.
            var wanted = SettingsManager.Current.DisableWebLauncherShortcuts
                ? []
                : launchers.Where(l => LauncherKinds.IsWeb(l.Kind)).ToList();

            if (wanted.Count == 0)
            {
                RemoveGroup();
                return;
            }

            string companion = CompanionPath();
            if (!File.Exists(companion))
            {
                Logger.Info("Skipping Start Menu shortcuts: the companion exe is not deployed yet");
                return;
            }

            Directory.CreateDirectory(dir);

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var launcher in wanted)
            {
                string file = Path.Combine(dir, ShortcutFileName(launcher, expected));
                WriteShortcut(file, launcher, companion);
            }

            Prune(dir, expected);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to sync Start Menu shortcuts for web launchers");
        }
    }

    /// <summary>Deletes the whole group — the feature turned off, or no web launchers left.</summary>
    internal static void RemoveGroup()
    {
        try
        {
            string dir = GroupDirectory();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Removing the Start Menu group failed");
        }
    }

    private static void WriteShortcut(string file, Launcher launcher, string companion)
    {
        // The launcher id, not the name: the name is the file name and can change, but the command
        // must keep working across a rename. Exactly the command a taskbar pin's RelaunchCommand
        // runs, so a shortcut launch and a pin launch are the same code path — which is what makes
        // regular-window mode light the pinned button either way.
        string arguments = $"--launcher {launcher.Id}";

        string icon = Path.Combine(MainWindow.GetPhysicalAppDataDir(), $"app-icon-{launcher.Id}.ico");
        if (!File.Exists(icon))
        {
            // Written on demand rather than assumed: a launcher whose icon has never been rendered
            // would otherwise get the companion exe's generic icon in Start search.
            MainWindow.EnsureLauncherIconSaved(launcher);
        }

        // An icon *resource* reference — "path,0", never a bare path. Windows cannot parse a path
        // on its own and silently falls back to the generic document icon; the same trap that made
        // pinned taskbar buttons come up blank.
        string iconLocation = File.Exists(icon) ? $"{icon},0" : $"{companion},0";

        MainWindow.CreateOrUpdateLauncherShortcut(
            file, companion, arguments, launcher.Name, iconLocation);
    }

    /// <summary>
    /// Deletes every shortcut in the group that no current web launcher claims.
    /// </summary>
    /// <remarks>
    /// Only <c>.lnk</c> files, and only inside our own group — never the Programs root. This is a
    /// delete loop over a shell folder, so it stays as narrow as it can be.
    /// </remarks>
    private static void Prune(string dir, HashSet<string> expected)
    {
        foreach (string existing in Directory.GetFiles(dir, "*.lnk"))
        {
            if (expected.Contains(Path.GetFileName(existing))) continue;

            try { File.Delete(existing); }
            catch (Exception ex) { Logger.Debug(ex, "Could not remove stale shortcut {File}", existing); }
        }
    }

    /// <summary>
    /// A file name for this launcher, unique within the group.
    /// </summary>
    /// <remarks>
    /// The file name <em>is</em> the display name in Start search and Command Palette, so it is the
    /// launcher's own name rather than anything with an id in it. Two launchers may share a name,
    /// which the shell would silently resolve by one overwriting the other — hence the suffix, and
    /// hence <paramref name="taken"/> being the same set the prune step uses.
    /// </remarks>
    private static string ShortcutFileName(Launcher launcher, HashSet<string> taken)
    {
        string safe = SanitizeFileName(launcher.Name);
        if (safe.Length == 0) safe = "Web Launcher";

        string candidate = $"{safe}.lnk";
        for (int suffix = 2; taken.Contains(candidate); suffix++)
            candidate = $"{safe} ({suffix}).lnk";

        taken.Add(candidate);
        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var cleaned = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        return cleaned.Trim().TrimEnd('.');
    }

    private static string GroupDirectory() =>
        Path.Combine(MainWindow.GetPhysicalStartMenuProgramsDir(), FolderName);

    private static string CompanionPath() =>
        Path.Combine(MainWindow.GetPhysicalAppDataDir(), "LittleLauncherFlyout.exe");
}
