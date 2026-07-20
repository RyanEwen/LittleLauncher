using LittleLauncher.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LittleLauncher.Services;

/// <summary>Rendering engine a browser is built on. Determines command-line flags for app-window mode.</summary>
public enum BrowserEngine { Chromium, Gecko }

/// <summary>An installed browser discovered on disk.</summary>
public record KnownBrowser(string DisplayName, string ExePath, string ProfileDataDir, BrowserEngine Engine);

/// <summary>A browser profile. For Chromium <c>DirectoryName</c> is a folder name relative to the user-data dir; for Gecko it is an absolute path.</summary>
public record BrowserProfile(string DirectoryName, string DisplayName);

/// <summary>
/// Discovery of installed browsers and their profiles, shared by the item editor
/// (which offers them as app-window targets) and <c>FlyoutWindow</c> (which launches into them).
/// </summary>
public static class BrowserCatalog
{
    /// <summary>
    /// Infers the engine from the executable. Prefers sniffing sibling DLLs, since
    /// Chromium forks rename their exe freely; falls back to a known-name list and
    /// finally assumes Chromium (the far more common case).
    /// </summary>
    public static BrowserEngine DetectEngine(string exePath)
    {
        string? dir = Path.GetDirectoryName(exePath);
        if (dir != null && (File.Exists(Path.Combine(dir, "chrome.dll")) ||
                            File.Exists(Path.Combine(dir, "msedge.dll"))))
            return BrowserEngine.Chromium;

        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (name is "firefox" or "zen" or "waterfox" or "librewolf" or "floorp" or "mercury" or "firedragon")
            return BrowserEngine.Gecko;

        return BrowserEngine.Chromium;
    }

    /// <summary>Resolves the user's default browser via the <c>https</c> protocol association. Null if unresolvable.</summary>
    public static string? GetDefaultBrowserExePath()
    {
        try
        {
            int size = 512;
            var sb = new System.Text.StringBuilder(size);
            int hr = NativeMethods.AssocQueryString(
                NativeMethods.ASSOCF_NONE, NativeMethods.ASSOCSTR_EXECUTABLE,
                "https", "open", sb, ref size);
            if (hr == 0)
            {
                string exePath = sb.ToString();
                if (File.Exists(exePath))
                    return exePath;
            }
        }
        catch { }

        return null;
    }

