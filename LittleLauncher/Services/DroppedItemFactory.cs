using LittleLauncher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DataPackageView = global::Windows.ApplicationModel.DataTransfer.DataPackageView;
using IStorageItem = global::Windows.Storage.IStorageItem;
using StandardDataFormats = global::Windows.ApplicationModel.DataTransfer.StandardDataFormats;

namespace LittleLauncher.Services;

/// <summary>
/// Turns a shell or browser drag payload into <see cref="LauncherItem"/>s, so files,
/// shortcuts and links can be dropped straight into a launcher's flyout.
/// </summary>
/// <remarks>
/// Nothing here touches the network or the shell icon cache — <see cref="CreateItemsAsync"/>
/// is deliberately cheap so the drop completes immediately and the source app is released.
/// The slow work lives in <see cref="EnrichAsync"/>, which the caller runs afterwards.
/// </remarks>
internal static class DroppedItemFactory
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // Segoe Fluent Icons fallbacks, used until EnrichAsync finds a real icon.
    private const string AppGlyph = "";       // Open
    private const string WebGlyph = "";       // Globe
    private const string FolderGlyph = "";    // Folder
    private const string DocumentGlyph = "";  // Page

    /// <summary>
    /// Whether a payload looks droppable. Called from <c>DragOver</c>, which is synchronous —
    /// only the format list can be inspected there, never the data itself.
    /// </summary>
    /// <remarks>
    /// Text is deliberately <b>not</b> accepted on its own, even though <see cref="CreateItemsAsync"/>
    /// can read a URL out of it. The Windows 11 Start Menu offers the app's name as text and
    /// nothing else usable, so accepting text meant every Start Menu drag showed an "Add to
    /// launcher" cursor and then silently did nothing. A drop cursor is a promise.
    /// </remarks>
    public static bool CanAccept(DataPackageView data)
    {
        try
        {
            return data.Contains(StandardDataFormats.StorageItems)
                || data.Contains(StandardDataFormats.WebLink);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Drop payload could not be inspected");
            return false;
        }
    }

    /// <summary>
    /// Builds launcher items from a drag payload. Returns an empty list when nothing in the
    /// payload maps to something launchable (e.g. plain text that isn't a link).
    /// </summary>
    public static async Task<List<LauncherItem>> CreateItemsAsync(DataPackageView data)
    {
        var items = new List<LauncherItem>();

        if (data.Contains(StandardDataFormats.StorageItems))
        {
            IReadOnlyList<IStorageItem> dropped;
            try
            {
                dropped = await data.GetStorageItemsAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not read dropped storage items");
                dropped = [];
            }

            foreach (var entry in dropped)
            {
                // Store apps and other virtual shell targets have no filesystem path, so
                // there is nothing to launch. Dropping those needs the raw shell PIDL.
                if (string.IsNullOrEmpty(entry.Path))
                {
                    Logger.Info($"Skipped dropped item with no filesystem path: {entry.Name}");
                    continue;
                }

                var item = FromPath(entry.Path);
                if (item != null)
                    items.Add(item);
            }

            if (items.Count > 0)
                return items;
        }

        string? url = null;

        if (data.Contains(StandardDataFormats.WebLink))
        {
            try { url = (await data.GetWebLinkAsync())?.ToString(); }
            catch (Exception ex) { Logger.Debug(ex, "Could not read dropped web link"); }
        }

        // Fallback for a source that advertises a web link but can't produce it; text is
        // never the reason a drop was accepted (see CanAccept).
        if (string.IsNullOrEmpty(url) && data.Contains(StandardDataFormats.Text))
        {
            try { url = FirstUrl(await data.GetTextAsync()); }
            catch (Exception ex) { Logger.Debug(ex, "Could not read dropped text"); }
        }

        if (!string.IsNullOrEmpty(url))
            items.Add(CreateWebsite(url, null));

        return items;
    }

    /// <summary>
    /// Fills in the parts that need the network or the shell: website titles and icons.
    /// Safe to run after the items are already visible — they update in place.
    /// </summary>
    public static async Task EnrichAsync(IReadOnlyList<LauncherItem> items)
    {
        foreach (var item in items)
        {
            // Only replace the host placeholder — a name taken from a .url filename is
            // the user's own and outranks whatever <title> the site happens to carry.
            if (!item.IsWebsite || !IsHostPlaceholder(item))
                continue;

            try
            {
                var title = await FaviconService.FetchWebsiteTitleAsync(item.Path);
                if (!string.IsNullOrWhiteSpace(title))
                    item.Name = title;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, $"Could not fetch a title for {item.Path}");
            }
        }

        await FaviconService.FetchMissingItemIconsAsync(items);
    }

    /// <summary>Maps one dropped filesystem path to a launcher item, or null if unusable.</summary>
    private static LauncherItem? FromPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return new LauncherItem(NameFromPath(path), path, FolderGlyph);

            if (!File.Exists(path))
                return null;

            var extension = Path.GetExtension(path);

            // An internet shortcut is a website wearing a file's clothes.
            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                var url = ReadInternetShortcut(path);
                return url == null ? null : CreateWebsite(url, NameFromPath(path));
            }

            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return FromShortcut(path);

            bool isExecutable =
                extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);

            // An exe's product name ("Google Chrome") beats its filename ("chrome").
            var name = isExecutable
                ? FaviconService.GetApplicationName(path) ?? NameFromPath(path)
                : NameFromPath(path);

            return new LauncherItem(name, path, isExecutable ? AppGlyph : DocumentGlyph);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Could not build a launcher item for {path}");
            return null;
        }
    }

    /// <summary>
    /// Builds an item from a <c>.lnk</c>. Resolving to the real target keeps a dropped Start
    /// Menu shortcut identical to the same app picked from the item editor's app list, and
    /// survives the shortcut later being deleted. Shortcuts the shell can't resolve to a file
    /// (Store apps, control-panel targets) keep the <c>.lnk</c> itself, which still launches.
    /// </summary>
    private static LauncherItem FromShortcut(string lnkPath)
    {
        var (target, arguments) = AppCatalog.ResolveShortcut(lnkPath);
        bool resolved = !string.IsNullOrEmpty(target) && File.Exists(target);

        return new LauncherItem(
            // The shortcut's filename is what the user sees in the Start Menu, so it beats
            // the target exe's product name here.
            NameFromPath(lnkPath),
            resolved ? target! : lnkPath,
            AppGlyph,
            arguments: resolved ? arguments ?? "" : "");
    }

    private static LauncherItem CreateWebsite(string url, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

        return new LauncherItem(name!, url, WebGlyph, isWebsite: true);
    }

    /// <summary>Reads the <c>URL=</c> line out of an <c>.url</c> internet shortcut.</summary>
    private static string? ReadInternetShortcut(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                var url = line[4..].Trim();
                if (!string.IsNullOrEmpty(url))
                    return url;
            }
        }

        return null;
    }

    /// <summary>The first line of dropped text, if it is an http(s) URL.</summary>
    private static string? FirstUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var first = text.Split('\n')[0].Trim();

        return Uri.TryCreate(first, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? first
            : null;
    }

    private static bool IsHostPlaceholder(LauncherItem item) =>
        Uri.TryCreate(item.Path, UriKind.Absolute, out var uri)
        && string.Equals(item.Name, uri.Host, StringComparison.OrdinalIgnoreCase);

    private static string NameFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        // A drive root ("D:\") has no filename — fall back to the path itself.
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
