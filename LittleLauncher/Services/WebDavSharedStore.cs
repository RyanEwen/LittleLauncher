using LittleLauncher.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Services;

/// <summary>
/// Reads and writes one shared launcher's items on a WebDAV server.
/// </summary>
/// <remarks>
/// <para>Separate from <see cref="WebDavFileStore"/>, which serves the global sync from
/// <c>UserSettings</c>. This one takes its URL and credentials from the <see cref="Launcher"/>,
/// because the server a colleague shares from is routinely not the one you sync your own
/// settings to, and every participant authenticates as themselves.</para>
/// <para><b>This is the transport where 2-way sharing actually works cleanly.</b> The location is
/// already a real shared address, so there is no link to mint, no permission to grant and no
/// subscriber-side resolution step — both sides simply write to the same URL with their own
/// credentials.</para>
/// </remarks>
public static class WebDavSharedStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly HttpMethod PropFind = new("PROPFIND");

    /// <summary>Per-launcher credential key, so two shared launchers can use different servers.</summary>
    private static string CredentialKey(Launcher launcher) => $"webdav-shared-{launcher.Id}";

    /// <summary>Store the password for this launcher's WebDAV server.</summary>
    public static void SetPassword(Launcher launcher, string password) =>
        ProtectedStore.Save(CredentialKey(launcher), new WebDavCredentials { Password = password });

    /// <summary>Forget the password for this launcher's WebDAV server.</summary>
    public static void ClearPassword(Launcher launcher) => ProtectedStore.Clear(CredentialKey(launcher));

    /// <summary>True when this launcher has everything needed to reach its WebDAV file.</summary>
    public static bool HasCredentials(Launcher launcher) =>
        !string.IsNullOrWhiteSpace(launcher.SharedWebDavUrl)
        && !string.IsNullOrWhiteSpace(launcher.SharedWebDavUsername)
        && PasswordFor(launcher).Length > 0;

    private static string PasswordFor(Launcher launcher) =>
        ProtectedStore.Load<WebDavCredentials>(CredentialKey(launcher))?.Password ?? "";

    // ── Operations ──────────────────────────────────────────────────

    /// <summary>Push this launcher's items to its WebDAV file.</summary>
    public static async Task<(bool Success, string Message)> PushAsync(Launcher launcher)
    {
        if (!HasCredentials(launcher))
            return (false, "This shared launcher has no WebDAV credentials on this PC.");

        try
        {
            byte[] bytes = SharedLauncherPayload.Serialize(launcher);

            using var request = Authorized(launcher, HttpMethod.Put);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return (false, Describe(response.StatusCode, "upload"));

            Logger.Info($"Shared launcher '{launcher.Name}' pushed to WebDAV");
            return (true, $"Shared to {HostOf(launcher)}.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"WebDAV push failed for '{launcher.Name}'");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    /// <summary>Pull this launcher's items from its WebDAV file and apply them.</summary>
    public static async Task<(bool Success, string Message)> PullAsync(Launcher launcher)
    {
        if (!HasCredentials(launcher))
            return (false, "This shared launcher has no WebDAV credentials on this PC.");

        try
        {
            using var request = Authorized(launcher, HttpMethod.Get);
            using var response = await Http.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return (false, "The shared launcher file does not exist on the server yet.");
            if (!response.IsSuccessStatusCode)
                return (false, Describe(response.StatusCode, "download"));

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            var file = SharedLauncherPayload.Deserialize(bytes);
            if (file == null)
                return (false, "Could not parse the shared launcher file.");

            await SharedLauncherPayload.ApplyAsync(launcher, file);
            Logger.Info($"Shared launcher '{launcher.Name}' pulled from WebDAV");
            return (true, "Shared launcher updated.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"WebDAV pull failed for '{launcher.Name}'");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Check the server, credentials and file before someone commits to subscribing.
    /// </summary>
    public static async Task<(bool Success, int ItemCount, string Error)> VerifyAsync(Launcher launcher)
    {
        if (string.IsNullOrWhiteSpace(launcher.SharedWebDavUrl))
            return (false, 0, "Enter the WebDAV file URL.");
        if (!HasCredentials(launcher))
            return (false, 0, "Enter the WebDAV username and password.");

        try
        {
            using var request = Authorized(launcher, HttpMethod.Get);
            using var response = await Http.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, 0, "The server rejected that username or password.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (false, 0, "The server was reached, but that file does not exist on it.");
            if (!response.IsSuccessStatusCode)
                return (false, 0, Describe(response.StatusCode, "read"));

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
    /// Confirm the folder an owner is about to publish into is reachable and writable.
    /// </summary>
    /// <remarks>
    /// <c>PROPFIND</c> with <c>Depth: 0</c> against the parent collection, for the same reason the
    /// global WebDAV store does it: it asks about the folder itself rather than enumerating it.
    /// </remarks>
    public static async Task<(bool Success, string Message)> VerifyFolderAsync(Launcher launcher)
    {
        string url = launcher.SharedWebDavUrl.Trim();
        int slash = url.LastIndexOf('/');
        if (slash <= 0) return (false, "That does not look like a full file URL.");

        try
        {
            using var request = new HttpRequestMessage(PropFind, url[..(slash + 1)]);
            ApplyAuth(request, launcher);
            request.Headers.Add("Depth", "0");

            using var response = await Http.SendAsync(request);
            return response.IsSuccessStatusCode
                ? (true, "Folder is reachable.")
                : (false, Describe(response.StatusCode, "check"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static HttpRequestMessage Authorized(Launcher launcher, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, launcher.SharedWebDavUrl.Trim());
        ApplyAuth(request, launcher);
        return request;
    }

    /// <remarks>
    /// Basic auth sent pre-emptively rather than after a 401 challenge — it halves the round
    /// trips, and some servers answer an unauthenticated request with 404 instead of 401, which
    /// would surface as "wrong path" when the real problem is the credential.
    /// </remarks>
    private static void ApplyAuth(HttpRequestMessage request, Launcher launcher)
    {
        string pair = $"{launcher.SharedWebDavUsername}:{PasswordFor(launcher)}";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
    }

    private static string HostOf(Launcher launcher) =>
        Uri.TryCreate(launcher.SharedWebDavUrl, UriKind.Absolute, out var uri) ? uri.Host : "the server";

    private static string Describe(HttpStatusCode status, string operation) => status switch
    {
        HttpStatusCode.Unauthorized => "The WebDAV server rejected the stored credentials.",
        HttpStatusCode.Forbidden => "The WebDAV server refused access to that file.",
        HttpStatusCode.Conflict => "The folder does not exist on the server. Create it first.",
        HttpStatusCode.InsufficientStorage => "The WebDAV server is out of space.",
        _ => $"WebDAV {operation} failed ({(int)status}).",
    };
}
