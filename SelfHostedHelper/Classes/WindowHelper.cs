// Copyright © 2024-2026 The SelfHostedHelper Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Interop;
using static SelfHostedHelper.Classes.NativeMethods;

namespace SelfHostedHelper.Classes;

public static class WindowHelper
{
    public static void SetTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }

    public static void SetNoActivate(Window window)
    {
        window.SourceInitialized += (sender, e) =>
        {
            var helper = new WindowInteropHelper(window);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, GetWindowLong(helper.Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        };
    }
}