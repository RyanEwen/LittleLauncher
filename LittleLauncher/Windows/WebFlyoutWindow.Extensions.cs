// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LittleLauncher.Windows;

/// <summary>
/// Browser extensions in the flyout: catching a store install, and showing an extension's panel.
/// </summary>
/// <remarks>
/// <para>WebView2 gives the host an install call and nothing else — no store integration and no
/// browser-action UI. <see cref="BrowserExtensionService"/> owns the install side; this owns the
/// two places the flyout has to make up for the missing browser.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>Header buttons currently standing in for extension browser actions.</summary>
    private readonly List<Button> _extensionButtons = [];

    // ── Catching a store install ────────────────────────────────────

    /// <summary>
    /// Turns a downloaded extension package into an installed extension.
    /// </summary>
    /// <remarks>
    /// <para>The Chrome Web Store's button works as far as it can: WebView2 presents as Chrome, so
    /// the page offers to install and gets far enough to hand over the package. What it cannot do is
    /// finish, because the last step runs through a private API WebView2 does not implement — which
    /// is where this comes in. A <c>.crx</c> is a signature header followed by a plain ZIP, so
    /// unpacking it produces exactly the folder <c>AddBrowserExtensionAsync</c> wants.</para>
    /// <para>The download is allowed to complete to disk rather than being cancelled and re-fetched:
    /// the store's URL is signed and single-use, and a second request for it from the host — outside
    /// the browser's cookie jar — is not the same request.</para>
    /// <para>Everything that is not an extension package is left entirely alone, including the
    /// default download UI. A launcher is not a download manager, and quietly changing what happens
    /// to an ordinary file would be a surprise nobody asked for.</para>
    /// </remarks>
    private void HandleDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
    {
        string uri;
        string path;

        try
        {
            uri = e.DownloadOperation.Uri ?? "";
            path = e.DownloadOperation.ResultFilePath ?? "";
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading a download failed for launcher {Name}", _launcher.Name);
            return;
        }

        // Both, because neither is reliable alone: the store serves its package from a URL with no
        // .crx on the end, and a .crx saved from anywhere else never touches that host.
        bool isExtension =
            path.EndsWith(".crx", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("/service/update2/crx", StringComparison.OrdinalIgnoreCase);

        if (!isExtension) return;

        // Its own dialog would announce a file the user never has to think about again.
        e.Handled = true;

        var operation = e.DownloadOperation;
        operation.StateChanged += async (sender, _) =>
        {
            if (sender is not CoreWebView2DownloadOperation download) return;
            if (download.State != CoreWebView2DownloadState.Completed) return;

            await InstallDownloadedExtensionAsync(download.ResultFilePath);
        };
    }

    private async Task InstallDownloadedExtensionAsync(string archivePath)
    {
        string? folder = await BrowserExtensionService.InstallAsync(archivePath);

        if (folder == null)
        {
            SetStatus("That extension could not be unpacked. It may not be a browser extension.",
                busy: false, showRetry: false);
            return;
        }

        // Onto this browser now, so it takes effect without reopening — every other profile picks
        // it up as its own browser next starts. See BrowserExtensionService.ApplyAsync.
        if (_webView?.CoreWebView2 is { } core)
            await BrowserExtensionService.ApplyAsync(core);

        RefreshExtensionButtons();

        // The download itself was only ever a delivery mechanism.
        try { if (File.Exists(archivePath)) File.Delete(archivePath); }
        catch (Exception ex) { Logger.Debug(ex, "Could not delete the downloaded package {Path}", archivePath); }

        Logger.Info("Installed browser extension {Name} for launcher {Launcher}",
            BrowserExtensionService.ReadName(folder), _launcher.Name);
    }

    // ── Showing an extension's panel ────────────────────────────────

    /// <summary>
    /// Puts a header button in front of every extension that declares a popup.
    /// </summary>
    /// <remarks>
    /// <para>This is the browser toolbar WebView2 does not have.
    /// <c>CoreWebView2BrowserExtension</c> carries an id, a name and an enabled flag and nothing
    /// about browser actions, so the popup page and its icon are read out of the extension's own
    /// <c>manifest.json</c> — which the host has, because the host is what unpacked it.</para>
    /// <para>Extensions with no popup get no button. A content-script or
    /// <c>declarativeNetRequest</c> extension — which is what an MV3 blocker is — does its whole job
    /// with no UI at all, and a button that opened an empty page would suggest otherwise.</para>
    /// </remarks>
    private void RefreshExtensionButtons()
    {
        foreach (var button in _extensionButtons)
            _headerButtons.Children.Remove(button);

        _extensionButtons.Clear();

        foreach (string folder in BrowserExtensionService.InstalledFolders.ToList())
        {
            if (BrowserExtensionService.ReadAction(folder) is not { } action) continue;

            string name = BrowserExtensionService.ReadName(folder);
            var button = BuildExtensionButton(folder, name, action.Icon);

            // Ahead of the window controls, where a browser puts its extensions: those act on the
            // window, these act on the page.
            _headerButtons.Children.Insert(0, button);
            _extensionButtons.Add(button);
        }
    }

    private Button BuildExtensionButton(string folder, string name, string? iconPath)
    {
        // U+E8B7 is Segoe Fluent's Puzzle-ish "Repair" glyph, used when the extension declares no
        // icon of its own — the same fallback role the globe plays for a bookmark.
        var button = BuildHeaderButton("", name, (_, _) => _ = ShowExtensionPopupAsync(folder, name));

        if (iconPath == null) return button;

        string full = Path.Combine(folder, iconPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return button;

        try
        {
            button.Content = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(full)),
                Width = 16,
                Height = 16,
            };
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Loading the extension icon {Path} failed", full);
        }

        return button;
    }

    /// <summary>
    /// Opens one extension's popup page in a window of its own.
    /// </summary>
    /// <remarks>
    /// <para>The popup is an ordinary page at <c>chrome-extension://{id}/{page}</c>, so showing it
    /// is a second WebView2 <b>on the same profile</b> — which is what gives it the extension's
    /// storage and its APIs. On any other profile it would load and then behave as though the
    /// extension had never run.</para>
    /// <para>The id comes from <c>GetBrowserExtensionsAsync</c> matched by name, because that is the
    /// only place it exists: it is derived from the install path, so nothing on disk knows it.</para>
    /// </remarks>
    private async Task ShowExtensionPopupAsync(string folder, string name)
    {
        var core = _webView?.CoreWebView2;
        if (core == null) return;

        if (BrowserExtensionService.ReadAction(folder) is not { } action) return;

        try
        {
            var installed = await core.Profile.GetBrowserExtensionsAsync();
            var match = installed.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                SetStatus($"{name} is not loaded in this launcher yet. Reopen it and try again.",
                    busy: false, showRetry: false);
                return;
            }

            string url = $"chrome-extension://{match.Id}/{action.Popup.TrimStart('/')}";
            await ExtensionPopupWindow.ShowAsync(name, url, GetUserDataFolder(_launcher), _hwnd, w => _openModal = w);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Opening the popup for {Name} failed", name);
        }
        finally
        {
            _openModal = null;
        }
    }
}
