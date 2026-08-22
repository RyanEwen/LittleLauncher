// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// The browser keyboard shortcuts, and the sheet that lists them.
/// </summary>
/// <remarks>
/// <para><b>Chromium's own accelerators are deliberately off</b> — <c>ConfigureCore</c> sets
/// <c>AreBrowserAcceleratorKeysEnabled = false</c>, so a tray flyout does not inherit Ctrl+N,
/// Ctrl+P and the rest of a browser window's keys, most of which have nowhere sensible to go from
/// here. Turning them back on is not the way to get Ctrl+W: it would bring the whole set, and the
/// ones the flyout wants (close *this* tab, cycle *these* tabs) do not mean what Chromium means by
/// them anyway. So the host owns them, and the set is the short list a launcher can actually
/// honour.</para>
/// <para><b>Two routes in, because focus is in one of two places.</b> With the chrome focused — the
/// address box, a tab chip, a bookmark — XAML sees the key and
/// <see cref="KeyboardAccelerator"/> is enough. With the <em>page</em> focused, which is most of the
/// time, the key never reaches this window at all: WebView2's child HWNDs take it, and
/// <c>WM_KEYDOWN</c> never reaches the window subclass. That is the same wall the Escape-to-dismiss
/// behaviour hits, and is why <c>_pageHadFocus</c> exists.</para>
/// <para>So the page half is a document-created script that listens in the <b>capture</b> phase and
/// posts the combo back — capture, so a page that swallows keydown on the way up cannot take the
/// browser's own shortcuts with it. Both routes end in <see cref="InvokeShortcut"/>, so there is one
/// list of what the keys do and no way for the two to drift.</para>
/// <para>The limits are worth knowing: a key pressed inside a cross-origin iframe belongs to that
/// frame's document and never reaches this listener, and neither route sees a key while a native
/// menu or the browser's own find bar has the keyboard. Both are edge cases in a launcher, and
/// neither is fixable from here without the controller WinUI's WebView2 does not expose.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>
    /// The one list. Everything — the accelerators, the page script and the sheet — is built from
    /// it, so a shortcut cannot be handled without being documented or documented without working.
    /// </summary>
    /// <param name="Id">Matched by both routes; also the page script's wire value.</param>
    /// <param name="Keys">As shown on the sheet, and parsed into a XAML accelerator.</param>
    /// <param name="Description">What it does, in the flyout's terms rather than a browser's.</param>
    /// <param name="Group">Heading it sits under on the sheet.</param>
    private record Shortcut(string Id, string Keys, string Description, string Group);

    private static readonly Shortcut[] Shortcuts =
    [
        new("tab.next",     "Ctrl+Tab",          "Next tab",                          "Tabs"),
        new("tab.previous", "Ctrl+Shift+Tab",    "Previous tab",                      "Tabs"),
        new("tab.new",      "Ctrl+T",            "New tab on this launcher's page",   "Tabs"),
        new("tab.close",    "Ctrl+W",            "Close the current tab",             "Tabs"),
        new("tab.reopen",   "Ctrl+Shift+T",      "Reopen the tab you just closed",    "Tabs"),
        new("tab.first",    "Ctrl+1 … Ctrl+8",   "Jump to that tab",                  "Tabs"),
        new("tab.last",     "Ctrl+9",            "Jump to the last tab",              "Tabs"),

        new("nav.back",     "Alt+Left",          "Back",                              "Navigation"),
        new("nav.forward",  "Alt+Right",         "Forward",                           "Navigation"),
        new("nav.reload",   "Ctrl+R  or  F5",    "Reload the page",                   "Navigation"),
        new("nav.address",  "Ctrl+L",            "Focus the address bar",             "Navigation"),
        new("nav.home",     "Alt+Home",          "Back to this launcher's own page",  "Navigation"),

        new("page.zoomIn",  "Ctrl+Plus  or  Ctrl+Wheel up",   "Zoom in",              "Page"),
        new("page.zoomOut", "Ctrl+Minus  or  Ctrl+Wheel down", "Zoom out",             "Page"),
        new("page.zoomOff", "Ctrl+0",            "Reset zoom to 100%",                "Page"),
        new("page.bookmark", "Ctrl+D",           "Add or remove this page's bookmark", "Page"),
        new("page.browser", "Ctrl+Shift+O",      "Open this page in your browser",    "Page"),

        new("app.shortcuts", "Ctrl+/",           "Show this list",                    "Launcher"),
        new("app.close",    "Escape",            "Close the flyout",                  "Launcher"),
    ];

    /// <summary>Addresses of tabs closed this session, newest last. Ctrl+Shift+T pops it.</summary>
    private readonly List<string> _closedTabUrls = [];

    /// <summary>How many closed tabs are worth offering back before it is just clutter.</summary>
    private const int ClosedTabMemory = 10;

    // ── The page half ───────────────────────────────────────────────

    /// <summary>
    /// Listens for the shortcut combos in the page and hands them to the host.
    /// </summary>
    /// <remarks>
    /// <para>Capture phase and <c>preventDefault</c>, so the page neither swallows the key first nor
    /// acts on it afterwards — a chat app that binds Ctrl+W to something of its own would otherwise
    /// both close the tab and do that. Everything not in the list is left entirely alone: the script
    /// returns before touching the event, so ordinary typing is untouched.</para>
    /// <para>Ctrl+wheel is caught here too, for a different reason: left to the browser it drives a
    /// zoom this app can neither read nor reset. The cost is that a page using Ctrl+wheel for a
    /// canvas of its own does not see it, which is the same trade the keys above already make.</para>
    /// </remarks>
    private const string ShortcutBridgeScript = """
        (function () {
            if (window.__llShortcuts) return;
            if (!window.chrome || !window.chrome.webview) return;
            window.__llShortcuts = true;

            function idFor(e) {
                var ctrl = e.ctrlKey, shift = e.shiftKey, alt = e.altKey;
                var k = e.key;

                if (ctrl && !alt) {
                    if (e.code === 'Tab') return shift ? 'tab.previous' : 'tab.next';
                    if (!shift) {
                        if (k === 't' || k === 'T') return 'tab.new';
                        if (k === 'w' || k === 'W') return 'tab.close';
                        if (k === 'r' || k === 'R') return 'nav.reload';
                        if (k === 'l' || k === 'L') return 'nav.address';
                        if (k === 'd' || k === 'D') return 'page.bookmark';
                        if (k === '/') return 'app.shortcuts';
                        if (k === '0') return 'page.zoomOff';
                        if (k === '+' || k === '=') return 'page.zoomIn';
                        if (k === '-' || k === '_') return 'page.zoomOut';
                        if (k >= '1' && k <= '8') return 'tab.index:' + k;
                        if (k === '9') return 'tab.last';
                    }
                    if (shift) {
                        if (k === 'T') return 'tab.reopen';
                        if (k === 'O') return 'page.browser';
                    }
                }

                if (alt && !ctrl && !shift) {
                    if (k === 'ArrowLeft') return 'nav.back';
                    if (k === 'ArrowRight') return 'nav.forward';
                    if (k === 'Home') return 'nav.home';
                }

                if (!ctrl && !alt && !shift && k === 'F5') return 'nav.reload';

                return null;
            }

            // A middle-click, or a Ctrl/Shift-click, means "open it but leave me here". The new
            // window that follows arrives at the host through NewWindowRequested, which carries no
            // modifier state at all — so the gesture is reported as it happens and the host pairs
            // the two up by time. Nothing is prevented here: the page goes on doing exactly what it
            // would have done, and only *where the result lands* changes.
            window.addEventListener('mousedown', function (e) {
                var background = e.button === 1 || (e.button === 0 && (e.ctrlKey || e.shiftKey));
                if (!background) return;

                try { window.chrome.webview.postMessage(JSON.stringify({ __ll: 'bgIntent' })); } catch (err) { }
            }, true);

            window.addEventListener('keydown', function (e) {
                var id = idFor(e);
                if (!id) return;

                e.preventDefault();
                e.stopPropagation();
                try { window.chrome.webview.postMessage(JSON.stringify({ __ll: 'key', id: id })); } catch (err) { }
            }, true);

            // Ctrl+wheel is the browser's own zoom, and the browser's own zoom is the one thing this
            // window cannot undo: it lives on the controller WinUI never exposes, so nothing in the
            // menu, the settings dialog or Ctrl+0 can reach it. Taken here it becomes the launcher's
            // zoom, which every one of those can put back. IsZoomControlEnabled is off to match, so
            // a page that swallows this event first still cannot zoom out of the app's reach.
            var wheeled = 0;
            window.addEventListener('wheel', function (e) {
                if (!e.ctrlKey || e.altKey || e.shiftKey || !e.deltaY) return;

                e.preventDefault();
                e.stopPropagation();

                // One rung per notch, and a trackpad pinch arrives as a stream of small deltas
                // rather than one of ~100, so without a threshold a pinch crosses the whole ladder.
                if (wheeled * e.deltaY < 0) wheeled = 0;
                wheeled += e.deltaY;
                if (Math.abs(wheeled) < 100) return;

                var id = wheeled < 0 ? 'page.zoomIn' : 'page.zoomOut';
                wheeled = 0;
                try { window.chrome.webview.postMessage(JSON.stringify({ __ll: 'key', id: id })); } catch (err) { }
            }, { capture: true, passive: false });
        })();
        """;

    private async Task InstallShortcutBridgeAsync(CoreWebView2 core)
    {
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutBridgeScript);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Installing the shortcut bridge failed for launcher {Name}", _launcher.Name);
        }
    }

    // ── The chrome half ─────────────────────────────────────────────

    /// <summary>
    /// Adds the same shortcuts as XAML accelerators, for when focus is not in the page.
    /// </summary>
    /// <remarks>
    /// Built from the same table, with the handful of combos XAML cannot express left to the page
    /// script — <c>Ctrl+Plus</c> and the numeric jumps differ by keyboard layout, and are not worth
    /// a per-layout table for the case where focus happens to be on a tab chip.
    /// </remarks>
    private void InstallShortcutAccelerators(UIElement root)
    {
        foreach (var shortcut in Shortcuts)
        {
            if (!TryParseAccelerator(shortcut.Keys, out var key, out var modifiers)) continue;

            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            string id = shortcut.Id;
            accelerator.Invoked += (_, e) =>
            {
                e.Handled = true;
                InvokeShortcut(id);
            };
            root.KeyboardAccelerators.Add(accelerator);
        }
    }

    /// <summary>Turns "Ctrl+Shift+Tab" into a key and its modifiers, or gives up.</summary>
    private static bool TryParseAccelerator(string keys, out global::Windows.System.VirtualKey key,
                                            out global::Windows.System.VirtualKeyModifiers modifiers)
    {
        key = global::Windows.System.VirtualKey.None;
        modifiers = global::Windows.System.VirtualKeyModifiers.None;

        // Anything with an "or", an ellipsis or a symbol name is documentation rather than one
        // combo; the page script owns those.
        if (keys.Contains(' ') || keys.Contains('…')) return false;

        foreach (string part in keys.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part)
            {
                case "Ctrl": modifiers |= global::Windows.System.VirtualKeyModifiers.Control; continue;
                case "Shift": modifiers |= global::Windows.System.VirtualKeyModifiers.Shift; continue;
                case "Alt": modifiers |= global::Windows.System.VirtualKeyModifiers.Menu; continue;
            }

            if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
            {
                key = (global::Windows.System.VirtualKey)char.ToUpperInvariant(part[0]);
                continue;
            }

            if (!Enum.TryParse(part, ignoreCase: true, out global::Windows.System.VirtualKey named)) return false;
            key = named;
        }

        return key != global::Windows.System.VirtualKey.None;
    }

    // ── What they do ────────────────────────────────────────────────

    /// <summary>Runs one shortcut, whichever route it arrived by.</summary>
    private void InvokeShortcut(string id)
    {
        // Ctrl+1..8 carry their digit rather than being eight entries in the table.
        if (id.StartsWith("tab.index:", StringComparison.Ordinal))
        {
            if (int.TryParse(id.AsSpan("tab.index:".Length), out int oneBased))
                ActivateTabAt(oneBased - 1);
            return;
        }

        switch (id)
        {
            case "tab.next": CycleTab(1); break;
            case "tab.previous": CycleTab(-1); break;
            case "tab.new": OpenNewTab(); break;
            case "tab.close": if (_activeTab is { } closing) CloseTab(closing); break;
            case "tab.reopen": ReopenClosedTab(); break;
            case "tab.last": ActivateTabAt(_tabs.Count - 1); break;

            case "nav.back": GoBack(); break;
            case "nav.forward": GoForward(); break;
            case "nav.reload": ReloadPage(); break;
            case "nav.address": FocusAddressBar(); break;
            case "nav.home": GoHome(); break;

            case "page.zoomIn": StepZoom(1); break;
            case "page.zoomOut": StepZoom(-1); break;
            case "page.zoomOff": SetZoom(100); break;
            case "page.bookmark": ToggleBookmarkForAddress(); break;
            case "page.browser": OpenInBrowser(); break;

            case "app.shortcuts": _ = ShowShortcutsAsync(); break;
            case "app.close": HideFlyout(); break;
        }
    }

    /// <summary>Moves <paramref name="delta"/> tabs along, wrapping, as a browser does.</summary>
    private void CycleTab(int delta)
    {
        if (_tabs.Count < 2 || _activeTab == null) return;

        int index = _tabs.IndexOf(_activeTab);
        if (index < 0) return;

        // Modulo twice, so a negative delta lands in range rather than on a negative index.
        int next = ((index + delta) % _tabs.Count + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[next]);
    }

    private void ActivateTabAt(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        ActivateTab(_tabs[index]);
    }

    /// <summary>Notes a closing tab's address, so Ctrl+Shift+T can bring it back.</summary>
    /// <remarks>
    /// The address only — a tab is closed to release its browser, and holding the browser open to
    /// make reopening cheaper would undo the reason for closing it. Reopening reloads, exactly as
    /// it does in a browser after a restart.
    /// </remarks>
    private void RememberClosedTab(WebTab tab)
    {
        string url = "";
        try { url = tab.View.CoreWebView2?.Source ?? tab.NavigatedUrl; }
        catch (Exception ex) { Logger.Debug(ex, "Reading a closing tab's address failed"); }

        url = NormalizeUrl(url);
        if (string.IsNullOrEmpty(url) || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;

        _closedTabUrls.Add(url);
        if (_closedTabUrls.Count > ClosedTabMemory) _closedTabUrls.RemoveAt(0);
    }

    private void ReopenClosedTab()
    {
        if (_closedTabUrls.Count == 0) return;

        string url = _closedTabUrls[^1];
        _closedTabUrls.RemoveAt(_closedTabUrls.Count - 1);

        // Forward, unlike a bookmark opened in a new tab: reopening is a request to go back to
        // that page, not to park it behind the one in front.
        _ = OpenLinkTabAsync(url);
    }

    private void FocusAddressBar()
    {
        if (!_launcher.WebShowAddressBar)
        {
            // Asking for the address bar is asking to see it. Turning it on for good rather than
            // for this visit matches the More menu, which is the only other way to get it.
            _launcher.WebShowAddressBar = true;
            Classes.Settings.SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            ApplyAddressBarVisibility();
        }

        _addressBox.Focus(FocusState.Programmatic);
        _addressBox.SelectAll();
    }

    /// <summary>Moves <paramref name="rungs"/> along the zoom ladder, as a browser's zoom does.</summary>
    /// <remarks>
    /// <para>Persisted rather than held for the visit, because <see cref="Launcher.WebZoomPercent"/>
    /// is a launcher setting and there is no per-visit zoom to write to — see <c>ApplyZoom</c> for
    /// why this is a CSS zoom rather than the controller's.</para>
    /// <para>Rungs of <see cref="Launcher.WebZoomLevels"/>, not a flat percentage: a step used to
    /// add ten points, which walks straight off the levels the "…" menu and the settings dialog
    /// offer. Two presses left the launcher on 120%, a value neither picker could show, and the
    /// settings combo answered by displaying its first entry instead.</para>
    /// </remarks>
    private void StepZoom(int rungs)
    {
        int current = _launcher.ResolvedWebZoomPercent;
        int[] levels = Launcher.WebZoomLevelsIncluding(current);
        int index = Array.IndexOf(levels, current);

        SetZoom(levels[Math.Clamp(index + rungs, 0, levels.Length - 1)]);
    }

    /// <summary>Puts the launcher on <paramref name="percent"/>, wherever the change came from.</summary>
    /// <remarks>
    /// Compared against the <em>resolved</em> zoom rather than the stored one, so resetting a
    /// launcher that has never been zoomed is the no-op it looks like rather than a write of 100
    /// over an unset 0, which would save settings and wake the sync service to say nothing.
    /// </remarks>
    private void SetZoom(int percent)
    {
        int clamped = Math.Clamp(percent, Launcher.MinWebZoomPercent, Launcher.MaxWebZoomPercent);
        if (clamped == _launcher.ResolvedWebZoomPercent) return;

        _launcher.WebZoomPercent = clamped;
        Classes.Settings.SettingsManager.SaveSettings();
        Services.AutoSyncService.NotifyLaunchersChanged();
        ApplyZoom();
    }

    // ── The sheet ───────────────────────────────────────────────────

    /// <summary>Shows the shortcut list, pinned open like every other window the flyout raises.</summary>
    private async Task ShowShortcutsAsync()
    {
        if (_isModalOpen) return;

        _isModalOpen = true;
        SetTopmost(false);
        try
        {
            var rows = Shortcuts.Select(s => (s.Group, s.Keys, s.Description)).ToList();
            await ShortcutsWindow.ShowAsync(_launcher.Name, rows, _hwnd, w => _openModal = w);
        }
        finally
        {
            _isModalOpen = false;
            _openModal = null;

            if (_hwnd != IntPtr.Zero && NativeMethodsIsWindow(_hwnd))
            {
                SetTopmost(true);
                RestoreActivation();
            }
        }
    }

    private static bool NativeMethodsIsWindow(IntPtr hwnd) => Classes.NativeMethods.IsWindow(hwnd);
}
