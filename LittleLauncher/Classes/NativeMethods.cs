// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using System.Text;

namespace LittleLauncher.Classes;

/// <summary>
/// Centralized class for all P/Invoke declarations and unmanaged code imports.
/// </summary>
public static partial class NativeMethods
{
    #region Constants

    // Window Styles
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_OVERLAPPEDWINDOW = 0x00CF0000; // WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// Window sits in the always-on-top band. Read rather than written: the band is entered
    /// through <c>OverlappedPresenter.IsAlwaysOnTop</c>, and this is how a window asks whether it
    /// is in it before re-asserting its place within it.
    /// </summary>
    internal const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>Window does not take foreground focus when clicked — used by floating toolbars.</summary>
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// Window composites through a per-window alpha value. Required before
    /// <see cref="SetLayeredWindowAttributes"/> will do anything.
    /// </summary>
    internal const int WS_EX_LAYERED = 0x00080000;

    /// <summary>Tells <see cref="SetLayeredWindowAttributes"/> to use the alpha argument.</summary>
    internal const uint LWA_ALPHA = 0x00000002;

    // Window Event Hook Constants
    internal const uint EVENT_OBJECT_DESTROY = 0x8001;
    internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const int OBJID_WINDOW = 0;

    // SetWindowPos Flags
    /// <summary>
    /// HWND_TOPMOST. Must be pointer-sized: passing it through an <c>int</c> parameter cannot
    /// sign-extend into a 64-bit handle slot, and the call fails with an invalid handle.
    /// </summary>
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_ASYNCWINDOWPOS = 0x4000;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    // Monitor Flags
    internal const int MONITOR_DEFAULTTONEAREST = 2;
    internal const int S_OK = 0;

    #endregion

    #region Enums

