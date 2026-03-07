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

    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    static void Main(string[] args)
    {
        // Ensure cursor coordinates are reported in physical pixels.
        // Without this, a pinned non-MSIX helper can be DPI-virtualized,
        // causing the flyout anchor to be offset on scaled displays.
        _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

        if (args.Length > 0 && args[0] == "--pin")
        {
            // Load the custom icon (if any) so the taskbar shows it while pinning
            string iconPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LittleLauncher", "app-icon.ico");
            nint hIcon = File.Exists(iconPath)
                ? LoadImage(0, iconPath, 1 /* IMAGE_ICON */, 0, 0, 0x0010 /* LR_LOADFROMFILE */)
                : 0;

            if (hIcon != 0)
            {
                // Set the custom icon on the MessageBox once it appears
                var thread = new Thread(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        Thread.Sleep(50);
                        var mb = FindWindow(null, "Pin to Taskbar");
                        if (mb != 0)
                        {
                            SendMessage(mb, 0x0080 /* WM_SETICON */, 1 /* ICON_BIG */, hIcon);
                            SendMessage(mb, 0x0080 /* WM_SETICON */, 0 /* ICON_SMALL */, hIcon);
                            break;
                        }
                    }
                });
                thread.IsBackground = true;
                thread.Start();
            }

            MessageBoxW(
                0,
                "This app is now running so you can pin it to the taskbar.\n\n" +
                "Right-click the taskbar icon for this app and choose \"Pin to taskbar\".\n\n" +
                "Click OK to close this window once you're done.",
                "Pin to Taskbar",
                0x00000040 /* MB_ICONINFORMATION */);
            return;
        }

        var target = FindWindow(null, "Little Launcher Host");

        if (target == 0)
        {
            // Main app isn't running — launch it, then signal the flyout.
            string myDir = AppContext.BaseDirectory;
            string mainExe = Path.Combine(myDir, "LittleLauncher.exe");
            if (!File.Exists(mainExe))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = mainExe,
                WorkingDirectory = myDir,
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
