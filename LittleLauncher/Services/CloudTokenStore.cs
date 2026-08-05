namespace LittleLauncher.Services;

/// <summary>
/// One provider's OAuth tokens, as persisted.
/// </summary>
public sealed class CloudTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>When <see cref="AccessToken"/> stops being usable.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }

    /// <summary>Display name or email of the signed-in account, for the settings UI.</summary>
    public string AccountName { get; set; } = "";

    /// <summary>
    /// The scopes this grant actually covers, space-separated, as the provider returned them.
    /// </summary>
    /// <remarks>
    /// <para>Recorded because a stored refresh token is only good for the scopes it was issued
    /// for. When the app later needs a wider one — sharing needs write access outside the private
    /// app folder — the token in hand silently is not enough, and the failure surfaces as an
    /// opaque 403 at the moment the user tries to share rather than as "you need to grant this".
    /// <see cref="CoversScope"/> lets that be detected before the call is made.</para>
    /// <para>Empty in tokens saved before this field existed. Treat empty as "unknown, assume
    /// only the base scope" — the safe direction, since it triggers a re-consent that would have
    /// been needed anyway rather than an error.</para>
    /// </remarks>
    public string GrantedScope { get; set; } = "";

    /// <summary>
    /// Scopes that are granted but never reported back, so comparing on them always fails.
    /// </summary>
    /// <remarks>
    /// <c>offline_access</c> is the one that matters: Microsoft honours it — a refresh token is
    /// issued — but omits it from the token response's <c>scope</c> field. A naive comparison
    /// therefore decided sharing had been refused no matter what the user actually approved,
    /// which is exactly how this was found. The OIDC trio behave the same way.
    /// </remarks>
    private static readonly HashSet<string> NeverReported =
        new(StringComparer.OrdinalIgnoreCase) { "offline_access", "openid", "profile", "email" };

    /// <summary>True when this grant covers every scope in <paramref name="required"/>.</summary>
    /// <remarks>
    /// Compares on the final path segment, because providers are inconsistent about the form
    /// they report: Microsoft may answer a request for <c>Files.ReadWrite</c> with either the bare
    /// name or <c>https://graph.microsoft.com/Files.ReadWrite</c>, and Google always returns full
    /// URIs. Matching the whole string would make the result depend on which form came back.
    /// </remarks>
    public bool CoversScope(string required)
    {
        if (string.IsNullOrWhiteSpace(required)) return true;
        if (string.IsNullOrWhiteSpace(GrantedScope)) return false;

        var granted = Normalize(GrantedScope).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Normalize(required).All(granted.Contains);
    }

    private static IEnumerable<string> Normalize(string scopes) =>
        scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
              .Where(scope => !NeverReported.Contains(scope))
              .Select(scope => scope[(scope.LastIndexOf('/') + 1)..]);

    /// <summary>
    /// Treated as expired a minute early, so a token cannot lapse between the check and the
    /// request that uses it.
    /// </summary>
    public bool IsAccessTokenUsable =>
        !string.IsNullOrEmpty(AccessToken) && DateTimeOffset.UtcNow < ExpiresUtc.AddMinutes(-1);
}

/// <summary>
/// Stores cloud OAuth tokens on disk, encrypted to the current Windows user via
/// <see cref="ProtectedStore"/> — never in settings.json, which the sync feature uploads.
/// </summary>
public static class CloudTokenStore
{
    /// <summary>Load a provider's tokens, or null when not signed in / unreadable.</summary>
    public static CloudTokens? Load(string provider) => ProtectedStore.Load<CloudTokens>(provider);

    /// <summary>Persist a provider's tokens, encrypted to the current user.</summary>
    public static void Save(string provider, CloudTokens tokens) => ProtectedStore.Save(provider, tokens);

    /// <summary>Forget a provider's tokens — the local half of signing out.</summary>
    public static void Clear(string provider) => ProtectedStore.Clear(provider);
}
