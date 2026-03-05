// Copyright � 2024-2026 The SelfHostedHelper Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using static SelfHostedHelper.Classes.NativeMethods;

namespace SelfHostedHelper.Classes;

public static class WindowBlurHelper
{
    /// <summary>
    /// Enables acrylic blur effect on the specified window
    /// </summary>
    /// <param name="window">The window to apply blur to</param>
    /// <param name="blurOpacity">Opacity of the blur (0-255)</param>
    /// <param name="blurBackgroundColor">Background color in BGR format (default: 0x000000)</param>
    public static void EnableBlur(Window window, uint blurOpacity = 175, uint blurBackgroundColor = 0x000000)
    {
        blurOpacity = Math.Clamp(blurOpacity, 0, 255);

        var windowHelper = new WindowInteropHelper(window);

        var currentTheme = ApplicationThemeManager.GetAppTheme();
        if (currentTheme == ApplicationTheme.Light)
        {
            blurBackgroundColor = 0xFFFFFF; // use light background for light theme
        }

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = (blurOpacity << 24) | (blurBackgroundColor & 0xFFFFFF)
        };

        var accentStructSize = Marshal.SizeOf(accent);
        var accentPtr = Marshal.AllocHGlobal(accentStructSize);
        Marshal.StructureToPtr(accent, accentPtr, false);

        var data = new WindowCompositionAttributeData
        {
            Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
            SizeOfData = accentStructSize,
            Data = accentPtr
        };

        SetWindowCompositionAttribute(windowHelper.Handle, ref data);

        Marshal.FreeHGlobal(accentPtr);
    }
}
