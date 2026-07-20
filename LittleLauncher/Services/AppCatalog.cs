using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LittleLauncher.Services;

/// <summary>An installed desktop application. <c>ExePath</c> may also be a <c>shell:AppsFolder\…</c> launch path for packaged apps.</summary>
public record InstalledApp(string DisplayName, string ExePath);

/// <summary>An installed Chromium-registered Progressive Web App.</summary>
public record InstalledPwa(string DisplayName, string Aumid, string Domain);

/// <summary>
/// Enumerates installed applications and PWAs for the add/edit-item picker.
/// </summary>
/// <remarks>
/// These calls are slow and apartment-threaded (they drive <c>Shell.Application</c>),
/// so callers must run them via <see cref="AppPickerService.RunStaAsync"/> rather than
/// on the UI thread or a pooled MTA thread.
/// </remarks>
public static class AppCatalog
{
    /// <summary>Chromium PWA AUMID shape: <c>{domain}-{HEX}_{hash}!App</c>.</summary>
    private const string PwaAumidPattern = @"^([\w][\w.-]*\.[a-zA-Z]{2,})-[A-Fa-f0-9]+_[a-z0-9]+!App$";

    /// <summary>
    /// Discovers installed Progressive Web Apps by enumerating shell:AppsFolder
    /// for Chromium-based PWA entries (registered with AUMIDs like domain-HEX_hash!App).
    /// </summary>
    public static List<InstalledPwa> GetInstalledPwas()
    {
        var pwas = new List<InstalledPwa>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return pwas;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return pwas;

            foreach (dynamic item in folder.Items())
            {
                string? path = item.Path as string;
                string? name = item.Name as string;
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) continue;
                if (!path.EndsWith("!App", StringComparison.Ordinal)) continue;

                var match = Regex.Match(path, PwaAumidPattern);
                if (!match.Success) continue;

                string domain = match.Groups[1].Value;
                pwas.Add(new InstalledPwa(name, path, domain));
            }

            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        catch { }

        return pwas.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Builds a list of installed applications by scanning Start Menu shortcuts
    /// and the Windows Registry uninstall keys.
    /// </summary>
    public static List<InstalledApp> GetInstalledApplications()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        var startMenuDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        foreach (var dir in startMenuDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        var target = ResolveShortcutTarget(lnk);
                        if (string.IsNullOrEmpty(target)) continue;
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(target)) continue;

                        var name = Path.GetFileNameWithoutExtension(lnk);
                        if (IsNonAppName(name)) continue;

                        if (!apps.ContainsKey(target))
                            apps[target] = new InstalledApp(name, target);
                    }
                    catch { }
                }
            }
            catch { }
        }

        var uninstallKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var keyPath in uninstallKeys)
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var key = root.OpenSubKey(keyPath);
                    if (key == null) continue;

                    foreach (var subName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = key.OpenSubKey(subName);
                            if (sub == null) continue;

                            var systemComponent = sub.GetValue("SystemComponent");
                            if (systemComponent is int sc && sc == 1) continue;
                            var parentName = sub.GetValue("ParentDisplayName") as string;
                            if (!string.IsNullOrEmpty(parentName)) continue;
                            var releaseType = sub.GetValue("ReleaseType") as string;
                            if (!string.IsNullOrEmpty(releaseType)) continue;

                            var displayName = sub.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName)) continue;
                            if (IsNonAppName(displayName)) continue;

                            var exePath = ResolveAppExePath(sub);
                            if (string.IsNullOrEmpty(exePath)) continue;

                            if (!apps.ContainsKey(exePath))
                                apps[exePath] = new InstalledApp(displayName, exePath);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // Also enumerate shell:AppsFolder for Store/packaged apps
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder != null)
                {
                    var pwaPattern = new Regex(PwaAumidPattern);

                    // Build a set of display names already found via Start Menu / Registry
                    // so we don't duplicate them with shell:AppsFolder entries.
                    var existingNames = new HashSet<string>(
                        apps.Values.Select(a => a.DisplayName),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (dynamic item in folder.Items())
                    {
                        string? path = item.Path as string;
                        string? name = item.Name as string;
                        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) continue;

                        // Skip Chromium PWAs (handled by the PWA picker)
                        if (pwaPattern.IsMatch(path)) continue;

                        if (IsNonAppName(name)) continue;

                        // Skip if already discovered via Start Menu / Registry
                        if (existingNames.Contains(name)) continue;

                        string launchPath = $"shell:AppsFolder\\{path}";
                        if (!apps.ContainsKey(launchPath))
                            apps[launchPath] = new InstalledApp(name, launchPath);
                    }

                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder);
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
            }
        }
        catch { }

        return apps.Values
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Filters out SDKs, runtimes, drivers and other registry entries that aren't user-launchable apps.</summary>
    public static bool IsNonAppName(string name)
    {
        string[] filters =
        [
            "SDK", "Runtime", "Redistributable", "Targeting Pack", "Manifest",
            "Toolset", "Template", "Hosting Bundle", "AppHost", "SharedHost",
            "WindowsDesktop", "Host (", "- Debug", "IntelliTrace",
            "DiagnosticsHub", "IntelliSense", "Language Pack",
            "Driver", "Firmware", "BIOS", "Chipset",
            ".NET Framework", "Microsoft .NET", "Microsoft ASP.NET",
            "Microsoft Windows Desktop", "Microsoft Visual C++",
            "Uninstall"
        ];

        foreach (var filter in filters)
        {
            if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (name.StartsWith('{') || name.StartsWith("KB"))
            return true;

        return false;
    }

    /// <summary>Reads a <c>.lnk</c>'s target via WScript.Shell. Null if it can't be resolved.</summary>
    public static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string? target = shortcut.TargetPath;
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Derives an executable for an uninstall-key entry: prefers <c>DisplayIcon</c>,
    /// else looks in <c>InstallLocation</c> for an exe whose name resembles the display
    /// name, else takes the first exe found there.
    /// </summary>
    public static string? ResolveAppExePath(Microsoft.Win32.RegistryKey sub)
    {
        var displayIcon = sub.GetValue("DisplayIcon") as string;
        if (!string.IsNullOrEmpty(displayIcon))
        {
            var iconPath = displayIcon.Split(',')[0].Trim('"', ' ');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
                return iconPath;
        }

        var installLoc = sub.GetValue("InstallLocation") as string;
        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            var displayName = sub.GetValue("DisplayName") as string ?? "";
            foreach (var exe in Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var fn = Path.GetFileNameWithoutExtension(exe);
                if (displayName.Contains(fn, StringComparison.OrdinalIgnoreCase)
                    || fn.Contains(displayName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return exe;
            }
            var firstExe = Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (firstExe != null) return firstExe;
        }

        return null;
    }
}
