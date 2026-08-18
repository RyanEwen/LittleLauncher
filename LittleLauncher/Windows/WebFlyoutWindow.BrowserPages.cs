// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.Web.WebView2.Core;
using System;

namespace LittleLauncher.Windows;

/// <summary>
/// The browser's own pages, which WebView2 does not have.
/// </summary>
/// <remarks>
/// <para>A hosted WebView2 is Chromium without a browser around it: no new-tab page, no settings
/// page, no extensions page. Navigating to one of those addresses does not fail cleanly — it
/// reaches a scheme with nothing behind it and lands on an error, which is what a
/// <c>search://local-ntp/local-ntp.html</c> 404 is.</para>
/// <para>That surfaces through perfectly ordinary use: Bitwarden's biometric-unlock setup opens the
/// browser's extensions page to finish, and a page or extension that calls <c>window.open()</c> with
/// no address gets the new-tab page by definition. Neither is doing anything unusual — they are
/// assuming the browser they can see in the user agent.</para>
/// <para>So these are answered with the nearest honest equivalent rather than left to 404: a
/// new-tab page becomes this launcher's own empty tab, and a page the flyout genuinely cannot
/// provide says so and offers the real browser, where it exists.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>
    /// Intercepts a navigation to one of the browser's internal pages.
    /// </summary>
    /// <returns>True when the navigation was handled and the caller should stop.</returns>
    private bool TryHandleBrowserPageNavigation(CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e)
    {
        string uri;
        try { uri = e.Uri ?? ""; }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading a navigation address failed for launcher {Name}", _launcher.Name);
            return false;
        }

        if (!IsBrowserPage(uri)) return false;

        e.Cancel = true;

        // A new tab page is a request for an empty tab, which this launcher does have — so the tab
        // that asked simply becomes one, address bar and all. Blanking it rather than opening
        // another keeps window.open() with no address behaving as it does in a browser.
        if (IsNewTabPage(uri))
        {
            Logger.Debug("Answering {Uri} with an empty tab for launcher {Name}", uri, _launcher.Name);

            try { core.Navigate("about:blank"); }
            catch (Exception ex) { Logger.Debug(ex, "Blanking a tab failed"); }

            ApplyAddressBarVisibility();
            return true;
        }

        // Everything else is a page only a real browser has. Saying which, and offering the browser
        // that does have it, beats a 404 the user cannot act on — this is how Bitwarden's biometric
        // setup ends up somewhere it cannot finish.
        Logger.Info("Declined navigation to the browser page {Uri} in launcher {Name}", uri, _launcher.Name);

        ShowNotice("That page belongs to the browser itself, which a launcher does not have. "
                 + "Open this site in your browser to finish there.");
        return true;
    }

    /// <summary>True for an address only a full browser can serve.</summary>
    /// <remarks>
    /// <c>chrome-extension://</c> is deliberately absent: those are an extension's own pages, they
    /// work here, and the popup window depends on being able to navigate to one.
    /// </remarks>
    private static bool IsBrowserPage(string uri) =>
        uri.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("chrome-search://", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("search://", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("edge://", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the several addresses that all mean "the new tab page".</summary>
    private static bool IsNewTabPage(string uri) =>
        uri.Contains("local-ntp", StringComparison.OrdinalIgnoreCase) ||
        uri.Contains("newtab", StringComparison.OrdinalIgnoreCase) ||
        uri.Contains("new-tab-page", StringComparison.OrdinalIgnoreCase);
}
