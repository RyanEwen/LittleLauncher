// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// The header's "…" menu: the per-launcher options worth changing while looking at the launcher,
/// with the full settings window one item below them.
/// </summary>
/// <remarks>
/// <para>These are the settings whose right value is a per-moment judgement — how this launcher
/// presents itself, whether it gets out of the way when you click elsewhere, and how big its page
/// is. Everything that is configured once and forgotten (address, profiles, browsing data) stays in
/// launcher settings; putting it all here would just be a second settings window in a worse
/// place.</para>
/// <para>Zoom is the one that changed sides. It looks like a set-once setting and is not: the
/// gestures that change it are ones a user hits by accident, and the menu is where they then go
/// looking for the way back.</para>
/// <para>Each item writes the launcher and applies it immediately, because the menu is opened
/// <em>while looking at the thing it changes</em> — a toggle that only took effect on the next open
/// would be indistinguishable from one that did nothing.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>
    /// Opens the More menu under the header button.
    /// </summary>
    /// <remarks>
    /// <para>Built fresh each time rather than kept: every item's checked state is read from the
    /// launcher, and which items exist depends on the launcher's mode. Rebuilding is a few objects
    /// and removes any question of the menu going stale.</para>
    /// <para>Two things make it usable from a window this small, and both are the same traps owned
    /// windows hit here:</para>
    /// <list type="bullet">
    ///   <item><c>ShouldConstrainToRootBounds = false</c> — a flyout can be 400px wide and 34px of
    ///   chrome tall, and a menu constrained to that gets clipped exactly like the
    ///   <c>ContentDialog</c> the item editors had to stop using.</item>
    ///   <item><c>_isMenuOpen</c> pins the flyout for as long as the menu is up. Once unconstrained
    ///   the menu is hosted in a popup of its own, so opening it deactivates the flyout — which
    ///   would dismiss it and take the menu down with it.</item>
    /// </list>
    /// </remarks>
    private void ShowMoreMenu()
    {
        var menu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
        };

        // ── How it presents itself ──────────────────────────────
        menu.Items.Add(Toggle("Regular window", _launcher.WebRegularWindow, on =>
        {
            _launcher.WebRegularWindow = on;
            ApplyWindowMode();
        }));

        // Only meaningful as a window: a flyout already dismisses on focus loss unless pinned, and
        // its pin is the control for that. Hidden rather than disabled — a greyed item still reads
        // as something you are missing out on.
        if (_launcher.WebRegularWindow)
        {
            menu.Items.Add(Toggle("Close when focus is lost", _launcher.WebWindowAutoHide, on =>
            {
                _launcher.WebWindowAutoHide = on;
            }));
        }

        menu.Items.Add(Toggle(PinMenuLabel(), _launcher.WebPinFlyout, on =>
        {
            _launcher.WebPinFlyout = on;
            SetTopmost(true);
            UpdatePinButton();
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        // ── This page ───────────────────────────────────────────
        // The address bar carries a star for this and is off by default, so without the item here
        // the only way to bookmark what is on screen would be to turn on a row of chrome first.
        // An action rather than a toggle: its two readings are "add" and "remove", and a check
        // beside it would be claiming the page has a property, which is the bar’s property.
        var bookmarkItem = new MenuFlyoutItem
        {
            Text = IsCurrentAddressBookmarked() ? "Remove from bookmarks" : "Add to bookmarks",
            IsEnabled = !string.IsNullOrEmpty(CurrentAddressUrl()),
        };
        bookmarkItem.Click += (_, _) => ToggleBookmarkForAddress();
        menu.Items.Add(bookmarkItem);

        // Its header button became the address bar's toggle, which is the per-moment decision of
        // the two. Handing the page to the real browser is a once-in-a-while action, and the menu
        // is where those belong.
        var openInBrowser = new MenuFlyoutItem { Text = "Open in browser" };
        openInBrowser.Click += (_, _) => OpenInBrowser();
        menu.Items.Add(openInBrowser);

        menu.Items.Add(new MenuFlyoutSeparator());

        // ── What it shows ───────────────────────────────────────
        // Tabs above the address, which is the order the two strips sit in on the flyout —
        // header, then tabs, then the address of whichever tab they chose. The settings dialog
        // lists them the same way round.
        // Off, the strip appears on its own as soon as there is a second tab and goes away again
        // when there is not — so this reads as "keep it", not "switch tabs on". Turning it on with
        // one tab open is also the only way to reach the "+" without following a link first.
        menu.Items.Add(Toggle("Tab bar", _launcher.WebAlwaysShowTabs, on =>
        {
            _launcher.WebAlwaysShowTabs = on;
            ApplyTabBarVisibility();
        }));

        menu.Items.Add(Toggle("Address bar", _launcher.WebShowAddressBar, on =>
        {
            _launcher.WebShowAddressBar = on;
            ApplyAddressBarVisibility();
        }));

        // The bar is where bookmarks are managed, so being able to bring it back without walking to
        // launcher settings matters more than it did when it appeared on its own. Listed after the
        // address bar because that is the order the two strips appear in on the flyout.
        menu.Items.Add(Toggle("Bookmarks bar", _launcher.ShowsBookmarkBar, on =>
        {
            _launcher.WebShowBookmarkBar = on;
            RebuildBookmarkBar(force: true);
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        // ── How big it is, and where it opens ───────────────────
        // Zoom leads the section because it is the size question asked most often, and the only one
        // a user arrives at the menu already needing fixed.
        menu.Items.Add(BuildZoomSubmenu());

        menu.Items.Add(BuildAnchorSubmenu());

        // The one a user reaches for straight after dragging a flyout and finding the change did
        // not stick — which is exactly a moment spent looking at the flyout, so the menu is the
        // right place for it rather than two windows away. "Where you last dragged it" is one of the
        // Opens at values, because remembering a position and choosing one are the same question.
        // WebLockSize is the inverse of what is shown, because this one is on by default and a
        // bool defaulting to true cannot be turned off under WhenWritingDefault. Inverted here, in
        // the line that builds the item — never in the model.
        menu.Items.Add(Toggle("Remember size changes", !_launcher.WebLockSize, on =>
        {
            _launcher.WebLockSize = !on;
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(BuildAdvancedSubmenu());

        var shortcuts = new MenuFlyoutItem { Text = "Keyboard shortcuts" };
        shortcuts.Click += (_, _) => _ = ShowShortcutsAsync();
        menu.Items.Add(shortcuts);

        var settings = new MenuFlyoutItem { Text = "Launcher settings…" };
        settings.Click += (_, _) => _ = OpenLauncherSettingsAsync();
        menu.Items.Add(settings);

        // Below it, and reached from the same place: the item flyout's context menu has carried
        // App Settings all along, and a web launcher had no way to the app's own settings without
        // going to the tray icon it was opened from.
        var appSettings = new MenuFlyoutItem { Text = "App settings…" };
        appSettings.Click += (_, _) => OpenAppSettings();
        menu.Items.Add(appSettings);

        // The flyout must not dismiss itself while the menu is up, and must be free to again the
        // moment it closes — including when the menu is dismissed by clicking away, which is the
        // common case and raises Closed just the same.
        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;

        menu.ShowAt(_moreButton);
    }

    /// <summary>
    /// The "Advanced" submenu: the three things a launcher can do to its own browser that nothing
    /// else in the app exposes.
    /// </summary>
    /// <remarks>
    /// <para><b>Buried on purpose.</b> None of these is part of using a launcher, and two of them
    /// are only ever wanted when something is already wrong. A submenu keeps them off the path
    /// somebody walks to change their zoom, while still putting them somewhere findable - which is
    /// more than they had, because until now every one of them meant opening DevTools by hand or
    /// pasting script into a console.</para>
    /// <para>Deliberately not here: anything already reachable. Reload, zoom, open-in-browser and
    /// the bars are all on the menu above, and browsing data is in launcher settings. A second
    /// route to the same thing is worth it only where the moment of needing it is the moment of
    /// looking at the flyout, which is the argument zoom already won and these do not need.</para>
    /// </remarks>
    private MenuFlyoutSubItem BuildAdvancedSubmenu()
    {
        var submenu = new MenuFlyoutSubItem { Text = "Advanced" };

        // Nothing in the app opened DevTools. A web launcher is a browser with no menu bar, so the
        // only way in was a right-click on the page hoping the default context menu was still
        // there - and diagnosing anything inside a launcher started with finding that out.
        var devTools = new MenuFlyoutItem { Text = "Developer tools" };
        devTools.Click += (_, _) =>
        {
            try { _webView?.CoreWebView2?.OpenDevToolsWindow(); }
            catch (Exception ex) { Logger.Warn(ex, "Opening developer tools failed for launcher {Name}", _launcher.Name); }
        };
        submenu.Items.Add(devTools);

        // The header's reload is the ordinary one, which is answered from cache and so is no help
        // against the case people actually reload for: a page serving something stale. There is no
        // WebView2 API for it, but the DevTools protocol has one and the runtime speaks it.
        var hardReload = new MenuFlyoutItem { Text = "Reload, ignoring cache" };
        hardReload.Click += (_, _) => _ = ReloadIgnoringCacheAsync();
        submenu.Items.Add(hardReload);

        submenu.Items.Add(new MenuFlyoutSeparator());

        // See RebuildNotificationBridgeAsync for why this cannot be done for the user.
        var rebuild = new MenuFlyoutItem { Text = "Rebuild notification bridge" };
        rebuild.Click += (_, _) => _ = RebuildNotificationBridgeAsync();
        submenu.Items.Add(rebuild);

        return submenu;
    }

    /// <summary>Reloads the page with the HTTP cache bypassed.</summary>
    private async Task ReloadIgnoringCacheAsync()
    {
        var core = _webView?.CoreWebView2;
        if (core == null) return;

        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
        }
        catch (Exception ex)
        {
            // Worth falling back rather than doing nothing: an ordinary reload is most of what was
            // asked for, and the difference only shows on a resource the cache is holding.
            Logger.Warn(ex, "Cache-bypassing reload failed for launcher {Name}; reloading normally", _launcher.Name);
            try { core.Reload(); } catch (Exception inner) { Logger.Warn(inner, "Reload failed"); }
        }
    }

    /// <summary>
    /// Removes this site's service worker so the site installs it again, wrapped.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the one thing the bridge cannot do on the user's behalf, and the reason it
    /// is a menu item rather than automatic.</b> A worker installed before the bridge existed is
    /// adopted by walking the registration out to a marked URL and back - but that needs a script
    /// URL, and a site enforcing Trusted Types will only accept one minted by a policy its own CSP
    /// names. Teams refuses to let us create a policy at all, so it can never be adopted. It will
    /// mint its own, though, every time it loads and finds nothing registered. Creating that state
    /// is all this does.</para>
    /// <para><b>It costs the registration's push subscription.</b> Unregistering throws it away and
    /// the site has to subscribe again, which every site tested does on its next load - but "does"
    /// is its behaviour, not a promise this can make. That is exactly why it is a thing somebody
    /// chooses from a menu called Advanced and not something that happens to them: the same act,
    /// done automatically to every launcher, was built twice and withdrawn twice.</para>
    /// <para>It also clears the once-ever adoption marker. A guard written before the risky step
    /// records failures as successes, so a site whose adoption failed is marked done and would
    /// never be retried; this is the only way back for one in that state.</para>
    /// </remarks>
    private async Task RebuildNotificationBridgeAsync()
    {
        var core = _webView?.CoreWebView2;
        if (core == null) return;

        Logger.Info("Rebuilding {Name}'s notification bridge: resetting notification permission and removing its service worker",
            _launcher.Name);

        // The permission goes back to "not asked", and that is the half that makes the rest work.
        // A site sets push up *during* its permission flow - it asks, and on being granted it
        // registers the worker push is delivered to - so one that already holds the grant simply
        // never runs that code again. Unregistering alone therefore gets a site that quietly does
        // nothing on the way back up, which is exactly the dance this item exists to replace:
        // log out, reset the site permission, clear the workers, hard reload, log back in.
        //
        // Nobody is prompted twice for it either: a launcher with WebAllowAllPermissions answers
        // the request silently, so the site asks, is granted, and registers, with nothing on screen.
        try
        {
            string origin = new Uri(core.Source).GetLeftPart(UriPartial.Authority);
            await core.Profile.SetPermissionStateAsync(
                CoreWebView2PermissionKind.Notifications, origin, CoreWebView2PermissionState.Default);

            Logger.Info("Reset notification permission for {Origin} so {Name}'s site asks again", origin, _launcher.Name);
        }
        catch (Exception ex)
        {
            // Worth carrying on: removing the worker is still most of the job, and a site that does
            // re-register without being asked again is helped by it.
            Logger.Warn(ex, "Could not reset notification permission for launcher {Name}", _launcher.Name);
        }

        // The HTTP cache goes too, and this is not belt-and-braces. The wrap ends in importScripts
        // of the worker's own URL, and that request is never offered to the host - so it is answered
        // from wherever the browser already has that URL. A wrap cached under it means the wrap
        // imports itself, recursing until the stack gives out, and unregistering does not touch the
        // HTTP cache: the site re-registers, the import is answered from the same poisoned entry,
        // and the worker dies exactly as before. Serving the wrap `no-store` stops new ones being
        // stored; this is what clears the ones already there.
        try
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            Logger.Info("Cleared {Name}'s HTTP cache so its worker's import cannot be answered from a stale wrap",
                _launcher.Name);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not clear the disk cache for launcher {Name}", _launcher.Name);
        }

        const string script = """
            (async () => {
                try { localStorage.removeItem('__llWorkerAdopted'); } catch (e) { }
                try {
                    var rs = await navigator.serviceWorker.getRegistrations();
                    for (var i = 0; i < rs.length; i++) { try { await rs[i].unregister(); } catch (e) { } }
                } catch (e) { }
                location.reload();
            })();
            """;

        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Rebuilding the notification bridge failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>
    /// The "Zoom" submenu: what the page's zoom is now, and every level it can be put back to.
    /// </summary>
    /// <remarks>
    /// <para>Zoom used to be settings-only, on the grounds that it is set once and forgotten. It is
    /// not: Ctrl+wheel and Ctrl+plus/minus change it in passing, often by accident, and the launcher
    /// is then stuck at a size with nothing on screen admitting why. That makes the current level a
    /// thing worth <em>reading</em> off the menu, which is why it is in the parent item's text
    /// rather than only in the checked rung.</para>
    /// <para>Reset leads, because coming back to 100% is the reason this submenu exists. The ladder
    /// below it is <see cref="Launcher.WebZoomLevels"/>, the same rungs the settings dialog offers
    /// and the zoom keys step through, plus the current level when a launcher holds one that is not
    /// on it. Nudging is left to the keyboard and the wheel: a menu item closes the menu, so a
    /// "Zoom in" here would be one rung per trip.</para>
    /// </remarks>
    private MenuFlyoutSubItem BuildZoomSubmenu()
    {
        int current = _launcher.ResolvedWebZoomPercent;
        var submenu = new MenuFlyoutSubItem { Text = $"Zoom ({current}%)" };

        var reset = new MenuFlyoutItem
        {
            Text = "Reset to 100%",
            KeyboardAcceleratorTextOverride = "Ctrl+0",
            IsEnabled = current != 100,
        };
        reset.Click += (_, _) => SetZoom(100);
        submenu.Items.Add(reset);

        submenu.Items.Add(new MenuFlyoutSeparator());

        foreach (int level in Launcher.WebZoomLevelsIncluding(current))
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{level}%",
                GroupName = "WebZoom",
                IsChecked = level == current,
            };
            item.Click += (_, _) => SetZoom(level);
            submenu.Items.Add(item);
        }

        return submenu;
    }

    /// <summary>
    /// The "Opens at" submenu — the one promoted geometry option that is not a toggle.
    /// </summary>
    /// <remarks>
    /// <para>A ten-way choice, so it is a submenu of radio items rather than something checkable.
    /// It belongs with Remember position for the reason its settings row's subtitle gives: with
    /// Remember position on, the anchor decides only the <em>first</em> open, and reading one
    /// without the other is how someone concludes the anchor does nothing.</para>
    /// <para>Picking one clears <c>WebFlyoutPosition</c>, exactly as the settings row does. A
    /// remembered position outranks the anchor, so leaving it in place would mean choosing a corner
    /// and watching the flyout open precisely where it did before.</para>
    /// </remarks>
    private MenuFlyoutSubItem BuildAnchorSubmenu()
    {
        var submenu = new MenuFlyoutSubItem { Text = "Opens at" };

        (string Label, int Value)[] anchors =
        [
            ("Near its tray icon", WebAnchors.Tray),
            ("Where you last dragged it", WebAnchors.LastPosition),
            ("Top left", WebAnchors.TopLeft),
            ("Top centre", WebAnchors.TopCenter),
            ("Top right", WebAnchors.TopRight),
            ("Left", WebAnchors.Left),
            ("Centre", WebAnchors.Center),
            ("Right", WebAnchors.Right),
            ("Bottom left", WebAnchors.BottomLeft),
            ("Bottom centre", WebAnchors.BottomCenter),
            ("Bottom right", WebAnchors.BottomRight),
        ];

        int current = WebAnchors.Normalize(_launcher.WebAnchor);
        foreach (var (label, value) in anchors)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = label,
                GroupName = "WebAnchor",
                IsChecked = value == current,
            };
            item.Click += (_, _) =>
            {
                if (WebAnchors.Normalize(_launcher.WebAnchor) == value) return;

                _launcher.WebAnchor = value;

                // Except the value that *is* the remembered position — see the settings row.
                if (value != WebAnchors.LastPosition) _launcher.WebFlyoutPosition = "";
                SettingsManager.SaveSettings();
                Services.AutoSyncService.NotifyLaunchersChanged();
            };
            submenu.Items.Add(item);
        }

        return submenu;
    }

    /// <summary>
    /// Builds a checkable item that writes the launcher and saves.
    /// </summary>
    /// <remarks>
    /// The save is here rather than in each callback so no item can forget it, and
    /// <c>NotifyLaunchersChanged</c> rides along with it — a launcher change that saves without
    /// telling the sync service is reverted by the next periodic download.
    /// </remarks>
    private static ToggleMenuFlyoutItem Toggle(string text, bool isChecked, Action<bool> apply)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked };
        item.Click += (_, _) =>
        {
            apply(item.IsChecked);
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        return item;
    }

    /// <summary>
    /// Opens the app's own settings window, and gets out of its way.
    /// </summary>
    /// <remarks>
    /// A flyout is dismissed first, exactly as the item flyout's App Settings does: it is
    /// always-on-top, so leaving it up would park it over the window it had just opened. A regular
    /// window is left alone — it is not topmost unless the user pinned it, and closing the page
    /// they still have open would be a steep price for reaching a settings window.
    /// </remarks>
    private void OpenAppSettings()
    {
        var owner = _owner ?? MainWindow.Current;
        if (owner == null) return;

        if (!_launcher.WebRegularWindow) HideFlyout();

        SettingsWindow.ShowInstance(owner);
    }

    /// <summary>The pin's label, which names what it does in this launcher's current mode.</summary>
    /// <remarks>
    /// <see cref="Launcher.WebPinFlyout"/> is one flag with two readings — see
    /// <c>PinTooltip</c>. The menu spells them out where the header button only has a glyph.
    /// </remarks>
    private string PinMenuLabel() =>
        _launcher.WebRegularWindow ? "Always on top" : "Stay open when focus is lost";

    /// <summary>
    /// Applies a change of window mode to the live window.
    /// </summary>
    /// <remarks>
    /// Everything regular-window mode changes is window state rather than something re-read per
    /// open, so switching it from the menu has to push all of it now: the taskbar button and
    /// switcher eligibility, always-on-top (which the pin's meaning depends on), and whether the
    /// window may be minimized, without which its taskbar button's click does nothing at all. The
    /// header's minimize button goes with that last one: it is only offered in this mode, because
    /// it is the only mode with a taskbar button to bring the window back from.
    /// </remarks>
    private void ApplyWindowMode()
    {
        SetMinimizable(_launcher.WebRegularWindow);
        UpdateMinimizeButton();
        SetTopmost(true);
        UpdatePinButton();
        ApplyTaskbarButton(_isOpen);
    }
}
