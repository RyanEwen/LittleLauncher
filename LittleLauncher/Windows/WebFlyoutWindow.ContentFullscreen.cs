// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LittleLauncher.Windows;

public sealed partial class WebFlyoutWindow
{
    private Button? _enterFullscreenButton;
    private int _fullscreenAvailabilityVersion;

    // A page being fullscreen-enabled says nothing about whether it contains media.
    // Only offer a target that is visible in the viewport and supports the standard API.
    // Custom players own their own fullscreen transition and controls. Invoking the Fullscreen
    // API on one of their internal elements can hide or break those controls. Offer this action
    // only for a video with native controls; custom players keep their own fullscreen button.
    // Cross-origin frames and closed shadow roots intentionally remain the site's responsibility.
    private const string FullscreenVideoTargetScript = """
        (() => {
            if (!document.fullscreenEnabled || document.fullscreenElement) return null;
            const visibleArea = element => {
                const r = element.getBoundingClientRect();
                if (!element.checkVisibility({checkOpacity: true, checkVisibilityCSS: true})) return 0;
                return Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0)) *
                    Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
            };
            return [...document.querySelectorAll('video')]
                .filter(video => video.controls && typeof video.requestFullscreen === 'function' &&
                    visibleArea(video) > 0)
                .sort((a, b) => visibleArea(b) - visibleArea(a))[0] ?? null;
        })()
        """;

    /// <summary>Creates the media fullscreen action, hidden until a suitable video is detected.</summary>
    private Button BuildEnterFullscreenButton()
    {
        string label = (string)Application.Current.Resources["EnterFullscreenContent"];
        var button = BuildHeaderButton("\uE740", label, async (_, _) => await EnterContentFullscreenAsync());
        button.Visibility = Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        return _enterFullscreenButton = button;
    }

    /// <summary>
    /// Checks on navigation, tab changes and titlebar hover, without polling hidden pages.
    /// Only the latest answer from the active browser may update the shared header.
    /// </summary>
    private async Task RefreshFullscreenAvailabilityAsync()
    {
        if (_enterFullscreenButton is not { } button) return;
        int version = ++_fullscreenAvailabilityVersion;
        button.Visibility = Visibility.Collapsed;
        if (!_isOpen || _isFullScreen || _webView?.CoreWebView2 is not { } core) return;

        try
        {
            string result = await core.ExecuteScriptAsync($"Boolean({FullscreenVideoTargetScript})");
            if (version == _fullscreenAvailabilityVersion && IsActiveCore(core) && _isOpen && !_isFullScreen)
                button.Visibility = result == "true" ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Detecting fullscreen content failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>
    /// Rechecks the target at click time and requests fullscreen as a user gesture.
    /// The browser's fullscreen event remains the sole owner of host presentation changes.
    /// </summary>
    private async Task EnterContentFullscreenAsync()
    {
        if (!_isOpen || _isFullScreen || _webView?.CoreWebView2 is not { } core) return;

        try
        {
            // Fullscreen requires transient user activation. This flag represents the user's
            // explicit header click; ordinary availability probes never receive it.
            string parameters = JsonSerializer.Serialize(new
            {
                expression = $"(async () => {{ const video = {FullscreenVideoTargetScript}; if (!video) return false; await video.requestFullscreen(); return true; }})()",
                userGesture = true,
                awaitPromise = true,
                returnByValue = true,
            });
            string result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters);
            using var response = JsonDocument.Parse(result);
            bool succeeded = response.RootElement.TryGetProperty("result", out var evaluation)
                && evaluation.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True;
            if (!succeeded && IsActiveCore(core) && _isOpen)
                ShowNotice((string)Application.Current.Resources["FullscreenContentUnavailable"]);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Entering content fullscreen failed for launcher {Name}", _launcher.Name);
            if (IsActiveCore(core) && _isOpen)
                ShowNotice((string)Application.Current.Resources["FullscreenContentUnavailable"]);
        }

        await RefreshFullscreenAvailabilityAsync();
    }
}
