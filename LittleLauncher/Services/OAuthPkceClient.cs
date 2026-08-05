using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// OAuth 2.0 Authorization Code flow with PKCE and a loopback redirect — the flow both Microsoft
/// and Google define for native desktop apps.
/// </summary>
/// <remarks>
/// <para><b>Hand-rolled rather than MSAL + the Google SDK, deliberately.</b> Both providers are
/// plain OAuth 2.0 here and the app needs exactly one small file from each, so two large SDKs
/// with two different token-cache and threading idioms would be more surface, not less. One flow
/// serving both keeps the refresh and revocation logic in a single place, and keeps the
/// dependency list — which this project keeps short on purpose — unchanged.</para>
/// <para><b>Why loopback and not a custom URI scheme:</b> a registered scheme is global to the
/// machine, so any other app can claim it and receive the authorization code. A loopback listener
/// can only be reached by something already running locally, and the port is chosen per attempt.
/// PKCE then makes an intercepted code useless without the verifier, which never leaves this
/// process.</para>
/// </remarks>
public static class OAuthPkceClient
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Everything that differs between the two providers.</summary>
    public sealed class Endpoint
    {
        public required string AuthorizeUrl { get; init; }
        public required string TokenUrl { get; init; }
        public required string ClientId { get; init; }
        public required string Scope { get; init; }

        /// <summary>Google Desktop clients require this on the token call; Microsoft does not.</summary>
        public string ClientSecret { get; init; } = "";

        /// <summary>Extra authorize-request parameters, e.g. Google's offline access.</summary>
        public IReadOnlyDictionary<string, string> ExtraAuthorizeParams { get; init; }
            = new Dictionary<string, string>();
    }

    /// <summary>
    /// Run an interactive sign-in: open the system browser, wait for the redirect, exchange the
    /// code. Returns null if the user cancelled or the flow failed.
    /// </summary>
    /// <remarks>
    /// The system browser — not an embedded WebView — because that is what both providers
    /// require for native apps, and it is what lets the user see the real address bar and reuse
    /// sessions and password managers they already trust. This app never sees the credentials.
    /// </remarks>
    /// <param name="scopeOverride">
    /// Ask for something other than the endpoint's default scope — used for incremental consent,
    /// where the wider sharing permission is requested only when the user first needs it rather
    /// than being demanded of everyone at first sign-in.
    /// </param>
    public static async Task<CloudTokens?> SignInAsync(
        Endpoint endpoint, string? scopeOverride = null, CancellationToken ct = default)
    {
        string verifier = CreateCodeVerifier();
        string challenge = CreateCodeChallenge(verifier);

        using var listener = new HttpListener();
        int port = StartOnFreePort(listener, out string redirectUri);
        if (port == 0)
        {
            Logger.Error("Could not open a loopback listener for OAuth sign-in");
            return null;
        }

        try
        {
            string scope = string.IsNullOrWhiteSpace(scopeOverride) ? endpoint.Scope : scopeOverride;
            string authorizeUrl = BuildAuthorizeUrl(endpoint, redirectUri, challenge, scope);
            OpenInBrowser(authorizeUrl);

            string? code = await WaitForCodeAsync(listener, ct);
            if (code == null) return null;

            return await ExchangeCodeAsync(endpoint, code, verifier, redirectUri, scope, ct);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "OAuth sign-in failed");
            return null;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    /// <summary>
    /// Return a usable access token, refreshing it first if needed. Returns null when the refresh
    /// token is no longer accepted, which means the user must sign in again.
    /// </summary>
    public static async Task<string?> GetAccessTokenAsync(
        string provider, Endpoint endpoint, CancellationToken ct = default)
    {
        var tokens = CloudTokenStore.Load(provider);
        if (tokens == null) return null;

        if (tokens.IsAccessTokenUsable) return tokens.AccessToken;
        if (string.IsNullOrEmpty(tokens.RefreshToken)) return null;

        var refreshed = await RefreshAsync(endpoint, tokens, ct);
        if (refreshed == null)
        {
            // A rejected refresh token is terminal — revoked, expired, or the password changed.
            // Clearing it turns every later call into a clean "signed out" rather than a retry
            // storm against an endpoint that will keep saying no.
            Logger.Warn($"{provider} refresh token rejected; signing out locally");
            CloudTokenStore.Clear(provider);
            return null;
        }

        CloudTokenStore.Save(provider, refreshed);
        return refreshed.AccessToken;
    }

    // ── Flow steps ──────────────────────────────────────────────────

    private static string BuildAuthorizeUrl(
        Endpoint endpoint, string redirectUri, string challenge, string scope)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = endpoint.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };

        foreach (var (key, value) in endpoint.ExtraAuthorizeParams)
            query[key] = value;

        return endpoint.AuthorizeUrl + "?" + string.Join("&",
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>
    /// Bind a listener to an OS-assigned free port on loopback.
    /// </summary>
    /// <remarks>
    /// The port cannot be fixed: another app may already hold it, and two sign-ins could overlap.
    /// Both providers allow any port on <c>http://localhost</c> for native clients precisely
    /// because of this. A few attempts covers the race between finding a free port and binding it.
    /// </remarks>
    private static int StartOnFreePort(HttpListener listener, out string redirectUri)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int port = GetFreePort();

            // `localhost`, not `127.0.0.1`: the redirect URI has to match what is registered, and
            // both vendors document the registered value as `http://localhost` (any port is then
            // accepted). Sending the numeric form against that registration is rejected outright
            // — AADSTS50011 from Microsoft — even though it resolves to the same interface.
            string prefix = $"http://localhost:{port}/";
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add(prefix);
                listener.Start();
                redirectUri = prefix;
                return port;
            }
            catch (HttpListenerException ex)
            {
                Logger.Debug(ex, $"Loopback port {port} unavailable; retrying");
            }
        }

        redirectUri = "";
        return 0;
    }

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Wait for the browser to hit the loopback redirect, and read the code out of it.</summary>
    private static async Task<string?> WaitForCodeAsync(HttpListener listener, CancellationToken ct)
    {
        var contextTask = listener.GetContextAsync();

        // Without this the listener would wait forever on a user who closed the browser tab.
        var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5), ct));
        if (completed != contextTask)
        {
            Logger.Info("OAuth sign-in timed out or was cancelled");
            return null;
        }

        var context = await contextTask;
        string? code = context.Request.QueryString["code"];
        string? error = context.Request.QueryString["error"];

        await RespondAsync(context, code != null
            ? "Signed in. You can close this tab and return to Little Launcher."
            : $"Sign-in failed: {WebUtility.HtmlEncode(error ?? "no authorization code returned")}");

        if (code == null)
            Logger.Warn($"OAuth sign-in returned no code (error: {error ?? "none"})");

        return code;
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=\"utf-8\">" +
                "<title>Little Launcher</title>" +
                "<body style=\"font-family:Segoe UI,system-ui,sans-serif;padding:3rem;\">" +
                $"<p>{message}</p></body>");

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not write the OAuth redirect response");
        }
    }

    private static async Task<CloudTokens?> ExchangeCodeAsync(
        Endpoint endpoint, string code, string verifier, string redirectUri, string scope,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = endpoint.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrEmpty(endpoint.ClientSecret))
            form["client_secret"] = endpoint.ClientSecret;

        return await PostTokenRequestAsync(endpoint, form, existingRefreshToken: null, scope, ct);
    }

    private static async Task<CloudTokens?> RefreshAsync(
        Endpoint endpoint, CloudTokens current, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = endpoint.ClientId,
            ["refresh_token"] = current.RefreshToken,
            ["grant_type"] = "refresh_token",

            // Ask for exactly what is already held, never the endpoint default: a token that was
            // widened for sharing must not be quietly narrowed back on the next refresh.
            ["scope"] = current.GrantedScope.Length > 0 ? current.GrantedScope : endpoint.Scope,
        };
        if (!string.IsNullOrEmpty(endpoint.ClientSecret))
            form["client_secret"] = endpoint.ClientSecret;

        var refreshed = await PostTokenRequestAsync(
            endpoint, form, current.RefreshToken, current.GrantedScope, ct);
        if (refreshed != null)
            refreshed.AccountName = current.AccountName;
        return refreshed;
    }

    /// <param name="existingRefreshToken">
    /// Carried forward when the response omits one. Google only returns a refresh token on the
    /// first consent, so a refresh response that replaced it with empty would sign the user out
    /// an hour later.
    /// </param>
    /// <param name="requestedScope">
    /// Fallback for providers that omit <c>scope</c> from the token response — Microsoft returns
    /// it, Google does not always. Recording what was asked for is better than recording nothing,
    /// since the request only succeeds if the user consented to it.
    /// </param>
    private static async Task<CloudTokens?> PostTokenRequestAsync(
        Endpoint endpoint, Dictionary<string, string> form, string? existingRefreshToken,
        string requestedScope, CancellationToken ct)
    {
        using var response = await Http.PostAsync(
            endpoint.TokenUrl, new FormUrlEncodedContent(form), ct);

        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warn($"Token request failed ({(int)response.StatusCode}): {body}");
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string access = root.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
        if (access.Length == 0) return null;

        string refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
        if (refresh.Length == 0 && existingRefreshToken != null)
            refresh = existingRefreshToken;

        int expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int secs)
            ? secs
            : 3600;

        string grantedScope = root.TryGetProperty("scope", out var sc) && sc.GetString() is { Length: > 0 } s
            ? s
            : requestedScope;

        return new CloudTokens
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            GrantedScope = grantedScope,
        };
    }

    // ── PKCE ────────────────────────────────────────────────────────

    private static string CreateCodeVerifier()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void OpenInBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
}
