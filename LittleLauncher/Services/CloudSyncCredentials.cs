namespace LittleLauncher.Services;

/// <summary>
/// OAuth client registrations for the native cloud providers.
/// </summary>
/// <remarks>
/// <para><b>These are not secrets, and are not treated as such by either vendor.</b> A desktop
/// app cannot keep a credential confidential — anyone can read it out of the binary — so both
/// Microsoft and Google define the installed-app flow to be secure without one. Security comes
/// from PKCE and from the redirect being a loopback address only a local process can receive.
/// Google still issues a "client secret" for Desktop clients and still requires it on the token
/// call; it is a client *identifier* in practice, which is why it can sit in a public repo.</para>
/// <para>The env-var overrides exist so a second registration can be pointed at for testing
/// without editing source. They win when set.</para>
/// <para>Registering these is a one-time manual step — see
/// <see href="../../.claude/docs/sync.md">sync.md</see> for exactly what to create in the Entra
/// and Google Cloud portals. Until they are filled in, the two providers report themselves as
/// unconfigured rather than failing at sign-in.</para>
/// </remarks>
public static class CloudSyncCredentials
{
    // ── OneDrive (Microsoft Entra app registration) ─────────────────
    // Platform: "Mobile and desktop applications", redirect http://localhost
    // Supported account types: personal Microsoft accounts only
    // Delegated permission: Files.ReadWrite.AppFolder
    private const string OneDriveClientIdDefault = "eca1f6fc-ab2b-4dc6-8e93-86c74f846a26";

    // ── Google Drive (Google Cloud OAuth client) ────────────────────
    // Application type: Desktop app
    // Scope: https://www.googleapis.com/auth/drive.appdata  (non-sensitive)
    private const string GoogleClientIdDefault =
        "1038414978494-8rv57eknel671pjqv4dhgjlunmai5rbd.apps.googleusercontent.com";
    // Deliberately empty in source. Google treats installed-app secrets as non-confidential,
    // but this repository is public and git history cannot be unpublished, so the value is
    // injected at build time instead (see BuildValue) and never committed.
    private const string GoogleClientSecretDefault = "";

    public static string OneDriveClientId =>
        Environment.GetEnvironmentVariable("LITTLELAUNCHER_ONEDRIVE_CLIENT_ID") is { Length: > 0 } v
            ? v
            : OneDriveClientIdDefault;

    public static string GoogleClientId =>
        Environment.GetEnvironmentVariable("LITTLELAUNCHER_GOOGLE_CLIENT_ID") is { Length: > 0 } v
            ? v
            : GoogleClientIdDefault;

    public static string GoogleClientSecret =>
        Environment.GetEnvironmentVariable("LITTLELAUNCHER_GOOGLE_CLIENT_SECRET") is { Length: > 0 } v
            ? v
            : BuildValue("GoogleClientSecret") is { Length: > 0 } b
                ? b
                : GoogleClientSecretDefault;

    /// <summary>
    /// A credential baked in at build time via an MSBuild property, e.g.
    /// <c>-p:GoogleClientSecret=...</c>.
    /// </summary>
    /// <remarks>
    /// <para>The env-var override above is a *runtime* lookup, which is fine for a developer
    /// machine and useless for a shipped build: an end user has no such variable set. Anything
    /// that must reach real users has to be compiled in, and this is how it gets there without
    /// living in a public repository.</para>
    /// <para>Absent in an ordinary local build, which is correct: Google Drive then reports
    /// itself unconfigured rather than failing at sign-in with something unexplainable.</para>
    /// </remarks>
    private static string? BuildValue(string key) =>
        typeof(CloudSyncCredentials).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>True when this build has a usable registration for the provider.</summary>
    public static bool HasOneDrive => !string.IsNullOrWhiteSpace(OneDriveClientId);

    /// <summary>True when this build has a usable registration for the provider.</summary>
    public static bool HasGoogleDrive => !string.IsNullOrWhiteSpace(GoogleClientId);

    /// <summary>
    /// Message shown when a provider was selected but this build carries no registration for it.
    /// </summary>
    public static string NotConfiguredMessage(string providerName) =>
        $"This build has no {providerName} app registration, so sign-in is unavailable. " +
        $"See .claude/docs/sync.md for how to create one.";
}
