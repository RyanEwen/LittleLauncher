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
    /// Releases the mouse capture so a window can be handed to the system's own move loop.
    /// </summary>
    /// <remarks>
    /// Paired with <c>SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)</c>: that makes Windows drag a
    /// borderless window as though the pointer were on a title bar it does not have. The message
    /// is synchronous — it returns when the user lets go — so the resulting position can be read
    /// straight after the call.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    internal const uint WM_NCLBUTTONDOWN = 0x00A1;
    internal static readonly IntPtr HTCAPTION = new(2);

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

    internal const int SW_HIDE = 0;
    internal const int SW_MAXIMIZE = 3;
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
    private static void SetPropertyStoreString(IPropertyStore store, PROPERTYKEY key, string value)
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
}
