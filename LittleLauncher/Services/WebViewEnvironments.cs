// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LittleLauncher.Services;

/// <summary>
/// One <see cref="CoreWebView2Environment"/> per user-data folder, shared by everything that opens
/// a browser on it.
/// </summary>
/// <remarks>
/// <para>Every browser used to build its own: a new environment per tab, per extension popup, per
/// launcher. WebView2 permits that on one folder as long as the options match, and ours always do,
/// so it worked. What it also did was churn: each environment is another client of the same browser
/// process, and an extension's service worker is started and stopped around them.</para>
/// <para>That churn is visible from inside an extension. Bitwarden's background worker registers a
/// content script by id on startup, those registrations persist across restarts, and the console
/// filled with <c>Duplicate script ID 'fido2-page-script-registration'</c> as it came back up over
/// and over. An unhandled rejection during an extension's startup is not a cosmetic problem: what
/// it aborts is the rest of that startup.</para>
/// <para>Sharing costs nothing and is what a browser does anyway. Keyed by folder, because the
/// folder <em>is</em> the profile: two launchers on the shared profile get the same environment,
/// and a private one gets its own.</para>
/// <para><b>A failed creation is not cached.</b> The runtime being missing or the folder being
/// locked is a condition that can pass, and one bad attempt must not be the permanent answer for
/// every launcher that comes after.</para>
/// </remarks>
internal static class WebViewEnvironments
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<CoreWebView2Environment>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The environment for one user-data folder, created once and handed out after that.</summary>
    internal static async Task<CoreWebView2Environment> GetAsync(string userDataFolder)
    {
        var pending = Cache.GetOrAdd(userDataFolder, folder =>
            new Lazy<Task<CoreWebView2Environment>>(() => CreateAsync(folder),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await pending.Value;
        }
        catch
        {
            // Only drop the entry we were actually waiting on: another caller may have replaced it
            // with a successful one while this attempt was failing.
            Cache.TryRemove(new KeyValuePair<string, Lazy<Task<CoreWebView2Environment>>>(userDataFolder, pending));
            throw;
        }
    }

    /// <summary>
    /// Forgets the environment for a folder, for when the folder itself is going away.
    /// </summary>
    /// <remarks>
    /// Clearing a launcher's browsing data deletes the profile directory. An environment still
    /// pointing at it would be handed to the next browser opened there, on a folder that no longer
    /// exists.
    /// </remarks>
    internal static void Forget(string userDataFolder) => Cache.TryRemove(userDataFolder, out _);

    private static async Task<CoreWebView2Environment> CreateAsync(string userDataFolder)
    {
        // AreBrowserExtensionsEnabled is set for every launcher, never only for ones with an
        // extension: the options must match across every environment on a folder, and connecting to
        // an already-running one with a different value fails outright with ERROR_INVALID_STATE.
        // Sharing the environment makes that far harder to get wrong, but the rule still holds for
        // anything added here later.
        return await CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: "",
            userDataFolder: userDataFolder,
            options: new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = true,

                // What makes a hidden launcher keep receiving.
                //
                // A dismissed KeepRunning launcher is collapsed, which is deliberate - the page has
                // to report visibilityState 'hidden' or an app decides the user is looking at it and
                // raises no desktop notification. But hidden is also what Chromium throttles:
                // background timers first, then intensive throttling at a tick a minute. A client
                // whose delivery rides on a timer stops receiving entirely, which is what left a
                // launcher silent until it was opened and then played the whole backlog at once.
                //
                // These are the switches that turn that off while leaving visibility alone, so the
                // page stays hidden *and* awake. Page.setWebLifecycleState was tried first and does
                // not do it: it addresses freezing, not throttling, and a one-shot call does not
                // survive Chromium re-evaluating the page later.
                //
                // Cheaper than it sounds - a collapsed page still is not compositing or decoding
                // anything. It is timers and script that keep running, which for a chat client is
                // the point.
                AdditionalBrowserArguments =
                    "--disable-background-timer-throttling " +
                    "--disable-renderer-backgrounding " +
                    "--disable-backgrounding-occluded-windows",
            });
    }
}
