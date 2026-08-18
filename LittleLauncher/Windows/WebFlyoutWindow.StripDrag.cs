// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace LittleLauncher.Windows;

/// <summary>
/// Starting a drag from a button — for the bookmark bar and the tab strip, which both need it.
/// </summary>
/// <remarks>
/// <para><b><c>CanDrag = true</c> on a <see cref="Button"/> does nothing, and this is why the
/// bookmark bar's drag never worked.</b> A Button captures the pointer on press for its own
/// press/click handling, so the gesture WinUI would otherwise recognise as the start of a drag never
/// reaches the drag machinery — the flag is set, the handlers are wired, and nothing ever
/// fires. It works on list items because <c>ListViewBase</c> runs its own drag logic above the
/// container; a bare Button has nothing doing that for it.</para>
/// <para>So the drag is started by hand: watch the pointer while it is down, and once it has moved
/// past a threshold call <see cref="Microsoft.UI.Xaml.UIElement.StartDragAsync"/>, which raises
/// <c>DragStarting</c> and runs the ordinary drag-and-drop from there. The threshold is what keeps
/// Click intact — a press that does not travel is still a click, so a bookmark still opens and a tab
/// still switches.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>How far the pointer must travel with the button down before it is a drag.</summary>
    /// <remarks>
    /// Roughly the system's own drag threshold. Too small and a click that shifts by a pixel starts
    /// dragging instead of opening the page; too large and the gesture feels stuck.
    /// </remarks>
    private const double DragThresholdDips = 6;

    /// <summary>
    /// Makes a strip button draggable, keeping its click.
    /// </summary>
    /// <remarks>
    /// The handlers are added with <c>AddHandler(..., handledEventsToo: true)</c>, because Button
    /// marks its own pointer events handled — without that flag, none of these ever run.
    /// </remarks>
    private static void WireStripDragSource(Button button)
    {
        bool tracking = false;
        global::Windows.Foundation.Point start = default;

        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            // Left button only: middle-click closes a tab and opens a bookmark behind, and neither
            // should arm a drag.
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;

            tracking = true;
            start = e.GetCurrentPoint(button).Position;
        }), handledEventsToo: true);

        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (!tracking) return;

            var point = e.GetCurrentPoint(button);
            if (!point.Properties.IsLeftButtonPressed)
            {
                tracking = false;
                return;
            }

            if (Math.Abs(point.Position.X - start.X) < DragThresholdDips &&
                Math.Abs(point.Position.Y - start.Y) < DragThresholdDips)
                return;

            // Armed once per press: StartDragAsync runs the whole gesture, and a second call while
            // one is in flight throws.
            tracking = false;

            try { _ = button.StartDragAsync(e.GetCurrentPoint(button)); }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Starting a strip drag failed");
            }
        }), handledEventsToo: true);

        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            tracking = false;
        }), handledEventsToo: true);

        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        {
            tracking = false;
        }), handledEventsToo: true);
    }
}
