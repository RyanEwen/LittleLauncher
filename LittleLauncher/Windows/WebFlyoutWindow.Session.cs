// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LittleLauncher.Windows;

/// <summary>
/// Continue where you left off: the pages a launcher had open, across a restart.
/// </summary>
/// <remarks>
/// <para><c>_rememberedUrl</c> already carries the launcher's own tab through an idle unload, but
/// only in memory — quitting Little Launcher forgot it, and forgot every other tab outright. A
/// launcher used as a small browser therefore came back at its address with the rest gone.</para>
/// <para><b>Written as it changes, read once per launcher per run.</b> Restoring happens when the
/// launcher is <em>opened</em>, never at startup, which is what keeps the resource contract: a
/// launcher nobody opens still builds nothing, and one that is opened pays exactly what its tabs
/// cost — which is what they were already costing before the restart. All but the active tab are
/// built in the background, so only the page being looked at renders.</para>
/// <para>The addresses are the whole session. Scroll position, form state and history are not
/// carried: they live in the browser that was torn down, and promising them would mean keeping it.
/// This is the same bargain <c>Ctrl+Shift+T</c> makes.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>True once this launcher has restored, so a later open does not do it again.</summary>
    /// <remarks>
    /// A restore is for the first open of a run. After that the tabs are live and are themselves
    /// the session — re-reading the stored list would resurrect tabs the user has since closed.
    /// </remarks>
    private bool _sessionRestored;

    /// <summary>Guards the save while a restore is mid-flight.</summary>
    /// <remarks>
    /// Restoring creates tabs, and creating a tab saves the session. Without this the half-built
    /// list would be written over the stored one, and a crash mid-restore would leave the launcher
    /// with whichever tabs happened to exist at that moment.
    /// </remarks>
    private bool _restoringSession;

    /// <summary>Records the open tabs, so the next run can put them back.</summary>
    internal void SaveSession()
    {
        if (_restoringSession) return;

        var urls = new List<string>();
        foreach (var tab in _tabs)
        {
            string url = "";
            try { url = tab.View.CoreWebView2?.Source ?? ""; }
            catch (Exception ex) { Logger.Debug(ex, "Reading a tab's address failed for launcher {Name}", _launcher.Name); }

            // A browser that has been created but has not navigated yet reports Source as the
            // *empty string*, not null — so `?? NavigatedUrl` never fired and this whole save ran
            // during tab creation, found nothing, and wrote nothing. A launcher whose single tab
            // then never changed had no session recorded at all, which is exactly what "my tabs
            // are not coming back" looked like.
            if (string.IsNullOrEmpty(url)) url = tab.NavigatedUrl;

            url = NormalizeUrl(url);
            if (string.IsNullOrEmpty(url) || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) continue;

            urls.Add(url);
        }

        int active = _activeTab == null ? 0 : Math.Max(0, _tabs.IndexOf(_activeTab));

        // Nothing to say is said as nothing: an empty list under WhenWritingDefault leaves the key
        // out entirely, so a launcher that has never been opened carries no session at all.
        var stored = urls.Count == 0 ? null : urls;

        if (SameAsStored(stored, active)) return;

        _launcher.WebSessionTabs = stored;
        _launcher.WebSessionActiveTab = active;
        SettingsManager.SaveSettings();
    }

    /// <summary>True when the launcher already holds this exact session, so nothing need be written.</summary>
    /// <remarks>
    /// This runs on every tab change and every navigation of a launcher's own tab, which for a chat
    /// app is often — and each save writes the whole settings file. Comparing first turns the common
    /// case, where nothing actually moved, into no write at all.
    /// </remarks>
    private bool SameAsStored(List<string>? urls, int active)
    {
        if (_launcher.WebSessionActiveTab != active) return false;

        var stored = _launcher.WebSessionTabs;
        if (stored == null || urls == null) return stored == null && urls == null;

        return stored.SequenceEqual(urls, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Forgets the session — the launcher was closed down to nothing.</summary>
    internal void ClearSession()
    {
        if (_launcher.WebSessionTabs == null && _launcher.WebSessionActiveTab == 0) return;

        _launcher.WebSessionTabs = null;
        _launcher.WebSessionActiveTab = 0;
        SettingsManager.SaveSettings();
    }

    /// <summary>True when there is a stored session and this run has not used it yet.</summary>
    private bool HasSessionToRestore =>
        !_sessionRestored && _tabs.Count == 0 && _launcher.WebSessionTabs is { Count: > 0 };

    /// <summary>
    /// Rebuilds the tabs this launcher had open.
    /// </summary>
    /// <remarks>
    /// The active tab is created first and in the foreground, so the page the user was last looking
    /// at is the one that appears and starts loading; the rest follow behind it. Doing it the other
    /// way round would show whichever tab happened to be first and then swap under them.
    /// </remarks>
    private async Task RestoreSessionAsync()
    {
        var urls = _launcher.WebSessionTabs?.ToList() ?? [];

        // Set before the first await: a second show while this is running must not start again.
        _sessionRestored = true;
        if (urls.Count == 0) return;

        int active = urls.Count == 0 ? 0 : Math.Clamp(_launcher.WebSessionActiveTab, 0, urls.Count - 1);

        _restoringSession = true;
        try
        {
            // The launcher's own tab is whichever held the address it opens at, so the restored set
            // keeps one home tab and the rest come back as the user's own pages.
            string home = NormalizeUrl(CurrentTargetUrl());
            bool homeUsed = false;

            for (int i = 0; i < urls.Count; i++)
            {
                string url = urls[i];
                bool isHome = !homeUsed && string.Equals(url, home, StringComparison.OrdinalIgnoreCase);
                if (isHome) homeUsed = true;

                await CreateTabAsync(
                    homeKey: isHome ? PrimaryTabKey : null,
                    navigateTo: url,
                    background: i != active);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Restoring the session failed for launcher {Name}", _launcher.Name);
        }
        finally
        {
            _restoringSession = false;
        }

        // Written once at the end, so the stored list reflects what actually came back rather than
        // each step of it.
        SaveSession();
    }
}
