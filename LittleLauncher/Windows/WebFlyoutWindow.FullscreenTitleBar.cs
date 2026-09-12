// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Windows;

public sealed partial class WebFlyoutWindow
{
    private StackPanel? _normalHeaderParent;
    private DispatcherQueueTimer? _fullscreenHeaderTimer;
    private int _fullscreenHeaderMisses;

    /// <summary>
    /// Moves the existing titlebar into an overlay during fullscreen, preserving all
    /// controls and their handlers without reserving space above the browser.
    /// </summary>
    private void ConfigureFullscreenTitleBar()
    {
        _fullscreenHeaderTimer?.Stop();
        _fullscreenHeaderMisses = 0;

        if (_isFullScreen)
        {
            if (_header.Parent is StackPanel parent)
            {
                _normalHeaderParent = parent;
                parent.Children.Remove(_header);
                Grid.SetRow(_header, 1);
                _header.VerticalAlignment = VerticalAlignment.Top;
                // Fullscreen content must not show through the titlebar's controls.
                _header.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
                _root.Children.Add(_header);
            }

            _header.Visibility = Visibility.Collapsed;
            _fullscreenHeaderTimer ??= DispatcherQueue.CreateTimer();
            _fullscreenHeaderTimer.Interval = TimeSpan.FromMilliseconds(100);
            _fullscreenHeaderTimer.Tick -= FullscreenHeaderTimer_Tick;
            _fullscreenHeaderTimer.Tick += FullscreenHeaderTimer_Tick;
            if (_isOpen) _fullscreenHeaderTimer.Start();
        }
        else if (_normalHeaderParent is { } parent)
        {
            _root.Children.Remove(_header);
            _header.ClearValue(Grid.RowProperty);
            _header.ClearValue(FrameworkElement.VerticalAlignmentProperty);
            _header.ClearValue(Panel.BackgroundProperty);
            parent.Children.Insert(0, _header);
            _normalHeaderParent = null;
        }

        UpdateMaximizeButton();
    }

    /// <summary>
    /// Watches screen coordinates because the hosted browser consumes pointer events.
    /// A short exit grace period and open-menu guard keep the controls usable.
    /// </summary>
    private void FullscreenHeaderTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_isOpen || !_isFullScreen || _isHiding || IsMinimized)
        {
            _header.Visibility = Visibility.Collapsed;
            return;
        }

        if (!GetCursorPos(out var point) || !GetWindowRect(_hwnd, out var rect)) return;
        double scale = GetScale();
        bool shown = _header.Visibility == Visibility.Visible;
        int revealHeight = (int)Math.Ceiling((shown ? HeaderHeightDips : 6) * scale);
        bool hovered = point.X >= rect.Left && point.X < rect.Right
            && point.Y >= rect.Top && point.Y < rect.Top + revealHeight;

        if (hovered || (shown && (_isMovingWindow || _isMenuOpen || _isModalOpen || _positionPicker?.IsOpen == true)))
        {
            _fullscreenHeaderMisses = 0;
            _header.Visibility = Visibility.Visible;
        }
        else if (++_fullscreenHeaderMisses >= 3)
        {
            _header.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Asks the page to exit fullscreen so browser and host state change together.</summary>
    private async Task ExitPageFullscreenAsync()
    {
        try
        {
            if (_webView?.CoreWebView2 is { } core)
                await core.ExecuteScriptAsync("if (document.fullscreenElement) document.exitFullscreen();");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Exiting page fullscreen failed for launcher {Name}", _launcher.Name);
        }
    }
}
