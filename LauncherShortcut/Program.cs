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
/// --pin mode:    Keeps the process alive with a dialog so the user can right-click
///                the taskbar icon and choose "Pin to taskbar", then close the dialog.
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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // CBT hook state for setting the MessageBox icon before it becomes visible
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    private static HookProc? _hookDelegate; // prevent GC while hook is active
    private static nint _cbtHook;
    private static nint _hIconBig;
    private static nint _hIconSmall;

    private static nint CbtCallback(int code, nint wParam, nint lParam)
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
                UnhookWindowsHookEx(_cbtHook);
                _cbtHook = 0;
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    static void Main(string[] args)
    {
        // Ensure cursor coordinates are reported in physical pixels.
        // Without this, a pinned non-MSIX helper can be DPI-virtualized,
        // causing the flyout anchor to be offset on scaled displays.
        _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

        if (args.Length > 0 && args[0] == "--pin")
        {
            // Load the custom icon at proper sizes so the taskbar shows it
            // immediately when the pin dialog appears (via CBT hook below).
            // Use AppContext.BaseDirectory — in MSIX, the companion exe lives
            // in the physical VFS redirect folder, so the icon is right next to it.
            string iconPath = Path.Combine(AppContext.BaseDirectory, "app-icon.ico");

            if (File.Exists(iconPath))
            {
                // Use system metrics for DPI-correct icon sizes
                int bigSize = GetSystemMetrics(11 /* SM_CXICON */);    // 32 @100%, 48 @150%
                int smallSize = GetSystemMetrics(49 /* SM_CXSMICON */); // 16 @100%, 24 @150%

                _hIconBig = LoadImage(0, iconPath, 1 /* IMAGE_ICON */, bigSize, bigSize,
                    0x0010 /* LR_LOADFROMFILE */);
                _hIconSmall = LoadImage(0, iconPath, 1 /* IMAGE_ICON */, smallSize, smallSize,
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
                0x00000040 /* MB_ICONINFORMATION */);

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

        // App is running — signal it to show the flyout.
        GetCursorPos(out var pt);
        var msg = (int)RegisterWindowMessage("LittleLauncher_ShowFlyout");
        PostMessage(target, msg, pt.X, pt.Y);
    }
}
