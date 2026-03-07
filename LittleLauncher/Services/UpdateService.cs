using LittleLauncher.Classes.Settings;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace LittleLauncher.Services;

/// <summary>
/// Checks GitHub Releases for newer versions and optionally downloads/installs them.
/// </summary>
public static class UpdateService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

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
        public bool UpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string? ReleaseUrl { get; init; }
        public string? MsiDownloadUrl { get; init; }
        public string? ReleaseNotes { get; init; }
    }

    /// <summary>
    /// Checks GitHub for a newer release.
    /// Returns null on network/parse errors.
    /// </summary>
    public static async Task<UpdateCheckResult?> CheckForUpdateAsync()
    {
        try
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
                UpdateAvailable = updateAvailable,
                CurrentVersion = $"v{current.Major}.{current.Minor}.{current.Build}",
                LatestVersion = release.TagName,
                ReleaseUrl = release.HtmlUrl,
                MsiDownloadUrl = msiUrl,
                ReleaseNotes = release.Body,
            };
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to check for updates");
            return null;
        }
    }

    /// <summary>
    /// Downloads the MSI installer to a temp directory and launches it.
    /// Returns true if the installer was launched successfully.
    /// </summary>
    public static async Task<(bool Success, string Message)> DownloadAndInstallAsync(
        string msiUrl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "LittleLauncher-Update");
            Directory.CreateDirectory(tempDir);
            string msiPath = Path.Combine(tempDir, Path.GetFileName(new Uri(msiUrl).LocalPath));

            // Download with progress
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

            // Remove the Mark of the Web (Zone.Identifier ADS) so SmartScreen
            // doesn't block the MSI that was downloaded from the internet.
            try
            {
                string zoneFile = msiPath + ":Zone.Identifier";
                File.Delete(zoneFile);
            }
            catch { /* ADS removal is best-effort */ }

            // Launch a helper script that waits for this process to exit,
            // then runs the MSI installer. This avoids file-lock failures
            // when the installer tries to replace the running executable.
            // The MSI is per-user (no elevation needed). A helper script
            // waits for the app to exit before launching the installer.
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
                start "" msiexec /i "{msiPath}"
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

            return (true, "Installer launched successfully.");
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

    internal static string GetCurrentVersion()
    {
        var asm = typeof(UpdateService).Assembly.GetName();
        return $"v{asm.Version!.Major}.{asm.Version.Minor}.{asm.Version.Build}";
    }

    private static Version? ParseVersion(string tag)
    {
        // Strip leading 'v' if present
        var clean = tag.TrimStart('v', 'V');
        return Version.TryParse(clean, out var v) ? v : null;
    }

    // ── GitHub API DTOs ─────────────────────────────────────────────

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
