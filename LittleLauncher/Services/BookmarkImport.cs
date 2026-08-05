using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>Represents a node in a browser bookmark tree — either a folder or a URL bookmark.</summary>
public sealed class BookmarkNode
{
    public string Name { get; init; } = "";
    public string? Url { get; init; }   // null for folders
    public List<BookmarkNode> Children { get; } = [];
    public bool IsFolder => Url == null;

    public int CountLeaves()
    {
        if (!IsFolder) return 1;
        return Children.Sum(c => c.CountLeaves());
    }
}

/// <summary>A single bookmark, flattened out of its folder tree for searching.</summary>
/// <param name="FolderPath">The folder chain it came from, e.g. "Bookmarks bar / Home".</param>
public sealed record FlatBookmark(string Name, string Url, string FolderPath)
{
    /// <summary>True when every whitespace-separated term appears in the name, URL or folder.</summary>
    /// <remarks>
    /// Term-wise rather than substring: bookmark titles and URLs put the useful words in a
    /// different order from the one the user remembers, so "cameras hass" has to find
    /// "Cameras" at hass.ryan-ewen.com.
    /// </remarks>
    public bool Matches(string[] terms)
    {
        foreach (string term in terms)
        {
            if (Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (Url.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (FolderPath.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Reads as a bookmark, not as a record.
    /// </summary>
    /// <remarks>
    /// A <c>ListView</c> row falls back to <c>ToString()</c> for its accessible name, so the
    /// compiler-generated one had screen readers announcing
    /// "FlatBookmark { Name = …, Url = …, FolderPath = … }" for every row. The rows render from a
    /// template either way; this is purely what assistive tech and automation hear.
    /// </remarks>
    public override string ToString() => $"{Name} — {Url}";
}

/// <summary>
/// Reads bookmark trees out of installed browser profiles for the "Import bookmarks"
/// flow. Chromium profiles store JSON; Gecko profiles store either an LZ4-compressed
/// JSONLZ4 backup or a places.sqlite database.
/// </summary>
public static class BookmarkImport
{
    /// <summary>
    /// Reads a profile's bookmark tree, picking the reader that matches the browser's engine.
    /// </summary>
    /// <remarks>
    /// The engine switch and the profile-path rule below it are easy to get subtly wrong, so both
    /// callers — the multi-select import flow and the single-bookmark picker — share this rather
    /// than repeating them.
    /// </remarks>
    public static List<BookmarkNode> ReadBookmarks(KnownBrowser browser, BrowserProfile profile)
    {
        // Gecko profiles carry the full path in DirectoryName; Chromium stores a folder name
        // relative to the user-data directory.
        string profileDir = browser.Engine == BrowserEngine.Gecko
            ? profile.DirectoryName
            : Path.Combine(browser.ProfileDataDir, profile.DirectoryName);

        return browser.Engine == BrowserEngine.Gecko
            ? ReadGeckoBookmarks(profileDir)
            : ReadChromiumBookmarks(profileDir);
    }

    /// <summary>
    /// Flattens a bookmark tree to its URL leaves, each tagged with the folder path it came from.
    /// </summary>
    /// <remarks>
    /// A flat list is what makes bookmarks searchable: the folder a bookmark lives in becomes a
    /// piece of text to match and to show as context, instead of a level the user has to expand.
    /// </remarks>
    public static List<FlatBookmark> Flatten(IEnumerable<BookmarkNode> roots)
    {
        var results = new List<FlatBookmark>();

        void Walk(BookmarkNode node, string path)
        {
            if (!node.IsFolder)
            {
                results.Add(new FlatBookmark(node.Name, node.Url!, path));
                return;
            }

            string childPath = string.IsNullOrEmpty(path) ? node.Name : $"{path} / {node.Name}";
            foreach (var child in node.Children)
                Walk(child, childPath);
        }

        foreach (var root in roots)
            Walk(root, "");

        return results;
    }

    /// <summary>Reads bookmarks from a Chromium profile directory, returning a root folder tree.</summary>
    public static List<BookmarkNode> ReadChromiumBookmarks(string profileDir)
    {
        var result = new List<BookmarkNode>();
        string path = Path.Combine(profileDir, "Bookmarks");
        // Newer Chrome versions store bookmarks in "AccountBookmarks" instead of "Bookmarks"
        if (!File.Exists(path))
            path = Path.Combine(profileDir, "AccountBookmarks");
        if (!File.Exists(path)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("roots", out var roots)) return result;

            static BookmarkNode? ParseNode(JsonElement el)
            {
                if (!el.TryGetProperty("type", out var tp)) return null;
                string type = tp.GetString() ?? "";
                string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                if (type == "url")
                {
                    string url = el.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(url) ||
                        url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                        return null;
                    return new BookmarkNode { Name = string.IsNullOrEmpty(name) ? url : name, Url = url };
                }
                if (type == "folder")
                {
                    var folder = new BookmarkNode { Name = name };
                    if (el.TryGetProperty("children", out var children))
                        foreach (var child in children.EnumerateArray())
                        {
                            var parsed = ParseNode(child);
                            if (parsed != null) folder.Children.Add(parsed);
                        }
                    return folder;
                }
                return null;
            }

            var rootSections = new[] {
                ("bookmark_bar", "Bookmarks Bar"),
                ("other",        "Other Bookmarks"),
                ("synced",       "Synced Bookmarks"),
            };
            foreach (var (key, displayName) in rootSections)
            {
                if (!roots.TryGetProperty(key, out var rootEl)) continue;
                var parsed = ParseNode(rootEl);
                if (parsed == null || parsed.CountLeaves() == 0) continue;
                // Wrap using a friendly top-level name instead of the browser's internal label
                var container = new BookmarkNode { Name = displayName };
                foreach (var child in parsed.Children) container.Children.Add(child);
                result.Add(container);
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Reads bookmarks from a Firefox/Gecko profile directory using the most recent
    /// auto-generated jsonlz4 backup, falling back to places.sqlite if no backups exist.
    /// </summary>
    public static List<BookmarkNode> ReadGeckoBookmarks(string profileDir)
    {
        var result = new List<BookmarkNode>();

        // Try jsonlz4 backup first
        string backupDir = Path.Combine(profileDir, "bookmarkbackups");
        string? latestFile = Directory.Exists(backupDir)
            ? Directory.GetFiles(backupDir, "*.jsonlz4")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        // Fall back to places.sqlite when no jsonlz4 backups exist
        if (latestFile == null)
            return ReadGeckoBookmarksFromSqlite(profileDir);


        try
        {
            byte[] raw = File.ReadAllBytes(latestFile);
            // mozLz40\0 magic (8 bytes) + original size little-endian int32 (4 bytes) + LZ4 block data
            if (raw.Length < 12 ||
                raw[0] != 'm' || raw[1] != 'o' || raw[2] != 'z' || raw[3] != 'L' ||
                raw[4] != 'z' || raw[5] != '4' || raw[6] != '0' || raw[7] != 0)
                return result;

            int origSize = BitConverter.ToInt32(raw, 8);
            if (origSize <= 0 || origSize > 64 * 1024 * 1024) return result;

            byte[] json = DecompressLz4Block(raw.AsSpan(12), origSize);
            using var doc = JsonDocument.Parse(json);

            static BookmarkNode? ParseGeckoNode(JsonElement el)
            {
                if (!el.TryGetProperty("type", out var tp)) return null;
                string type = tp.GetString() ?? "";
                string name = el.TryGetProperty("title", out var n) ? n.GetString() ?? "" : "";

                if (type == "text/x-moz-place")
                {
                    string uri = el.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(uri) ||
                        uri.StartsWith("place:", StringComparison.OrdinalIgnoreCase) ||
                        uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                        return null;
                    return new BookmarkNode { Name = string.IsNullOrEmpty(name) ? uri : name, Url = uri };
                }
                if (type == "text/x-moz-place-container")
                {
                    var folder = new BookmarkNode { Name = name };
                    if (el.TryGetProperty("children", out var children))
                        foreach (var child in children.EnumerateArray())
                        {
                            var parsed = ParseGeckoNode(child);
                            if (parsed != null) folder.Children.Add(parsed);
                        }
                    return folder;
                }
                return null;  // separator or unrecognised type
            }

            // Root is the placesRoot container; its direct children are the top-level sections
            if (doc.RootElement.TryGetProperty("children", out var topChildren))
                foreach (var child in topChildren.EnumerateArray())
                {
                    var node = ParseGeckoNode(child);
                    if (node != null && node.CountLeaves() > 0)
                        result.Add(node);
                }
        }
        catch { }

        return result;
    }

    /// <summary>Decompresses an LZ4 block-format payload (no frame header).</summary>
    internal static byte[] DecompressLz4Block(ReadOnlySpan<byte> src, int outputSize)
    {
        var dst = new byte[outputSize];
        int sPos = 0, dPos = 0;

        while (sPos < src.Length && dPos < outputSize)
        {
            byte token = src[sPos++];

            // Literals
            int litLen = (token >> 4) & 0xF;
            if (litLen == 15)
            {
                byte extra;
                do { extra = src[sPos++]; litLen += extra; } while (extra == 255);
            }
            src.Slice(sPos, litLen).CopyTo(dst.AsSpan(dPos));
            sPos += litLen;
            dPos += litLen;

            if (sPos >= src.Length) break;  // final sequence has no match portion

            // Match offset (little-endian 16-bit)
            int offset = src[sPos] | (src[sPos + 1] << 8);
            sPos += 2;

            // Match length: base is 4, plus token low nibble, plus optional extension bytes
            int matchLen = 4 + (token & 0xF);
            if ((token & 0xF) == 15)
            {
                byte extra;
                do { extra = src[sPos++]; matchLen += extra; } while (extra == 255);
            }

            // Copy match — may overlap with current write position, so byte-by-byte
            int matchSrc = dPos - offset;
            for (int i = 0; i < matchLen; i++)
                dst[dPos++] = dst[matchSrc++];
        }

        return dst;
    }

    /// <summary>Reads bookmarks from a Firefox/Gecko places.sqlite database.</summary>
    public static List<BookmarkNode> ReadGeckoBookmarksFromSqlite(string profileDir)
    {
        var result = new List<BookmarkNode>();
        string dbPath = Path.Combine(profileDir, "places.sqlite");
        if (!File.Exists(dbPath)) return result;

        // Copy to temp to avoid SQLite lock conflicts with a running browser
        string tempDb = Path.Combine(Path.GetTempPath(), $"ll_places_{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(dbPath, tempDb, overwrite: true);

            using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly");
            conn.Open();

            // Build folder hierarchy from moz_bookmarks + moz_places
            // type=1 → bookmark, type=2 → folder; parent links form the tree
            // Well-known root folder IDs: 1=Places root, 2=Bookmarks Menu, 3=Toolbar, 4=Tags, 5=Other Bookmarks
            var folders = new Dictionary<long, BookmarkNode>();
            var childrenMap = new Dictionary<long, List<(long id, int type, string title, string? url, long parent)>>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT b.id, b.type, COALESCE(b.title, ''), p.url, b.parent, b.position
                    FROM moz_bookmarks b
                    LEFT JOIN moz_places p ON b.fk = p.id
                    WHERE b.type IN (1, 2)
                    ORDER BY b.position
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    int type = reader.GetInt32(1);
                    string title = reader.GetString(2);
                    string? url = reader.IsDBNull(3) ? null : reader.GetString(3);
                    long parent = reader.GetInt64(4);

                    if (type == 2) // folder
                        folders[id] = new BookmarkNode { Name = title };

                    if (!childrenMap.TryGetValue(parent, out var list))
                    {
                        list = [];
                        childrenMap[parent] = list;
                    }
                    list.Add((id, type, title, url, parent));
                }
            }

            // Recursively build tree
            void BuildChildren(BookmarkNode parentNode, long parentId)
            {
                if (!childrenMap.TryGetValue(parentId, out var children)) return;
                foreach (var (id, type, title, url, _) in children)
                {
                    if (type == 2 && folders.TryGetValue(id, out var folder))
                    {
                        BuildChildren(folder, id);
                        if (folder.CountLeaves() > 0)
                            parentNode.Children.Add(folder);
                    }
                    else if (type == 1 && !string.IsNullOrEmpty(url))
                    {
                        if (url.StartsWith("place:", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string name = string.IsNullOrEmpty(title) ? url : title;
                        parentNode.Children.Add(new BookmarkNode { Name = name, Url = url });
                    }
                }
            }

            // Well-known top-level folders under the Places root (id=1)
            var topLevel = new (long id, string displayName)[]
            {
                (2, "Bookmarks Menu"),
                (3, "Bookmarks Toolbar"),
                (5, "Other Bookmarks"),
            };

            foreach (var (folderId, displayName) in topLevel)
            {
                if (!folders.TryGetValue(folderId, out var folder)) continue;
                folder = new BookmarkNode { Name = displayName };
                BuildChildren(folder, folderId);
                if (folder.CountLeaves() > 0)
                    result.Add(folder);
            }
        }
        catch { }
        finally
        {
            try { File.Delete(tempDb); } catch { }
        }

        return result;
    }
}