    public enum MonitorDpiType
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT
    }

    #endregion

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;  // positive = bottom-up, negative = top-down
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0;
        public uint dsBitfields1;
        public uint dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    #endregion

    #region Delegates

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    internal delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    internal delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    #endregion

    #region user32.dll

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>Index for a window's owner. Setting it makes a window always render above its owner.</summary>
    internal const int GWLP_HWNDPARENT = -8;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hMonitor);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    /// <summary>The monitor a window is on — used to size it to that screen, not the primary.</summary>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Sets a layered window's per-window alpha. The window must already have
    /// <see cref="WS_EX_LAYERED"/>, and <paramref name="dwFlags"/> must include
    /// <see cref="LWA_ALPHA"/> for <paramref name="bAlpha"/> to be honoured.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    // Virtual screen origin: the top-left of the bounding box around every monitor.
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;

    // The size of the box a second click must land in to count as a double-click, in pixels.
    // Paired with GetDoubleClickTime above: both are user settings, so anything recognising a
    // double-click by hand has to read them rather than pick its own numbers.
    internal const int SM_CXDOUBLECLK = 36;
    internal const int SM_CYDOUBLECLK = 37;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    /// <summary>
    /// True when <paramref name="hWnd"/> is a descendant of <paramref name="hWndParent"/>.
    /// </summary>
    /// <remarks>
    /// A hosted browser runs its own child HWNDs, so focus landing inside one deactivates the
    /// XAML window even though the user has not left it. Panels that dismiss on focus loss test
    /// the foreground window against this before hiding.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    /// <summary>
    /// Walks a window relationship — <see cref="GW_OWNER"/> is the one used here.
    /// </summary>
    /// <remarks>
    /// **An owned window is not a child**, which is exactly the gap <see cref="IsChild"/> leaves.
    /// A file picker, the Windows Security passkey prompt and a print dialog are all top-level
    /// windows *owned by* the window that raised them — and they belong to whichever process
    /// raised them, which for anything a hosted browser opens is not ours either. Panels that
    /// dismiss on focus loss have to walk this chain, or opening a file picker from a page
    /// dismisses the panel the picker belongs to.
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>The owner of a top-level window. See <see cref="GetWindow"/>.</summary>
    internal const uint GW_OWNER = 4;

    internal const int SW_HIDE = 0;
    internal const int SW_MAXIMIZE = 3;
    internal const int SW_MINIMIZE = 6;
    internal const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    internal const uint WM_SETICON = 0x0080;
    internal const IntPtr ICON_SMALL = 0;
    internal const IntPtr ICON_BIG = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x0010;

    #endregion

    #region shell32.dll — system tray (Shell_NotifyIcon)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON    = 0x00000002;
    internal const uint NIF_TIP     = 0x00000004;
    internal const uint NIF_GUID    = 0x00000020;
    internal const uint NIM_ADD     = 0x00000000;
    internal const uint NIM_MODIFY  = 0x00000001;
    internal const uint NIM_DELETE  = 0x00000002;

    // Tray callback notification events (lParam values sent to uCallbackMessage handler)
    internal const int WM_LBUTTONUP    = 0x0202;
    internal const int WM_RBUTTONUP    = 0x0205;
    internal const int WM_CONTEXTMENU  = 0x007B;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    #endregion

    #region gdi32.dll

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll", EntryPoint = "GetObject")]
    internal static extern int GetObjectDibSection(IntPtr hgdiobj, int cbBuffer, out DIBSECTION lpvObject);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);
    internal const uint SRCCOPY = 0x00CC0020;

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    internal const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // No color table needed for 32bpp
    }

    #endregion

    #region kernel32.dll

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegisterApplicationRestart(string pwzCommandline, int dwFlags);

    #endregion

    #region dwmapi.dll

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_ROUND = 2;

    /// <summary>Square corners — a window filling the screen must not round away its own pixels.</summary>
    internal const int DWMWCP_DONOTROUND = 1;

    /// <summary>Windows 11 window border colour, as a COLORREF (0x00BBGGRR).</summary>
    internal const int DWMWA_BORDER_COLOR = 34;

    /// <summary>Sentinel restoring the system default border colour.</summary>
    internal const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;

    #endregion

    #region shcore.dll

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    #endregion

    #region comctl32.dll

    [DllImport("comctl32.dll")]
    internal static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    #endregion

    #region shlwapi.dll

    internal const int ASSOCF_NONE = 0;
    internal const int ASSOCSTR_EXECUTABLE = 2;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int AssocQueryString(
        int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref int pcchOut);

    #endregion

    #region IPropertyStore (COM) — per-window AppUserModelID

    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // PKEY_AppUserModel_ID = { {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5 }
    internal static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    // PKEY_AppUserModel_RelaunchIconResource = { {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 3 }
    internal static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 3
    };

    // PKEY_AppUserModel_RelaunchCommand = { {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 2 }
    internal static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchCommand = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 2
    };

    // PKEY_AppUserModel_RelaunchDisplayNameResource = { {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 4 }
    internal static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 4
    };

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int Commit();
    }

    /// <summary>
    /// Adds OLE support to the calling thread's apartment. Required for drag and drop.
    /// </summary>
    /// <remarks>
    /// WinUI 3 initialises its UI thread with <c>CoInitializeEx</c> only. OLE drag and drop needs
    /// the extra initialisation <c>OleInitialize</c> performs — without it a hosted WebView2
    /// silently supports no dragging at all: not HTML5 drags inside the page, and not files
    /// dragged in from outside. The call is safe to repeat; on an already-initialised STA thread
    /// it returns <c>S_FALSE</c>.
    /// </remarks>
    [LibraryImport("ole32.dll")]
    internal static partial int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(IntPtr pvar);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    internal const int GPS_READWRITE = 2;

    /// <summary>
    /// Sets the AppUserModel.ID property on a specific HWND so the taskbar
    /// treats it as its own group, independent of the process exe.
    /// </summary>
    internal static void SetWindowAppUserModelId(IntPtr hwnd, string appId)
    {
        SetWindowPropertyStoreString(hwnd, PKEY_AppUserModel_ID, appId);
    }

    /// <summary>
    /// Sets the Relaunch properties (Icon, Command, DisplayName) on a HWND
    /// so the taskbar/pinned entry uses the specified icon and relaunch command.
    /// </summary>
    internal static void SetWindowRelaunchProperties(IntPtr hwnd, string iconResource, string command, string displayName)
    {
        var IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        int hr = SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out var store);
        if (hr != S_OK || store == null) return;

        try
        {
            SetPropertyStoreString(store, PKEY_AppUserModel_RelaunchIconResource, iconResource);
            SetPropertyStoreString(store, PKEY_AppUserModel_RelaunchCommand, command);
            SetPropertyStoreString(store, PKEY_AppUserModel_RelaunchDisplayNameResource, displayName);
            store.Commit();
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Sets a VT_LPWSTR string value on an IPropertyStore. Does NOT call Commit().
    /// </summary>
    internal static void SetPropertyStoreString(IPropertyStore store, PROPERTYKEY key, string value)
    {
        const int PROPVARIANT_SIZE = 24;
        IntPtr pv = Marshal.AllocCoTaskMem(PROPVARIANT_SIZE);
        try
        {
            for (int i = 0; i < PROPVARIANT_SIZE; i++)
                Marshal.WriteByte(pv, i, 0);

            Marshal.WriteInt16(pv, 0, 31); // VT_LPWSTR
            Marshal.WriteIntPtr(pv, 8, Marshal.StringToCoTaskMemUni(value));

            store.SetValue(ref key, pv);
        }
        finally
        {
            PropVariantClear(pv);
            Marshal.FreeCoTaskMem(pv);
        }
    }

    /// <summary>
    /// Sets a single VT_LPWSTR property on a window's IPropertyStore.
    /// </summary>
    private static void SetWindowPropertyStoreString(IntPtr hwnd, PROPERTYKEY key, string value)
    {
        var IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        int hr = SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out var store);
        if (hr != S_OK || store == null) return;

        try
        {
            SetPropertyStoreString(store, key, value);
            store.Commit();
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    #endregion

    #region Window messages — regular-window mode

    /// <summary>Restores a minimized window to its previous size and position.</summary>
    internal const int SW_RESTORE = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    internal const uint WM_SYSCOMMAND = 0x0112;
    internal const int SC_MINIMIZE = 0xF020;
    internal const int SC_CLOSE = 0xF060;

    #endregion

    #region ITaskbarList (COM)

    /// <summary>
    /// Tells the shell to add or drop a window's taskbar button.
    /// </summary>
    /// <remarks>
    /// <b>Not sufficient on its own, and not redundant either.</b> Measured on Windows 11:
    /// <c>AddTab</c> returns <c>S_OK</c> and does nothing at all for a <c>WS_EX_TOOLWINDOW</c>
    /// window, even with a matching AUMID stamped — the style is what decides eligibility. But for
    /// a window that has just *become* eligible, this is what makes the shell notice without the
    /// hide/show cycle the documentation reaches for, which this app cannot do (<c>SW_HIDE</c>
    /// makes WinUI drop the composition surfaces the off-screen park exists to protect). Dropping
    /// the call left a correctly-restyled window with no button.
    /// </remarks>
    [ComImport]
    [Guid("56FDF342-FD6D-11D0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
    }

    /// <summary>CLSID_TaskbarList.</summary>
    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    internal class TaskbarListClass { }

    private static ITaskbarList? _taskbarList;

    /// <summary>The shell's taskbar list, created once and kept. Null if it cannot be created.</summary>
    /// <remarks>
    /// Cached rather than created per call: this runs on every open and dismissal of a
    /// regular-window launcher, and <c>HrInit</c> is the expensive half. A null return is a normal
    /// outcome to handle, not an error to throw on.
    /// </remarks>
    internal static ITaskbarList? GetTaskbarList()
    {
        if (_taskbarList != null) return _taskbarList;

        try
        {
            var list = (ITaskbarList)new TaskbarListClass();
            if (list.HrInit() != 0) return null;
            return _taskbarList = list;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region Jump lists (COM) — the task list on a pinned taskbar button

    /// <summary>PKEY_Title = { {F29F85E0-4FF9-1068-AB91-08002B27B3D9}, 2 }</summary>
    /// <remarks>
    /// The label a jump list task shows. It is not optional: a shell link with no title falls
    /// back to its own path, so every task in the list reads "LittleLauncherFlyout.exe".
    /// </remarks>
    internal static readonly PROPERTYKEY PKEY_Title = new()
    {
        fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        pid = 2
    };

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint cObjects);
        [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    /// <summary>
    /// The collection a jump list is assembled into. Its first two slots are
    /// <see cref="IObjectArray"/>'s, because it derives from it — C# interfaces cannot express
    /// COM inheritance, so the base methods are repeated to keep the vtable order right.
    /// </summary>
    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IObjectCollection
    {
        [PreserveSig] int GetCount(out uint cObjects);
        [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int AddObject([MarshalAs(UnmanagedType.Interface)] object pvObject);
        [PreserveSig] int AddFromArray(IObjectArray poaSource);
        [PreserveSig] int RemoveObjectAt(uint index);
        [PreserveSig] int Clear();
    }

    /// <summary>
    /// Publishes the custom part of an AppUserModelID's jump list — the menu Windows shows when
    /// its taskbar button is right-clicked.
    /// </summary>
    /// <remarks>
    /// <para><b>The list belongs to an AUMID, not to a process</b>, which is what makes a list per
    /// launcher possible: <c>SetAppID</c> names whichever identity is being published for, and
    /// <see cref="Models.Launcher.PinAumid"/> already records one per pinned launcher.</para>
    /// <para>The sequence is fixed and unforgiving. <c>BeginList</c>, then the content, then
    /// <c>CommitList</c> — and nothing at all appears until the commit. <c>BeginList</c> also
    /// hands back the destinations the user has removed by hand, which must be read (and released)
    /// even though user tasks cannot be removed and so can never be among them.</para>
    /// </remarks>
    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICustomDestinationList
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig] int BeginList(out uint pcMinSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        [PreserveSig] int AppendKnownCategory(int category);
        [PreserveSig] int AddUserTasks(IObjectArray poa);
        [PreserveSig] int CommitList();
        [PreserveSig] int GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig] int AbortList();
    }

    /// <summary>
    /// A shell link, used here only as the carrier for one jump list task.
    /// </summary>
    /// <remarks>
    /// The getters take raw buffers rather than <see cref="StringBuilder"/>: nothing in this app
    /// calls them, and a wrong marshalling attribute on a method that is never invoked is a trap
    /// waiting for the first caller. Only the vtable order matters, and that is preserved.
    /// </remarks>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        [PreserveSig] int GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(IntPtr pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory(IntPtr pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments(IntPtr pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    [ClassInterface(ClassInterfaceType.None)]
    internal class DestinationListClass { }

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    [ClassInterface(ClassInterfaceType.None)]
    internal class EnumerableObjectCollectionClass { }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    internal class ShellLinkClass { }

    internal static readonly Guid IID_IObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");


    /// <summary>
    /// Names a jump list task, by setting System.Title on the shell link carrying it.
    /// </summary>
    /// <remarks>
    /// The link has to be handed in as <c>object</c>: the title lives on the same COM object's
    /// <see cref="IPropertyStore"/> face, and reaching it means a QueryInterface, which is what
    /// casting the runtime callable wrapper does.
    /// </remarks>
    internal static void SetShellLinkTitle(object shellLink, string title)
    {
        if (shellLink is not IPropertyStore store) return;

        SetPropertyStoreString(store, PKEY_Title, title);
        store.Commit();
    }

    #endregion
}
