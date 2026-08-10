using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// The <c>launchers.json</c> payload shared by every global sync transport: its wire format, the
/// guard that stops an automatic download overwriting newer local work, and the merge that
/// applies a downloaded copy to the live launcher collection.
/// </summary>
/// <remarks>
/// <para>This exists so <see cref="SftpSyncService"/> and <see cref="FolderSyncService"/> cannot
/// drift apart. The format has to stay identical or a user syncing one machine over SFTP and
/// another over OneDrive would not be able to move between them, and — more importantly — the
/// newer-local guard has a history of data loss when it was applied unevenly (it originally ran
/// on the startup download only, so periodic syncs overwrote local edits every few minutes).
/// Duplicating that logic per transport is how it comes back.</para>
/// </remarks>
internal static class LauncherPayload
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>The file written to the remote server or sync folder.</summary>
    public const string FileName = "launchers.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Envelope for the remote launchers file. Includes a UTC timestamp so downloads can skip
    /// overwriting newer local data.
    /// </summary>
    private sealed class LaunchersEnvelope
    {
        public DateTimeOffset LastModified { get; set; }
        public List<Launcher> Launchers { get; set; } = [];
    }

    /// <summary>
    /// Serialize launchers to a seekable stream as JSON, wrapped in a timestamped envelope.
    /// </summary>
    public static MemoryStream Serialize(ObservableCollection<Launcher> launchers)
    {
        var envelope = new LaunchersEnvelope
        {
            LastModified = DateTimeOffset.UtcNow,
            Launchers = new List<Launcher>(launchers)
        };
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, envelope, JsonOptions);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Deserialize launchers from a JSON stream, which must be seekable.
    /// Supports both the envelope format and the legacy plain array format.
    /// Returns the launcher list and an optional timestamp (null for legacy data).
    /// </summary>
    public static (List<Launcher>? Launchers, DateTimeOffset? LastModified) Deserialize(Stream stream)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<LaunchersEnvelope>(stream, JsonOptions);
            if (envelope?.Launchers != null && envelope.Launchers.Count > 0)
                return (envelope.Launchers, envelope.LastModified);
        }
        catch { }

        // Fall back to legacy plain array
        stream.Position = 0;
        try
        {
            var list = JsonSerializer.Deserialize<List<Launcher>>(stream, JsonOptions);
            return (list, null);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Decide whether a download must be abandoned because local changes are newer.
    /// </summary>
    /// <param name="remoteTimestamp">The envelope timestamp, or null for legacy data.</param>
    /// <param name="force">
    /// Set only for a download the user explicitly asked for. Automatic syncs must never
    /// overrule newer local work.
    /// </param>
    /// <param name="reason">Filled in with a user-facing explanation when the download is blocked.</param>
    /// <returns>True when the caller must return without applying anything.</returns>
    public static bool ShouldSkipDownload(DateTimeOffset? remoteTimestamp, bool force, out string reason)
    {
        reason = "";
        if (force) return false;

        var localModified = SettingsManager.Current.LaunchersModifiedUtc;
        if (localModified != default)
        {
            Logger.Info($"Download skipped: local launcher changes at {localModified:u} have not been uploaded yet");
            reason = "Local launcher changes are newer than the server; skipped download.";
            return true;
        }

        if (remoteTimestamp.HasValue)
        {
            var localSettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LittleLauncher", "settings.json");
            if (File.Exists(localSettingsPath))
            {
                var localFileModified = File.GetLastWriteTimeUtc(localSettingsPath);
                if (localFileModified > remoteTimestamp.Value.UtcDateTime)
                {
                    Logger.Info($"Download skipped: local settings ({localFileModified:u}) " +
                                $"are newer than remote ({remoteTimestamp.Value.UtcDateTime:u})");
                    reason = "Local settings are newer than the sync location; skipped download.";
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Apply a downloaded launcher list: merge it in, normalize legacy glyphs, fetch any missing
    /// icons, and save. The single tail end of every successful global download.
    /// </summary>
    public static async Task ApplyAsync(List<Launcher> launchers)
    {
        await MergeAsync(launchers);

        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            foreach (var item in launcher.Items)
            {
                item.NormalizeGlyph();
                if (item.IsGroup)
                    foreach (var child in item.Children)
                        child.NormalizeGlyph();
            }
            await FaviconService.FetchMissingItemIconsAsync(launcher.Items);
        }

        SettingsManager.SaveSettings();
    }

    /// <summary>
    /// Merge downloaded launchers into the existing Launchers collection on the UI thread.
    /// Existing launchers are updated in-place (preserving object references for PropertyChanged
    /// subscriptions and FlyoutWindow instances). New launchers are added; missing ones removed.
    /// </summary>
    private static async Task MergeAsync(List<Launcher> launchers)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher != null)
        {
            MergeLaunchers(launchers);
        }
        else
        {
            var tcs = new TaskCompletionSource();
            App.MainDispatcherQueue.TryEnqueue(() =>
            {
                MergeLaunchers(launchers);
                tcs.SetResult();
            });
            await tcs.Task;
        }

        static void MergeLaunchers(List<Launcher> launchers)
        {
            var current = SettingsManager.Current.Launchers;
            var downloadedById = launchers.ToDictionary(l => l.Id);

            // Remove launchers that no longer exist on the server
            for (int i = current.Count - 1; i >= 0; i--)
            {
                if (!downloadedById.ContainsKey(current[i].Id))
                {
                    Windows.LauncherPanels.Dispose(current[i].Id);
                    current.RemoveAt(i);
                }
            }

            // Update existing launchers in-place; add new ones
            foreach (var downloaded in launchers)
            {
                var existing = current.FirstOrDefault(l => l.Id == downloaded.Id);
                if (existing != null)
                {
                    CopyInto(existing, downloaded);
                }
                else
                {
                    current.Add(downloaded);
                }
            }
        }
    }

    /// <summary>
    /// Copy every synced property of <paramref name="downloaded"/> onto the live
    /// <paramref name="existing"/> launcher, in place.
    /// </summary>
    /// <remarks>
    /// <para><b>In place is the point.</b> The existing object is kept so `PropertyChanged`
    /// subscriptions and the launcher's `FlyoutWindow` / `WebFlyoutWindow` instance survive the
    /// download; replacing the reference would leave every open panel bound to an orphan.
    /// Collections are cleared and refilled for the same reason.</para>
    /// <para><b>This must list every property that should travel between machines.</b> A newly
    /// downloaded launcher is added wholesale and therefore carries everything, so anything
    /// missing here fails only on the *second* machine and only for launchers that already
    /// exist — which is why the omissions below went unnoticed for so long: view mode,
    /// icons-per-row, title visibility and the whole bookmark bar never propagated at all.
    /// When adding a property to <see cref="Launcher"/>, add it here too.</para>
    /// </remarks>
    private static void CopyInto(Launcher existing, Launcher downloaded)
    {
        // ── Identity and tray presence ──────────────────────────────
        existing.Name = downloaded.Name;
        existing.TrayIconMode = downloaded.TrayIconMode;
        existing.CustomTrayIconPath = downloaded.CustomTrayIconPath;
        existing.NIconHide = downloaded.NIconHide;

        // ── Flyout presentation ─────────────────────────────────────
        existing.ViewMode = downloaded.ViewMode;
        existing.IconModeIconsPerRow = downloaded.IconModeIconsPerRow;
        existing.ShowTitle = downloaded.ShowTitle;

        // ── Web launcher ────────────────────────────────────────────
        // A web launcher carries no items, so without these it would arrive on the
        // other machine as an empty shortcut launcher.
        existing.Kind = downloaded.Kind;
        existing.WebUrl = downloaded.WebUrl;
        existing.WebFlyoutWidth = downloaded.WebFlyoutWidth;
        existing.WebFlyoutHeight = downloaded.WebFlyoutHeight;
        existing.WebZoomPercent = downloaded.WebZoomPercent;
        existing.WebHiddenPolicy = downloaded.WebHiddenPolicy;
        existing.WebIdleUnloadMinutes = downloaded.WebIdleUnloadMinutes;
        existing.WebReloadOnShow = downloaded.WebReloadOnShow;
        existing.WebPinFlyout = downloaded.WebPinFlyout;
        existing.WebSharedProfile = downloaded.WebSharedProfile;
        existing.WebAnchor = downloaded.WebAnchor;
        existing.WebRememberPosition = downloaded.WebRememberPosition;

        // Travels, unlike WebFlyoutPosition below, because it is a decision about the launcher
        // rather than a record of where a window ended up on one machine's monitors — and the
        // size it locks (WebFlyoutWidth/Height) is synced two lines up, so leaving it behind would
        // send the size without the rule that governs it.
        existing.WebLockSize = downloaded.WebLockSize;

        // Deliberately NOT synced: WebFlyoutPosition. It is a remembered pixel position on one
        // machine's monitor layout, not a preference — copying it lands the flyout somewhere
        // arbitrary on a different display arrangement, or entirely off-screen. WebAnchor above
        // is the part the user actually chose, and it does travel.

        // ── Bookmark bar ────────────────────────────────────────────
        existing.WebUseBookmarks = downloaded.WebUseBookmarks;
        existing.WebDefaultBookmarkUrl = downloaded.WebDefaultBookmarkUrl;

        // IconPath travels with the bookmark even though it is a local path: it is derived from
        // the launcher id and URL, so it names the same location on both machines. The file may
        // not exist here yet, which the bookmark bar already handles by drawing no icon until
        // the page has been visited and its icon adopted.
        existing.WebBookmarks.Clear();
        foreach (var bookmark in downloaded.WebBookmarks)
            existing.WebBookmarks.Add(bookmark);

        // ── Items ───────────────────────────────────────────────────
        existing.Items.Clear();
        foreach (var item in downloaded.Items)
            existing.Items.Add(item);
    }

    /// <summary>
    /// Write a file by way of a temporary file in the same directory, then move it into place.
    /// </summary>
    /// <remarks>
    /// Cloud clients watch their folders and upload the moment a file changes, so a plain write
    /// gives them a window in which to upload a half-written file — and the other machine then
    /// reads truncated JSON over a working configuration. A move within one volume is atomic, so
    /// the file is only ever seen whole. Falls back to a direct write if the move is refused,
    /// which some virtual drives do.
    /// </remarks>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Atomic write to {path} failed; writing directly");
            TryDelete(temp);
            File.WriteAllBytes(path, bytes);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Could not remove temporary file {path}");
        }
    }
}
