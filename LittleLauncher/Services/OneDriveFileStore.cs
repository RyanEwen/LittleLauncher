using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// Stores <c>launchers.json</c> in OneDrive through Microsoft Graph, in the app's own folder.
/// </summary>
/// <remarks>
/// <para><b>Scope is <c>Files.ReadWrite.AppFolder</c>.</b> Graph creates a folder reserved for
/// this app under <i>Apps/Little Launcher</i> and grants access to nothing else — the consent
/// prompt asks for one folder rather than the user's whole drive, which is both the honest
/// request and the easy one to accept.</para>
/// <para><b>This is personal Microsoft accounts only.</b> Microsoft has never extended the app
/// folder permission to OneDrive for Business, so the app registration targets the
/// <c>consumers</c> authority. Work and school accounts cannot use this provider at all; they
/// point the "Other folder" provider at their synced OneDrive folder instead.</para>
/// </remarks>
public sealed class OneDriveFileStore : ICloudFileStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string Provider = "onedrive";
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";

    /// <summary>The item path inside the app folder, as Graph's addressing syntax wants it.</summary>
    private const string ItemPath = "/me/drive/special/approot:/" + LauncherPayload.FileName;

    public string ProviderName => "OneDrive";
    public bool IsAvailable => CloudSyncCredentials.HasOneDrive;
    public bool IsSignedIn => CloudTokenStore.Load(Provider) != null;
    public string AccountName => CloudTokenStore.Load(Provider)?.AccountName ?? "";

    private static OAuthPkceClient.Endpoint Endpoint => new()
    {
        // The `consumers` authority, not `common`: the app folder scope only exists for personal
        // accounts, and `common` would let a work account reach a consent screen it cannot satisfy.
        AuthorizeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
        TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
        ClientId = CloudSyncCredentials.OneDriveClientId,

        // offline_access is what makes a refresh token be issued; without it the user would be
        // sent back to the browser every hour.
        Scope = PrivateScope,
    };

    /// <summary>
    /// What a sync-only user consents to: the app's own folder and nothing else in the drive.
    /// </summary>
    public const string PrivateScope = "Files.ReadWrite.AppFolder offline_access";

    /// <summary>
    /// What sharing additionally needs — write access to the drive proper.
    /// </summary>
    /// <remarks>
    /// <para>The app folder cannot be shared: it is private per-app storage with no way to grant
    /// another person access, so a launcher shared from it would be unreachable by the person it
    /// was shared with. Graph offers no middle ground for consumer OneDrive — there is no
    /// per-file equivalent of Google's <c>drive.file</c> — so sharing costs full drive access.</para>
    /// <para><b>Requested only when the user first shares to OneDrive</b>, never at first
    /// sign-in. Someone who only syncs should never be asked for their whole drive to enable a
    /// feature they are not using; that prompt is the single biggest reason people abandon an
    /// OAuth flow. Private launchers stay in the app folder either way — broadening the grant
    /// does not move them.</para>
    /// </remarks>
    public const string SharingScope = "Files.ReadWrite offline_access";

    public async Task<(bool Success, string Message)> SignInAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            return (false, CloudSyncCredentials.NotConfiguredMessage(ProviderName));

        var tokens = await OAuthPkceClient.SignInAsync(Endpoint, scopeOverride: null, ct);
        if (tokens == null)
            return (false, "OneDrive sign-in was cancelled or failed.");

        tokens.AccountName = await FetchAccountNameAsync(tokens.AccessToken, ct);
        CloudTokenStore.Save(Provider, tokens);

        Logger.Info($"Signed in to OneDrive as {tokens.AccountName}");
        return (true, $"Signed in to OneDrive{(tokens.AccountName.Length > 0 ? $" as {tokens.AccountName}" : "")}.");
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
    /// Incremental consent: called the first time the user shares a launcher to OneDrive,
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
            return (false, "OneDrive did not grant the extra permission.");

        // Belt and braces: a provider can return a narrower grant than was asked for, and
        // storing it as though sharing were enabled would fail later with an opaque error.
        if (!tokens.CoversScope(SharingScope))
        {
            // Log both sides: the only way to tell "the user declined" from "the comparison is
            // wrong" is to see what actually came back, and a stored token is encrypted.
            Logger.Warn($"OneDrive sharing consent insufficient. "
                        + $"Wanted '{SharingScope}', got '{tokens.GrantedScope}'");
            return (false, "OneDrive granted less than sharing needs. Try again and accept all the permissions.");
        }

        tokens.AccountName = existing?.AccountName ?? await FetchAccountNameAsync(tokens.AccessToken, ct);
        CloudTokenStore.Save(Provider, tokens);

        Logger.Info($"Sharing consent granted for OneDrive");
        return (true, "OneDrive sharing permission granted.");
    }

    public async Task<byte[]?> DownloadAsync(CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, $"{GraphRoot}{ItemPath}:/content", ct);
        using var response = await Http.SendAsync(request, ct);

        // A first sync has nothing to download yet; that is a normal state, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        await ThrowIfFailedAsync(response, "download", ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task UploadAsync(byte[] content, CancellationToken ct = default)
    {
        // Simple upload, valid up to 4 MB. A launchers.json is a few tens of KB, so the
        // resumable upload session Graph requires for larger files is not needed here.
        using var request = await AuthorizedAsync(HttpMethod.Put, $"{GraphRoot}{ItemPath}:/content", ct);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await Http.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, "upload", ct);
    }

    public async Task<DateTimeOffset?> GetRemoteModifiedAsync(CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(
            HttpMethod.Get, $"{GraphRoot}{ItemPath}?$select=lastModifiedDateTime", ct);
        using var response = await Http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(response, "metadata", ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("lastModifiedDateTime", out var m)
               && m.TryGetDateTimeOffset(out var when)
            ? when
            : null;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Build a request carrying a fresh access token, refreshing or failing loudly first.
    /// </summary>
    private static async Task<HttpRequestMessage> AuthorizedAsync(
        HttpMethod method, string url, CancellationToken ct)
    {
        string? token = await OAuthPkceClient.GetAccessTokenAsync(Provider, Endpoint, ct);
        if (token == null)
            throw new InvalidOperationException("Not signed in to OneDrive. Sign in again to continue syncing.");

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Read the account's display name from the drive itself rather than <c>/me</c>, which would
    /// need the additional <c>User.Read</c> scope for nothing but a label.
    /// </summary>
    private static async Task<string> FetchAccountNameAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphRoot}/me/drive?$select=owner");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return "";

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("owner", out var owner)
                   && owner.TryGetProperty("user", out var user)
                   && user.TryGetProperty("displayName", out var name)
                ? name.GetString() ?? ""
                : "";
        }
        catch (Exception ex)
        {
            // Cosmetic only — never block a working sign-in over a missing label.
            Logger.Debug(ex, "Could not read the OneDrive account name");
            return "";
        }
    }

    private static async Task ThrowIfFailedAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(ct);
        Logger.Warn($"OneDrive {operation} failed ({(int)response.StatusCode}): {body}");

        throw new InvalidOperationException(
            CloudErrors.Describe(response.StatusCode, body, "OneDrive", operation));
    }
}
