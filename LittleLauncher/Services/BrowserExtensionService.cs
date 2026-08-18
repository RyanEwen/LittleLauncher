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
    internal static async Task<string?> InstallAsync(string sourcePath)
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

            Remember(unpacked);
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

    /// <summary>Folders that should be loaded into every web launcher's profile.</summary>
    internal static List<string> InstalledFolders =>
        SettingsManager.Current.BrowserExtensionFolders ??= [];

    private static void Remember(string folder)
    {
        if (InstalledFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) return;

        InstalledFolders.Add(folder);
        SettingsManager.SaveSettings();
    }

    /// <summary>Forgets an extension and deletes the copy that was made for it.</summary>
    internal static void Uninstall(string folder)
    {
        InstalledFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        SettingsManager.SaveSettings();

        // Only ever a folder this service made, under its own root — never the source the user
        // picked from, which may be anywhere and is not ours to delete.
        if (folder.StartsWith(ExtensionsRoot, StringComparison.OrdinalIgnoreCase)) TryDelete(folder);
    }

    /// <summary>The <c>name</c> an unpacked extension declares, for showing it in a list.</summary>
    internal static string ReadName(string folder)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(folder, "manifest.json"));
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (json.RootElement.TryGetProperty("name", out var name) && name.GetString() is { } text)
                return text;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading the manifest name in {Folder} failed", folder);
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

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { Logger.Debug(ex, "Could not delete {Folder}", folder); }
    }
}
