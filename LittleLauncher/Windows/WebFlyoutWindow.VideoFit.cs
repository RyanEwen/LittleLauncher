// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.Web.WebView2.Core;
using LittleLauncher.Models;
using System;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public sealed partial class WebFlyoutWindow
{
    private int _videoFitVersion;
    private bool _videoFitApplied;

    // YouTube fullscreen wraps the video in a player element. Read the intrinsic video size,
    // not that wrapper's size, which already matches the window and includes its letterboxing.
    private const string FullscreenVideoRatioScript = """
        (() => {
            const root = document.fullscreenElement;
            if (!root) return null;
            const videos = root.matches('video') ? [root] : [...root.querySelectorAll('video')];
            const visible = videos.filter(video => {
                const rect = video.getBoundingClientRect();
                return video.videoWidth > 0 && video.videoHeight > 0 && rect.width > 0 && rect.height > 0;
            });
            visible.sort((a, b) => {
                const ar = a.getBoundingClientRect(), br = b.getBoundingClientRect();
                return br.width * br.height - ar.width * ar.height;
            });
            return visible.length ? visible[0].videoWidth / visible[0].videoHeight : null;
        })()
        """;

    private async Task FitFullscreenToVideoAsync(CoreWebView2 core, int version)
    {
        bool StillWanted() => version == _videoFitVersion && _isOpen && _isFullScreen &&
            _fullScreenInWindow && !_isMaximized && !_isResizing &&
            _launcher.WebFitFullscreenToVideo && IsActiveCore(core) && core.ContainsFullScreenElement;

        try
        {
            // Fullscreen and media metadata can arrive on different turns. Retry briefly, then
            // leave non-video fullscreen alone. Never keep polling or fight a manual resize.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (!StillWanted()) return;
                string result = await core.ExecuteScriptAsync(FullscreenVideoRatioScript);
                if (!StillWanted()) return;

                if (JsonNode.Parse(result) is JsonValue value && value.TryGetValue<double>(out double ratio) &&
                    double.IsFinite(ratio) && ratio > 0)
                {
                    if (!GetWindowRect(_hwnd, out var current)) return;
                    var monitor = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (!GetMonitorInfo(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST), ref monitor)) return;
                    if (!TryFitVideoBounds(current, monitor.rcWork, ratio, GetScale(), _resizeAnchor, out var fitted)) return;

                    _animationVersion++;
                    _isShowing = false;
                    _videoFitApplied = SetWindowPos(_hwnd, IntPtr.Zero, fitted.Left, fitted.Top,
                        fitted.Right - fitted.Left, fitted.Bottom - fitted.Top, SWP_NOZORDER | SWP_NOACTIVATE);
                    return;
                }

                await Task.Delay(150);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Fitting fullscreen video failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>Fits the video viewport, keeping its width within usable window and screen limits.</summary>
    private static bool TryFitVideoBounds(RECT current, RECT workArea, double ratio, double scale, int anchor, out RECT fitted)
    {
        fitted = current;
        if (!double.IsFinite(ratio) || ratio <= 0 || !double.IsFinite(scale) || scale <= 0) return false;

        int inset = (int)Math.Ceiling(GripThickness * scale) * 2;
        int availableWidth = workArea.Right - workArea.Left;
        int availableHeight = workArea.Bottom - workArea.Top;
        double minimumWidth = Math.Max(Math.Ceiling(Launcher.MinWebFlyoutWidth * scale) - inset,
            (Math.Ceiling(Launcher.MinWebFlyoutHeight * scale) - inset) * ratio);
        double maximumWidth = Math.Min(availableWidth - inset, (availableHeight - inset) * ratio);
        if (minimumWidth > maximumWidth || maximumWidth <= 0) return false;
        double width = Math.Clamp(current.Right - current.Left - inset, minimumWidth, maximumWidth);
        double height = width / ratio;
        if (width <= 0 || height <= 0) return false;
        int outerWidth = (int)Math.Round(width) + inset;
        int outerHeight = (int)Math.Round(height) + inset;
        // Pathological ratios or tiny work areas cannot fit within usable window dimensions.
        // Leave the current size alone rather than inventing a ratio.
        if (outerWidth < Math.Ceiling(Launcher.MinWebFlyoutWidth * scale) ||
            outerHeight < Math.Ceiling(Launcher.MinWebFlyoutHeight * scale) ||
            outerWidth > availableWidth || outerHeight > availableHeight) return false;

        // Preserve the point this window was placed around, including temporary preset moves.
        // Work from its current rectangle so a dragged launcher never jumps back to a screen corner.
        int left = WebAnchors.IsLeft(anchor) ? current.Left : WebAnchors.IsRight(anchor) ? current.Right - outerWidth
            : current.Left + (int)Math.Round((current.Right - current.Left - outerWidth) / 2.0);
        int top = WebAnchors.IsTop(anchor) ? current.Top : WebAnchors.IsBottom(anchor) ? current.Bottom - outerHeight
            : current.Top + (int)Math.Round((current.Bottom - current.Top - outerHeight) / 2.0);
        left = Math.Clamp(left, workArea.Left, workArea.Right - outerWidth);
        top = Math.Clamp(top, workArea.Top, workArea.Bottom - outerHeight);
        fitted = new RECT { Left = left, Top = top, Right = left + outerWidth, Bottom = top + outerHeight };
        return true;
    }
}
