// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.Win32;
using System;
using System.Text;
using System.Text.RegularExpressions;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Windows;

/// <summary>
/// Regular-window mode: presents a web launcher as an ordinary app window, so its pinned taskbar
/// button shows the running indicator while it is open and clicking that button closes it.
/// </summary>
/// <remarks>
/// <para><b>Why a switcher entry is inseparable from the taskbar button.</b> The running indicator
/// is derived purely from whether the launcher's AUMID group owns a taskbar-eligible window, and
/// eligibility is <c>WS_EX_TOOLWINDOW</c> — which governs the taskbar, Alt-Tab and Win+Tab as one.
/// Four ways round it were measured on Windows 11, and every one failed:</para>
/// <list type="number">
///   <item><c>ITaskbarList.AddTab</c> on the flyout with the tool bit kept and the correct AUMID
///   stamped — returns <c>S_OK</c> and produces no button. This is the API that looks like it
///   decouples the two, which is exactly why it had to be measured rather than trusted.</item>
///   <item>Clearing the tool bit on the flyout — button appears, and so does a switcher entry.</item>
///   <item>A 1×1 off-screen proxy window carrying the AUMID with
///   <c>WS_EX_APPWINDOW | WS_EX_NOACTIVATE</c> — button appears, and the proxy appears in the
///   switcher too, as a blank thumbnail. Worse clutter than a real window, not better.</item>
///   <item>The same proxy <em>owned</em> by a hidden tool window, on the theory that the switcher
///   represents an owned window by its root owner — no change. <c>WS_EX_APPWINDOW</c> forces
///   switcher inclusion regardless of ownership.</item>
/// </list>
/// <para>So the feature is one opt-in setting that changes what kind of window this is, rather than
/// a "show in the taskbar" toggle that could not honestly be delivered. Under
/// <see cref="Launcher.WebRegularWindow"/> the flyout also drops always-on-top and
/// dismiss-on-focus-loss, at which point a switcher entry is correct rather than clutter: there is
/// finally a reason to Alt-Tab <em>to</em> it.</para>
/// <para>Off — the default — none of this runs at all. No taskbar button, no switcher entry, no
/// window-style changes.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>Whether the flyout is currently presenting as a regular window.</summary>
    private bool _isRegularWindow;

    /// <summary>
    /// Applies or removes regular-window presentation to match whether the flyout is on screen.
    /// </summary>
    /// <remarks>
    /// The tool bit is cleared on show and restored on park, rather than dropped once at
    /// construction, and that is load-bearing: a dismissed flyout is <em>parked off the virtual
    /// screen, not hidden</em>, so it stays visible in the Win32 sense for the life of the app. A
    /// flyout made switcher-eligible once would sit in Alt-Tab forever, including for every
    /// launcher preloaded at startup under <c>KeepRunning</c> that the user has never opened.
    /// </remarks>
    private void ApplyTaskbarButton(bool wanted)
    {
        bool target = wanted && _launcher.WebRegularWindow;
        if (target == _isRegularWindow) return;
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        try
        {
            if (target)
            {
                // No pin means no button to light. Without a matching AUMID the window would raise
                // a taskbar button of its own *beside* the pin instead of lighting it, which is
                // both not the point and worse than leaving it alone — but the window still
                // becomes a regular window, because that is what the setting asked for.
                string aumid = PinAppUserModelId();
                if (!string.IsNullOrEmpty(aumid)) SetWindowAppUserModelId(_hwnd, aumid);

                ApplyWindowIcon();
                SetToolWindow(false);

                // The style change alone is not enough. Without this the window was correctly
                // restyled and simply had no button — the shell had not looked again. See the
                // remarks on ITaskbarList: it does nothing for a tool window, and everything for
                // one that has just stopped being one.
                GetTaskbarList()?.AddTab(_hwnd);
            }
            else
            {
                GetTaskbarList()?.DeleteTab(_hwnd);
                SetToolWindow(true);
            }

            _isRegularWindow = target;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Applying regular-window mode failed for launcher {Name}", _launcher.Name);
        }
    }

    private IntPtr _hIconSmall;
    private IntPtr _hIconBig;

    /// <summary>
    /// Gives the window the launcher's own icon, for the taskbar button and the switcher.
    /// </summary>
    /// <remarks>
    /// <para>A flyout has no title bar and never appears anywhere an icon is drawn, so it has never
    /// needed one — the moment it becomes a regular window it appears in two places that do, and
    /// without this both show a blank placeholder. The per-launcher <c>.ico</c> is the same file
    /// the pinned shortcut points at, so the button and its pin agree, and
    /// <c>EnsureLauncherIconSaved</c> writes it if no other surface has yet.</para>
    /// <para><b>Setting it once is not enough</b> — WinUI overrides <c>WM_SETICON</c> as it
    /// finishes initialising the window (WindowsAppSDK#2730), which is why this looked like it had
    /// simply not worked: the handles were set and then quietly replaced. The icons are therefore
    /// kept and re-sent from <see cref="PushWindowIcon"/> on every activation, which is the same
    /// workaround <c>SettingsWindow</c> already runs for the same reason.</para>
    /// <para>The HICONs are loaded once and deliberately leaked for the window's lifetime, as the
    /// shell goes on referencing them — the same bargain <c>WindowChrome.ApplyIcon</c> makes. The
    /// flyout instances are permanent (one per launcher), so this is two handles per web launcher
    /// that has ever run as a regular window, not a per-open leak.</para>
    /// </remarks>
    private void ApplyWindowIcon()
    {
        try
        {
            if (_hIconBig == IntPtr.Zero)
            {
                MainWindow.EnsureLauncherIconSaved(_launcher);

                string path = System.IO.Path.Combine(
                    MainWindow.GetPhysicalAppDataDir(), $"app-icon-{_launcher.Id}.ico");
                if (!System.IO.File.Exists(path)) return;

                // Also drives AppWindow.SetIcon, which is a separate path from WM_SETICON and the
                // one Alt-Tab prefers on a packaged build.
                Classes.WindowChrome.ApplyIcon(_hwnd, path);

                _hIconSmall = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                _hIconBig = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            }

            PushWindowIcon();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Applying the window icon failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>
    /// Re-asserts the window icon, which WinUI replaces during initialisation and activation.
    /// </summary>
    private void PushWindowIcon()
    {
        if (_hIconBig == IntPtr.Zero || _hwnd == IntPtr.Zero) return;

        if (_hIconSmall != IntPtr.Zero) SendMessage(_hwnd, WM_SETICON, ICON_SMALL, _hIconSmall);
        SendMessage(_hwnd, WM_SETICON, ICON_BIG, _hIconBig);
    }

    /// <summary>Adds or clears <c>WS_EX_TOOLWINDOW</c>, which governs taskbar and switchers alike.</summary>
    private void SetToolWindow(bool tool)
    {
        int current = GetWindowLong(_hwnd, GWL_EXSTYLE);
        int next = tool ? current | WS_EX_TOOLWINDOW : current & ~WS_EX_TOOLWINDOW;
        if (next == current) return;

        SetWindowLong(_hwnd, GWL_EXSTYLE, next);

        // The shell caches a window's taskbar eligibility. SWP_FRAMECHANGED asks it to look again
        // without the hide/show cycle the documentation reaches for — which is off the table here,
        // since SW_HIDE makes WinUI drop the composition surfaces the park exists to protect.
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Turns the taskbar button's click into a close, when the launcher asks for that.
    /// </summary>
    /// <remarks>
    /// <para>Returns whether the message was consumed. Clicking the taskbar button of the
    /// foreground window is a minimize request, so this is where that click is answered — but only
    /// under <see cref="Launcher.WebTaskbarClickCloses"/>. Left unconsumed it reaches
    /// <c>DefWindowProc</c> and the window genuinely minimizes, which is what an ordinary app
    /// window does and so is the default.</para>
    /// <para><b>The click produces nothing at all unless the window is minimizable</b>, which a
    /// flyout's <c>CreateForContextMenu</c> presenter is not. That is why regular-window mode sets
    /// <c>IsMinimizable</c>: without it the shell declines to send a minimize, and both behaviours
    /// — close and minimize — silently do nothing. Not obvious from this handler, which simply
    /// never runs.</para>
    /// </remarks>
    private bool HandleTaskbarMinimize(uint msg, IntPtr wParam)
    {
        if (!_isRegularWindow || msg != WM_SYSCOMMAND) return false;
        if (!_launcher.WebTaskbarClickCloses) return false;

        int command = (int)(wParam.ToInt64() & 0xFFF0);
        if (command is not (SC_MINIMIZE or SC_CLOSE)) return false;

        HideFlyout();
        return true;
    }

    /// <summary>
    /// The AUMID of this launcher's pinned taskbar button, or empty when it is not pinned.
    /// </summary>
    /// <remarks>
    /// <para>Read from the taskbar's own pin store in the registry. Three findings sit behind this,
    /// each of which killed an earlier plan:</para>
    /// <list type="bullet">
    ///   <item><b>Windows 11 does not keep these pins as <c>.lnk</c> files.</b>
    ///   <c>User Pinned\TaskBar</c> held only Chrome, Edge and VS Code on a machine showing eleven
    ///   Little Launcher pins, so reading the shortcut's property store — the obvious approach —
    ///   has nothing to read.</item>
    ///   <item><b>The AUMID is recoverable anyway</b>, verbatim, from the <c>Taskband</c> blobs.
    ///   That matters because it is minted inside the companion exe at pin time and recorded
    ///   nowhere else, so the alternative was to start recording it and help only future pins.</item>
    ///   <item><b>It carries the launcher id</b>, which is what makes the match safe:
    ///   <c>LittleLauncher.Launcher.{guid}.{tick}</c>. Only the tick is unknown, so the guid
    ///   anchors the search and no cross-launcher mix-up is possible.</item>
    /// </list>
    /// <para>Both values are searched: <c>Favorites</c> is the live pin order, and
    /// <c>FavoritesResolve</c> retains entries the former has dropped. Read on each open rather
    /// than cached — a pin can be added, removed or re-made while the app is running, and a re-pin
    /// mints a new AUMID.</para>
    /// <para><b>SUPERSEDED, AND KEPT ONLY AS A FALLBACK.</b> <see cref="Launcher.PinAumid"/> is
    /// recorded when the pin is made and is the only source that is always right; the scan below
    /// serves pins created before the app started recording it. <b>Do not promote it back.</b>
    /// Measured on a machine with eleven Little Launcher pins, those two blobs between them held
    /// the AUMIDs of eight — WhatsApp, Messenger and Web Launcher appeared in neither, and
    /// re-pinning did not add them. Windows 11 keeps some pins in a form that never embeds the
    /// string. The failure is loud, not silent: a window whose AUMID does not match its pin raises
    /// a <em>second</em> taskbar button beside it, which is how this was found.</para>
    /// </remarks>
    private string PinAppUserModelId()
    {
        if (!string.IsNullOrEmpty(_launcher.PinAumid)) return _launcher.PinAumid;

        // The blob is a packed shell structure, not a string table — matching the known AUMID shape
        // inside its UTF-16 bytes is deliberate, and far less brittle than parsing it.
        var pattern = new Regex(
            $@"LittleLauncher\.Launcher\.{Regex.Escape(_launcher.Id)}\.\d+",
            RegexOptions.IgnoreCase);

        foreach (string valueName in new[] { "Favorites", "FavoritesResolve" })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
                if (key?.GetValue(valueName) is not byte[] blob) continue;

                var match = pattern.Match(Encoding.Unicode.GetString(blob));
                if (match.Success) return match.Value;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Reading taskbar pin identity from {Value} failed", valueName);
            }
        }

        return "";
    }
}
