using LittleLauncher.Classes.Settings;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace LittleLauncher.Services;

/// <summary>The stored half of a WebDAV connection — the part that must not reach settings.json.</summary>
public sealed class WebDavCredentials
{
    public string Password { get; set; } = "";
}

/// <summary>
/// Stores <c>launchers.json</c> on any WebDAV server: Nextcloud, ownCloud, Fastmail Files, Koofr,
/// a NAS, or anything else speaking the protocol.
/// </summary>
/// <remarks>
/// <para><b>Why a standard rather than another vendor integration.</b> One implementation reaches
/// every WebDAV server there will ever be, and it costs no app registration, no consent screen, no
/// verification review, no client secret and no SDK — none of which can expire or be revoked by a
/// vendor. For self-hosted users, who are the same audience the SFTP transport already serves, it
/// is also the case where a synced folder most often is not an option: the server is reachable but
/// no sync client is installed on the machine.</para>
/// <para>Authentication is HTTP Basic over TLS, which is what these servers expect — Nextcloud and
/// ownCloud issue per-application passwords precisely for this. The password is held in
/// <see cref="ProtectedStore"/>, never in settings.json.</para>
/// </remarks>
public sealed class WebDavFileStore : ICloudFileStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string Provider = "webdav";

    /// <summary>PROPFIND, used to verify the base URL is a WebDAV collection.</summary>
    private static readonly HttpMethod PropFind = new("PROPFIND");

    public string ProviderName => "WebDAV";

    /// <summary>Always — there is no registration to configure, which is much of the appeal.</summary>
    public bool IsAvailable => true;

    public bool IsSignedIn =>
        !string.IsNullOrWhiteSpace(SettingsManager.Current.WebDavUrl)
        && !string.IsNullOrWhiteSpace(SettingsManager.Current.WebDavUsername)
        && !string.IsNullOrEmpty(StoredPassword);

    public string AccountName
    {
        get
        {
            var settings = SettingsManager.Current;
            if (string.IsNullOrWhiteSpace(settings.WebDavUsername)) return "";

            return Uri.TryCreate(settings.WebDavUrl, UriKind.Absolute, out var uri)
                ? $"{settings.WebDavUsername} at {uri.Host}"
                : settings.WebDavUsername;
        }
    }

    private static string StoredPassword =>
        ProtectedStore.Load<WebDavCredentials>(Provider)?.Password ?? "";

    /// <summary>Persist the password. Called by the UI before <see cref="SignInAsync"/>.</summary>
    public static void SetPassword(string password) =>
        ProtectedStore.Save(Provider, new WebDavCredentials { Password = password });

    /// <summary>
    /// Verify the configured server, credentials and path. There is no browser flow here — for
    /// WebDAV "signing in" is confirming that what the user typed actually works, which is worth
    /// doing at the moment they type it rather than at the first background sync.
    /// </summary>
    public async Task<(bool Success, string Message)> SignInAsync(CancellationToken ct = default)
    {
        var settings = SettingsManager.Current;

        if (string.IsNullOrWhiteSpace(settings.WebDavUrl))
            return (false, "Enter the WebDAV folder URL.");
        if (string.IsNullOrWhiteSpace(settings.WebDavUsername))
            return (false, "Enter the WebDAV username.");
        if (string.IsNullOrEmpty(StoredPassword))
            return (false, "Enter the WebDAV password.");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
            return (false, "That does not look like a valid URL.");

        try
        {
            using var request = Authorized(PropFind, BaseUrl);

            // Depth 0 asks about the collection itself rather than everything inside it — the
            // difference between one small response and enumerating a folder that may be large.
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent(
                "<?xml version=\"1.0\"?><propfind xmlns=\"DAV:\"><prop><resourcetype/></prop></propfind>",
                Encoding.UTF8, "application/xml");

            using var response = await Http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, "The server rejected that username or password.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (false, "The server was reached, but that folder does not exist on it.");
            if (!response.IsSuccessStatusCode)
                return (false, $"The server refused the request ({(int)response.StatusCode}).");

            string warning = uri.Scheme == Uri.UriSchemeHttps
                ? ""
                : " Warning: this is a plain http:// URL, so the password is sent unencrypted.";

            Logger.Info($"WebDAV connection verified at {uri.Host}");
            return (true, $"Connected to {uri.Host}.{warning}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "WebDAV connection test failed");
            return (false, $"Could not reach the server: {ex.Message}");
        }
    }

    /// <summary>Forget the stored password. The URL and username stay, so reconnecting is quick.</summary>
    public void SignOut() => ProtectedStore.Clear(Provider);

    public async Task<byte[]?> DownloadAsync(CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Get, FileUrl);
        using var response = await Http.SendAsync(request, ct);

        // A first sync has nothing to download yet; that is a normal state, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        await ThrowIfFailedAsync(response, "download", ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task UploadAsync(byte[] content, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Put, FileUrl);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await Http.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, "upload", ct);
    }

    public async Task<DateTimeOffset?> GetRemoteModifiedAsync(CancellationToken ct = default)
    {
        // HEAD rather than PROPFIND: every WebDAV server serves Last-Modified on it, and it
        // avoids parsing a multi-status XML body for one timestamp.
        using var request = Authorized(HttpMethod.Head, FileUrl);
        using var response = await Http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(response, "metadata", ct);

        return response.Content.Headers.LastModified ?? response.Headers.Date;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>The configured folder URL, always with a trailing slash so joining is predictable.</summary>
    private static string BaseUrl
    {
        get
        {
            string url = SettingsManager.Current.WebDavUrl.Trim();
            return url.EndsWith('/') ? url : url + "/";
        }
    }

    private static string FileUrl => BaseUrl + LauncherPayload.FileName;

    private static HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);

        // Sent pre-emptively rather than waiting for a 401 challenge: it halves the round trips,
        // and some servers answer an unauthenticated PROPFIND with 404 instead of 401, which would
        // otherwise look like a wrong path rather than a missing credential.
        string pair = $"{SettingsManager.Current.WebDavUsername}:{StoredPassword}";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));

        return request;
    }

    private static async Task ThrowIfFailedAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(ct);
        Logger.Warn($"WebDAV {operation} failed ({(int)response.StatusCode}): {body}");

        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The WebDAV server rejected the stored credentials. Reconnect.",
            HttpStatusCode.Forbidden => "The WebDAV server refused access to that folder.",
            HttpStatusCode.Conflict => "The WebDAV folder does not exist. Create it on the server first.",
            HttpStatusCode.InsufficientStorage => "The WebDAV server is out of space.",
            _ => $"WebDAV {operation} failed ({(int)response.StatusCode}).",
        });
    }
}
