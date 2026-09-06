// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Windows;

/// <summary>
/// The position picker that drops out of the header's maximize button on hover: the places the
/// window can be sent, drawn as spots on a little screen rather than listed as words.
/// </summary>
/// <remarks>
/// <para>Modelled on the snap layouts Windows 11 hangs off a maximize button, and put there for the
/// same reason: "this needs to be somewhere else" is a thought had while looking at the window, and
/// the button that already changes its shape is where the hand already is.</para>
/// <para><b>It moves the window and nothing else.</b> Nothing here is written to the launcher — this
/// is a gesture, the same kind of thing dragging the header is, and it is over when the window
/// lands. It shares its nine <em>presets</em> with the "Opens at" setting, because they are the
/// obvious places to put a window and there is no reason to invent a second set of them, but it does
/// not touch that setting and no cell is drawn as "current". Where the launcher opens stays a
/// question answered in the menu and in launcher settings.</para>
/// <para><b>It is the nine spots and nothing else.</b> The other two anchors are not places: "near
/// its tray icon" and "where you last dragged it" are rules about <em>opening</em>, and as moves
/// they come out as a corner the grid already offers and a no-op respectively. Each had a row below
/// a divider, and both were dead weight — the second doubly so, being disabled for any launcher not
/// already set to that anchor, since no other one keeps a remembered position.</para>
/// <para>The one thing that does outlive the click is the position itself, and only for a launcher
/// already set to open where it was last put: <see cref="RememberFlyoutPosition"/> follows this move
/// exactly as it follows a drag, and writes nothing under any other anchor.</para>
/// <para>The arithmetic goes through <see cref="CalculatePlacement"/> with an anchor override rather
/// than a second copy of it, so a corner here is the same corner an open would produce — same gap,
/// same clamping. The size is the window's current one, though: a move that also resized would not
/// be a move.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>How long the pointer rests on the maximize button before the picker drops.</summary>
    /// <remarks>
    /// Long enough that crossing the button on the way to Close never opens it, short enough that
    /// someone who paused there is not left waiting. Windows' own snap layouts sit in this range.
    /// </remarks>
    private const int PickerHoverDelayMs = 450;

    /// <summary>How often the open picker re-asks where the pointer actually is.</summary>
    private const int PickerWatchIntervalMs = 200;

    /// <summary>Consecutive misses before it closes — the grace period, in ticks of the above.</summary>
    /// <remarks>
    /// Two, so the pointer has ~400ms to cross the gap between the button and the picker. One would
    /// close the picker on the way to it, which is the bug this watch replaced.
    /// </remarks>
    private const int PickerWatchMisses = 2;

    // The little screen each cell draws, and the window drawn on it. Sized so the 3x3 grid comes
    // out narrower than the smallest flyout this could hang off — the picker is allowed to overflow
    // its host window, but it should not read as a second window in its own right.
    private const double ThumbWidth = 54;
    private const double ThumbHeight = 34;
    private const double ThumbPadding = 3;
    private const double BlockWidth = 20;
    private const double BlockHeight = 12;

    /// <summary>The eleven anchors, with the labels the picker and the "Opens at" menu share.</summary>
    /// <remarks>
    /// One list, because two would drift: these are the same names for the same places, and a corner
    /// renamed in one and not the other is a mismatch nothing would catch. The list is all the two
    /// surfaces share — the menu writes <see cref="Launcher.WebAnchor"/> with them and offers all
    /// eleven; the picker only moves the window, and offers the nine that are places. See the class
    /// remarks for why the other two are not on it.
    /// </remarks>
    private static readonly (string Label, int Value)[] AnchorChoices =
    [
        ("Near its tray icon", WebAnchors.Tray),
        ("Where you last dragged it", WebAnchors.LastPosition),
        ("Top left", WebAnchors.TopLeft),
        ("Top centre", WebAnchors.TopCenter),
        ("Top right", WebAnchors.TopRight),
        ("Left", WebAnchors.Left),
        ("Centre", WebAnchors.Center),
        ("Right", WebAnchors.Right),
        ("Bottom left", WebAnchors.BottomLeft),
        ("Bottom centre", WebAnchors.BottomCenter),
        ("Bottom right", WebAnchors.BottomRight),
    ];

    private Flyout? _positionPicker;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pickerOpenTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pickerWatchTimer;

    /// <summary>True while the pointer is inside the picker's own content.</summary>
    /// <remarks>
    /// The picker is a popup with a tree of its own, so its content's pointer events are the real
    /// thing — it is only the <em>button's</em> that cannot be trusted once the popup is up.
    /// </remarks>
    private bool _pointerInPicker;

    /// <summary>Consecutive watch ticks with the pointer on neither the button nor the picker.</summary>
    private int _pickerMisses;

    // ── Hover plumbing ──────────────────────────────────────────────

    /// <summary>Hangs the picker off the maximize button. Called once, as the header is built.</summary>
    private void WirePositionPicker()
    {
        _maximizeButton.PointerEntered += (_, _) => SchedulePickerOpen();

        // Only ever cancels a picker that has not appeared yet. Closing an open one from here is
        // what made it flicker — see StartPickerWatch.
        _maximizeButton.PointerExited += (_, _) => _pickerOpenTimer?.Stop();

        // A click on the button is a maximize, not a move. Taking the picker down first keeps it
        // from being left hanging beside a window that just changed size underneath it.
        _maximizeButton.Click += (_, _) => HidePositionPicker();
    }

    private void SchedulePickerOpen()
    {
        if (_positionPicker?.IsOpen == true) return;

        _pickerOpenTimer ??= DispatcherQueue.CreateTimer();
        _pickerOpenTimer.Stop();
        _pickerOpenTimer.Interval = TimeSpan.FromMilliseconds(PickerHoverDelayMs);
        _pickerOpenTimer.IsRepeating = false;
        _pickerOpenTimer.Tick -= PickerOpenTimer_Tick;
        _pickerOpenTimer.Tick += PickerOpenTimer_Tick;
        _pickerOpenTimer.Start();
    }

    private void PickerOpenTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
        ShowPositionPicker();

    /// <summary>
    /// Watches where the pointer really is, and closes the picker once it is on neither the button
    /// nor the picker itself.
    /// </summary>
    /// <remarks>
    /// <para><b>The button's <c>PointerExited</c> cannot be used for this, and that is the whole
    /// reason this poll exists.</b> Opening the picker puts WinUI's light-dismiss layer over the
    /// window, so the button reports the pointer as having left <em>immediately</em> — while it is
    /// still sitting on the button. An exit-driven close therefore closed the picker ~300ms after
    /// every open, the pointer re-entered the button, and the picker opened again: measured as a
    /// clean open/close cycle about once a second, which reads as a picker that refuses to stay up.
    /// </para>
    /// <para>A longer delay does not fix it, because nothing in that cycle is racing — the exit is
    /// simply wrong. So the question is asked of the cursor instead, which cannot be: the button by
    /// its screen rectangle, the picker by its own content's pointer events, which are real because
    /// the popup has a tree of its own.</para>
    /// <para>It runs only while the picker is up, at 200ms — cheap enough to be invisible, and
    /// nothing depends on it being prompt.</para>
    /// </remarks>
    private void StartPickerWatch()
    {
        _pickerMisses = 0;

        _pickerWatchTimer ??= DispatcherQueue.CreateTimer();
        _pickerWatchTimer.Stop();
        _pickerWatchTimer.Interval = TimeSpan.FromMilliseconds(PickerWatchIntervalMs);
        _pickerWatchTimer.IsRepeating = true;
        _pickerWatchTimer.Tick -= PickerWatchTimer_Tick;
        _pickerWatchTimer.Tick += PickerWatchTimer_Tick;
        _pickerWatchTimer.Start();
    }

    private void PickerWatchTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_positionPicker?.IsOpen != true)
        {
            sender.Stop();
            return;
        }

        if (_pointerInPicker || IsPointerOverMaximizeButton())
        {
            _pickerMisses = 0;
            return;
        }

        if (++_pickerMisses >= PickerWatchMisses) HidePositionPicker();
    }

    /// <summary>Hit-tests the cursor against the maximize button, in screen pixels.</summary>
    /// <remarks>
    /// The window is borderless — no non-client area at all — so its window rect and its client
    /// origin are the same point, and an element's offset within the XAML root scales straight onto
    /// it. That is the assumption the resize grips already work from.
    /// </remarks>
    private bool IsPointerOverMaximizeButton()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return false;
        if (_maximizeButton.XamlRoot == null || _maximizeButton.ActualWidth <= 0) return false;
        if (!GetWindowRect(_hwnd, out var window) || !GetCursorPos(out var cursor)) return false;

        var origin = _maximizeButton
            .TransformToVisual(null)
            .TransformPoint(new global::Windows.Foundation.Point(0, 0));

        double scale = GetScale();
        int left = window.Left + (int)Math.Round(origin.X * scale);
        int top = window.Top + (int)Math.Round(origin.Y * scale);
        int right = left + (int)Math.Round(_maximizeButton.ActualWidth * scale);
        int bottom = top + (int)Math.Round(_maximizeButton.ActualHeight * scale);

        return cursor.X >= left && cursor.X < right && cursor.Y >= top && cursor.Y < bottom;
    }

    /// <summary>Takes the picker down now, wherever it had got to in its hover dance.</summary>
    private void HidePositionPicker()
    {
        _pickerOpenTimer?.Stop();
        _pickerWatchTimer?.Stop();
        _positionPicker?.Hide();
    }

    // ── The picker ──────────────────────────────────────────────────

    /// <summary>Drops the picker under the maximize button.</summary>
    /// <remarks>
    /// <para>Built fresh each time, like the More menu — a few objects, and it keeps the question of
    /// its going stale from arising at all.</para>
    /// <para>The two traps every popup in this window hits apply here too —
    /// <c>ShouldConstrainToRootBounds = false</c>, because a flyout 400px wide with 34px of chrome
    /// clips anything hung inside it, and <c>_isMenuOpen</c>, because an unconstrained popup
    /// deactivates the window that owns it and the dismissal that follows would take the flyout and
    /// the picker down together. <see cref="FlyoutShowMode.Transient"/> additionally asks WinUI not
    /// to move focus into it, which is what stops a hover stealing the page's caret; the pin stays,
    /// because "asks not to" is not the same as "cannot".</para>
    /// <para><c>_isMenuOpen</c> is set <em>before</em> <c>ShowAt</c>, not from <c>Opened</c>, so no
    /// ordering between the two events has to be assumed.</para>
    /// <para>The guards are the states where the window's geometry is already spoken for: nothing
    /// here should offer to move a window the page, the pointer or the taskbar is holding.</para>
    /// </remarks>
    private void ShowPositionPicker()
    {
        _pickerOpenTimer?.Stop();
        if (_positionPicker?.IsOpen == true) return;
        if (!_isOpen || _isFullScreen || _isMovingWindow || _isResizing || IsMinimized) return;
        if (_maximizeButton.XamlRoot == null) return;

        var picker = _positionPicker = new Flyout
        {
            Content = BuildPickerContent(),
            Placement = FlyoutPlacementMode.Bottom,
            ShouldConstrainToRootBounds = false,
            ShowMode = FlyoutShowMode.Transient,
            FlyoutPresenterStyle = BuildPresenterStyle(),
        };

        picker.Opened += (_, _) => StartPickerWatch();
        picker.Closed += (_, _) =>
        {
            _isMenuOpen = false;
            _pointerInPicker = false;
            _pickerWatchTimer?.Stop();
            if (ReferenceEquals(_positionPicker, picker)) _positionPicker = null;
        };

        // The pin goes on before the popup exists, so a show that never happens would leave the
        // flyout unable to dismiss for the rest of its life — Closed is the only thing that lifts it
        // and it would never fire.
        _isMenuOpen = true;
        try
        {
            picker.ShowAt(_maximizeButton);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Showing the position picker failed");
            _isMenuOpen = false;
            _positionPicker = null;
        }
    }

    /// <summary>
    /// Moves the presenter's padding onto the content, so the content covers the whole picker.
    /// </summary>
    /// <remarks>
    /// The presenter's own padding is a ring the content does not reach, and the pointer resting in
    /// it is the pointer resting on nothing this class can see — which the watch reads as having
    /// left. Given to the content instead, there is no such ring. Based on the shipped style so the
    /// presenter keeps its background, border and shadow, and read with <c>TryGetValue</c> rather
    /// than the indexer for the reason recorded on the close button's brush.
    /// </remarks>
    private static Style BuildPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));

        if (Application.Current.Resources.TryGetValue("DefaultFlyoutPresenterStyle", out object found)
            && found is Style shipped)
        {
            style.BasedOn = shipped;
        }

        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        return style;
    }

    private FrameworkElement BuildPickerContent()
    {
        var stack = new StackPanel { Spacing = 6 };

        // Every pixel of the picker, not just its cells. A null Background is not hit-testable and a
        // transparent one is — the same rule the resize grips follow — and without it the gaps
        // between the cells, the caption and the padding are all holes the pointer falls through,
        // which the watch reads as the pointer having left and closes the picker out from under it.
        var root = new Border
        {
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = stack,
        };

        // The half of the answer the button cannot give. These are real: the popup has a tree of
        // its own, so the pointer entering and leaving it is reported properly.
        root.PointerEntered += (_, _) => _pointerInPicker = true;
        root.PointerExited += (_, _) => _pointerInPicker = false;

        // Named, because a grid of little screens says where but not what happens. "Move" is the
        // whole promise: nothing here changes where the launcher opens.
        stack.Children.Add(new TextBlock
        {
            Text = "Move to",
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(2, 0, 0, 0),
        });

        var grid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
        for (int i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // TopLeft..BottomRight are consecutive and run left to right, top to bottom — the order the
        // grid lays them out in, so the cells need no table of their own.
        for (int index = 0; index < 9; index++)
        {
            int anchor = WebAnchors.TopLeft + index;
            var cell = BuildPickerButton(anchor, BuildAnchorThumbnail(anchor, out var block), block);
            Grid.SetRow(cell, index / 3);
            Grid.SetColumn(cell, index % 3);
            grid.Children.Add(cell);
        }

        stack.Children.Add(grid);
        return root;
    }

    /// <summary>One cell of the grid: a thumbnail, and the move it commits.</summary>
    /// <remarks>
    /// The pointer lights the window in the thumbnail, the way a snap layout lights the zone it
    /// would put you in — the button's own hover fill says "clickable", and the accent block says
    /// which of the nine places that click means.
    /// </remarks>
    private Button BuildPickerButton(int anchor, UIElement thumbnail, Border block)
    {
        var button = new Button
        {
            Content = thumbnail,
            Padding = new Thickness(3),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(5),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };

        button.PointerEntered += (_, _) =>
            block.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        button.PointerExited += (_, _) =>
            block.Background = (Brush)Application.Current.Resources["ControlStrongFillColorDefaultBrush"];

        string label = AnchorLabel(anchor);
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => MoveToPreset(anchor);
        return button;
    }

    private static string AnchorLabel(int anchor)
    {
        foreach (var (label, value) in AnchorChoices)
        {
            if (value == anchor) return label;
        }

        return "";
    }

    // ── Thumbnails ──────────────────────────────────────────────────

    /// <summary>The little screen every thumbnail is drawn on.</summary>
    private static Border BuildScreen(UIElement child) => new()
    {
        Width = ThumbWidth,
        Height = ThumbHeight,
        CornerRadius = new CornerRadius(3),
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
        Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
        Padding = new Thickness(ThumbPadding),
        Child = child,
    };

    /// <summary>The window drawn on the screen. Handed back so the pointer can light it.</summary>
    private static Border BuildBlock() => new()
    {
        Width = BlockWidth,
        Height = BlockHeight,
        CornerRadius = new CornerRadius(2),
        Background = (Brush)Application.Current.Resources["ControlStrongFillColorDefaultBrush"],
    };

    /// <summary>One of the nine spots, drawn where the placement code would put it.</summary>
    /// <remarks>
    /// The alignments are the same three-way test <see cref="CalculatePlacement"/> runs against the
    /// real work area, so the picture cannot disagree with the window: left, right or centred
    /// across; top, bottom or centred down. The screen's padding stands in for the placement's gap.
    /// </remarks>
    private static Border BuildAnchorThumbnail(int anchor, out Border block)
    {
        block = BuildBlock();
        block.HorizontalAlignment =
            WebAnchors.IsLeft(anchor) ? HorizontalAlignment.Left :
            WebAnchors.IsRight(anchor) ? HorizontalAlignment.Right :
            HorizontalAlignment.Center;
        block.VerticalAlignment =
            WebAnchors.IsTop(anchor) ? VerticalAlignment.Top :
            WebAnchors.IsBottom(anchor) ? VerticalAlignment.Bottom :
            VerticalAlignment.Center;

        return BuildScreen(block);
    }

    // ── The move ────────────────────────────────────────────────────

    /// <summary>Sends the window to one of the nine spots, and changes nothing else.</summary>
    /// <remarks>
    /// <para>Through <see cref="CalculatePlacement"/> with the preset as an override, so a corner
    /// here is the corner an open would produce — same gap, same clamping — without the launcher
    /// having to be set to it. The size handed in is the window's current one: a move that also
    /// resized would not be a move.</para>
    /// <para>The origin it is measured from is the window's <em>own centre</em>, which is what picks
    /// the monitor: "top left" means the corner of the screen you are looking at, not the corner of
    /// whichever screen the tray icon happens to be on. That differs from an open, which has a tray
    /// click to measure from and should follow it.</para>
    /// <para>No slide. The window is already on screen and being looked at, so animating it across
    /// would be motion for its own sake; the flyout's slide exists to bring a window in from an edge
    /// it is not yet at.</para>
    /// </remarks>
    private void MoveToPreset(int anchor)
    {
        HidePositionPicker();

        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        // The page owns the geometry while it is fullscreen and gets it back on exit, so a move from
        // here would be undone — and would leave _preFullScreenRect pointing somewhere else.
        if (_isFullScreen) return;

        // A slide still in flight would otherwise keep writing its own geometry over this one.
        _animationVersion++;
        _isShowing = false;
        _isHiding = false;

        // Maximized fills the work area, so it has no size to carry to a corner. The state is
        // dropped rather than restored, because the placement below supplies the geometry itself —
        // and the size that travels is the one it grew from.
        RECT rect;
        if (_isMaximized)
        {
            rect = _preMaximizeRect;
            ExitMaximized(restoreGeometry: false);
        }
        else if (!GetWindowRect(_hwnd, out rect))
        {
            return;
        }

        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);

        var placement = CalculatePlacement(
            rect.Left + (width / 2), rect.Top + (height / 2), anchor, (width, height));

        MoveResize(placement.Left, placement.Top, placement.Width, placement.Height);
        _lastEntranceEdge = placement.Edge;

        // A move is a move however it was made: a launcher set to open where it was last put should
        // follow this one exactly as it follows a drag of the header. Self-gating — it writes
        // nothing under any of the other anchors, so this is not the picker touching a setting.
        RememberFlyoutPosition();
    }
}
