// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using System.Collections.Generic;
using System.Linq;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// Routes "open this launcher" to the window its <see cref="Launcher.Kind"/> calls for:
/// <see cref="FlyoutWindow"/> for an item launcher, <see cref="WebFlyoutWindow"/> for a web
/// launcher.
/// </summary>
/// <remarks>
/// Every caller that used to reach for <c>FlyoutWindow</c> by name goes through here instead,
/// so a launcher's kind is resolved in exactly one place rather than at each tray click, pinned
/// shortcut and settings page.
/// </remarks>
internal static class LauncherPanels
{
    /// <summary>Shows the launcher's panel, or dismisses it if it is already open.</summary>
    public static void Toggle(MainWindow owner, int screenX, int screenY, string launcherId)
    {
        if (IsWebLauncher(launcherId))
            WebFlyoutWindow.Toggle(owner, screenX, screenY, launcherId);
        else
            FlyoutWindow.Toggle(owner, screenX, screenY, launcherId);
    }

    /// <summary>
    /// Acts on one entry of a launcher's taskbar jump list: launches the item, or opens the
    /// launcher on the bookmark.
    /// </summary>
    /// <remarks>
    /// <para>The two kinds do genuinely different things here, which is why this is a kind
    /// decision and not a shared "launch entry N". An item launcher's task starts something and
    /// shows no window of its own; a web launcher's task has nowhere to open a page except the
    /// launcher itself, so it opens it.</para>
    /// <para><b>An entry that cannot be found opens the launcher instead.</b> A jump list is a
    /// snapshot, so a task can outlive the item it names - the launcher was edited, or synced
    /// from another machine - and the alternative to a fallback is a click that does nothing at
    /// all. Opening what the button opens is both the honest answer and the one that puts the
    /// user in front of the real list.</para>
    /// </remarks>
    public static void LaunchFromJumpList(MainWindow owner, string launcherId, int index, int token,
        int screenX, int screenY)
    {
        var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
        if (launcher == null) return;

        bool handled = launcher.IsWebLauncher
            ? WebFlyoutWindow.OpenBookmarkFromJumpList(owner, launcher, index, token, screenX, screenY)
            : FlyoutWindow.LaunchItemFromJumpList(launcher, index, token);

        if (!handled)
            Toggle(owner, screenX, screenY, launcherId);
    }

    /// <summary>
    /// Runs one of a launcher's own commands, from its taskbar jump list.
    /// </summary>
    /// <remarks>
    /// Here rather than in the service that published it, for the same reason every other entry
    /// point is: two of these mean different things depending on the launcher's kind, and this is
    /// the one place allowed to ask what kind it is. An action that does not apply is ignored
    /// rather than approximated - a stale pin naming "Edit items" on a launcher since turned into
    /// a web launcher has asked for something that no longer exists.
    /// </remarks>
    public static void RunLauncherAction(MainWindow owner, string launcherId, int action)
    {
        var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
        if (launcher == null) return;

        switch (action)
        {
            case LauncherActions.LauncherSettings:
                _ = LauncherSettingsWindow.ShowAsync(launcher);
                break;

            case LauncherActions.EditItems when !launcher.IsWebLauncher:
                FlyoutWindow.ShowInEditMode(owner, launcherId);
                break;

            case LauncherActions.OpenInBrowser when launcher.IsWebLauncher:
                WebFlyoutWindow.OpenAddressExternally(launcher);
                break;

            case LauncherActions.AppSettings:
                SettingsWindow.ShowInstance(owner);
                break;
        }
    }

    /// <summary>Destroys both possible panels for a launcher that has been deleted.</summary>
    public static void Dispose(string launcherId)
    {
        FlyoutWindow.DisposeLauncher(launcherId);
        WebFlyoutWindow.DisposeLauncher(launcherId);
    }

    /// <summary>
    /// Destroys the panel a launcher no longer uses, after its kind was changed. Without this a
    /// switched launcher keeps a warmed-up flyout (or a loaded browser) it can never show again.
    /// </summary>
    public static void SyncKind(Launcher launcher)
    {
        if (launcher.IsWebLauncher)
            FlyoutWindow.DisposeLauncher(launcher.Id);
        else
            WebFlyoutWindow.DisposeLauncher(launcher.Id);
    }

    /// <summary>
    /// Warms up the flyouts. Web launchers are deliberately excluded: their whole point is that
    /// nothing runs until the user asks for it, so they are built on first open instead.
    /// </summary>
    public static void WarmUp(MainWindow owner, IEnumerable<Launcher> launchers)
    {
        var all = launchers as IReadOnlyCollection<Launcher> ?? launchers.ToList();

        FlyoutWindow.WarmUp(owner, all.Where(l => !l.IsWebLauncher));

        // Bar-mode web launchers warm up too — the bar is XAML, not a browser. A single-address
        // web launcher still builds nothing: its first frame is the page, so there would be
        // nothing to pre-render but an empty window.
        WebFlyoutWindow.WarmUp(owner, all.Where(l => l.ShowsBookmarkBar));

        // The one kind that does start a browser up front, because the user asked for it: a
        // launcher set to keep running is one whose notifications are meant to arrive without it
        // being opened, and a page that has never loaded has no connection to receive them on.
        WebFlyoutWindow.PreloadKeepRunning(owner, all);
    }

    private static bool IsWebLauncher(string launcherId) =>
        SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId)?.IsWebLauncher == true;
}
