// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    /// <summary>Header buttons for the extensions the user pinned there.</summary>
    private readonly List<Button> _extensionButtons = [];

    /// <summary>The one button that opens the list of extensions.</summary>
    private Button? _extensionsButton;

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

            if (download.State == CoreWebView2DownloadState.Completed)
            {
                await InstallDownloadedExtensionAsync(download.ResultFilePath);
                return;
            }

            // Interrupted is the *expected* ending for a Chrome Web Store install rather than a
            // failure to report as one — the package never reaches disk. See HandleInterruptedAsync.
            if (download.State == CoreWebView2DownloadState.Interrupted)
                await HandleInterruptedAsync(uri);
        };
    }

    /// <summary>
    /// The host Chromium substitutes for the Web Store's download endpoint in unbranded builds.
    /// </summary>
    /// <remarks>
    /// <para>The address that serves <c>.crx</c> packages lives in Google's <em>non-open-source</em>
    /// branding configuration, so any Chromium built without it — WebView2, Electron, plain
    /// Chromium — emits this placeholder in place of the real host. The request then fails DNS
    /// before it is a network call at all, which the store page reports as "Download
    /// interrupted".</para>
    /// <para>It is an absent constant rather than a lock, and supplying it is what every Chromium
    /// browser with Web Store support already does — Edge, Brave, Vivaldi and Opera each put the
    /// endpoint back in their own build. Edge's install dialog says "Add to Microsoft Edge" because
    /// Edge implemented the client side, not because it was granted something.</para>
    /// </remarks>
    private const string RemovedStoreHost = "permanently-removed.invalid";

    /// <summary>The endpoint the placeholder stands in for.</summary>
    private const string StoreDownloadEndpoint = "https://clients2.google.com/service/update2/crx";

    /// <summary>Salvages an interrupted extension download.</summary>
    /// <remarks>
    /// Chromium builds the whole query correctly — extension id, accepted format, product version,
    /// <c>installsource=ondemand</c> — and only the host is missing, so restoring it is a
    /// substitution rather than a request this app composes. <c>response=redirect</c> means the
    /// endpoint answers with a 302 to the package, which <c>HttpClient</c> follows by itself.
    /// </remarks>
    private async Task HandleInterruptedAsync(string url)
    {
        if (url.Contains(RemovedStoreHost, StringComparison.OrdinalIgnoreCase))
            url = RestoreStoreEndpoint(url);

        await InstallFromUrlAsync(url);
    }

    /// <summary>Puts the real download host back into a store URL, keeping its query untouched.</summary>
    private static string RestoreStoreEndpoint(string url)
    {
        int query = url.IndexOf('?', StringComparison.Ordinal);
        string parameters = query >= 0 ? url[query..] : "";

        // "puff" is a differential format: it answers with a patch against a version this profile
        // has never held. Asking only for crx3 keeps the reply a whole package.
        parameters = parameters.Replace("acceptformat=crx3,puff", "acceptformat=crx3",
            StringComparison.OrdinalIgnoreCase);

        return StoreDownloadEndpoint + parameters;
    }

    /// <summary>
    /// Fetches an extension package the browser refused to save, and installs it.
    /// </summary>
    /// <remarks>
    /// Only ever called for a URL WebView2 itself just tried to download as an extension, so this is
    /// not a general fetcher — the address came from the page's own install flow, not from anything
    /// this app composed.
    /// </remarks>
    private async Task InstallFromUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string temp = Path.Combine(Path.GetTempPath(), $"ll-extension-{Guid.NewGuid():N}.crx");

        try
        {
            using var http = new System.Net.Http.HttpClient();
            byte[] bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(temp, bytes);

            await InstallDownloadedExtensionAsync(temp);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Fetching the extension package from {Url} failed", url);

            // Said in the flyout, because the store page has only said "Download interrupted" and
            // left the user with nothing to act on.
            // ShowNotice, not SetStatus: the store page is on screen, and the status overlay shares
            // its row with the browser — which draws over it. See BuildNoticeBar.
            ShowNotice("That extension could not be installed from the store. Download its .zip and "
                     + "add it under Settings → Browser Extensions → Add Folder.");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { Logger.Debug(ex, "Could not delete {Path}", temp); }
        }
    }

    private async Task InstallDownloadedExtensionAsync(string archivePath)
    {
        string? folder = await BrowserExtensionService.InstallAsync(archivePath);

        if (folder == null)
        {
            ShowNotice("That extension could not be unpacked. It may not be a browser extension.");
            return;
        }

        // Onto this browser now, so it takes effect without reopening — every other profile picks
        // it up as its own browser next starts. See BrowserExtensionService.ApplyAsync.
        if (_webView?.CoreWebView2 is { } core)
            await BrowserExtensionService.ApplyAsync(core);

        RefreshExtensionButtons();

        // The package was only ever a delivery mechanism; the unpacked copy is what runs. Deleting
        // it is best-effort — InstallFromUrlAsync cleans up its own temp file regardless.
        try { if (File.Exists(archivePath)) File.Delete(archivePath); }
        catch (Exception ex) { Logger.Debug(ex, "Could not delete the package {Path}", archivePath); }

        string name = BrowserExtensionService.ReadName(folder);
        Logger.Info("Installed browser extension {Name} for launcher {Launcher}", name, _launcher.Name);

        // Said out loud, because the store page is still showing "Download interrupted" — from its
        // side the install genuinely did fail, and without this the user believes nothing happened.
        ShowNotice($"{name} is installed and running in this launcher.");
    }

    // ── Showing an extension's panel ────────────────────────────────

    /// <summary>
    /// Rebuilds the extensions button, and whichever extensions are pinned beside it.
    /// </summary>
    /// <remarks>
    /// <para>One button that opens a list, the way every browser does it — not one button per
    /// extension. A flyout header is a handful of slots wide, so a launcher with four extensions
    /// spent them all on extensions and left nothing for the window controls. The list is also where
    /// an extension with no popup can still be seen: an MV3 blocker does its whole job through
    /// <c>declarativeNetRequest</c> with no UI, and a toolbar alone would never mention it.</para>
    /// <para>Pinning promotes one to the header, which is the point of pinning — a blocker you check
    /// constantly is worth a slot, and the other three are not. The pins are per profile, since the
    /// extensions are.</para>
    /// </remarks>
    private void RefreshExtensionButtons()
    {
        foreach (var button in _extensionButtons)
            _headerButtons.Children.Remove(button);

        _extensionButtons.Clear();

        var installed = BrowserExtensionService.Installed
            .Where(e => !string.IsNullOrEmpty(e.Folder) && Directory.Exists(e.Folder))
            .ToList();

        if (installed.Count == 0)
        {
            if (_extensionsButton != null)
            {
                _headerButtons.Children.Remove(_extensionsButton);
                _extensionsButton = null;
            }
            return;
        }

        // The menu first, then whichever are pinned beside it — reading left to right as "the
        // list, and the ones worth a slot of their own". Inserted at a running slot rather than
        // repeatedly at the same index, which would have reversed the pinned run.
        int slot = ExtensionSlot;

        // U+E710 is Segoe Fluent's Add; U+E713 Settings. Neither is a puzzle piece — Segoe
        // Fluent has none — so E74C (OpenPane) reads closest to "a list of things behind this".
        _extensionsButton ??= BuildHeaderButton("", "Extensions", (_, _) => ShowExtensionsMenu());

        // Re-seated rather than left wherever it was: the pinned run is rebuilt around it.
        _headerButtons.Children.Remove(_extensionsButton);
        _headerButtons.Children.Insert(slot++, _extensionsButton);

        foreach (var extension in installed.Where(e => IsPinned(e.Id, e.Name)))
        {
            if (BrowserExtensionService.ReadAction(extension.Folder) is not { } action) continue;

            var pinned = BuildExtensionButton(extension.Folder, extension.Name, action.Icon);
            _headerButtons.Children.Insert(slot++, pinned);
            _extensionButtons.Add(pinned);
        }
    }

    /// <summary>
    /// Where extension buttons go in the header: immediately after the address-bar toggle.
    /// </summary>
    /// <remarks>
    /// They act on the page, so they belong with the page controls rather than among the window
    /// controls further right — the same reasoning that keeps Back and Reload on the left. Placed
    /// relative to the address-bar button rather than at a fixed index, so adding a header button
    /// later cannot silently move them somewhere else.
    /// </remarks>
    private int ExtensionSlot
    {
        get
        {
            // Anchored on the "…" button now that the address-bar toggle has gone: extensions sit
            // with the page controls, immediately after it, rather than among the window controls
            // further right. Computed rather than written as a constant, so adding a header button
            // later cannot silently move them.
            int more = _headerButtons.Children.IndexOf(_moreButton);
            return more < 0 ? 0 : more + 1;
        }
    }

    /// <summary>Opens the list of extensions, each with its popup and a pin.</summary>
    private void ShowExtensionsMenu()
    {
        if (_extensionsButton == null) return;

        var menu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
        };

        foreach (var extension in BrowserExtensionService.Installed.ToList())
        {
            if (string.IsNullOrEmpty(extension.Folder) || !Directory.Exists(extension.Folder)) continue;

            var action = BrowserExtensionService.ReadAction(extension.Folder);
            var item = new MenuFlyoutSubItem { Text = extension.Name };

            // An extension with no popup still appears, and says so rather than being absent — its
            // absence would read as "not installed" for exactly the kind that needs no UI.
            if (action is { } popup)
            {
                var open = new MenuFlyoutItem { Text = "Open" };
                open.Click += (_, _) => _ = ShowExtensionPopupAsync(extension.Folder, extension.Name);
                item.Items.Add(open);
            }
            else
            {
                item.Items.Add(new MenuFlyoutItem { Text = "Runs in the background", IsEnabled = false });
            }

            var pin = new ToggleMenuFlyoutItem
            {
                Text = "Pin to the header",
                IsChecked = IsPinned(extension.Id, extension.Name),

                // Nothing to pin without a popup: the button would open nothing.
                IsEnabled = action != null,
            };
            pin.Click += (_, _) =>
            {
                SetPinned(extension.Id, extension.Name, pin.IsChecked);
                RefreshExtensionButtons();
            };
            item.Items.Add(pin);

            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0) return;

        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;
        menu.ShowAt(_extensionsButton);
    }

    /// <summary>
    /// Which extensions are pinned, keyed the way the extension itself is identified.
    /// </summary>
    /// <remarks>
    /// By store id where there is one, and by name otherwise — the same fallback the rest of this
    /// feature uses for an extension added from a folder, which has no id to be known by.
    /// </remarks>
    private static string PinKey(string id, string name) => string.IsNullOrEmpty(id) ? name : id;

    private static bool IsPinned(string id, string name) =>
        Classes.Settings.SettingsManager.Current.PinnedBrowserExtensions is { } pinned &&
        pinned.Contains(PinKey(id, name), StringComparer.OrdinalIgnoreCase);

    private static void SetPinned(string id, string name, bool pinned)
    {
        var list = Classes.Settings.SettingsManager.Current.PinnedBrowserExtensions ??= [];
        string key = PinKey(id, name);

        list.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (pinned) list.Add(key);

        Classes.Settings.SettingsManager.SaveSettings();
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
                ShowNotice($"{name} is not loaded in this launcher yet. Reopen it and try again.");
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
