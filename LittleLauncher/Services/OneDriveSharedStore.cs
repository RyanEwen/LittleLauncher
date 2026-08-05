using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Services;

/// <summary>
/// Shares one launcher through the owner's OneDrive, reached by everyone else via a share link.
/// </summary>
/// <remarks>
/// <para><b>Why not the app folder the global sync uses:</b> it is private per-user storage with
/// no mechanism to grant another person access, so a launcher published there would be invisible
/// to the person it was shared with. Sharing therefore writes to an ordinary Drive folder and
/// mints an anonymous editable link — which is exactly why it needs
/// <see cref="OneDriveFileStore.SharingScope"/> and why that is requested only when someone first
/// shares this way, never at ordinary sign-in.</para>
/// <para><b>The two sides are asymmetric and that is the point.</b> The owner knows a file; the
/// subscriber knows only a link, resolves it against their own account, and never learns where in
/// the owner's drive it lives. Both then read and write the same item, so 2-way works.</para>
/// </remarks>
public static class OneDriveSharedStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string Provider = "onedrive";
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";

    /// <summary>Where shared launchers are published in the owner's drive.</summary>
    private const string ShareFolder = "Apps/Little Launcher Shared";

    private static readonly OneDriveFileStore Store = new();

    /// <summary>True when the account has granted the wider permission sharing needs.</summary>
    public static bool HasConsent => Store.HasSharingConsent;

    /// <summary>Ask for the sharing permission. Safe to call when it is already held.</summary>
    public static Task<(bool Success, string Message)> RequestConsentAsync() =>
        Store.RequestSharingConsentAsync();

    // ── Owner ───────────────────────────────────────────────────────

    /// <summary>
    /// Publish this launcher's items and return the link to hand to other people.
    /// </summary>
    public static async Task<(bool Success, string Message)> PushAsync(Launcher launcher)
    {
        if (!Store.IsSignedIn) return (false, "Sign in to OneDrive first.");
        if (!HasConsent) return (false, "OneDrive sharing permission has not been granted yet.");

        try
        {
            byte[] bytes = SharedLauncherPayload.Serialize(launcher);

            // A subscriber only ever has ids, so once resolved use them; the owner addresses the
            // file by path the first time, which also creates it.
            string url = launcher.SharedItemId.Length > 0
                ? $"{GraphRoot}/drives/{launcher.SharedDriveId}/items/{launcher.SharedItemId}/content"
                : $"{GraphRoot}/me/drive/root:/{ShareFolder}/{FileNameFor(launcher)}:/content";

            using var request = await AuthorizedAsync(HttpMethod.Put, url);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return (false, await DescribeAsync(response, "publish"));

            // Remember the ids so later syncs skip the path lookup, and so a rename of the
            // launcher does not orphan the file it has been publishing to.
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            CacheIds(launcher, doc.RootElement);

            if (launcher.SharedLinkUrl.Length == 0)
            {
                var (linkOk, link) = await CreateLinkAsync(launcher);
                if (!linkOk) return (false, link);
                launcher.SharedLinkUrl = link;
            }

            Logger.Info($"Shared launcher '{launcher.Name}' published to OneDrive");
            return (true, "Published to OneDrive.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"OneDrive share push failed for '{launcher.Name}'");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Mint an editable, anonymous link to the published file.
    /// </summary>
    /// <remarks>
    /// <c>type: "edit"</c> because 2-way sharing needs subscribers to write back; a view link
    /// would silently make every push from a subscriber fail. <c>scope: "anonymous"</c> keeps the
    /// link usable without the owner having to name each person — the link *is* the credential,
    /// which is worth being explicit about in the UI.
    /// </remarks>
    private static async Task<(bool Success, string Result)> CreateLinkAsync(Launcher launcher)
    {
        using var request = await AuthorizedAsync(
            HttpMethod.Post,
            $"{GraphRoot}/drives/{launcher.SharedDriveId}/items/{launcher.SharedItemId}/createLink");

        request.Content = new StringContent(
            """{"type":"edit","scope":"anonymous"}""", Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return (false, await DescribeAsync(response, "create a share link"));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string? link = doc.RootElement.TryGetProperty("link", out var l)
                       && l.TryGetProperty("webUrl", out var w)
            ? w.GetString()
            : null;

        return link is { Length: > 0 }
            ? (true, link)
            : (false, "OneDrive did not return a share link.");
    }

    // ── Subscriber ──────────────────────────────────────────────────

    /// <summary>Fetch the shared items and apply them.</summary>
    public static async Task<(bool Success, string Message)> PullAsync(Launcher launcher)
    {
        if (!Store.IsSignedIn) return (false, "Sign in to OneDrive first.");

        try
        {
            if (!await EnsureResolvedAsync(launcher))
                return (false, "Could not resolve that OneDrive share link.");

            using var request = await AuthorizedAsync(
                HttpMethod.Get,
                $"{GraphRoot}/drives/{launcher.SharedDriveId}/items/{launcher.SharedItemId}/content");

            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (false, "The shared launcher file no longer exists.");
            if (!response.IsSuccessStatusCode)
                return (false, await DescribeAsync(response, "download"));

            var file = SharedLauncherPayload.Deserialize(await response.Content.ReadAsByteArrayAsync());
            if (file == null) return (false, "Could not parse the shared launcher file.");

            await SharedLauncherPayload.ApplyAsync(launcher, file);
            return (true, "Shared launcher updated.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"OneDrive share pull failed for '{launcher.Name}'");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    /// <summary>Check a link resolves and holds valid data, before someone subscribes to it.</summary>
    public static async Task<(bool Success, int ItemCount, string Error)> VerifyAsync(Launcher launcher)
    {
        if (!Store.IsSignedIn) return (false, 0, "Sign in to OneDrive first.");
        if (launcher.SharedLinkUrl.Length == 0) return (false, 0, "Paste the share link.");

        try
        {
            if (!await EnsureResolvedAsync(launcher))
                return (false, 0, "That link could not be opened. Check it was copied in full.");

            using var request = await AuthorizedAsync(
                HttpMethod.Get,
                $"{GraphRoot}/drives/{launcher.SharedDriveId}/items/{launcher.SharedItemId}/content");

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return (false, 0, await DescribeAsync(response, "read"));

            var file = SharedLauncherPayload.Deserialize(await response.Content.ReadAsByteArrayAsync());
            return file == null
                ? (false, 0, "The file exists but could not be parsed.")
                : (true, file.Items.Count, "");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Turn a share link into drive and item ids, once, and remember them.
    /// </summary>
    /// <remarks>
    /// Graph's <c>/shares/{encoded}</c> endpoint takes the link in its own base64url form with a
    /// <c>u!</c> prefix — a plain URL, or standard base64, is rejected.
    /// </remarks>
    private static async Task<bool> EnsureResolvedAsync(Launcher launcher)
    {
        if (launcher.SharedItemId.Length > 0 && launcher.SharedDriveId.Length > 0) return true;

        string encoded = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(launcher.SharedLinkUrl))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var request = await AuthorizedAsync(HttpMethod.Get, $"{GraphRoot}/shares/{encoded}/driveItem");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warn($"Could not resolve OneDrive share link ({(int)response.StatusCode})");
            return false;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CacheIds(launcher, doc.RootElement);
        return launcher.SharedItemId.Length > 0 && launcher.SharedDriveId.Length > 0;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static void CacheIds(Launcher launcher, JsonElement item)
    {
        if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } itemId)
            launcher.SharedItemId = itemId;

        if (item.TryGetProperty("parentReference", out var parent)
            && parent.TryGetProperty("driveId", out var drive)
            && drive.GetString() is { Length: > 0 } driveId)
        {
            launcher.SharedDriveId = driveId;
        }
    }

    /// <summary>A filesystem-safe name, since this lands in a real Drive folder people will see.</summary>
    private static string FileNameFor(Launcher launcher)
    {
        string name = string.IsNullOrWhiteSpace(launcher.Name) ? "launcher" : launcher.Name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');
        return Uri.EscapeDataString(name + ".json");
    }

    private static async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string url)
    {
        string? token = await OAuthPkceClient.GetAccessTokenAsync(Provider, OneDriveEndpoint);
        if (token == null)
            throw new InvalidOperationException("Not signed in to OneDrive. Sign in again to continue.");

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static OAuthPkceClient.Endpoint OneDriveEndpoint => new()
    {
        AuthorizeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
        TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
        ClientId = CloudSyncCredentials.OneDriveClientId,
        Scope = OneDriveFileStore.SharingScope,
    };

    private static async Task<string> DescribeAsync(HttpResponseMessage response, string operation)
    {
        string body = await response.Content.ReadAsStringAsync();
        Logger.Warn($"OneDrive share {operation} failed ({(int)response.StatusCode}): {body}");
        return CloudErrors.Describe(response.StatusCode, body, "OneDrive", operation);
    }
}
