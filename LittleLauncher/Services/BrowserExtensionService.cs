// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LittleLauncher.Services;

/// <summary>
/// The browser extensions a web launcher runs, and the two ways one gets installed.
/// </summary>
/// <remarks>
/// <para><b>WebView2 installs from an unpacked folder and nothing else.</b> The whole API is
/// <c>AddBrowserExtensionAsync(folder)</c>, <c>GetBrowserExtensionsAsync()</c> and the
/// <c>AreBrowserExtensionsEnabled</c> option — there is no store integration, no <c>.crx</c>
/// handler and no browser-action UI. So this owns the parts a browser would otherwise do for
/// itself: keeping a list of what should be installed, unpacking archives into folders, and
/// reconciling that list onto each profile as its browser starts.</para>
/// <para><b>Extensions belong to a profile, not a launcher.</b> Every launcher on the shared
/// profile therefore sees the same set from one install, and a launcher with a private profile gets
/// its own copy — which is why the list is kept once, in <c>UserSettings</c>, and applied to
/// whichever profile is starting.</para>
/// <para><b>The store's install button is worth intercepting.</b> The Chrome Web Store renders and
/// its button responds, because WebView2 presents as Chrome — it just cannot finish, since the
/// install runs through a private API WebView2 does not implement. Where the attempt degrades into
/// an ordinary <c>.crx</c> download, <see cref="TryInstallFromArchiveAsync"/> turns that into a real
/// install: a CRX3 file is a signature header followed by a plain ZIP, so unpacking it produces
/// exactly the folder the API wants.</para>
/// </remarks>
internal static class BrowserExtensionService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>"Cr24" — the four bytes every CRX begins with.</summary>
    private static readonly byte[] CrxMagic = [0x43, 0x72, 0x32, 0x34];

    /// <summary>Where unpacked extensions are kept, one folder each.</summary>
    internal static string ExtensionsRoot =>
        Path.Combine(MainWindow.GetPhysicalAppDataDir(), "BrowserExtensions");

    // ── Installing ──────────────────────────────────────────────────

    /// <summary>
    /// Takes a folder or an archive and records it as an extension to run.
    /// </summary>
    /// <remarks>
    /// A folder is copied rather than referenced. The path the user picked is usually a download
    /// they are about to tidy away, and an extension whose folder has been deleted fails to load
    /// with nothing on screen to explain why — so the copy under <see cref="ExtensionsRoot"/> is
    /// what the profile is pointed at, and it lives as long as the extension does.
    /// </remarks>
    /// <param name="storeId">
    /// The Chrome Web Store id when the package came from the store, so another machine can fetch
    /// its own copy. Empty for a folder or zip the user picked, which nothing elsewhere can
    /// reproduce — see <see cref="Models.BrowserExtension.Id"/>.
    /// </param>
    internal static async Task<string?> InstallAsync(string sourcePath, string storeId = "")
    {
        try
        {
            string? unpacked = Directory.Exists(sourcePath)
                ? await CopyFolderAsync(sourcePath)
                : await TryInstallFromArchiveAsync(sourcePath);

            if (unpacked == null) return null;

            if (!File.Exists(Path.Combine(unpacked, "manifest.json")))
            {
                Logger.Warn("No manifest.json in {Path}; not an unpacked extension", unpacked);
                TryDelete(unpacked);
                return null;
            }

            Remember(unpacked, storeId);
            return unpacked;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Installing a browser extension from {Path} failed", sourcePath);
            return null;
        }
    }

    /// <summary>
    /// Unpacks a <c>.crx</c> or <c>.zip</c> into a folder WebView2 can load.
    /// </summary>
    /// <remarks>
    /// A CRX3 is <c>Cr24</c>, a version, a header length, that many bytes of signature header, and
    /// then a standard ZIP from the next byte on. Skipping to the ZIP is the whole of "unpacking" —
    /// the signatures are Chrome's business, and nothing here verifies them, so this is only ever
    /// pointed at a file the user asked for. A plain <c>.zip</c> is read from byte zero.
    /// </remarks>
    internal static async Task<string?> TryInstallFromArchiveAsync(string archivePath)
    {
        if (!File.Exists(archivePath)) return null;

        byte[] bytes = await File.ReadAllBytesAsync(archivePath);
        int zipStart = 0;

        if (bytes.Length > 16 && bytes.Take(4).SequenceEqual(CrxMagic))
        {
            // Cr24 | version (4) | header length (4) | header | zip
            uint headerLength = BitConverter.ToUInt32(bytes, 8);
            long start = 12L + headerLength;

            if (start <= 0 || start >= bytes.Length)
            {
                Logger.Warn("CRX header length {Length} is outside {File}", headerLength, archivePath);
                return null;
            }

            zipStart = (int)start;
        }

        string target = Path.Combine(ExtensionsRoot, Path.GetFileNameWithoutExtension(archivePath) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);

        try
        {
            using var stream = new MemoryStream(bytes, zipStart, bytes.Length - zipStart, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            ExtractSafely(zip, target);
            return target;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unpacking {File} failed", archivePath);
            TryDelete(target);
            return null;
        }
    }

    /// <summary>
    /// Extracts an archive, refusing entries that would land outside the target folder.
    /// </summary>
    /// <remarks>
    /// A zip entry may name <c>..\..\anything</c>, and <c>ExtractToDirectory</c> on a hostile
    /// archive is the classic path-traversal write. This is a file the user chose, but it arrived
    /// from the internet, so each entry's resolved path is checked to be under the target.
    /// </remarks>
    private static void ExtractSafely(ZipArchive zip, string target)
    {
        string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        foreach (var entry in zip.Entries)
        {
            // Directory entries have an empty name and are created by the file entries below.
            if (string.IsNullOrEmpty(entry.Name)) continue;

            string destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn("Skipping archive entry {Entry}: it resolves outside the extension folder", entry.FullName);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static async Task<string?> CopyFolderAsync(string source)
    {
        string target = Path.Combine(ExtensionsRoot, new DirectoryInfo(source).Name + "-" + Guid.NewGuid().ToString("N")[..8]);

        await Task.Run(() =>
        {
            foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, target, StringComparison.OrdinalIgnoreCase));

            Directory.CreateDirectory(target);

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, target, StringComparison.OrdinalIgnoreCase), overwrite: true);
        });

        return target;
    }

    // ── The list ────────────────────────────────────────────────────

    /// <summary>What should be loaded into every web launcher's profile.</summary>
    internal static List<Models.BrowserExtension> Installed
    {
        get
        {
            var list = SettingsManager.Current.BrowserExtensions ??= [];
            MigrateFolders(list);
            RepairMissingFolders(list);
            return list;
        }
    }

    /// <summary>
    /// Re-finds the unpacked folder for an extension whose path was lost.
    /// </summary>
    /// <remarks>
    /// Builds between 1.33.0.7 and 1.33.0.8 wrote the extension list with <c>Folder</c> marked
    /// <c>[JsonIgnore]</c>, so it never reached settings.json — every extension came back after a
    /// restart pointing nowhere, loaded into no profile, and disappeared from the header. The
    /// unpacked copies are still on disk, and their manifests still declare the names that were
    /// saved, so the pairing can simply be worked out again rather than asking the user to
    /// reinstall.
    /// </remarks>
    private static void RepairMissingFolders(List<Models.BrowserExtension> list)
    {
        var broken = list.Where(e => string.IsNullOrEmpty(e.Folder) || !Directory.Exists(e.Folder)).ToList();
        if (broken.Count == 0 || !Directory.Exists(ExtensionsRoot)) return;

        var claimed = list.Select(e => e.Folder)
            .Where(f => !string.IsNullOrEmpty(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool repaired = false;

        foreach (var extension in broken)
        {
            foreach (string folder in SafeDirectories(ExtensionsRoot))
            {
                if (claimed.Contains(folder)) continue;
                if (!string.Equals(ReadName(folder), extension.Name, StringComparison.OrdinalIgnoreCase)) continue;

                extension.Folder = folder;
                claimed.Add(folder);
                repaired = true;

                Logger.Info("Re-found the folder for browser extension {Name}", extension.Name);
                break;
            }
        }

        if (repaired) SettingsManager.SaveSettings();
    }

    /// <summary>Folders still on disk, for the code that only needs paths.</summary>
    internal static List<string> InstalledFolders =>
        [.. Installed.Select(e => e.Folder).Where(f => !string.IsNullOrEmpty(f))];

    /// <summary>
    /// Brings the old folder-only list onto the record model.
    /// </summary>
    /// <remarks>
    /// Those entries become local-only: a bare folder carries no store id, so there is nothing
    /// another machine could fetch by. Re-adding such an extension from the store is what makes it
    /// portable — which the settings list says, rather than leaving it to be discovered.
    /// </remarks>
    private static void MigrateFolders(List<Models.BrowserExtension> list)
    {
        var folders = SettingsManager.Current.BrowserExtensionFolders;
        if (folders == null || folders.Count == 0) return;

        foreach (string folder in folders)
        {
            if (list.Any(e => string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new Models.BrowserExtension { Name = ReadName(folder), Folder = folder });
        }

        SettingsManager.Current.BrowserExtensionFolders = null;
        SettingsManager.SaveSettings();
    }

    private static void Remember(string folder, string id)
    {
        var list = Installed;

        var existing = list.FirstOrDefault(e => string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Id = id;
            existing.Name = ReadName(folder);
        }
        else
        {
            list.Add(new Models.BrowserExtension { Id = id, Name = ReadName(folder), Folder = folder });
        }

        SettingsManager.SaveSettings();
        AutoSyncService.NotifyLaunchersChanged();
    }

    /// <summary>Forgets an extension and deletes the copy that was made for it.</summary>
    /// <remarks>
    /// The removal is what travels: the synced list is authoritative, so an extension gone from it
    /// is uninstalled on every other machine when they next read it.
    /// </remarks>
    internal static void Uninstall(string folder)
    {
        Installed.RemoveAll(e => string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase));
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyLaunchersChanged();

        // Only ever a folder this service made, under its own root — never the source the user
        // picked from, which may be anywhere and is not ours to delete.
        if (folder.StartsWith(ExtensionsRoot, StringComparison.OrdinalIgnoreCase)) TryDelete(folder);
    }

    /// <summary>
    /// Makes this machine's extensions match a list that arrived over sync.
    /// </summary>
    /// <remarks>
    /// <para>Installs what is listed and missing, and removes what is here and not listed — the
    /// synced list is the authority, which is what makes an uninstall on one machine an uninstall on
    /// all of them.</para>
    /// <para><b>Local-only extensions are left entirely alone.</b> One added from a folder has no id
    /// to appear in the list under, so treating its absence as a removal would delete it on the very
    /// machine it was added to, every time that machine synced.</para>
    /// </remarks>
    internal static async Task ReconcileAsync(List<Models.BrowserExtension> wanted)
    {
        var local = Installed;
        bool changed = false;

        // Gone from the list, and reproducible — so its absence is a decision rather than a machine
        // that simply never had it.
        foreach (var extension in local.Where(e => e.IsPortable).ToList())
        {
            if (wanted.Any(w => string.Equals(w.Id, extension.Id, StringComparison.OrdinalIgnoreCase))) continue;

            Logger.Info("Removing browser extension {Name}: it was uninstalled on another machine", extension.Name);
            local.Remove(extension);
            if (!string.IsNullOrEmpty(extension.Folder)) TryDelete(extension.Folder);
            changed = true;
        }

        foreach (var extension in wanted.Where(w => w.IsPortable))
        {
            if (local.Any(e => string.Equals(e.Id, extension.Id, StringComparison.OrdinalIgnoreCase))) continue;

            Logger.Info("Fetching browser extension {Name} ({Id}): installed on another machine",
                extension.Name, extension.Id);

            if (await TryFetchFromStoreAsync(extension.Id) != null) changed = true;
        }

        if (changed) SettingsManager.SaveSettings();
    }

    /// <summary>What crosses to another machine: identity and name, never a path.</summary>
    internal static List<Models.BrowserExtension> Portable() =>
        [.. Installed.Where(e => e.IsPortable)
            .Select(e => new Models.BrowserExtension { Id = e.Id, Name = e.Name })];

    /// <summary>
    /// Fetches an extension by store id, the way the other machine originally got it.
    /// </summary>
    /// <remarks>
    /// The same endpoint an install from the store uses, with a query composed here rather than
    /// taken from a page — there is no page involved when the trigger is a sync download. The
    /// product version is what the update service matches against; a current one is enough for it
    /// to answer with the current package.
    /// </remarks>
    private static async Task<string?> TryFetchFromStoreAsync(string id)
    {
        string url = "https://clients2.google.com/service/update2/crx"
                   + "?response=redirect&acceptformat=crx3&prodversion=131.0.0.0"
                   + $"&x=id%3D{Uri.EscapeDataString(id)}%26installsource%3Dondemand%26uc";

        string temp = Path.Combine(Path.GetTempPath(), $"ll-extension-{Guid.NewGuid():N}.crx");

        try
        {
            using var http = new System.Net.Http.HttpClient();
            byte[] bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(temp, bytes);

            string? folder = await TryInstallFromArchiveAsync(temp);
            if (folder == null) return null;

            Remember(folder, id);
            return folder;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not fetch browser extension {Id}", id);
            return null;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { Logger.Debug(ex, "Could not delete {Path}", temp); }
        }
    }

    /// <summary>
    /// The name an unpacked extension goes by.
    /// </summary>
    /// <remarks>
    /// <para><b>A manifest name is often not a name.</b> A localised extension declares
    /// <c>"name": "__MSG_extName__"</c> and keeps the real string in
    /// <c>_locales/{default_locale}/messages.json</c> — so uBlock Origin Lite reads as
    /// <c>__MSG_extName__</c> until that indirection is followed. It showed that way in the settings
    /// list, in the header button's tooltip, and in the log.</para>
    /// <para>It also broke more than labels: <see cref="ApplyAsync"/> and the popup lookup both
    /// match against what WebView2 reports, and WebView2 reports the <em>resolved</em> name. Every
    /// comparison failed, so the extension was re-added to each profile in turn and its popup could
    /// never be found.</para>
    /// </remarks>
    internal static string ReadName(string folder)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(folder, "manifest.json"));
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (!json.RootElement.TryGetProperty("name", out var name) || name.GetString() is not { } text)
                return Path.GetFileName(folder);

            string? locale = json.RootElement.TryGetProperty("default_locale", out var declared)
                ? declared.GetString()
                : null;

            return ResolveMessage(folder, text, locale);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading the manifest name in {Folder} failed", folder);
        }

        return Path.GetFileName(folder);
    }

    /// <summary>
    /// Follows a <c>__MSG_key__</c> placeholder into the extension's own message catalogue.
    /// </summary>
    /// <remarks>
    /// The declared <c>default_locale</c> first, then the usual English folders — an extension whose
    /// default locale is missing from its own package is broken, but showing its folder name is a
    /// better answer than showing the placeholder.
    /// </remarks>
    private static string ResolveMessage(string folder, string value, string? defaultLocale)
    {
        const string prefix = "__MSG_";
        const string suffix = "__";

        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(suffix, StringComparison.Ordinal))
            return value;

        string key = value[prefix.Length..^suffix.Length];

        foreach (string? locale in new[] { defaultLocale, "en", "en_US", "en_GB" })
        {
            if (string.IsNullOrEmpty(locale)) continue;

            string path = Path.Combine(folder, "_locales", locale, "messages.json");
            if (!File.Exists(path)) continue;

            try
            {
                using var stream = File.OpenRead(path);
                using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

                // Keys are matched case-insensitively, which the extension platform also does.
                foreach (var entry in json.RootElement.EnumerateObject())
                {
                    if (!string.Equals(entry.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Value.TryGetProperty("message", out var message) && message.GetString() is { } resolved)
                        return resolved;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Reading {Path} failed", path);
            }
        }

        return Path.GetFileName(folder);
    }

    /// <summary>
    /// The extension's popup page and icon, from its manifest, or null when it has neither.
    /// </summary>
    /// <remarks>
    /// Read here because WebView2 will not tell us: <c>CoreWebView2BrowserExtension</c> carries an
    /// id, a name and an enabled flag, and nothing about browser actions. The manifest is the only
    /// source for "does this extension have a panel, and what is it called".
    /// </remarks>
    internal static (string Popup, string? Icon)? ReadAction(string folder)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(folder, "manifest.json"));
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            // MV3 calls it "action"; MV2 called it "browser_action". Both are read, because an
            // extension side-loaded from an old archive is exactly the case this has to survive.
            if (!json.RootElement.TryGetProperty("action", out var action) &&
                !json.RootElement.TryGetProperty("browser_action", out action))
                return null;

            if (!action.TryGetProperty("default_popup", out var popup) || popup.GetString() is not { } page)
                return null;

            string? icon = null;
            if (action.TryGetProperty("default_icon", out var icons))
            {
                icon = icons.ValueKind == JsonValueKind.String
                    ? icons.GetString()
                    // An object keyed by size: the largest is the one worth showing.
                    : icons.EnumerateObject()
                        .OrderByDescending(p => int.TryParse(p.Name, out int size) ? size : 0)
                        .Select(p => p.Value.GetString())
                        .FirstOrDefault();
            }

            return (page, icon);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading the manifest action in {Folder} failed", folder);
            return null;
        }
    }

    // ── Applying them to a profile ──────────────────────────────────

    /// <summary>
    /// Loads everything on the list into one profile, skipping what is already there.
    /// </summary>
    /// <remarks>
    /// Run as each browser is created, because an extension added while a launcher was closed has
    /// to arrive when it next opens — and because a launcher on a private profile has its own copy
    /// to install. Matching is by declared name: <c>AddBrowserExtensionAsync</c> gives back an id
    /// derived from the path, so there is nothing to compare a folder against until it is in.
    /// </remarks>
    internal static async Task ApplyAsync(CoreWebView2 core)
    {
        var folders = InstalledFolders;
        if (folders.Count == 0) return;

        try
        {
            var existing = await core.Profile.GetBrowserExtensionsAsync();
            var present = existing.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string folder in folders.ToList())
            {
                if (!Directory.Exists(folder))
                {
                    Logger.Warn("Extension folder {Folder} has gone; leaving it listed so the user can see why", folder);
                    continue;
                }

                if (present.Contains(ReadName(folder))) continue;

                await core.Profile.AddBrowserExtensionAsync(folder);
                Logger.Info("Loaded browser extension from {Folder}", folder);
            }
        }
        catch (Exception ex)
        {
            // Never fatal to opening a launcher: a page that loads without its ad blocker is worth
            // far more than a flyout that refuses to open.
            Logger.Warn(ex, "Applying browser extensions failed");
        }
    }

    /// <summary>Lists the unpacked extension folders, or nothing if the root cannot be read.</summary>
    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not list extensions in {Root}", root);
            return [];
        }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { Logger.Debug(ex, "Could not delete {Folder}", folder); }
    }
}
