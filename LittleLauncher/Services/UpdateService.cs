using LittleLauncher.Classes.Settings;
using static LittleLauncher.Classes.NativeMethods;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using WinRT.Interop;
using global::Windows.ApplicationModel;
using global::Windows.Services.Store;

namespace LittleLauncher.Services;

/// <summary>
/// Checks for updates using either GitHub Releases (WiX/unpackaged) or
/// Microsoft Store APIs (MSIX/packaged) and optionally installs them.
/// </summary>
public static class UpdateService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const string PackagedApplicationId = "App";

    /// <summary>Store product ID for Little Launcher (the ID in its Store listing URL).</summary>
    private const string StoreProductId = "9P3ZZBDQ6PJF";

    public enum UpdateSource
    {
        GitHubRelease,
        MicrosoftStore,
    }

    private const string Owner = "RyanEwen";
    private const string Repo = "LittleLauncher";
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LittleLauncher/{GetCurrentVersion()}");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>Result of an update check.</summary>
    public sealed class UpdateCheckResult
    {
        public UpdateSource Source { get; init; }
        public bool UpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string? ReleaseUrl { get; init; }
        public string? MsiDownloadUrl { get; init; }
        public string? ReleaseNotes { get; init; }

        public bool IsStoreManaged => Source == UpdateSource.MicrosoftStore;
    }

    /// <summary>
    /// Cached result from the most recent update check (set by <see cref="CheckForUpdateAsync"/>).
    /// </summary>
    public static UpdateCheckResult? LatestResult { get; private set; }

    /// <summary>
    /// Checks for a newer release using the update path appropriate for the current install type.
    /// Returns null on network or platform errors.
    /// </summary>
    public static async Task<UpdateCheckResult?> CheckForUpdateAsync()
    {
        try
        {
            var result = HasPackageIdentity()
                ? await CheckForStoreUpdateAsync()
                : await CheckForGitHubUpdateAsync();

            LatestResult = result;
            return result;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to check for updates");
            return null;
        }
    }

    /// <summary>
    /// Downloads and installs the update represented by <paramref name="result"/>.
    /// </summary>
    public static Task<(bool Success, string Message)> DownloadAndInstallAsync(
        UpdateCheckResult result,
        nint ownerWindowHandle,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return result.Source switch
        {
            UpdateSource.MicrosoftStore => DownloadAndInstallStoreUpdateAsync(ownerWindowHandle, progress, cancellationToken),
            _ when !string.IsNullOrEmpty(result.MsiDownloadUrl) => DownloadAndInstallMsiAsync(result.MsiDownloadUrl, progress, cancellationToken),
            _ => Task.FromResult((false, "No installer is available for this update.")),
        };
    }

    private static async Task<UpdateCheckResult?> CheckForGitHubUpdateAsync()
    {
        var release = await Http.GetFromJsonAsync(LatestReleaseUri, GitHubJsonContext.Default.GitHubRelease);
        if (release == null || string.IsNullOrEmpty(release.TagName))
            return null;

        var current = ParseVersion(GetCurrentVersion());
        var latest = ParseVersion(release.TagName);
        if (current == null || latest == null)
            return null;

        bool updateAvailable = latest > current;

        string? msiUrl = null;
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "x64";
        string expectedAsset = $"LittleLauncher-{arch}-Setup.msi";

        if (release.Assets != null)
        {
            foreach (var asset in release.Assets)
            {
                if (string.Equals(asset.Name, expectedAsset, StringComparison.OrdinalIgnoreCase))
                {
                    msiUrl = asset.BrowserDownloadUrl;
                    break;
                }
            }
        }

        return new UpdateCheckResult
        {
            Source = UpdateSource.GitHubRelease,
            UpdateAvailable = updateAvailable,
            CurrentVersion = $"v{current.Major}.{current.Minor}.{current.Build}",
            LatestVersion = release.TagName,
            ReleaseUrl = release.HtmlUrl,
            MsiDownloadUrl = msiUrl,
            ReleaseNotes = release.Body,
        };
    }

    private static async Task<UpdateCheckResult?> CheckForStoreUpdateAsync()
    {
        var context = StoreContext.GetDefault();
        var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();

        var currentPackageVersion = PackageVersionToVersion(Package.Current.Id.Version);
        string currentVersion = FormatPackageVersion(Package.Current.Id.Version);
        string currentPackageName = Package.Current.Id.FamilyName;

        // GetAppAndOptionalStorePackageUpdatesAsync lists every package the Store can update —
        // including framework dependencies — so the app's own package has to be picked out by
        // family name. Its presence in that list *is* the "an update is waiting for you" signal.
        //
        // What it is NOT is a source of the new version number: StorePackageUpdate.Package
        // describes the package as installed, so Id.Version reports the version already on the
        // machine. Verified against a live Store update (installed 1.27.1.0, published 1.28.0.0):
        // the list held exactly one entry, ours, reporting 1.27.1.0. Requiring the listed version
        // to be strictly newer — an earlier attempt to stop the UI offering an update to the
        // running version — therefore never matched, and the Store path silently reported "up to
        // date" forever. Do not reintroduce that comparison.
        bool ourPackageListed = updates.Any(update => update.Package != null && string.Equals(
            update.Package.Id.FamilyName, currentPackageName, StringComparison.OrdinalIgnoreCase));

        // The version to show, and the guard against the stale-list case the old comparison was
        // reaching for, both come from the Store catalog instead — the only place that knows what
        // is actually published. It is best-effort: a null answer means "cannot say", and the
        // Store's own list is then trusted on its own rather than being vetoed by a failed
        // lookup. Only a catalog version that is genuinely newer is ever displayed.
        Version? publishedVersion = ourPackageListed ? await TryGetPublishedVersionAsync() : null;
        bool publishedIsNewer = publishedVersion != null && publishedVersion > currentPackageVersion;
        bool updateAvailable = ourPackageListed && (publishedVersion == null || publishedIsNewer);

        // Logged on every check because the failure this replaced was invisible: the check
        // succeeded, so nothing was written, and "up to date" was indistinguishable from a bug.
        Logger.Info(
            "Store update check: {Count} package update(s) listed, ours present: {Ours}, "
            + "installed {Current}, published {Published}, update available: {Available}",
            updates.Count, ourPackageListed, currentPackageVersion,
            publishedVersion?.ToString() ?? "unknown", updateAvailable);

        return new UpdateCheckResult
        {
            Source = UpdateSource.MicrosoftStore,
            UpdateAvailable = updateAvailable,
            CurrentVersion = currentVersion,

            // Empty means "there is a newer version but its number is not known" — the UI words
            // that case without a version rather than inventing one.
            LatestVersion = updateAvailable && publishedIsNewer
                ? $"v{publishedVersion!.Major}.{publishedVersion.Minor}.{publishedVersion.Build}"
                : updateAvailable ? "" : currentVersion,
        };
    }

    /// <summary>
    /// Reads the version currently published to the Store for this product, or null if it cannot
    /// be determined. Uses the Store's public display-catalog endpoint, which is what the Store
    /// client itself reads; there is no WinRT API that reports a pending update's version.
    /// </summary>
    private static async Task<Version?> TryGetPublishedVersionAsync()
    {
        try
        {
            var uri = new Uri(
                $"https://displaycatalog.mp.microsoft.com/v7.0/products/{StoreProductId}"
                + "?market=US&languages=en-us&fieldsTemplate=Details");

            using var stream = await Http.GetStreamAsync(uri);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);

            string arch = Package.Current.Id.Architecture switch
            {
                global::Windows.System.ProcessorArchitecture.Arm64 => "arm64",
                global::Windows.System.ProcessorArchitecture.X86 => "x86",
                _ => "x64",
            };

            Version? best = null;

            if (!doc.RootElement.TryGetProperty("Product", out var product)
                || !product.TryGetProperty("DisplaySkuAvailabilities", out var skus))
            {
                return null;
            }

            foreach (var sku in skus.EnumerateArray())
            {
                if (!sku.TryGetProperty("Sku", out var skuInfo)
                    || !skuInfo.TryGetProperty("Properties", out var props)
                    || !props.TryGetProperty("Packages", out var packages))
                {
                    continue;
                }

                foreach (var package in packages.EnumerateArray())
                {
                    // The catalog's numeric "Version" is a packed 64-bit value; the version in
                    // human form only appears inside the package full name
                    // (Name_1.28.0.0_arm64__hash), which is also where the architecture is.
                    if (package.TryGetProperty("PackageFullName", out var fullNameElement)
                        && fullNameElement.GetString() is { } fullName)
                    {
                        var parts = fullName.Split('_');
                        if (parts.Length < 3) continue;
                        if (!parts[2].Equals(arch, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Version.TryParse(parts[1], out var parsed)) continue;
                        if (best == null || parsed > best) best = parsed;
                    }
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to read the published Store version");
            return null;
        }
    }

    private static async Task<(bool Success, string Message)> DownloadAndInstallMsiAsync(
        string msiUrl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "LittleLauncher-Update");
            Directory.CreateDirectory(tempDir);
            string msiPath = Path.Combine(tempDir, Path.GetFileName(new Uri(msiUrl).LocalPath));

            using var response = await Http.GetAsync(msiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            long bytesRead = 0;

            using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = new FileStream(msiPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    bytesRead += read;
                    if (totalBytes > 0)
                        progress?.Report((double)bytesRead / totalBytes);
                }
            }

            progress?.Report(1.0);

            try
            {
                string zoneFile = msiPath + ":Zone.Identifier";
                File.Delete(zoneFile);
            }
            catch { }

            int pid = Environment.ProcessId;
            string scriptPath = Path.Combine(tempDir, "install-update.cmd");
            string script = $"""
                @echo off
                echo Waiting for Little Launcher to exit...
                :wait
                tasklist /FI "PID eq {pid}" 2>NUL | find /I "{pid}" >NUL
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >NUL
                    goto wait
                )
                echo Installing update...
                msiexec /i "{msiPath}" /passive
                """;
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            return (true, "Installer will launch after the app closes.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Download was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download and install update from {Url}", msiUrl);
            return (false, $"Download failed: {ex.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> DownloadAndInstallStoreUpdateAsync(
        nint ownerWindowHandle,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var context = StoreContext.GetDefault();
            if (ownerWindowHandle != 0)
                InitializeWithWindow.Initialize(context, ownerWindowHandle);

            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates.Count == 0)
            {
                // Nothing to download can mean genuinely up to date, or that Windows already
                // staged the update in the background and is waiting for this app to exit.
                // GetAppAndOptionalStorePackageUpdatesAsync stops listing a package once it is
                // staged, so the two are indistinguishable from here. Offering the restart is
                // the useful move either way: it costs a relaunch if wrong, and completes a
                // stuck update if right.
                return (false,
                    "No download is pending. If an update was already downloaded in the "
                    + "background, restart Little Launcher to finish installing it.");
            }

            // ── Download first, install second ───────────────────────────
            // These two have opposite requirements and must not be a single call.
            // RequestDownloadStorePackageUpdatesAsync runs while the app is in use and does not
            // block, which is the only place real progress can come from. Installing needs every
            // process in the package to exit. Calling the combined API from a tray app that
            // never closes hides the download behind a wait that cannot resolve, which is why
            // this used to sit on "waiting to close app" showing nothing at all.
            var download = context.RequestDownloadStorePackageUpdatesAsync(updates);
            download.Progress = (_, status) =>
                progress?.Report(Math.Clamp(status.PackageDownloadProgress, 0.0, 1.0));

            var downloadResult = await download;
            if (downloadResult.OverallState is not (StorePackageUpdateState.Completed
                or StorePackageUpdateState.Deploying))
            {
                return (false, DescribeStoreUpdateState(downloadResult.OverallState, downloadResult));
            }

            progress?.Report(1.0);

            // The install cannot finish while we are running, so ask Windows to bring us back
            // afterwards and then get out of the way. RegisterApplicationRestart has to be in
            // place before shutdown begins; MainWindow sets it at startup, and reasserting it
            // here means a relaunch still happens if that call was missed or overwritten.
            RegisterApplicationRestart("--silent", 0);

            var operation = context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            operation.Progress = (_, status) =>
            {
                double normalized = status.PackageDownloadProgress >= 0.8
                    ? 1.0
                    : Math.Clamp(status.PackageDownloadProgress / 0.8, 0.0, 0.99);
                progress?.Report(normalized);
            };

            var result = await operation;
            progress?.Report(1.0);

            return result.OverallState switch
            {
                StorePackageUpdateState.Completed => (true, SchedulePackagedRelaunchAfterExit()),

                // Bytes are down and the install is queued behind our exit. That is success from
                // here: reporting it as a failure would leave the user pressing a button that
                // has already done its job.
                StorePackageUpdateState.Deploying => (true, SchedulePackagedRelaunchAfterExit()),
                StorePackageUpdateState.Canceled => (false, "Update was cancelled in the Microsoft Store dialog."),
                StorePackageUpdateState.ErrorLowBattery => (false, "Update paused because the device battery is too low."),
                StorePackageUpdateState.ErrorWiFiRecommended => (false, "Update was paused because a non-metered connection is recommended."),
                StorePackageUpdateState.ErrorWiFiRequired => (false, "Update requires Wi-Fi before the Microsoft Store can continue."),
                StorePackageUpdateState.OtherError => (false, BuildStoreUpdateErrorMessage(result)),
                _ => (false, "The Microsoft Store could not install the update."),
            };
        }
        catch (OperationCanceledException)
        {
            return (false, "Update was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to install update from the Microsoft Store");
            return (false, $"Microsoft Store update failed: {ex.Message}");
        }
    }

    internal static string GetCurrentVersion()
    {
        var asm = typeof(UpdateService).Assembly.GetName();
        return $"v{asm.Version!.Major}.{asm.Version.Minor}.{asm.Version.Build}";
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            _ = Package.Current.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatPackageVersion(PackageVersion version)
    {
        return $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private static Version PackageVersionToVersion(PackageVersion version)
    {
        return new Version(version.Major, version.Minor, version.Build, version.Revision);
    }

    /// <summary>
    /// One place mapping a Store update state to something worth showing the user, so the
    /// download and install halves cannot describe the same condition differently.
    /// </summary>
    private static string DescribeStoreUpdateState(
        StorePackageUpdateState state, StorePackageUpdateResult result) => state switch
    {
        StorePackageUpdateState.Canceled => "Update was cancelled in the Microsoft Store dialog.",
        StorePackageUpdateState.ErrorLowBattery => "Update paused because the device battery is too low.",
        StorePackageUpdateState.ErrorWiFiRecommended => "Update was paused because a non-metered connection is recommended.",
        StorePackageUpdateState.ErrorWiFiRequired => "Update requires Wi-Fi before the Microsoft Store can continue.",
        StorePackageUpdateState.OtherError => BuildStoreUpdateErrorMessage(result),
        _ => "The Microsoft Store could not download the update.",
    };

    private static string BuildStoreUpdateErrorMessage(StorePackageUpdateResult result)
    {
        foreach (var status in result.StorePackageUpdateStatuses)
        {
            if (status.PackageUpdateState == StorePackageUpdateState.Completed)
                continue;

            return status.PackageUpdateState switch
            {
                StorePackageUpdateState.ErrorLowBattery => "Update paused because the device battery is too low.",
                StorePackageUpdateState.ErrorWiFiRecommended => "Update was paused because a non-metered connection is recommended.",
                StorePackageUpdateState.ErrorWiFiRequired => "Update requires Wi-Fi before the Microsoft Store can continue.",
                StorePackageUpdateState.Canceled => "Update was cancelled in the Microsoft Store dialog.",
                _ => "The Microsoft Store could not install the update. Try again later.",
            };
        }

        return "The Microsoft Store could not install the update. Try again later.";
    }

    /// <summary>
    /// Arranges for the app to come back after it exits, so Windows can apply an update that is
    /// already staged. The caller exits; this only sets the return trip up.
    /// </summary>
    /// <remarks>
    /// An MSIX package cannot be installed while any of its processes are running, and Little
    /// Launcher lives in the tray and starts with Windows — so a staged update can sit unapplied
    /// indefinitely while the Store reports the app as up to date. Restarting applies anything
    /// staged without needing to detect it, which matters because there is no reliable way to
    /// ask: <c>Package.CheckUpdateAvailabilityAsync</c> only covers .appinstaller installs, not
    /// Store-distributed packages.
    /// </remarks>
    public static void RestartToApplyPackagedUpdate()
    {
        RegisterApplicationRestart("--silent", 0);
        SchedulePackagedRelaunchAfterExit();
    }

    private static string SchedulePackagedRelaunchAfterExit()
    {
        try
        {
            string aumid = $"{Package.Current.Id.FamilyName}!{PackagedApplicationId}";
            string tempDir = Path.Combine(Path.GetTempPath(), "LittleLauncher-Update");
            Directory.CreateDirectory(tempDir);

            int pid = Environment.ProcessId;
            string scriptPath = Path.Combine(tempDir, "restart-store-update.cmd");
            string script = $"""
                @echo off
                :wait
                tasklist /FI "PID eq {pid}" 2>NUL | find /I "{pid}" >NUL
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >NUL
                    goto wait
                )
                timeout /t 2 /nobreak >NUL
                start "" explorer.exe "shell:AppsFolder\{aumid}"
                """;
            File.WriteAllText(scriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            return "Little Launcher will relaunch after the Microsoft Store finishes applying the update.";
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to schedule relaunch after Microsoft Store update");
            return "Update installed. Restart Little Launcher manually if it does not relaunch automatically.";
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var clean = tag.TrimStart('v', 'V');
        return Version.TryParse(clean, out var v) ? v : null;
    }

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

[JsonSerializable(typeof(UpdateService.GitHubRelease))]
internal partial class GitHubJsonContext : JsonSerializerContext
{
}
