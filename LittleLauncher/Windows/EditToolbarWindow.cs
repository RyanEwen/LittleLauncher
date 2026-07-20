using LittleLauncher.Classes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Windows;

/// <summary>
/// The edit-mode toolbar, floating in its own bar just above the flyout.
/// </summary>
/// <remarks>
/// <para>A separate window rather than a row inside the flyout. Keeping it out of
/// <c>ContentStack</c> means edit mode no longer changes the flyout's height at all, so the
/// toolbar cannot crowd the launcher's content or perturb the height arithmetic — the source
/// of several sizing bugs when it lived inside.</para>
/// <para>It is borderless, always-on-top and <c>WS_EX_NOACTIVATE</c>, so clicking a button
/// does not steal focus from the flyout, and <c>WS_EX_TOOLWINDOW</c> keeps it out of Alt-Tab.
/// It must be repositioned whenever the flyout moves or resizes.</para>
/// </remarks>
internal sealed class EditToolbarWindow : Window
{
    /// <summary>Gap between the bar and the flyout's top edge, in DIPs.</summary>
    private const int GapDips = 6;

    private readonly IntPtr _hwnd;
    private readonly FrameworkElement _content;

    public EditToolbarWindow(FrameworkElement content)
    {
        _content = content;
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        GetAppWindow().SetPresenter(presenter);

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Content = content;
        ThemeManager.ApplySavedTheme(this);

        // No focus theft, no Alt-Tab entry. Applied *before* Activate so the first show does
        // not pull foreground away from the flyout.
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        // A WinUI 3 window does not create its content island until it is activated — without
        // this the HWND is shown but renders nothing at all.
        Activate();
        ShowWindow(_hwnd, SW_HIDE);
    }

    private AppWindow GetAppWindow() =>
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

    /// <summary>
    /// Sizes the bar to its content and parks it centred above <paramref name="ownerHwnd"/>,
    /// clamped into the work area so it can never sit off-screen.
    /// </summary>
    public void PositionAbove(IntPtr ownerHwnd, int contentWidthDips, int contentHeightDips)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd) || !IsWindow(ownerHwnd))
            return;

        double scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        int width = (int)Math.Ceiling(contentWidthDips * scale);
        int height = (int)Math.Ceiling(contentHeightDips * scale);

        GetWindowRect(ownerHwnd, out var owner);
        int left = owner.Left + (((owner.Right - owner.Left) - width) / 2);
        int top = owner.Top - height - (int)(GapDips * scale);

        var centre = new POINT { X = owner.Left + ((owner.Right - owner.Left) / 2), Y = owner.Top };
        IntPtr monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(monitor, ref info);
        var work = info.rcWork;

        if (left < work.Left) left = work.Left;
        if (left + width > work.Right) left = work.Right - width;

        // If there is no room above (flyout anchored to the top of the screen), sit below it.
        if (top < work.Top)
            top = owner.Bottom + (int)(GapDips * scale);

        SetWindowPos(_hwnd, HWND_TOPMOST, left, top, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void HideBar()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            ShowWindow(_hwnd, SW_HIDE);
    }
}
