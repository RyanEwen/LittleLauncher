using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LauncherShortcut;

/// <summary>
/// Tiny companion exe intended to be pinned to the Windows taskbar.
///
/// Default mode:  Signals the main Little Launcher app to show its flyout via
///                a registered window message, passing the cursor position, then exits.
///                If Little Launcher is not running, launches it first, waits for its
///                window to appear, then signals the flyout.
/// --item mode:   Signals the main app to launch one entry of the launcher - an item, or a
///                bookmark for a web launcher - rather than showing the flyout. This is what a
///                task on the pinned button's jump list runs; the app publishes those tasks and
///                bakes the position and token into each one. Same "start the app if it is not
///                running" path as the default mode.
/// --pin mode:    Keeps the process alive with a MessageBox dialog so the user can
///                right-click the taskbar icon and choose "Pin to taskbar".
///
/// Taskbar pinning strategy (MSIX):
///   In MSIX builds, Start Menu shortcuts are NOT used for launcher flyout pinning
///   because VFS-redirected shortcuts cause duplicate "(2)" pins when combined with
///   relaunch properties. Instead, pinning relies entirely on window-level relaunch
///   properties (PKEY_AppUserModel_ID, RelaunchCommand, RelaunchIconResource,
///   RelaunchDisplayNameResource) set on the MessageBox HWND via IPropertyStore.
///   A CBT hook intercepts the MessageBox before it becomes visible to set the
///   per-launcher icon, relaunch properties, and window title.
///
/// AUMID format: LittleLauncher.Launcher.{guid}.{TickCount64}
///   WARNING: Windows caches pin display names per AUMID in CloudStore. If you
///   ever need to change the pin display name format, you MUST change the AUMID
///   format to bust the cache. Simply changing RelaunchDisplayNameResource is
///   not sufficient — the cached name persists across unpin/repin cycles.
///
/// Arguments:
///   --launcher {guid}  Target launcher ID
///   --name {name}      Launcher display name (used for pin title: "Little Launcher - {name}")
///   --item {index}     Launch this entry of the launcher instead of showing the flyout
///   --token {n}        Identifies what was published at that position, so an edited launcher
///                      cannot have the wrong entry launched out from under a stale jump list
///   --action {n}       Run one of the launcher's own commands (settings, edit, open in browser).
///                      The number is meaningless here and is forwarded as-is; Models/
///                      LauncherActions.cs on the app side is the only place it means anything.
///   --pin              Show the MessageBox for pin-to-taskbar flow
///   --layout {guid}    Legacy alias for --launcher
/// </summary>
static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, int Msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hInstance, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")]
    private static extern bool SetWindowText(nint hWnd, string lpString);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // Shell imports for per-launcher AUMID and window property stores.
    // In MSIX, these are the sole mechanism for taskbar pin identity — no Start Menu
    // shortcuts are created for launchers (VFS shortcuts + relaunch properties conflict
    // and cause duplicate "(2)" pins). See repo memory: msix-taskbar-pinning.md.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, out nint ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(nint pvar);

    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    // {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}
    private static readonly Guid AppUserModelGuid = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3);
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = AppUserModelGuid, pid = 5 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchCommand = new() { fmtid = AppUserModelGuid, pid = 2 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource = new() { fmtid = AppUserModelGuid, pid = 3 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource = new() { fmtid = AppUserModelGuid, pid = 4 };

    // CBT hook state for setting the MessageBox icon before it becomes visible
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    private static HookProc? _hookDelegate; // prevent GC while hook is active
    private static nint _cbtHook;
    private static nint _hIconBig;
    private static nint _hIconSmall;

    // Relaunch properties set on the MessageBox HWND via IPropertyStore.
    // These tell the taskbar exactly what command, icon, and display name to use
    // for the pin. In MSIX this is the ONLY identity mechanism (no Start Menu
    // shortcuts). In non-MSIX it supplements the Start Menu shortcut as a fallback.
    // The display name format must be "Little Launcher - {name}" — changing this
    // requires a new AUMID format to bust Windows' per-AUMID CloudStore cache.
    private static string? _relaunchAppId;
    private static string? _relaunchCommand;
    private static string? _relaunchIcon;
    private static string? _relaunchDisplayName;

    private static nint CbtCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            const int HCBT_CREATEWND = 3;
            const int HCBT_ACTIVATE = 5;
            if (code == HCBT_CREATEWND || code == HCBT_ACTIVATE)
            {
                // wParam is the HWND — set our icon immediately.
                // HCBT_CREATEWND fires before the window is visible, so the shell
                // picks up our icon for the taskbar without flashing the embedded one.
                // HCBT_ACTIVATE is a backup to re-set the icon once fully initialized.
                if (_hIconBig != 0)
                    SendMessage(wParam, 0x0080 /* WM_SETICON */, 1 /* ICON_BIG */, _hIconBig);
                if (_hIconSmall != 0)
                    SendMessage(wParam, 0x0080 /* WM_SETICON */, 0 /* ICON_SMALL */, _hIconSmall);
                // Unhook on activate — the dialog is fully initialized at this point
                if (code == HCBT_ACTIVATE && _cbtHook != 0)
                {
                    // Set relaunch properties so the taskbar pin uses the correct
                    // command, icon, and display name — independent of shell indexing.
                    if (_relaunchAppId != null)
                        SetWindowRelaunchProperties(wParam);

                    // Override the MessageBox title to the launcher display name.
                    // The taskbar reads the window title for the button tooltip,
                    // so this must match the relaunch display name.
                    if (_relaunchDisplayName != null)
                        SetWindowText(wParam, _relaunchDisplayName);

                    UnhookWindowsHookEx(_cbtHook);
                    _cbtHook = 0;
                }
            }
        }
        catch
        {
            // Swallow: an unhandled managed exception here causes
            // STATUS_FATAL_USER_CALLBACK_EXCEPTION and kills the process.
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Ensure cursor coordinates are reported in physical pixels.
        // Without this, a pinned non-MSIX helper can be DPI-virtualized,
        // causing the flyout anchor to be offset on scaled displays.
        _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

        // Parse --launcher {guid} argument (may be in any position)
        string? launcherId = null;
        string? launcherName = null;
        string? explicitIconPath = null;
        string? explicitAumid = null;
        int itemIndex = -1;
        int itemToken = 0;
        int launcherAction = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--launcher" || args[i] == "--layout")
                launcherId ??= args[i + 1];
            else if (args[i] == "--name")
                launcherName ??= args[i + 1];
            else if (args[i] == "--icon")
                explicitIconPath ??= args[i + 1];
            else if (args[i] == "--aumid")
                explicitAumid ??= args[i + 1];
            else if (args[i] == "--item" && itemIndex < 0)
                _ = int.TryParse(args[i + 1], out itemIndex);
            else if (args[i] == "--token")
                _ = int.TryParse(args[i + 1], out itemToken);
            else if (args[i] == "--action" && launcherAction == 0)
                _ = int.TryParse(args[i + 1], out launcherAction);
        }

        if (args.Length > 0 && args[0] == "--pin")
        {
            // Resolve icon path:
            // Both LoadImage (WM_SETICON) and RelaunchIconResource use the same
            // --icon timestamped path. Windows caches icon bitmaps per file path
            // in its icon cache DB, so reusing a stable path across pin attempts
            // serves stale cached bitmaps. The timestamped copy busts this cache.
            // CleanUpStaleIconFiles keeps the most recent -pin*.ico per launcher
            // so the file persists for the pinned shortcut's RelaunchIconResource.
            string loadIconPath = Path.Combine(AppContext.BaseDirectory, "app-icon.ico");

            if (!string.IsNullOrEmpty(explicitIconPath) && File.Exists(explicitIconPath))
                loadIconPath = explicitIconPath;
            else if (!string.IsNullOrEmpty(launcherId))
            {
                string perLauncherIcon = Path.Combine(AppContext.BaseDirectory, $"app-icon-{launcherId}.ico");
                if (File.Exists(perLauncherIcon))
                    loadIconPath = perLauncherIcon;
            }

            if (!string.IsNullOrEmpty(launcherId))
            {
                // --aumid is the main app telling us which identity it has recorded against this
                // launcher. It must win: a window that wants to light this pinned button has to
                // carry the identical string, and only the app side can remember it — this process
                // exits as soon as the pin is made. Minting our own is the fallback for an older
                // app calling a newer companion.
                //
                // Either way it carries a tick, for the original reason: every pin attempt needs a
                // unique AUMID to bust Windows' per-AUMID icon cache from any previous (possibly
                // broken) pin. File size alone isn't enough — it stays the same if the icon content
                // hasn't changed.
                string aumid = !string.IsNullOrEmpty(explicitAumid)
                    ? explicitAumid
                    : $"LittleLauncher.Launcher.{launcherId}.{Environment.TickCount64}";
                SetCurrentProcessExplicitAppUserModelID(aumid);

                string exePath = Path.Combine(AppContext.BaseDirectory, "LittleLauncherFlyout.exe");
                _relaunchAppId = aumid;
                _relaunchCommand = $"\"{exePath}\" --launcher {launcherId}";
                // Must be an icon *resource* reference — "path,index" — not a bare path.
                // Windows cannot parse a path on its own, so the taskbar silently falls back to
                // the generic document icon: the pinned button came up blank while the window's
                // title bar (which is driven by WM_SETICON, not this) showed the right icon.
                // Same format the Start Menu shortcut's IconLocation already uses.
                _relaunchIcon = File.Exists(loadIconPath) ? $"{loadIconPath},0" : $"{exePath},0";
                _relaunchDisplayName = string.IsNullOrEmpty(launcherName)
                    ? "Little Launcher"
                    : $"Little Launcher - {launcherName}";
            }

            if (File.Exists(loadIconPath))
            {
                // Use system metrics for DPI-correct icon sizes
                int bigSize = GetSystemMetrics(11 /* SM_CXICON */);    // 32 @100%, 48 @150%
                int smallSize = GetSystemMetrics(49 /* SM_CXSMICON */); // 16 @100%, 24 @150%

                _hIconBig = LoadImage(0, loadIconPath, 1 /* IMAGE_ICON */, bigSize, bigSize,
                    0x0010 /* LR_LOADFROMFILE */);
                _hIconSmall = LoadImage(0, loadIconPath, 1 /* IMAGE_ICON */, smallSize, smallSize,
                    0x0010 /* LR_LOADFROMFILE */);

                // Install a CBT hook to set the icon on the MessageBox window
                // before it becomes visible (HCBT_ACTIVATE fires pre-paint).
                _hookDelegate = CbtCallback;
                _cbtHook = SetWindowsHookEx(5 /* WH_CBT */, _hookDelegate, 0,
                    GetCurrentThreadId());
            }

            MessageBoxW(
                0,
                "This app is now running so you can pin it to the taskbar.\n\n" +
                "Right-click the taskbar icon for this app and choose \"Pin to taskbar\".\n\n" +
                "Click OK to close this window once you're done.",
                "Pin to Taskbar",
                0x00010040 /* MB_ICONINFORMATION | MB_SETFOREGROUND */);

            // Clean up in case the hook wasn't triggered
            if (_cbtHook != 0)
            {
                UnhookWindowsHookEx(_cbtHook);
                _cbtHook = 0;
            }
            return;
        }

        var target = FindWindow(null, "Little Launcher Host");

        if (target == 0)
        {
            // Main app isn't running — launch it, then signal the flyout.
            string myDir = AppContext.BaseDirectory;
            string mainExe = Path.Combine(myDir, "LittleLauncher.exe");

            // Fallback for MSIX: when the companion exe lives in AppData,
            // the main exe isn't in the same directory. Read the breadcrumb
            // file written by the main app during EnsureFlyoutShortcut().
            if (!File.Exists(mainExe))
            {
                string breadcrumb = Path.Combine(myDir, "main-exe-path.txt");
                if (File.Exists(breadcrumb))
                {
                    string candidate = File.ReadAllText(breadcrumb).Trim();
                    if (File.Exists(candidate))
                        mainExe = candidate;
                }
            }

            if (!File.Exists(mainExe))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = mainExe,
                Arguments = "--silent",
                WorkingDirectory = Path.GetDirectoryName(mainExe) ?? myDir,
                UseShellExecute = false
            });

            // Poll for the host window to appear (up to 10 seconds).
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(100);
                target = FindWindow(null, "Little Launcher Host");
                if (target != 0)
                    break;
            }

            if (target == 0)
                return;
        }

        // App is running — signal it. A jump list task names an entry to launch; everything else
        // means "show the flyout", and the two are different messages because their two words of
        // payload mean different things.
        if (itemIndex >= 0 && !string.IsNullOrEmpty(launcherId))
        {
            var launchMsg = (int)RegisterWindowMessage($"LittleLauncher_LaunchItem_{launcherId}");
            PostMessage(target, launchMsg, itemIndex, itemToken);
            return;
        }

        if (launcherAction > 0 && !string.IsNullOrEmpty(launcherId))
        {
            var actionMsg = (int)RegisterWindowMessage($"LittleLauncher_LauncherAction_{launcherId}");
            PostMessage(target, actionMsg, launcherAction, 0);
            return;
        }

        GetCursorPos(out var pt);
        // Use a per-launcher message name if a launcher ID was specified,
        // otherwise use the legacy message for backward compat.
        string msgName = string.IsNullOrEmpty(launcherId)
            ? "LittleLauncher_ShowFlyout"
            : $"LittleLauncher_ShowFlyout_{launcherId}";
        var msg = (int)RegisterWindowMessage(msgName);
        PostMessage(target, msg, pt.X, pt.Y);
    }

    /// <summary>
    /// Sets PKEY_AppUserModel_ID, RelaunchCommand, RelaunchIconResource, and
    /// RelaunchDisplayNameResource on the given HWND via its IPropertyStore.
    /// Uses raw COM vtable calls for Native AOT compatibility.
    /// </summary>
    private static unsafe void SetWindowRelaunchProperties(nint hwnd)
    {
        var IID_IPropertyStore = new Guid(0x886D8EEB, 0x8CF2, 0x4446, 0x8D, 0x02, 0xCD, 0xBA, 0x1D, 0xBD, 0xCF, 0x99);
        int hr = SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out nint pStore);
        if (hr != 0 || pStore == 0) return;

        try
        {
            nint* vtbl = *(nint**)pStore;
            // IPropertyStore vtable: [0]QI [1]AddRef [2]Release [3]GetCount [4]GetAt [5]GetValue [6]SetValue [7]Commit
            var setValue = (delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, nint, int>)vtbl[6];
            var commit = (delegate* unmanaged[Stdcall]<nint, int>)vtbl[7];

            if (_relaunchAppId != null)
                SetPropString(pStore, setValue, PKEY_AppUserModel_ID, _relaunchAppId);
            if (_relaunchCommand != null)
                SetPropString(pStore, setValue, PKEY_AppUserModel_RelaunchCommand, _relaunchCommand);
            if (_relaunchIcon != null)
                SetPropString(pStore, setValue, PKEY_AppUserModel_RelaunchIconResource, _relaunchIcon);
            if (_relaunchDisplayName != null)
                SetPropString(pStore, setValue, PKEY_AppUserModel_RelaunchDisplayNameResource, _relaunchDisplayName);

            commit(pStore);
        }
        finally
        {
            nint* vtbl = *(nint**)pStore;
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2];
            release(pStore);
        }
    }

    private static unsafe void SetPropString(
        nint pStore,
        delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, nint, int> setValue,
        PROPERTYKEY key, string value)
    {
        const int PROPVARIANT_SIZE = 24;
        nint pv = Marshal.AllocCoTaskMem(PROPVARIANT_SIZE);
        try
        {
            // Zero-init then write VT_LPWSTR (31) + string pointer
            new Span<byte>((void*)pv, PROPVARIANT_SIZE).Clear();
            Marshal.WriteInt16(pv, 0, 31); // VT_LPWSTR
            Marshal.WriteIntPtr(pv, 8, Marshal.StringToCoTaskMemUni(value));

            setValue(pStore, &key, pv);
        }
        finally
        {
            PropVariantClear(pv);
            Marshal.FreeCoTaskMem(pv);
        }
    }
}
