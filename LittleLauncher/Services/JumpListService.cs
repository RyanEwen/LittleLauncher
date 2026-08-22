// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Services;

/// <summary>
/// Publishes each pinned launcher's contents as the jump list on its taskbar button: the menu
/// Windows shows when a pinned icon is right-clicked.
/// </summary>
/// <remarks>
/// <para><b>A jump list belongs to an AppUserModelID</b>, not to a process, and every pinned
/// launcher already has one of its own in <see cref="Launcher.PinAumid"/>. That is what makes a
/// separate list per launcher possible from a single app: the shell is told which identity is
/// being published for, one launcher at a time. A launcher that has never been pinned has no
/// AUMID and is skipped, so nothing is written for launchers that could not show it.</para>
/// <para><b>The tasks are the launcher's own contents</b>: its items, flattened exactly as the
/// flyout flattens them, or its bookmarks for a web launcher. The right-click menu is a shorter
/// version of what the button opens. Windows caps how many entries it will show, and the cap is
/// whatever the user's recent-items setting works out to, so the list is simply cut at that
/// point: the first N, in flyout order.</para>
/// <para><b>Then the launcher's own commands</b> - its settings, editing its items, opening it in a
/// real browser, the app's settings. They are what the menu is <em>for</em> as much as the entries
/// are, so they are never the part that gets cut: the entries are trimmed to whatever room is left
/// after them, not the other way round. A launcher with nothing to list still gets them, which is
/// the one case where the menu is worth having and the entries are not.</para>
/// <para><b>No separator between the two, though one is available.</b>
/// <c>PKEY_AppUserModel_IsDestListSeparator</c> on an otherwise empty shell link draws a rule, and
/// it was tried: the shell renders it noticeably brighter than the dividers it draws around its own
/// Pin and Close rows, so a menu with one looks broken rather than organised. The commands read as
/// a group on their own, being the only rows with no site icon.</para>
/// <para><b>Nothing here can be a live menu.</b> The shell never tells an app its jump list was
/// opened, nor that anything in it was removed; the list is a static thing that sits in the user's
/// profile until it is republished. So it is republished whenever settings are saved, debounced,
/// and skipped entirely when the resulting list is identical to the one already out there, which
/// is the common case since most saves have nothing to do with a pinned launcher's items.</para>
/// <para><b>The heading is Windows' word "Tasks", and that is not a choice.</b> Only a custom
/// category can carry a name of ours, and a category is made of <em>destinations</em>, which the
/// shell requires to be file types the publishing app is registered to open - checked against the
/// AppUserModelID being published for. A per-launcher AUMID is registered for nothing at all: it is
/// minted at pin time with a tick in it, so it is a fresh identity on every pin with nothing to
/// hang a file association on. Measured, on three launchers, freshly published:
/// <c>AppendCategory</c> returns <c>E_ACCESSDENIED</c>. The property that makes one list per
/// launcher possible is the same one that forbids naming it, and it is also why no entry offers
/// "Remove from this list" - that verb belongs to destinations. See pinvoke.md before trying
/// again.</para>
/// <para><b>A task can only be a command line</b>, because it is a shell link. Each one runs the
/// companion exe with the item's position and a token, and the companion posts that to the running
/// app the same way a pinned click already posts "show the flyout": see
/// <c>LauncherPanels.LaunchFromJumpList</c> for the other end. The token is what makes a stale
/// pin safe, because it identifies the item by content. A list left over from before an edit
/// either still finds its item or falls back to opening the launcher, and never launches whatever
/// happens to have taken that position.</para>
/// </remarks>
internal static class JumpListService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>How long a settings save waits before the list is rebuilt.</summary>
    /// <remarks>
    /// Long enough that a burst of saves (a drag that reorders several items, a sync merge)
    /// produces one publish rather than one each. Nothing is watching the menu meanwhile.
    /// </remarks>
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(2);

    /// <summary>The most entries to publish when the shell's own answer is unusable.</summary>
    /// <remarks>
    /// <c>BeginList</c> reports how many slots the user's settings allow, which is the number to
    /// respect. It can come back as 0, meaning the user has turned recent items off entirely, and
    /// this is the fallback for that: user tasks are not recent items and are still shown.
    /// </remarks>
    private const int FallbackSlots = 10;

    /// <summary>A hard ceiling on tasks, whatever the shell says it has room for.</summary>
    private const int MaxSlots = 30;

    /// <summary>
    /// Joins the parts of a hashed key, so two different splits of the same characters cannot
    /// hash alike. A control character precisely because no name, path or URL contains one.
    /// </summary>
    private static readonly string Unit = new((char)31, 1);

    private static DispatcherQueue? _queue;
    private static DispatcherQueueTimer? _debounce;
    /// <summary>The signature last published per launcher, so an unchanged list costs nothing.</summary>
    private static readonly Dictionary<string, string> _published = [];

    // ── Entry points ────────────────────────────────────────────────

    /// <summary>
    /// Binds the service to the UI thread and publishes the first lists.
    /// </summary>
    /// <remarks>
    /// Called once from <c>MainWindow</c> after the tray icons exist. Everything before this is a
    /// no-op rather than an error: settings are loaded and saved well before the window is up.
    /// </remarks>
    internal static void Initialize(DispatcherQueue queue)
    {
        _queue = queue;
        _debounce = queue.CreateTimer();
        _debounce.Interval = DebounceInterval;
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => PublishAll();

        // Every path that changes a launcher ends in a save, whether it was an item edited in the
        // flyout, a bookmark renamed from the bar, a rename in settings or a sync merge. Watching
        // the save is what makes this one subscription rather than a call added to each of them
        // and forgotten by the next one.
        SettingsManager.Saved += RequestRefresh;

        RequestRefresh();
    }

    /// <summary>Rebuilds every pinned launcher's list shortly, coalescing repeat calls.</summary>
    internal static void RequestRefresh()
    {
        var queue = _queue;
        if (queue == null) return;

        if (queue.HasThreadAccess)
            RestartDebounce();
        else
            queue.TryEnqueue(RestartDebounce);
    }

    /// <summary>
    /// Drops the jump list of a launcher that is going away.
    /// </summary>
    /// <remarks>
    /// A refresh cannot do this: it only knows the launchers that still exist, and a deleted one
    /// takes its AUMID with it. Left behind, the list outlives the launcher and keeps answering on
    /// a pin the user has yet to remove.
    /// </remarks>
    internal static void Remove(Launcher launcher)
    {
        string aumid = ResolvePinAumid(launcher);
        if (string.IsNullOrEmpty(aumid)) return;

        _published.Remove(launcher.Id);
        RunOnStaThread(() => DeleteList(aumid));
    }

    // ── Identity ────────────────────────────────────────────────────

    /// <summary>
    /// A stable number for what an item <em>is</em>, used to recognise it again when a task from
    /// an older jump list is clicked.
    /// </summary>
    /// <remarks>
    /// Content, not position: the position is only a hint that saves a search, and an item's
    /// position changes every time anything above it is added, moved or removed. There is no id on
    /// <see cref="LauncherItem"/> to use instead, and adding one would change the sync wire format
    /// and the item comparison the merge depends on. A hash of the fields that decide what the
    /// item launches costs nothing and cannot drift between machines.
    /// </remarks>
    internal static int ItemToken(LauncherItem item) =>
        StableHash(string.Join(Unit, item.Name, item.Path, item.Arguments));

    /// <summary>The same, for a web launcher's bookmark.</summary>
    internal static int BookmarkToken(WebBookmark bookmark) =>
        StableHash(string.Join(Unit, bookmark.Name, bookmark.Url));

    /// <summary>
    /// FNV-1a, trimmed to 30 bits so the result is positive and survives the trip through a
    /// window message's <c>LPARAM</c> and back.
    /// </summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash & 0x3FFFFFFF);
        }
    }

    // ── Building the list (UI thread) ───────────────────────────────

    private static void RestartDebounce()
    {
        _debounce?.Stop();
        _debounce?.Start();
    }

    /// <summary>
    /// Snapshots every pinned launcher on the UI thread, then hands the result to a worker.
    /// </summary>
    /// <remarks>
    /// The split is the point. Rasterising icons and talking to the shell is slow enough to be
    /// visible if it happened here, and the launcher objects are observable collections the UI
    /// thread is free to be editing, so the worker is given plain values and never sees them.
    /// </remarks>
    private static void PublishAll()
    {
        try
        {
            string companion = CompanionExePath();
            if (!File.Exists(companion))
            {
                Logger.Debug("No companion exe at {Path}; skipping jump lists", companion);
                return;
            }

            bool dark = ThemeManager.IsDarkTheme();
            var plans = new List<JumpListPlan>();
            var stale = new HashSet<string>(StringComparer.Ordinal);
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var launcher in SettingsManager.Current.Launchers.ToList())
            {
                string aumid = ResolvePinAumid(launcher);
                if (string.IsNullOrEmpty(aumid)) continue;

                var plan = BuildPlan(launcher, aumid, companion, dark);
                foreach (var task in plan.Entries.Concat(plan.Actions))
                    live.Add(CacheFileName(task.IconSource));

                if (!_published.TryGetValue(launcher.Id, out var previous) || previous != plan.Signature)
                    stale.Add(launcher.Id);

                plans.Add(plan);
            }

            if (plans.Count == 0) return;

            RunOnStaThread(() =>
            {
                foreach (var plan in plans)
                {
                    if (!stale.Contains(plan.LauncherId)) continue;

                    if (Publish(plan))
                        MarkPublished(plan);
                }

                SweepIconCache(live);
            });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to prepare taskbar jump lists");
        }
    }

    private static void MarkPublished(JumpListPlan plan)
    {
        var queue = _queue;
        if (queue == null) return;

        queue.TryEnqueue(() => _published[plan.LauncherId] = plan.Signature);
    }

    private static JumpListPlan BuildPlan(Launcher launcher, string aumid, string companion, bool dark)
    {
        var entries = launcher.IsWebLauncher
            ? BuildBookmarkTasks(launcher, companion)
            : BuildItemTasks(launcher, companion, dark);

        var actions = BuildActionTasks(launcher, companion, dark);

        // The AUMID is part of the signature, not just the payload: a re-pin mints a new one, and
        // a launcher whose entries did not change would otherwise never be published under it.
        string signature = string.Join(Unit,
            entries.Concat(actions).Select(t => string.Join(Unit, t.Title, t.Arguments, t.IconSource)))
            + Unit + aumid;

        return new JumpListPlan(launcher.Id, launcher.Name, aumid, signature, entries, actions);
    }

    /// <summary>
    /// The launcher-level commands that sit below the entries.
    /// </summary>
    /// <remarks>
    /// Chosen per kind rather than listed and disabled, because a jump list has no disabled state:
    /// an item launcher has items to edit and no browser to open in, and a web launcher is the
    /// other way round. Both end with the app's own settings, which is the one entry that is not
    /// about this launcher and so goes last.
    /// </remarks>
    private static List<JumpTask> BuildActionTasks(Launcher launcher, string companion, bool dark)
    {
        var actions = new List<JumpTask>();

        void Add(int action, string title, string tooltip, string glyph)
        {
            actions.Add(new JumpTask(
                Title: title,
                Tooltip: tooltip,
                Index: -1,
                Token: 0,
                Arguments: $"--launcher {launcher.Id} --action {action}",
                Companion: companion,
                IconSource: new IconSource("", "", glyph, "", dark)));
        }

        // Segoe Fluent: E713 Settings, E70F Edit, E774 Globe, E115 the app's own settings gear.
        Add(LauncherActions.LauncherSettings, "Launcher settings",
            $"Settings for {launcher.Name}", "\uE713");

        if (launcher.IsWebLauncher)
            Add(LauncherActions.OpenInBrowser, "Open in browser", launcher.WebAddress, "\uE774");
        else
            Add(LauncherActions.EditItems, "Edit items", $"Edit the items in {launcher.Name}", "\uE70F");

        // "App Settings" is what the tray menu calls it, and one name for one window matters more
        // than a name that reads well in isolation.
        Add(LauncherActions.AppSettings, "App Settings", "Settings for Little Launcher itself", "\uE115");

        return actions;
    }

    /// <summary>
    /// The identity this launcher's pinned taskbar button goes by.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Launcher.PinAumid"/> first, which is the one the app minted and stamped on
    /// the window, and the only one anything else should ever use.</para>
    /// <para><b>The taskbar's own pin store is a fallback, and only for this.</b> Launchers pinned
    /// before the app started recording the AUMID have an empty <c>PinAumid</c> and a live pin
    /// whose identity is written down nowhere else, so without this their jump lists could never
    /// be published and the only fix would be re-pinning every one of them by hand. Reading that
    /// store was tried once as the source of *window* identity and abandoned, because it holds
    /// some pins and not others and a window carrying the wrong AUMID raises a second taskbar
    /// button beside the pin - see the comment on the mint in <c>LauncherSettingsWindow</c>. That
    /// failure has no equivalent here: publishing for an AUMID nothing uses writes a list nobody
    /// ever opens. Incomplete is fine when the alternative is nothing; wrong was not.</para>
    /// <para>It is deliberately <b>not</b> written back onto the launcher. <c>PinAumid</c> means
    /// "the identity this app minted and can vouch for", and a guess promoted into it would be
    /// read later as window identity by code that has every right to trust it.</para>
    /// </remarks>
    private static string ResolvePinAumid(Launcher launcher)
    {
        if (!string.IsNullOrEmpty(launcher.PinAumid)) return launcher.PinAumid;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            if (key == null) return "";

            var pattern = new Regex(
                $@"LittleLauncher\.Launcher\.{Regex.Escape(launcher.Id)}\.\d+",
                RegexOptions.IgnoreCase);

            foreach (string name in new[] { "Favorites", "FavoritesResolve" })
            {
                if (key.GetValue(name) is not byte[] blob || blob.Length < 4) continue;

                // Both byte alignments, because the AUMID sits at whatever offset the surrounding
                // binary structure put it at, and a UTF-16 read that starts one byte out finds
                // nothing at all rather than something wrong.
                for (int offset = 0; offset < 2; offset++)
                {
                    var match = pattern.Match(Encoding.Unicode.GetString(blob, offset, blob.Length - offset));
                    if (match.Success) return match.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read the taskbar pin store for launcher {Name}", launcher.Name);
        }

        return "";
    }

    private static List<JumpTask> BuildItemTasks(Launcher launcher, string companion, bool dark)
    {
        var items = new List<LauncherItem>();
        MainWindow.CollectLaunchableItems(launcher.Items, items, MaxSlots);

        var tasks = new List<JumpTask>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            int token = ItemToken(item);
            tasks.Add(new JumpTask(
                Title: string.IsNullOrWhiteSpace(item.Name) ? item.Path : item.Name,
                Tooltip: item.Path,
                Index: index,
                Token: token,
                Arguments: TaskArguments(launcher.Id, index, token),
                Companion: companion,
                IconSource: new IconSource(item.Path, item.IconPath, item.IconGlyph, item.IconColor, dark)));
        }
        return tasks;
    }

    /// <summary>
    /// A web launcher's bookmarks as tasks, or nothing at all when it has none.
    /// </summary>
    /// <remarks>
    /// A launcher with a single address <em>is</em> that address - it shows no bookmark bar, and
    /// its one entry goes exactly where clicking the button already goes. A menu whose only line
    /// repeats the button is worse than no menu, so that launcher publishes nothing and Windows
    /// shows the plain right-click menu it would have shown anyway.
    /// </remarks>
    private static List<JumpTask> BuildBookmarkTasks(Launcher launcher, string companion)
    {
        // Asked of the bookmarks rather than of the bar: a launcher with one page has nothing to
        // list whether or not its bar is on screen, which is the whole point of this gate.
        if (!launcher.HoldsSeveralSites) return [];

        // Flattened: a jump list is one flat menu with no submenus, so a folder can only appear
        // as what is inside it. Order is preserved, so a folder's contents sit where the folder did.
        var bookmarks = launcher.WebBookmarks.SelectMany(b => b.Flatten()).Take(MaxSlots).ToList();

        var tasks = new List<JumpTask>(bookmarks.Count);
        for (int index = 0; index < bookmarks.Count; index++)
        {
            var bookmark = bookmarks[index];
            int token = BookmarkToken(bookmark);
            tasks.Add(new JumpTask(
                Title: string.IsNullOrWhiteSpace(bookmark.Name) ? bookmark.Url : bookmark.Name,
                Tooltip: bookmark.Url,
                Index: index,
                Token: token,
                Arguments: TaskArguments(launcher.Id, index, token),
                Companion: companion,
                // A bookmark's URL is never an icon source: it is a page, not a file the shell can
                // extract from, so only the fetched favicon is offered.
                IconSource: new IconSource("", bookmark.IconPath, "", "", false)));
        }
        return tasks;
    }

    private static string TaskArguments(string launcherId, int index, int token) =>
        $"--launcher {launcherId} --item {index} --token {token}";

    private static string CompanionExePath() => MainWindow.GetFlyoutCompanionPath();

    // ── Publishing (worker thread) ──────────────────────────────────

    /// <summary>
    /// Runs shell COM work on a private STA thread.
    /// </summary>
    /// <remarks>
    /// Off the UI thread because a publish reads icon files and rasterises glyphs, and STA because
    /// that is the apartment the shell's objects expect. A fresh thread per publish is affordable:
    /// publishes are debounced and skipped when nothing changed, so they are rare.
    /// </remarks>
    private static void RunOnStaThread(Action action)
    {
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Taskbar jump list update failed");
            }
        })
        {
            IsBackground = true,
            Name = "JumpListPublisher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static bool Publish(JumpListPlan plan)
    {
        // Nothing to show at all. Publishing an empty list is not the same as leaving the old one
        // alone: a launcher whose list was never dropped keeps offering entries it no longer has.
        if (plan.Entries.Count == 0 && plan.Actions.Count == 0)
            return DeleteList(plan.Aumid);

        object? listObj = null;
        object? collectionObj = null;

        try
        {
            listObj = new NativeMethods.DestinationListClass();
            var list = (NativeMethods.ICustomDestinationList)listObj;

            int hr = list.SetAppID(plan.Aumid);
            if (hr != 0) return false;

            var iidObjectArray = NativeMethods.IID_IObjectArray;
            hr = list.BeginList(out uint slots, ref iidObjectArray, out object removed);
            if (removed != null) Marshal.ReleaseComObject(removed);
            if (hr != 0)
            {
                Logger.Debug("BeginList failed for {Aumid}: 0x{Hr:X8}", plan.Aumid, hr);
                return false;
            }

            int room = Math.Clamp(slots == 0 ? FallbackSlots : (int)slots, 1, MaxSlots);

            // The commands are not what gets cut. They are a fixed, short list the user needs a
            // route to, while the entries are a sample of something they can see in full by
            // clicking the button - so the entries are trimmed to whatever is left, down to none.
            var entries = plan.Entries.Take(Math.Max(0, room - plan.Actions.Count)).ToList();

            collectionObj = new NativeMethods.EnumerableObjectCollectionClass();
            var collection = (NativeMethods.IObjectCollection)collectionObj;

            int added = 0;
            foreach (var task in entries)
            {
                object? link = CreateShellLink(task);
                if (link == null) continue;

                collection.AddObject(link);
                added++;
            }

            foreach (var task in plan.Actions)
            {
                object? link = CreateShellLink(task);
                if (link == null) continue;

                collection.AddObject(link);
                added++;
            }

            if (added == 0)
            {
                list.AbortList();
                return false;
            }

            hr = list.AddUserTasks((NativeMethods.IObjectArray)collectionObj);
            if (hr != 0)
            {
                list.AbortList();
                Logger.Debug("AddUserTasks failed for {Aumid}: 0x{Hr:X8}", plan.Aumid, hr);
                return false;
            }

            hr = list.CommitList();
            if (hr != 0)
            {
                Logger.Debug("CommitList failed for {Aumid}: 0x{Hr:X8}", plan.Aumid, hr);
                return false;
            }

            Logger.Info("Published {Count} jump list entr(ies) for {Aumid}", added, plan.Aumid);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to publish jump list for {Aumid}", plan.Aumid);
            return false;
        }
        finally
        {
            if (collectionObj != null) Marshal.ReleaseComObject(collectionObj);
            if (listObj != null) Marshal.ReleaseComObject(listObj);
        }
    }

    private static bool DeleteList(string aumid)
    {
        object? listObj = null;
        try
        {
            listObj = new NativeMethods.DestinationListClass();
            var list = (NativeMethods.ICustomDestinationList)listObj;
            list.DeleteList(aumid);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to delete jump list for {Aumid}", aumid);
            return false;
        }
        finally
        {
            if (listObj != null) Marshal.ReleaseComObject(listObj);
        }
    }

    private static object? CreateShellLink(JumpTask task)
    {
        object? linkObj = null;
        try
        {
            linkObj = new NativeMethods.ShellLinkClass();
            var link = (NativeMethods.IShellLinkW)linkObj;

            link.SetPath(task.Companion);
            link.SetArguments(task.Arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(task.Companion) ?? "");

            // The shell truncates a description itself, but only after copying it, so keep it
            // inside the documented limit rather than relying on that.
            if (!string.IsNullOrEmpty(task.Tooltip))
                link.SetDescription(task.Tooltip.Length > 259 ? task.Tooltip[..259] : task.Tooltip);

            var (iconPath, iconIndex) = ResolveTaskIcon(task.IconSource);
            if (!string.IsNullOrEmpty(iconPath))
                link.SetIconLocation(iconPath, iconIndex);

            NativeMethods.SetShellLinkTitle(linkObj, task.Title);
            return linkObj;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to build jump list task {Title}", task.Title);
            if (linkObj != null) Marshal.ReleaseComObject(linkObj);
            return null;
        }
    }

    // ── Icons ───────────────────────────────────────────────────────

    private static string IconCacheDir() =>
        Path.Combine(MainWindow.GetPhysicalAppDataDir(), "JumpListIcons");

    /// <summary>
    /// Finds an icon a shell link can point at, writing one out if the item has none.
    /// </summary>
    /// <remarks>
    /// <para>A shell link cannot be given a bitmap: it stores a path and an index, and Windows
    /// extracts the icon from that file when it draws the menu. Only files that carry icon
    /// resources qualify (an .exe, a .dll or an .ico), which is why an app item can simply point
    /// at its own executable, and why everything else has to be written to an .ico first.</para>
    /// <para>The written files are cached by content, so a list republished because one item moved
    /// does not re-rasterise the other nine, and <see cref="SweepIconCache"/> removes the ones
    /// nothing has asked for in a long time.</para>
    /// </remarks>
    private static (string Path, int Index) ResolveTaskIcon(IconSource source)
    {
        try
        {
            // An executable is its own icon, at full fidelity and with no file to keep.
            if (!string.IsNullOrEmpty(source.TargetPath)
                && HasIconResources(source.TargetPath)
                && File.Exists(source.TargetPath))
            {
                return (source.TargetPath, 0);
            }

            // A cached .ico can be pointed at directly, whatever produced it.
            if (!string.IsNullOrEmpty(source.IconPath)
                && source.IconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                && File.Exists(source.IconPath))
            {
                return (source.IconPath, 0);
            }

            string cached = Path.Combine(IconCacheDir(), CacheFileName(source));
            if (File.Exists(cached)) return (cached, 0);

            using var bitmap = MainWindow.ResolveItemIconBitmap(
                source.IconPath, source.Glyph, source.Color, 256, source.Dark);
            if (bitmap == null) return ("", 0);

            Directory.CreateDirectory(IconCacheDir());
            File.WriteAllBytes(cached, MainWindow.BitmapToIcoBytes(bitmap));
            return (cached, 0);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "No jump list icon for {Path}", source.TargetPath);
            return ("", 0);
        }
    }

    private static bool HasIconResources(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes cached task icons no published list points at any more.
    /// </summary>
    /// <remarks>
    /// The live set has to be gathered from <em>every</em> pinned launcher, not from the ones this
    /// round republished: a launcher skipped because its list was unchanged still has that list on
    /// screen, and deleting the icons under it would leave it as rows of text.
    /// </remarks>
    private static void SweepIconCache(HashSet<string> live)
    {
        try
        {
            string dir = IconCacheDir();
            if (!Directory.Exists(dir)) return;

            foreach (string file in Directory.EnumerateFiles(dir, "task-*.ico"))
            {
                if (!live.Contains(Path.GetFileName(file)))
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not sweep the jump list icon cache");
        }
    }

    // ── Snapshots ───────────────────────────────────────────────────

    /// <summary>Everything the worker needs to draw one item's icon, with no live objects in it.</summary>
    private sealed record IconSource(string TargetPath, string IconPath, string Glyph, string Color, bool Dark);

    /// <summary>The cache file an icon source rasterises to, named for the pixels it produces.</summary>
    private static string CacheFileName(IconSource source) =>
        $"task-{StableHash(string.Join(Unit, source.IconPath, source.Glyph, source.Color, source.Dark)):x8}.ico";

    private sealed record JumpTask(string Title, string Tooltip, int Index, int Token, string Arguments,
        string Companion, IconSource IconSource);

    private sealed record JumpListPlan(string LauncherId, string LauncherName, string Aumid, string Signature,
        List<JumpTask> Entries, List<JumpTask> Actions);
}
