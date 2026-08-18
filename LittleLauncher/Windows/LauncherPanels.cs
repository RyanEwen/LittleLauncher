// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
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
