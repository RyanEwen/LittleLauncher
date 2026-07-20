using LittleLauncher.Classes.Settings;
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

        // GetAppAndOptionalStorePackageUpdatesAsync lists every package the Store can
        // update — including framework dependencies — and can list a package at the same
        // version we already have (e.g. right after a submission is published, before the
        // update graph settles). Only look at the main app package, and only report an
        // update when it is strictly newer than what's installed; otherwise the UI would
        // offer an "update" to the same version the user is already running.
        var latestVersion = updates
            .Where(update => update.Package != null && string.Equals(
                update.Package.Id.FamilyName, currentPackageName, StringComparison.OrdinalIgnoreCase))
            .Select(update => PackageVersionToVersion(update.Package!.Id.Version))
            .DefaultIfEmpty(currentPackageVersion)
            .Max() ?? currentPackageVersion;

        bool updateAvailable = latestVersion > currentPackageVersion;

        return new UpdateCheckResult
        {
            Source = UpdateSource.MicrosoftStore,
            UpdateAvailable = updateAvailable,
            CurrentVersion = currentVersion,
            LatestVersion = updateAvailable
                ? $"v{latestVersion.Major}.{latestVersion.Minor}.{latestVersion.Build}"
                : currentVersion,
        };
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
                return (false, "No updates are currently available in the Microsoft Store.");

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
