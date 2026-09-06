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

    /// <summary>The AppUserModelID currently stamped on the window, so a changed one is noticed.</summary>
    private string _appliedAumid = "";

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
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        // Checked on every show, ahead of the presentation short-circuit below, because the
        // identity can move while the presentation does not. Re-pinning a launcher mints a fresh
        // AUMID, and a window still carrying the previous one does not fail quietly: it raises a
        // second taskbar button beside the pin the user just clicked, which is exactly what the
        // mint's own comment in LauncherSettingsWindow warns about. The window outlives the pin,
        // so nothing else is in a position to notice.
        if (target)
        {
            RestampAppUserModelId();
            ApplyRelaunchProperties();
        }

        if (target == _isRegularWindow) return;

        try
        {
            if (target)
            {
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

    /// <summary>
    /// Puts the launcher's current pin identity on the window, if it is not already there.
    /// </summary>
    /// <remarks>
    /// <para>No pin means no button to light, and stamping nothing is the right answer then - the
    /// window still becomes a regular window, because that is what the setting asked for.</para>
    /// <para>A window that already had a taskbar button is torn off the taskbar and re-added when
    /// the identity changes. The shell reads the AUMID when it creates the button and groups it
    /// from that; rewriting the property under a live button leaves the grouping where it was, so
    /// the button has to be made again to move.</para>
    /// </remarks>
    private void RestampAppUserModelId()
    {
        string aumid = PinAppUserModelId();
        if (string.IsNullOrEmpty(aumid) || string.Equals(aumid, _appliedAumid, StringComparison.Ordinal))
            return;

        try
        {
            bool hadButton = _isRegularWindow;
            if (hadButton) GetTaskbarList()?.DeleteTab(_hwnd);

            SetWindowAppUserModelId(_hwnd, aumid);
            _appliedAumid = aumid;

            if (hadButton) GetTaskbarList()?.AddTab(_hwnd);

            // Recorded here, where the identity is actually applied, and only when it is the one
            // this app minted. The window is pinnable from its own taskbar button now
            // (ApplyRelaunchProperties), so that string becomes a real pin's identity the moment
            // the user does it, and JumpListService has nothing else to publish under: its own
            // fallback scan only matches the ticked shape a settings-window pin produces. The
            // registry guess above is deliberately not recorded, for the reason set out there.
            //
            // Saved without notifying sync, because LauncherPayload does not carry PinAumid: a
            // launcher's pin identity is a fact about this machine's taskbar. Announcing it would
            // push a payload identical to the server's and block downloads until it landed.
            if (string.IsNullOrEmpty(_launcher.PinAumid) &&
                string.Equals(aumid, MintedPinIdentity, StringComparison.Ordinal))
            {
                _launcher.PinAumid = aumid;
                Classes.Settings.SettingsManager.SaveSettings();
            }

            Logger.Info("Taskbar identity for {Name} is now {Aumid}", _launcher.Name, aumid);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Re-stamping the taskbar identity failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>What the relaunch properties currently say, so unchanged ones are not rewritten.</summary>
    private string _appliedRelaunch = "";

    /// <summary>
    /// Tells the shell what to run, draw and call this window if its button is pinned.
    /// </summary>
    /// <remarks>
    /// <para><b>Without this the button cannot be pinned at all.</b> An AUMID alone names a group;
    /// it does not say how to start it again, so "Pin to taskbar" has nothing to write down and
    /// either does not appear or produces a pin that opens nothing. The three relaunch properties
    /// are the answer, and they are the same three the companion exe sets on its own message box
    /// for the settings window's <b>Pin to taskbar</b> button. That flow exists precisely because
    /// there was no other pinnable window, and a regular-window launcher now is one.</para>
    /// <para>The values must match that flow exactly, not merely be equivalent. The command is the
    /// companion with this launcher's id, which is what the pinned shortcut has always run. The icon
    /// is an icon <em>resource</em> reference, <c>"path,0"</c>, never a bare path, which Windows
    /// cannot parse and silently answers with the generic document icon. The display name keeps the
    /// <c>Little Launcher - {name}</c> format because Windows caches pin display names per AUMID in
    /// CloudStore, so a second format would show up as the old name on any launcher pinned both
    /// ways.</para>
    /// <para>The base <c>.ico</c> is used rather than the timestamped copy the settings flow makes.
    /// That copy busts Windows' per-path icon cache between pin attempts, which matters when the
    /// user is re-pinning to fix an icon; here the window is simply advertising itself, and writing
    /// a new file on every open to serve a pin that may never happen is the wrong trade. A launcher
    /// whose icon changed after it was pinned is re-pinned from settings, as it always was.</para>
    /// </remarks>
    private void ApplyRelaunchProperties()
    {
        try
        {
            // Cheap values first, and the cache check before any of the disk work below. This runs
            // from every show and every launcher change, while the properties themselves are
            // written once, so an unchanged launcher must cost nothing, rather than saving an icon
            // and stat-ing two paths only to throw the result away at the comparison.
            string exe = MainWindow.GetFlyoutCompanionPath();
            string command = $"\"{exe}\" --launcher {_launcher.Id}";
            string display = $"Little Launcher - {_launcher.Name}";

            // The icon is not part of the key and does not need to be: its path is derived from the
            // launcher id, so it cannot change while the command stays the same.
            string applied = command + "|" + display;
            if (string.Equals(applied, _appliedRelaunch, StringComparison.Ordinal)) return;

            if (!System.IO.File.Exists(exe)) return;

            MainWindow.EnsureLauncherIconSaved(_launcher);
            string ico = System.IO.Path.Combine(
                MainWindow.GetPhysicalAppDataDir(), $"app-icon-{_launcher.Id}.ico");
            string icon = System.IO.File.Exists(ico) ? $"{ico},0" : $"{exe},0";

            SetWindowRelaunchProperties(_hwnd, icon, command, display);
            _appliedRelaunch = applied;

            Logger.Info("Relaunch identity for {Name} is now {Command}", _launcher.Name, command);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Setting relaunch properties failed for launcher {Name}", _launcher.Name);
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
    /// <para><b>The first application happens in the constructor, before the window has ever been
    /// shown.</b> Loading it from the first regular-window show is too late to be honest about:
    /// <c>ShowFlyout</c> puts the window on screen and only then calls
    /// <see cref="ApplyTaskbarButton"/>, so the window stood in front of the user for a quarter of
    /// a second carrying no icon at all. Windows re-reads a window's icon each time it draws one,
    /// so its taskbar and switchers never showed that gap; a tool that reads it once, as the window
    /// appears, would keep whatever it found instead — which is what a third-party Alt-Tab
    /// replacement was doing when this was found.</para>
    /// <para>Every later call is therefore a re-assertion, not a load. It must stay cheap once the
    /// window has an icon — it runs from every show that enters regular-window mode, and re-reading
    /// would render and rewrite the <c>.ico</c> on every open for nothing.
    /// <see cref="InvalidateWindowIcon"/> is what keeps the icon current when the launcher's own
    /// icon moves.</para>
    /// </remarks>
    private void ApplyWindowIcon()
    {
        if (_hIconBig != IntPtr.Zero)
        {
            PushWindowIcon();
            return;
        }

        // Writes the file if no other surface has yet. The reload path deliberately does not do
        // this: whatever called it has just rewritten the same file.
        MainWindow.EnsureLauncherIconSaved(_launcher);
        LoadWindowIcon();
    }

    /// <summary>
    /// Re-reads the launcher's icon after <c>MainWindow.RefreshLauncherIcon</c> has rewritten it.
    /// </summary>
    /// <remarks>
    /// <para><b>The switcher entry and the taskbar button must show what the tray shows, and
    /// loading the icon once could not do that.</b> The handles used to be loaded on the first show
    /// that presented as a regular window and then kept for the life of the window, so both
    /// surfaces wore whatever the launcher's icon happened to be at that instant — and for a web
    /// launcher that is the worst instant available. The window is built and its icon applied
    /// before the page has loaded and its favicon been adopted, so what gets captured is the
    /// placeholder the launcher opened with. The tray icon then moved on and the window did not,
    /// which is exactly the mismatch this exists to close.</para>
    /// <para>Everything that can move a launcher's icon — a page favicon adopted, a tray icon mode
    /// or custom image chosen, a theme change re-rendering a glyph, a sync download replacing the
    /// launcher — runs through <c>MainWindow.RefreshLauncherIcon</c>. That is the one place this
    /// hangs off, rather than a <c>PropertyChanged</c> subscription of its own that would have to
    /// list those triggers again and would fall behind the next one added.</para>
    /// <para>The guard is on the handles rather than on the launcher, because every web flyout now
    /// takes its icon in the constructor: a zero handle means the load failed — no <c>.ico</c> on
    /// disk yet — and re-reading a file that was not there is not this method's job.</para>
    /// </remarks>
    internal static void InvalidateWindowIcon(string launcherId)
    {
        if (Instances.TryGetValue(launcherId, out var panel) && panel._hIconBig != IntPtr.Zero)
            panel.LoadWindowIcon();
    }

    /// <summary>
    /// Puts the launcher's <c>.ico</c> on the window, replacing whatever icon it already carries.
    /// </summary>
    /// <remarks>
    /// <para>Both paths are set, for the reason <c>WindowChrome.ApplyIcon</c> sets both:
    /// <c>WM_SETICON</c> drives the taskbar button, and <c>AppWindow.SetIcon</c> is the one the
    /// switcher prefers on a packaged build. It does not call that helper, though — the helper
    /// leaks its <c>WM_SETICON</c> handles by design, which is the right bargain for a window that
    /// sets its icon once and the wrong one for a window that now re-sets it on every icon change.
    /// </para>
    /// <para>The previous pair is destroyed <em>after</em> the new pair is on the window, never
    /// before. <c>SendMessage</c> is synchronous, so by then the window is already carrying the
    /// replacement; freeing first — which is what <c>SettingsWindow.ApplyWindowIcon</c> does —
    /// leaves the window pointing at a destroyed icon for the length of the load.</para>
    /// </remarks>
    private void LoadWindowIcon()
    {
        try
        {
            string path = System.IO.Path.Combine(
                MainWindow.GetPhysicalAppDataDir(), $"app-icon-{_launcher.Id}.ico");
            if (!System.IO.File.Exists(path)) return;

            var small = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            var big = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);

            // A load that fails leaves the window wearing the icon it already had. A stale icon is
            // the better of the two failures — the alternative is a blank placeholder in the
            // taskbar and the switcher, which reads as a broken window rather than an old icon.
            if (big == IntPtr.Zero)
            {
                if (small != IntPtr.Zero) DestroyIcon(small);
                return;
            }

            var native = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            if (native != IntPtr.Zero)
            {
                try { GetAppWindow().SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(native)); }
                finally { DestroyIcon(native); }
            }

            var previousSmall = _hIconSmall;
            var previousBig = _hIconBig;

            _hIconSmall = small;
            _hIconBig = big;
            PushWindowIcon();

            if (previousSmall != IntPtr.Zero) DestroyIcon(previousSmall);
            if (previousBig != IntPtr.Zero) DestroyIcon(previousBig);
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
    /// Brings an already-open launcher to the front, for a clicked notification or a tray click
    /// on a launcher that is minimized rather than dismissed.
    /// </summary>
    /// <remarks>
    /// <para>Restore first, then foreground. A minimized window ignores
    /// <c>SetForegroundWindow</c> — it comes to the front still minimized, which is
    /// indistinguishable from nothing happening, and is the state both the taskbar button's click
    /// and the header's minimize leave a regular-window launcher in.</para>
    /// <para>Safe in flyout mode too, where it is close to a no-op: the window is already topmost
    /// and never minimized, so this is a foreground call on a window that already has it.</para>
    /// </remarks>
    internal void BringToFront()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        try
        {
            if (IsIconic(_hwnd)) ShowWindow(_hwnd, SW_RESTORE);
            SetForegroundWindow(_hwnd);
            RestorePageFocus();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Activating {Name} for a notification click failed", _launcher.Name);
        }
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
    /// <para><b>The minted fallback is now recorded too</b>, not just the one the settings window's
    /// pin flow mints. This window is pinnable from its own taskbar button
    /// (<see cref="ApplyRelaunchProperties"/>), so the string it is carrying becomes a real pin's
    /// identity the moment the user does that, and something has to be able to find it afterwards.
    /// </para>
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

        // No pin to join, so give the window an identity of its own rather than none. Without this
        // it falls in with the process's default identity and shows up as a generic "LittleLauncher"
        // button — which is what an unpinned launcher opened from its Start Menu shortcut did. With
        // it the button is this launcher's, carrying its name and its icon.
        //
        // Stable, with no tick: the tick exists on a *pin* to bust Windows' per-AUMID icon cache
        // between pin attempts, and there is nothing to bust here. A stable id also means repeated
        // opens keep landing on one button instead of accumulating.
        return MintedPinIdentity;
    }

    /// <summary>
    /// The identity this app gives a launcher that has no pin to join.
    /// </summary>
    /// <remarks>
    /// Its own member because two things need to agree on it: <see cref="PinAppUserModelId"/>
    /// returns it as a last resort, and <see cref="RestampAppUserModelId"/> has to recognise it to
    /// know whether the string it is about to stamp is one this app minted or one the registry scan
    /// guessed at. Only the former may be recorded.
    /// </remarks>
    private string MintedPinIdentity => $"LittleLauncher.Launcher.{_launcher.Id}";
}
