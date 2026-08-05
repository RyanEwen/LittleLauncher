using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// Stores <c>launchers.json</c> in Google Drive through the Drive v3 API, in the hidden
/// application data folder.
/// </summary>
/// <remarks>
/// <para><b>Scope is <c>drive.appdata</c>.</b> Google classifies it as <i>non-sensitive</i>, so
/// it needs only basic OAuth verification — no security assessment, no restricted-scope review.
/// It reaches a per-app folder the user never sees in their Drive and cannot reach any of their
/// real files, which is both the smallest useful permission and the cheapest one to ship.</para>
/// <para>The flip side of a hidden folder: the file cannot be opened, inspected or recovered
/// from the Drive web UI. Uninstalling the app's Drive access deletes it. That is acceptable
/// because it is a replica — the launchers live in local settings — but it is why this is not
/// somewhere to put anything that is not also stored locally.</para>
/// </remarks>
public sealed class GoogleDriveFileStore : ICloudFileStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string Provider = "googledrive";
    private const string ApiRoot = "https://www.googleapis.com/drive/v3";
    private const string UploadRoot = "https://www.googleapis.com/upload/drive/v3";

    public string ProviderName => "Google Drive";
    public bool IsAvailable => CloudSyncCredentials.HasGoogleDrive;
    public bool IsSignedIn => CloudTokenStore.Load(Provider) != null;
    public string AccountName => CloudTokenStore.Load(Provider)?.AccountName ?? "";

    private static OAuthPkceClient.Endpoint Endpoint => new()
    {
        AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenUrl = "https://oauth2.googleapis.com/token",
        ClientId = CloudSyncCredentials.GoogleClientId,
        ClientSecret = CloudSyncCredentials.GoogleClientSecret,
        Scope = PrivateScope,
        ExtraAuthorizeParams = new Dictionary<string, string>
        {
            // Google issues a refresh token only when offline access is asked for, and only on
            // the *first* consent for a given client/account pair. `prompt=consent` forces the
            // screen again on re-authorisation so a user who signs out and back in is not left
            // with an access token that dies in an hour and cannot be renewed.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        },
    };

    /// <summary>The hidden per-app folder holding this user's own launchers. Non-sensitive.</summary>
    public const string PrivateScope = "https://www.googleapis.com/auth/drive.appdata";

    /// <summary>
    /// Adds per-file access so shared launchers can live in a real, shareable Drive file.
    /// </summary>
    /// <remarks>
    /// <para><b>Both scopes together, not one instead of the other.</b> Google allows it, and it
    /// is what keeps the user's own launchers in the invisible app-data folder while only the
    /// file they chose to share becomes a visible Drive object. Dropping appdata would move
    /// private config into their Drive for no reason.</para>
    /// <para><c>drive.file</c> is also classified <b>non-sensitive</b> — it only reaches files
    /// this app created — so adding it costs no extra verification. Google is the cheap case
    /// here; OneDrive has no equivalent and must ask for the whole drive.</para>
    /// </remarks>
    public const string SharingScope =
        PrivateScope + " https://www.googleapis.com/auth/drive.file";

    public async Task<(bool Success, string Message)> SignInAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            return (false, CloudSyncCredentials.NotConfiguredMessage(ProviderName));

        var tokens = await OAuthPkceClient.SignInAsync(Endpoint, scopeOverride: null, ct);
        if (tokens == null)
            return (false, "Google Drive sign-in was cancelled or failed.");

        tokens.AccountName = await FetchAccountNameAsync(tokens.AccessToken, ct);
        CloudTokenStore.Save(Provider, tokens);

        Logger.Info($"Signed in to Google Drive as {tokens.AccountName}");
        return (true, $"Signed in to Google Drive{(tokens.AccountName.Length > 0 ? $" as {tokens.AccountName}" : "")}.");
    }

    public void SignOut() => CloudTokenStore.Clear(Provider);

    /// <summary>
    /// True when the stored grant already covers sharing, so no second consent is needed.
    /// </summary>
    public bool HasSharingConsent =>
        CloudTokenStore.Load(Provider)?.CoversScope(SharingScope) == true;

    /// <summary>
    /// Ask for the wider sharing permission, keeping the existing account.
    /// </summary>
    /// <remarks>
    /// Incremental consent: called the first time the user shares a launcher to Google Drive,
    /// never at ordinary sign-in. The account is unchanged — this only widens what the app may
    /// do with it — so the existing display name is carried over rather than re-fetched.
    /// </remarks>
    public async Task<(bool Success, string Message)> RequestSharingConsentAsync(
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return (false, CloudSyncCredentials.NotConfiguredMessage(ProviderName));
        if (HasSharingConsent)
            return (true, "Already granted.");

        var existing = CloudTokenStore.Load(Provider);
        var tokens = await OAuthPkceClient.SignInAsync(Endpoint, SharingScope, ct);
        if (tokens == null)
            return (false, "Google Drive did not grant the extra permission.");

        // Belt and braces: a provider can return a narrower grant than was asked for, and
        // storing it as though sharing were enabled would fail later with an opaque error.
        if (!tokens.CoversScope(SharingScope))
        {
            // Log both sides: the only way to tell "the user declined" from "the comparison is
            // wrong" is to see what actually came back, and a stored token is encrypted.
            Logger.Warn($"Google Drive sharing consent insufficient. "
                        + $"Wanted '{SharingScope}', got '{tokens.GrantedScope}'");
            return (false, "Google Drive granted less than sharing needs. Try again and accept all the permissions.");
        }

        tokens.AccountName = existing?.AccountName ?? await FetchAccountNameAsync(tokens.AccessToken, ct);
        CloudTokenStore.Save(Provider, tokens);

        Logger.Info($"Sharing consent granted for Google Drive");
        return (true, "Google Drive sharing permission granted.");
    }

    public async Task<byte[]?> DownloadAsync(CancellationToken ct = default)
    {
        string? fileId = await FindFileIdAsync(ct);
        if (fileId == null) return null;

        using var request = await AuthorizedAsync(HttpMethod.Get, $"{ApiRoot}/files/{fileId}?alt=media", ct);
        using var response = await Http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(response, "download", ct);

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task UploadAsync(byte[] content, CancellationToken ct = default)
    {
        // Drive has no "create or replace by name" — a second create would silently make a
        // duplicate rather than overwrite, and the two machines would then diverge without
        // either seeing an error. So: find first, then update or create explicitly.
        string? fileId = await FindFileIdAsync(ct);
        fileId ??= await CreateFileAsync(ct);

        using var request = await AuthorizedAsync(
            HttpMethod.Patch, $"{UploadRoot}/files/{fileId}?uploadType=media", ct);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await Http.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, "upload", ct);
    }

    public async Task<DateTimeOffset?> GetRemoteModifiedAsync(CancellationToken ct = default)
    {
        var found = await FindFileAsync(ct);
        return found?.Modified;
    }

    // ── Drive plumbing ──────────────────────────────────────────────

    private sealed record DriveFile(string Id, DateTimeOffset? Modified);

    private static async Task<string?> FindFileIdAsync(CancellationToken ct) =>
        (await FindFileAsync(ct))?.Id;

    /// <summary>
    /// Locate the sync file inside the hidden app-data folder.
    /// </summary>
    /// <remarks>
    /// <c>spaces=appDataFolder</c> is required on every query: without it Drive searches the
    /// user's ordinary files, which this scope cannot see, and returns nothing at all.
    /// </remarks>
    private static async Task<DriveFile?> FindFileAsync(CancellationToken ct)
    {
        string query = Uri.EscapeDataString($"name = '{LauncherPayload.FileName}' and trashed = false");
        string url = $"{ApiRoot}/files?spaces=appDataFolder&q={query}&fields=files(id,modifiedTime)&pageSize=1";

        using var request = await AuthorizedAsync(HttpMethod.Get, url, ct);
        using var response = await Http.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, "lookup", ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0)
            return null;

        var file = files[0];
        string id = file.GetProperty("id").GetString() ?? "";
        DateTimeOffset? modified = file.TryGetProperty("modifiedTime", out var m)
                                   && m.TryGetDateTimeOffset(out var when)
            ? when
            : null;

        return id.Length == 0 ? null : new DriveFile(id, modified);
    }

    /// <summary>
    /// Create the (empty) file so a media upload has something to target.
    /// </summary>
    /// <remarks>
    /// Metadata-only create followed by a media update, rather than one multipart request:
    /// two plain calls beat hand-assembling a MIME body, and the create happens once per account.
    /// </remarks>
    private static async Task<string> CreateFileAsync(CancellationToken ct)
    {
        var metadata = new
        {
            name = LauncherPayload.FileName,
            parents = new[] { "appDataFolder" },
        };

        using var request = await AuthorizedAsync(HttpMethod.Post, $"{ApiRoot}/files?fields=id", ct);
        request.Content = new StringContent(
            JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, "create", ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Google Drive did not return a file id.");
    }

    private static async Task<HttpRequestMessage> AuthorizedAsync(
        HttpMethod method, string url, CancellationToken ct)
    {
        string? token = await OAuthPkceClient.GetAccessTokenAsync(Provider, Endpoint, ct);
        if (token == null)
            throw new InvalidOperationException("Not signed in to Google Drive. Sign in again to continue syncing.");

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Read the signed-in account's email for the settings UI.
    /// </summary>
    /// <remarks>
    /// <c>about.get</c> is reachable with the appdata scope, but treat it as optional anyway —
    /// a label is never worth failing a sign-in that otherwise worked.
    /// </remarks>
    private static async Task<string> FetchAccountNameAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/about?fields=user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return "";

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("user", out var user)) return "";

            return user.TryGetProperty("emailAddress", out var email) ? email.GetString() ?? ""
                 : user.TryGetProperty("displayName", out var name) ? name.GetString() ?? ""
                 : "";
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read the Google Drive account name");
            return "";
        }
    }

    private static async Task ThrowIfFailedAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(ct);
        Logger.Warn($"Google Drive {operation} failed ({(int)response.StatusCode}): {body}");

        // Google's own message is usually the whole diagnosis — "the Drive API is not enabled in
        // project N, enable it at <url>" — so it is surfaced rather than guessed at.
        throw new InvalidOperationException(
            CloudErrors.Describe(response.StatusCode, body, "Google Drive", operation));
    }
}
