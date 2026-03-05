using System.Runtime.InteropServices;

namespace LauncherShortcut;

/// <summary>
/// Tiny companion exe intended to be pinned to the Windows taskbar.
///
/// Default mode:  Signals the main TaskbarLauncher app to show its flyout, then exits.
/// --pin mode:    Keeps the process alive with a dialog so the user can right-click
///                the taskbar icon and choose "Pin to taskbar", then close the dialog.
/// </summary>
static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--pin")
        {
            MessageBoxW(
                0,
                "This app is now running so you can pin it to the taskbar.\n\n" +
                "Right-click the taskbar icon for this app and choose \"Pin to taskbar\".\n\n" +
                "Click OK to close this window once you're done.",
                "Pin to Taskbar",
                0x00000040 /* MB_ICONINFORMATION */);
            return;
        }

        try
        {
            using var evt = EventWaitHandle.OpenExisting("TaskbarLauncher_ShowFlyout");
            evt.Set();
        }
        catch
        {
            // Main app isn't running — nothing to signal.
        }
    }
}