    /// <summary>Probes well-known install locations for supported browsers. Returns only those present on disk.</summary>
    public static List<KnownBrowser> GetInstalledBrowsers()
    {
        var browsers = new List<KnownBrowser>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = new (string Name, string[] Paths, string ProfileDataDir, BrowserEngine Engine)[]
        {
            ("Microsoft Edge", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            ], Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), BrowserEngine.Chromium),
            ("Google Chrome", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
            ], Path.Combine(localAppData, "Google", "Chrome", "User Data"), BrowserEngine.Chromium),
            ("Brave", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
            ], Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"), BrowserEngine.Chromium),
            ("Vivaldi", [
                Path.Combine(localAppData, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vivaldi", "Application", "vivaldi.exe")
            ], Path.Combine(localAppData, "Vivaldi", "User Data"), BrowserEngine.Chromium),
            ("Chromium", [
                Path.Combine(localAppData, "Chromium", "Application", "chrome.exe")
            ], Path.Combine(localAppData, "Chromium", "User Data"), BrowserEngine.Chromium),
            ("Firefox", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe")
            ], Path.Combine(appData, "Mozilla", "Firefox"), BrowserEngine.Gecko),
            ("Zen", [
                Path.Combine(localAppData, "Programs", "zen", "zen.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zen Browser", "zen.exe"),
                Path.Combine(localAppData, "zen", "zen.exe"),
            ], Path.Combine(appData, "zen"), BrowserEngine.Gecko),
            ("Waterfox", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Waterfox", "waterfox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Waterfox", "waterfox.exe")
            ], Path.Combine(appData, "Waterfox"), BrowserEngine.Gecko),
            ("LibreWolf", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreWolf", "librewolf.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreWolf", "librewolf.exe")
            ], Path.Combine(appData, "librewolf"), BrowserEngine.Gecko),
        };

        foreach (var (name, paths, profileDataDir, engine) in candidates)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    browsers.Add(new KnownBrowser(name, path, profileDataDir, engine));
                    break;
                }
            }
        }

        return browsers;
    }

    /// <summary>Lists the profiles of a browser, dispatching on engine.</summary>
    public static List<BrowserProfile> GetBrowserProfiles(string profileDataDir, BrowserEngine engine)
    {
        if (string.IsNullOrEmpty(profileDataDir) || !Directory.Exists(profileDataDir))
            return [];

        return engine == BrowserEngine.Gecko
            ? GetGeckoProfiles(profileDataDir)
            : GetChromiumProfiles(profileDataDir);
    }

    /// <summary>
    /// Reads Chromium profiles from <c>Local State</c> (which carries friendly names),
    /// falling back to scanning for directories containing a <c>Preferences</c> file.
    /// </summary>
    public static List<BrowserProfile> GetChromiumProfiles(string userDataDir)
    {
        var profiles = new List<BrowserProfile>();
        if (string.IsNullOrEmpty(userDataDir) || !Directory.Exists(userDataDir))
            return profiles;

        string localStatePath = Path.Combine(userDataDir, "Local State");
        if (File.Exists(localStatePath))
        {
            try
            {
                string json = File.ReadAllText(localStatePath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("profile", out var profileRoot) &&
                    profileRoot.TryGetProperty("info_cache", out var infoCache))
                {
                    foreach (var entry in infoCache.EnumerateObject())
                    {
                        string dirName = entry.Name;
                        string displayName = dirName;
                        if (entry.Value.TryGetProperty("name", out var nameProp))
                            displayName = nameProp.GetString() ?? dirName;

                        if (Directory.Exists(Path.Combine(userDataDir, dirName)))
                            profiles.Add(new BrowserProfile(dirName, displayName));
                    }
                }
            }
            catch { }
        }

        if (profiles.Count == 0)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(userDataDir))
                {
                    if (File.Exists(Path.Combine(dir, "Preferences")))
                    {
                        string dirName = Path.GetFileName(dir);
                        profiles.Add(new BrowserProfile(dirName, dirName));
                    }
                }
            }
            catch { }
        }

        return profiles.OrderBy(p => p.DirectoryName != "Default")
                       .ThenBy(p => p.DisplayName)
                       .ToList();
    }

    /// <summary>Parses <c>profiles.ini</c>. Paths may be relative to the profile data dir or absolute.</summary>
    public static List<BrowserProfile> GetGeckoProfiles(string profileDataDir)
    {
        var profiles = new List<BrowserProfile>();
        string iniPath = Path.Combine(profileDataDir, "profiles.ini");
        if (!File.Exists(iniPath))
            return profiles;

        try
        {
            string? currentName = null;
            string? currentPath = null;
            bool isRelative = true;

            foreach (string rawLine in File.ReadLines(iniPath))
            {
                string line = rawLine.Trim();

                if (line.StartsWith('['))
                {
                    if (currentPath != null)
                    {
                        string fullPath = isRelative
                            ? Path.Combine(profileDataDir, currentPath.Replace('/', '\\'))
                            : currentPath;

                        if (Directory.Exists(fullPath))
                            profiles.Add(new BrowserProfile(fullPath, currentName ?? Path.GetFileName(fullPath)));
                    }

                    currentName = null;
                    currentPath = null;
                    isRelative = true;

                    if (!line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase))
                        currentPath = null;

                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();

                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    currentName = value;
                else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                    currentPath = value;
                else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase))
                    isRelative = value == "1";
            }

            if (currentPath != null)
            {
                string fullPath = isRelative
                    ? Path.Combine(profileDataDir, currentPath.Replace('/', '\\'))
                    : currentPath;

                if (Directory.Exists(fullPath))
                    profiles.Add(new BrowserProfile(fullPath, currentName ?? Path.GetFileName(fullPath)));
            }
        }
        catch { }

        return profiles;
    }
}
