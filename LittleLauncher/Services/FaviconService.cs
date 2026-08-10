using System.Runtime.InteropServices;
using LittleLauncher.Classes;
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
        => await FetchAndCacheCoreAsync(url, allowGoogleFaviconFallback: true, preferCachedResult: true);

    private static async Task<string?> FetchAndCacheCoreAsync(string url, bool allowGoogleFaviconFallback, bool preferCachedResult)
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
            if (preferCachedResult && File.Exists(localPath))
                return localPath;

            // 1. Try PWA manifest icons / HTML link icons from the page (preferred)
            byte[]? bytes = RasterOrNull(await TryFetchIconFromSiteAsync(url));

            // 2. Try Google's favicon service (works well for public sites)
            if (allowGoogleFaviconFallback && (bytes == null || bytes.Length < 100))
                bytes = RasterOrNull(await TryDownloadAsync(Http,
                    $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=64"));

            // 3. Fall back to /favicon.ico directly
            if (bytes == null || bytes.Length < 100)
                bytes = RasterOrNull(await TryDownloadAsync(TolerantHttp, $"{baseUrl}/favicon.ico"));

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

    /// <summary>Returns <paramref name="bytes"/> only if it's a raster image the app can render.</summary>
    private static byte[]? RasterOrNull(byte[]? bytes) => IsRasterImage(bytes) ? bytes : null;

    /// <summary>
    /// True when the bytes start with a known raster image signature (PNG, JPEG, GIF, BMP,
    /// ICO/CUR, WEBP). Rejects SVG and other vector/markup payloads, which WinUI's
    /// <c>BitmapImage</c> cannot decode — saving one as <c>.png</c> yields a blank icon, so
    /// callers should fall back (e.g. to the shell-extracted icon for PWAs).
    /// </summary>
    private static bool IsRasterImage(byte[]? b)
    {
        if (b == null || b.Length < 4) return false;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;      // PNG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;                       // JPEG
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;                       // GIF
        if (b[0] == 0x42 && b[1] == 0x4D) return true;                                       // BMP
        if (b[0] == 0x00 && b[1] == 0x00 && (b[2] == 0x01 || b[2] == 0x02) && b[3] == 0x00)  // ICO / CUR
            return true;
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46   // "RIFF"
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;  // "WEBP"
        return false;
    }

    /// <summary>
    /// Fetches the site's HTML, looks for a PWA manifest link and HTML link icons.
    /// Returns the best icon bytes found, or null.
    /// </summary>
    private static async Task<byte[]?> TryFetchIconFromSiteAsync(string pageUrl)
    {
        try
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
                return null;

            string baseUrl = $"{pageUri.Scheme}://{pageUri.Authority}";

            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            using var response = await TolerantHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;

            // Auth-gated apps like Google Keep / Tasks often redirect to accounts.google.com.
            // Treat off-origin HTML as unusable so PWAs can fall back to their installed shell icon.
            if (response.RequestMessage?.RequestUri is Uri finalUri
                && !string.Equals(finalUri.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

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
                else if (tag.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase))
                {
                    // An apple-touch-icon rarely declares its size and is 180px by convention.
                    // Scoring it 0 let a 16px shortcut icon that merely came first win, which is
                    // one way a site with a perfectly good large icon still produced a soft one.
                    size = 180;
                }

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
            Logger.Debug(ex, $"Failed to parse site HTML for icons: {pageUrl}");
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

    // ── PWA icon extraction ─────────────────────────────────────

    /// <summary>
    /// Extracts the icon for a shell:AppsFolder app (e.g., a PWA) using IShellItemImageFactory.
    /// Returns the cached PNG path, or null on failure.
    /// </summary>
    public static string? GetPwaIconFromShell(string aumid)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string safeName = string.Join("_", aumid.Split(Path.GetInvalidFileNameChars()));
            string localPath = Path.Combine(CacheDir, $"pwa_{safeName}.png");

            var IID_IShellItemImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            int hr = NativeMethods.SHCreateItemFromParsingName(
                $@"shell:AppsFolder\{aumid}", IntPtr.Zero,
                ref IID_IShellItemImageFactory, out var factory);
            if (hr != 0 || factory == null)
                return null;

            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                var size = new NativeMethods.SIZE { cx = 64, cy = 64 };
                // GetImage returns E_PENDING while the shell rasterizes the icon on a
                // background thread (uncached PWA/Store tiles). The list picker requests
                // 32px, so a warm 32px cache does NOT satisfy this 64px request — retry
                // until it's ready, otherwise an uncached PWA gets no persisted icon.
                const int E_PENDING = unchecked((int)0x8000000A);
                for (int attempt = 0; ; attempt++)
                {
                    hr = factory.GetImage(size, 0, out hBitmap);
                    if (hr == 0 && hBitmap != IntPtr.Zero) break;
                    if (hr == E_PENDING && attempt < 15) { System.Threading.Thread.Sleep(40); continue; }
                    return null;
                }

                NativeMethods.GetObject(hBitmap,
                    Marshal.SizeOf<NativeMethods.BITMAP>(), out var bm);
                int width = bm.bmWidth;
                int height = bm.bmHeight;
                if (width <= 0 || height <= 0)
                    return null;

                // BitBlt the source HBITMAP into a top-down 32bpp DIB section
                // we control. This avoids orientation ambiguity from the source
                // (DDB vs DIB, bottom-up vs top-down).
                var bmi = new NativeMethods.BITMAPINFO
                {
                    bmiHeader = new NativeMethods.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // negative = top-down
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0 // BI_RGB
                    }
                };

                IntPtr hdcScreen = IntPtr.Zero;
                IntPtr hdcSrc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
                IntPtr hdcDst = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
                IntPtr hDib = NativeMethods.CreateDIBSection(
                    hdcDst, ref bmi, NativeMethods.DIB_RGB_COLORS,
                    out IntPtr dibBits, IntPtr.Zero, 0);

                if (hDib == IntPtr.Zero || dibBits == IntPtr.Zero)
                {
                    if (hDib != IntPtr.Zero) NativeMethods.DeleteObject(hDib);
                    NativeMethods.DeleteDC(hdcSrc);
                    NativeMethods.DeleteDC(hdcDst);
                    // Fall back to Bitmap.FromHbitmap
                    using var fallback = Bitmap.FromHbitmap(hBitmap);
                    fallback.Save(localPath, System.Drawing.Imaging.ImageFormat.Png);
                    Logger.Info($"PWA icon cached (fallback) for {aumid} → {localPath}");
                    return localPath;
                }

                IntPtr oldSrc = NativeMethods.SelectObject(hdcSrc, hBitmap);
                IntPtr oldDst = NativeMethods.SelectObject(hdcDst, hDib);
                NativeMethods.BitBlt(hdcDst, 0, 0, width, height,
                    hdcSrc, 0, 0, NativeMethods.SRCCOPY);
                NativeMethods.SelectObject(hdcSrc, oldSrc);
                NativeMethods.SelectObject(hdcDst, oldDst);
                NativeMethods.DeleteDC(hdcSrc);
                NativeMethods.DeleteDC(hdcDst);

                // dibBits now has top-down 32bpp pixels; copy into a Bitmap
                using var bitmap = new Bitmap(width, height,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var rect = new Rectangle(0, 0, width, height);
                var data = bitmap.LockBits(rect,
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    int stride = width * 4;
                    unsafe
                    {
                        byte* src = (byte*)dibBits;
                        byte* dst = (byte*)data.Scan0;
                        for (int y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(
                                src + y * stride,
                                dst + y * data.Stride,
                                stride, stride);
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }

                NativeMethods.DeleteObject(hDib);
                bitmap.Save(localPath, System.Drawing.Imaging.ImageFormat.Png);

                Logger.Info($"PWA icon cached for {aumid} → {localPath}");
                return localPath;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                    NativeMethods.DeleteObject(hBitmap);
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to extract shell icon for PWA {aumid}");
            return null;
        }
    }

    private static bool IsShellExtractedPwaIconPath(string? path)
        => !string.IsNullOrEmpty(path)
           && Path.GetFileName(path).StartsWith("pwa_", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetPwaUrl(string aumid)
    {
        var match = Regex.Match(aumid,
            @"^([\w][\w.-]*\.[a-zA-Z]{2,})-[A-Fa-f0-9]+_[a-z0-9]+!App$");
        return match.Success ? $"https://{match.Groups[1].Value}/" : null;
    }

    /// <summary>
    /// For Chromium PWAs, prefer the site's own icon/manifest asset because the shell image
    /// factory often returns a softer rasterized bitmap than the original web icon.
    /// </summary>
    public static async Task<string?> GetBestPwaIconAsync(string aumid)
    {
        string? pwaUrl = TryGetPwaUrl(aumid);
        if (!string.IsNullOrEmpty(pwaUrl))
        {
            try
            {
                string? webIcon = await FetchAndCacheCoreAsync(pwaUrl, allowGoogleFaviconFallback: false, preferCachedResult: false);
                if (!string.IsNullOrEmpty(webIcon))
                    return webIcon;
            }
            catch (Exception ex)
            {
                // A self-hosted PWA domain may be unreachable / refuse the request;
                // never let that prevent the shell-icon fallback below.
                Logger.Debug(ex, $"PWA web icon fetch failed for {pwaUrl}");
            }
        }

        // Off the UI thread: the shell extraction may sleep-retry on E_PENDING.
        return await Task.Run(() => GetPwaIconFromShell(aumid));
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
            if (item.IsGroup)
            {
                await FetchMissingItemIconsAsync(item.Children);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Path))
                continue;

            bool hasExistingIcon = !string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath);
            bool shouldUpgradeShellPwaIcon = item.IsPwa && hasExistingIcon && IsShellExtractedPwaIconPath(item.IconPath);

            // Already has a valid local icon.
            // Existing shell-extracted PWA icons are upgraded opportunistically to the site's own icon.
            if (hasExistingIcon && !shouldUpgradeShellPwaIcon)
                continue;

            try
            {
                string? iconPath = null;

                if (item.IsWebsite)
                {
                    iconPath = GetCachedPath(item.Path)
                        ?? await FetchAndCacheAsync(item.Path);
                }
                else if (item.IsPwa)
                {
                    iconPath = await GetBestPwaIconAsync(item.Path);
                }
                else if (item.Path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
                {
                    string aumid = item.Path[@"shell:AppsFolder\".Length..];
                    iconPath = await GetBestPwaIconAsync(aumid);
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

    // ── Stale icon refresh ─────────────────────────────────────────

    /// <summary>How old a cached auto-fetched icon may get before it is re-fetched.</summary>
    public static readonly TimeSpan IconMaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// True when the path points into the favicon cache directory — i.e. the icon was
    /// auto-fetched (website favicon, extracted app icon, or PWA icon). User-chosen
    /// custom images live elsewhere (the "icons" folder or an arbitrary path) and are
    /// never considered auto-fetched.
    /// </summary>
    public static bool IsAutoFetchedIconPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return string.Equals(dir, Path.GetFullPath(CacheDir), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-fetches auto-fetched icons that are older than <see cref="IconMaxAge"/>.
    /// Items with custom icons (outside the favicon cache) are never touched.
    /// A failed fetch keeps the existing cached file, so icons only ever improve.
    /// Returns true when any icon file was rewritten or any IconPath changed.
    /// </summary>
    /// <param name="visitedPaths">
    /// Tracks cache files already examined this pass so items sharing a favicon
    /// (same host) don't trigger duplicate downloads. Pass one set across launchers.
    /// </param>
    public static async Task<bool> RefreshStaleItemIconsAsync(
        IEnumerable<LauncherItem> items, HashSet<string>? visitedPaths = null)
    {
        visitedPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                changed |= await RefreshStaleItemIconsAsync(item.Children, visitedPaths);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Path) || !IsAutoFetchedIconPath(item.IconPath))
                continue;

            // Dead paths are handled by the missing-icon pipeline, not here.
            if (!File.Exists(item.IconPath))
                continue;

            if (!visitedPaths.Add(item.IconPath))
                continue;

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(item.IconPath) < IconMaxAge)
                continue;

            try
            {
                string? newPath;
                if (item.IsWebsite)
                {
                    newPath = await FetchAndCacheCoreAsync(item.Path,
                        allowGoogleFaviconFallback: true, preferCachedResult: false);
                }
                else if (item.IsPwa)
                {
                    newPath = await GetBestPwaIconAsync(item.Path);
                }
                else if (item.Path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
                {
                    newPath = await GetBestPwaIconAsync(item.Path[@"shell:AppsFolder\".Length..]);
                }
                else
                {
                    newPath = GetApplicationIcon(item.Path);
                }

                if (string.IsNullOrEmpty(newPath))
                    continue;

                Logger.Info($"Refreshed stale icon for {item.Path} → {newPath}");
                changed = true;
                if (!string.Equals(newPath, item.IconPath, StringComparison.OrdinalIgnoreCase))
                    item.IconPath = newPath;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Failed to refresh icon for {item.Path}");
            }
        }

        return changed;
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
