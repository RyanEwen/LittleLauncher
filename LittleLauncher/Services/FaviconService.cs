using LittleLauncher.Models;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LittleLauncher.Services;

/// <summary>
/// Downloads website favicons and caches them locally in AppData.
/// Also fetches website titles and extracts application metadata.
/// </summary>
internal static partial class FaviconService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Separate client that accepts any certificate — used only for title/icon
    // fetching from self-hosted services that often use self-signed certs.
    private static readonly HttpClient TolerantHttp = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LittleLauncher", "favicons");

    // ── Website metadata ────────────────────────────────────────────

    /// <summary>
    /// Downloads the favicon for <paramref name="url"/> and returns the local file path.
    /// Returns null if the download fails.
    /// </summary>
    public static async Task<string?> FetchAndCacheAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            string host = uri.Host;
            if (string.IsNullOrEmpty(host))
                return null;

            Directory.CreateDirectory(CacheDir);
            string localPath = Path.Combine(CacheDir, $"{host}.png");
            string baseUrl = $"{uri.Scheme}://{uri.Authority}";

            // 1. Try PWA manifest icons / HTML link icons from the page (preferred)
            byte[]? bytes = await TryFetchIconFromSiteAsync(baseUrl);

            // 2. Try Google's favicon service (works well for public sites)
            if (bytes == null || bytes.Length < 100)
                bytes = await TryDownloadAsync(Http,
                    $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=64");

            // 3. Fall back to /favicon.ico directly
            if (bytes == null || bytes.Length < 100)
                bytes = await TryDownloadAsync(TolerantHttp, $"{baseUrl}/favicon.ico");

            if (bytes == null || bytes.Length < 100)
                return null;

            await File.WriteAllBytesAsync(localPath, bytes);
            Logger.Info($"Favicon cached for {host} → {localPath}");
            return localPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to fetch favicon for {url}");
            return null;
        }
    }

    /// <summary>
    /// Fetches the site's HTML, looks for a PWA manifest link and HTML link icons.
    /// Returns the best icon bytes found, or null.
    /// </summary>
    private static async Task<byte[]?> TryFetchIconFromSiteAsync(string baseUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            using var response = await TolerantHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var buffer = new char[65536];
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);
            var html = new string(buffer, 0, read);

            // 1. Try PWA manifest icons (preferred — linked in HTML, or common paths)
            byte[]? manifestIconBytes = null;
            var manifestMatch = ManifestLinkRegex().Match(html);
            if (manifestMatch.Success)
            {
                var manifestHref = manifestMatch.Groups[1].Value;
                var manifestUrl = ResolveUrl(baseUrl, manifestHref);
                if (manifestUrl != null)
                    manifestIconBytes = await TryFetchManifestIconAsync(manifestUrl);
            }

            if (manifestIconBytes is not { Length: >= 100 })
            {
                foreach (var path in CommonManifestPaths)
                {
                    manifestIconBytes = await TryFetchManifestIconAsync(baseUrl + path);
                    if (manifestIconBytes is { Length: >= 100 }) break;
                }
            }

            if (manifestIconBytes is { Length: >= 100 })
                return manifestIconBytes;

            // 2. Fallback: HTML <link rel="icon"> tags
            var linkMatches = IconLinkRegex().Matches(html);
            string? bestHref = null;
            int bestSize = 0;

            foreach (Match m in linkMatches)
            {
                var tag = m.Value;
                var hrefMatch = HrefRegex().Match(tag);
                if (!hrefMatch.Success) continue;

                var href = System.Net.WebUtility.HtmlDecode(hrefMatch.Groups[1].Value);
                var sizeMatch = SizesRegex().Match(tag);
                int size = 0;
                if (sizeMatch.Success && int.TryParse(sizeMatch.Groups[1].Value, out var w))
                    size = w;

                // Prefer the largest icon, or the first one found
                if (size > bestSize || bestHref == null)
                {
                    bestHref = href;
                    bestSize = size;
                }
            }

            if (bestHref != null)
            {
                var iconUrl = ResolveUrl(baseUrl, bestHref);
                if (iconUrl != null)
                {
                    var bytes = await TryDownloadAsync(TolerantHttp, iconUrl);
                    if (bytes is { Length: >= 100 })
                        return bytes;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Failed to parse site HTML for icons: {baseUrl}");
        }

        return null;
    }

    /// <summary>
    /// Fetches a PWA manifest.json and downloads the largest icon listed.
    /// </summary>
    private static async Task<byte[]?> TryFetchManifestIconAsync(string manifestUrl)
    {
        try
        {
            using var response = await TolerantHttp.GetAsync(manifestUrl);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Array)
                return null;

            // Pick the largest icon (by sizes like "192x192")
            string? bestSrc = null;
            int bestSize = 0;

            foreach (var icon in icons.EnumerateArray())
            {
                if (!icon.TryGetProperty("src", out var srcProp))
                    continue;

                var src = srcProp.GetString();
                if (string.IsNullOrEmpty(src)) continue;

                int size = 0;
                if (icon.TryGetProperty("sizes", out var sizesProp))
                {
                    var sizesStr = sizesProp.GetString() ?? "";
                    var sizeMatch = Regex.Match(sizesStr, @"(\d+)x\d+");
                    if (sizeMatch.Success)
                        int.TryParse(sizeMatch.Groups[1].Value, out size);
                }

                if (size > bestSize || bestSrc == null)
                {
                    bestSrc = src;
                    bestSize = size;
                }
            }

            if (bestSrc == null)
                return null;

            // Manifest icon src can be relative to the manifest URL
            int lastSlash = manifestUrl.LastIndexOf('/');
            var baseForManifest = lastSlash >= 0 ? manifestUrl.Substring(0, lastSlash + 1) : manifestUrl;
            var iconUrl = ResolveUrl(baseForManifest, bestSrc);

            return iconUrl != null ? await TryDownloadAsync(TolerantHttp, iconUrl) : null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Failed to parse manifest: {manifestUrl}");
            return null;
        }
    }

    /// <summary>Downloads bytes from a URL. Returns null on failure.</summary>
    private static async Task<byte[]?> TryDownloadAsync(HttpClient client, string url)
    {
        try
        {
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch { return null; }
    }

    /// <summary>Resolves a potentially relative URL against a base.</summary>
    private static string? ResolveUrl(string baseUrl, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs))
            return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), href, out var resolved))
            return resolved.ToString();
        return null;
    }

    /// <summary>
    /// Fetches the name of a website or web app. Tries, in order:
    /// HTML &lt;title&gt;, meta tags, manifest link, common manifest paths.
    /// </summary>
    public static async Task<string?> FetchWebsiteTitleAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            string baseUrl = $"{uri.Scheme}://{uri.Authority}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            using var response = await TolerantHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var buffer = new char[65536];
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);
            var html = new string(buffer, 0, read);

            // 1. PWA manifest linked in HTML (preferred source)
            var manifestMatch = ManifestLinkRegex().Match(html);
            if (manifestMatch.Success)
            {
                var manifestHref = manifestMatch.Groups[1].Value;
                var manifestUrl = ResolveUrl(baseUrl, manifestHref);
                if (manifestUrl != null)
                {
                    var name = await TryFetchManifestNameAsync(manifestUrl);
                    if (name != null) return name;
                }
            }

            // 2. Try common manifest paths (many SPAs have one but don't link it)
            var manifestName = await TryFetchManifestNameFromCommonPaths(baseUrl);
            if (manifestName != null) return manifestName;

            // 3. HTML <title>
            var titleMatch = TitleRegex().Match(html);
            if (titleMatch.Success)
            {
                var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
                if (!string.IsNullOrEmpty(title))
                    return title;
            }

            // 4. Meta tags: application-name, og:title
            var metaMatch = AppNameMetaRegex().Match(html);
            if (metaMatch.Success)
            {
                var name = System.Net.WebUtility.HtmlDecode(metaMatch.Groups[1].Value).Trim();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to fetch title for {url}");
            return null;
        }
    }

    /// <summary>Fetches the "name" or "short_name" from a manifest URL.</summary>
    private static async Task<string?> TryFetchManifestNameAsync(string manifestUrl)
    {
        try
        {
            using var response = await TolerantHttp.GetAsync(manifestUrl);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var nameProp))
            {
                var name = nameProp.GetString()?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            if (root.TryGetProperty("short_name", out var shortProp))
            {
                var name = shortProp.GetString()?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    private static readonly string[] CommonManifestPaths =
        ["/manifest.json", "/manifest.webmanifest", "/site.webmanifest"];

    /// <summary>Tries common manifest paths to find the app name.</summary>
    private static async Task<string?> TryFetchManifestNameFromCommonPaths(string baseUrl)
    {
        foreach (var path in CommonManifestPaths)
        {
            var name = await TryFetchManifestNameAsync(baseUrl + path);
            if (name != null) return name;
        }
        return null;
    }

    /// <summary>
    /// Returns the cached favicon path for a URL if it exists, otherwise null.
    /// </summary>
    public static string? GetCachedPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        string localPath = Path.Combine(CacheDir, $"{uri.Host}.png");
        return File.Exists(localPath) ? localPath : null;
    }

    // ── Application metadata ────────────────────────────────────────

    /// <summary>
    /// Extracts the product name or file description from an executable.
    /// Falls back to the filename without extension.
    /// </summary>
    public static string? GetApplicationName(string exePath)
    {
        try
        {
            if (!File.Exists(exePath))
            {
                // Try resolving via PATH
                var resolved = ResolveExePath(exePath);
                if (resolved == null) return Path.GetFileNameWithoutExtension(exePath);
                exePath = resolved;
            }

            var info = FileVersionInfo.GetVersionInfo(exePath);
            return !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName
                 : !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription
                 : Path.GetFileNameWithoutExtension(exePath);
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(exePath);
        }
    }

    /// <summary>
    /// Extracts the icon from an executable and caches it as a PNG.
    /// Returns the cached path, or null on failure.
    /// </summary>
    public static string? GetApplicationIcon(string exePath)
    {
        try
        {
            var resolved = File.Exists(exePath) ? exePath : ResolveExePath(exePath);
            if (resolved == null) return null;

            Directory.CreateDirectory(CacheDir);
            string safeName = Path.GetFileNameWithoutExtension(resolved).ToLowerInvariant();
            string localPath = Path.Combine(CacheDir, $"app_{safeName}.png");

            using var icon = Icon.ExtractAssociatedIcon(resolved);
            if (icon == null) return null;

            using var bitmap = icon.ToBitmap();
            bitmap.Save(localPath, System.Drawing.Imaging.ImageFormat.Png);
            Logger.Info($"App icon cached for {resolved} → {localPath}");
            return localPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to extract icon for {exePath}");
            return null;
        }
    }

    private static string? ResolveExePath(string name)
    {
        // If it already looks like a rooted path, just check existence
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        // Search PATH
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
        foreach (var dir in paths)
        {
            var full = Path.Combine(dir.Trim(), name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // ── Batch icon pipeline ────────────────────────────────────────

    /// <summary>
    /// Fetches missing icons for a collection of launcher items.
    /// For websites: downloads cached favicons. For apps: extracts icons from executables.
    /// This is the single pipeline used by all import paths (manual add, sync, file import, settings restore).
    /// </summary>
    public static async Task FetchMissingItemIconsAsync(IEnumerable<LauncherItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsCategory || string.IsNullOrWhiteSpace(item.Path))
                continue;

            // Already has a valid local icon
            if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
                continue;

            try
            {
                string? iconPath = null;

                if (item.IsWebsite)
                {
                    iconPath = GetCachedPath(item.Path)
                        ?? await FetchAndCacheAsync(item.Path);
                }
                else
                {
                    iconPath = GetApplicationIcon(item.Path);
                }

                if (!string.IsNullOrEmpty(iconPath))
                    item.IconPath = iconPath;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Failed to fetch icon for {item.Path}");
            }
        }
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta[^>]*name=[""'](?:application-name|og:title)[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex AppNameMetaRegex();

    [GeneratedRegex(@"<link[^>]*rel=[""']manifest[""'][^>]*href=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestLinkRegex();

    [GeneratedRegex(@"<link[^>]*rel=[""'](?:icon|shortcut icon|apple-touch-icon)[""'][^>]*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex IconLinkRegex();

    [GeneratedRegex(@"href=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"sizes=[""'](\d+)x\d+[""']", RegexOptions.IgnoreCase)]
    private static partial Regex SizesRegex();
}
