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

        /// <summary>
        /// The browser extensions this machine has, by identity — never their contents.
        /// </summary>
        /// <remarks>
        /// Null on a payload written before extensions existed, which is the difference between
        /// "no extensions" and "this machine cannot say". Only the first is a reason to uninstall
        /// anything, so the merge treats null as no instruction at all.
        /// </remarks>
        public List<BrowserExtension>? Extensions { get; set; }
    }

    /// <summary>
    /// Serialize launchers to a seekable stream as JSON, wrapped in a timestamped envelope.
    /// </summary>
    public static MemoryStream Serialize(ObservableCollection<Launcher> launchers)
    {
        var envelope = new LaunchersEnvelope
        {
            LastModified = DateTimeOffset.UtcNow,
            Launchers = new List<Launcher>(launchers),

            // Identity only — BrowserExtension.Folder is [JsonIgnore] because it names a path that
            // exists on exactly one machine.
            Extensions = BrowserExtensionService.Portable(),
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
    public static (List<Launcher>? Launchers, DateTimeOffset? LastModified, List<BrowserExtension>? Extensions)
        Deserialize(Stream stream)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<LaunchersEnvelope>(stream, JsonOptions);
            if (envelope?.Launchers != null && envelope.Launchers.Count > 0)
                return (envelope.Launchers, envelope.LastModified, envelope.Extensions);
        }
        catch { }

        // Fall back to legacy plain array
        stream.Position = 0;
        try
        {
            var list = JsonSerializer.Deserialize<List<Launcher>>(stream, JsonOptions);
            return (list, null, null);
        }
        catch
        {
            return (null, null, null);
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
    /// <param name="extensions">
    /// The browser extensions the other machine had, or <c>null</c> for a payload written before
    /// they were carried. Null is "cannot say" and means no reconciliation — an empty list is a
    /// real answer and does uninstall, so the two must stay distinguishable.
    /// </param>
    public static async Task ApplyAsync(List<Launcher> launchers, List<BrowserExtension>? extensions = null)
    {
        var rebound = await MergeAsync(launchers);

        if (extensions != null) await BrowserExtensionService.ReconcileAsync(extensions);

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

        // Rebuild the flyouts of the launchers whose item objects were replaced above. A flyout's
        // rows hold the item objects themselves, and its own rebuild check is a content hash of
        // what it draws, which a change to an item's arguments does not move.
        // Without this the flyout is left bound to items the launcher no longer contains, and
        // every operation that finds an item by reference (remove, move, edit) silently does
        // nothing, while a drag reorder writes the stale objects back over the merge.
        if (rebound.Count > 0)
            await RunOnUiThreadAsync(() =>
            {
                foreach (string id in rebound)
                    Windows.FlyoutWindow.InvalidateItems(id, force: true);
            });
    }

    /// <summary>
    /// Merge downloaded launchers into the existing Launchers collection on the UI thread.
    /// Existing launchers are updated in-place (preserving object references for PropertyChanged
    /// subscriptions and FlyoutWindow instances). New launchers are added; missing ones removed.
    /// </summary>
    /// <returns>
    /// The ids of the launchers whose item objects were replaced, which is what the caller has to
    /// rebuild a flyout for.
    /// </returns>
    private static async Task<List<string>> MergeAsync(List<Launcher> launchers)
    {
        var rebound = new List<string>();
        await RunOnUiThreadAsync(() => MergeLaunchers(launchers, rebound));
        return rebound;

        static void MergeLaunchers(List<Launcher> launchers, List<string> rebound)
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
                    if (CopyInto(existing, downloaded))
                        rebound.Add(existing.Id);
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
    /// Collections are cleared and refilled for the same reason, and <c>Items</c> goes further:
    /// it is left alone entirely unless the download differs, because the objects inside it are
    /// what a flyout's rows are bound to.</para>
    /// <para><b>This must list every property that should travel between machines.</b> A newly
    /// downloaded launcher is added wholesale and therefore carries everything, so anything
    /// missing here fails only on the *second* machine and only for launchers that already
    /// exist — which is why the omissions below went unnoticed for so long: view mode,
    /// icons-per-row, title visibility and the whole bookmark bar never propagated at all.
    /// When adding a property to <see cref="Launcher"/>, add it here too.</para>
    /// </remarks>
    /// <returns>Whether the item objects were replaced, which orphans anything bound to them.</returns>
    private static bool CopyInto(Launcher existing, Launcher downloaded)
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

        // Legacy, and synced for exactly one reason: a launcher edited on a machine still running
        // a build from before one address and a bar of them merged arrives carrying its address
        // here and nothing in WebBookmarks. MigrateWebModel at the foot of this method turns it
        // into the first bookmark, the same way loading an old settings file does.
        existing.WebUrl = downloaded.WebUrl;
        existing.WebDefaultBookmarkUrl = downloaded.WebDefaultBookmarkUrl;
        existing.WebFlyoutWidth = downloaded.WebFlyoutWidth;
        existing.WebFlyoutHeight = downloaded.WebFlyoutHeight;
        existing.WebZoomPercent = downloaded.WebZoomPercent;
        existing.WebHiddenPolicy = downloaded.WebHiddenPolicy;
        existing.WebIdleUnloadMinutes = downloaded.WebIdleUnloadMinutes;
        existing.WebReloadOnShow = downloaded.WebReloadOnShow;
        existing.WebLinksInBrowser = downloaded.WebLinksInBrowser;
        existing.WebPinFlyout = downloaded.WebPinFlyout;
        existing.WebSharedProfile = downloaded.WebSharedProfile;
        existing.WebAnchor = downloaded.WebAnchor;

        // These six were added to the model and the settings UI and never added here, so they were
        // silently local-only: a launcher set up as a regular window on one machine arrived on the
        // other as a flyout, and turning its address bar on had to be done twice. Every one of them
        // is a decision about the launcher rather than a record of one machine's state, which is the
        // line that decides what travels — see WebFlyoutPosition below for the other side of it.
        //
        // Anything added to Launcher's Web* properties belongs in this method unless it fails that
        // test. The gap is invisible until someone uses two machines.
        existing.WebShowAddressBar = downloaded.WebShowAddressBar;
        existing.WebAlwaysShowTabs = downloaded.WebAlwaysShowTabs;
        existing.WebRegularWindow = downloaded.WebRegularWindow;
        existing.WebWindowAutoHide = downloaded.WebWindowAutoHide;
        existing.WebTaskbarClickCloses = downloaded.WebTaskbarClickCloses;
        existing.WebAllowAllPermissions = downloaded.WebAllowAllPermissions;
        // Legacy, and synced for the same reason WebUrl is: a launcher edited on a machine running
        // an older build arrives saying it remembered its position, and MigrateWebModel at the foot
        // of this method turns that into the WebAnchors.LastPosition it now means.
        existing.WebRememberPosition = downloaded.WebRememberPosition;

        // Travels, unlike WebFlyoutPosition below, because it is a decision about the launcher
        // rather than a record of where a window ended up on one machine's monitors — and the
        // size it locks (WebFlyoutWidth/Height) is synced two lines up, so leaving it behind would
        // send the size without the rule that governs it.
        existing.WebLockSize = downloaded.WebLockSize;

        // Deliberately NOT synced: WebSessionTabs / WebSessionActiveTab. What one machine had open
        // is not a preference about the launcher, and restoring another machine's tabs would both
        // surprise and cost a browser each. Same reasoning as the position below.
        //
        // Deliberately NOT synced: WebFlyoutPosition. It is a remembered pixel position on one
        // machine's monitor layout, not a preference — copying it lands the flyout somewhere
        // arbitrary on a different display arrangement, or entirely off-screen. WebAnchor above
        // is the part the user actually chose, and it does travel.

        // ── Bookmark bar ────────────────────────────────────────────
        existing.WebBookmarkIconsOnly = downloaded.WebBookmarkIconsOnly;

        // IconPath travels with the bookmark even though it is a local path: it is derived from
        // the launcher id and URL, so it names the same location on both machines. The file may
        // not exist here yet, which the bookmark bar already handles by drawing no icon until
        // the page has been visited and its icon adopted.
        existing.WebBookmarks.Clear();
        foreach (var bookmark in downloaded.WebBookmarks)
            existing.WebBookmarks.Add(bookmark);

        // ── Items ───────────────────────────────────────────────────
        // Only when they differ, and this is not an optimisation. Refilling the collection with
        // equal-valued copies is invisible in the settings file and breaks every open flyout: its
        // rows hold the item objects, so replacing them leaves it displaying items the launcher
        // no longer contains, and remove, move and edit all look for the row's item by reference
        // and quietly find nothing. Most downloads carry items identical to the ones already
        // here, which is how a periodic sync came to disable editing until the app was restarted.
        bool itemsChanged = !ItemsMatch(existing.Items, downloaded.Items);
        if (itemsChanged)
        {
            existing.Items.Clear();
            foreach (var item in downloaded.Items)
                existing.Items.Add(item);
        }

        // Last, and after the bookmarks are in place: a launcher sent by an older build carries its
        // address in the legacy fields, and this is what turns that into a first bookmark. Running
        // it on every merge rather than only at startup is the whole point — the old build goes on
        // writing those fields for as long as the other machine is not upgraded.
        existing.MigrateWebModel();

        return itemsChanged;
    }

    /// <summary>
    /// Run <paramref name="action"/> on the UI thread, from whichever thread the caller is on.
    /// </summary>
    /// <remarks>
    /// A download runs on a background thread, but everything it applies (the launcher
    /// collection and the windows bound to it) belongs to the UI thread.
    /// </remarks>
    private static async Task RunOnUiThreadAsync(Action action)
    {
        if (DispatcherQueue.GetForCurrentThread() != null)
        {
            action();
            return;
        }

        var tcs = new TaskCompletionSource();
        App.MainDispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        await tcs.Task;
    }

    /// <summary>
    /// Whether two item lists are identical in every field that travels between machines,
    /// nested children included.
    /// </summary>
    /// <remarks>
    /// <para>Compares the serialized form rather than field by field, because that is the wire
    /// format these payloads already agree on: it cannot fall behind a property added to
    /// <see cref="LauncherItem"/> later, and a comparison that quietly stopped covering a new
    /// field would drop the very changes the caller is applying.</para>
    /// <para>Callers use this to leave the collection alone when a download carries the same
    /// items it already has: refilling it with equal-valued copies is not the no-op it looks
    /// like, because the objects are what every open flyout is bound to.</para>
    /// </remarks>
    internal static bool ItemsMatch(IEnumerable<LauncherItem> left, IEnumerable<LauncherItem> right)
    {
        try
        {
            return JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);
        }
        catch (Exception ex)
        {
            // An answer that cannot be worked out is "changed": replacing the items is the older
            // behaviour and is always correct, only wasteful.
            Logger.Debug(ex, "Could not compare launcher items; treating them as changed");
            return false;
        }
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
