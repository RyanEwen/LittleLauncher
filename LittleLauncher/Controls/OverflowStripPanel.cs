// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;

namespace LittleLauncher.Controls;

/// <summary>
/// A left-to-right row that shows as many of its children as fit and says how many it could not.
/// </summary>
/// <remarks>
/// <para>The bookmark bar's shape. A bar that simply scrolls is the wrong answer for a strip 34px
/// tall with no visible scrollbar: the bookmarks past the edge are not merely off screen, they are
/// undiscoverable, since nothing on the bar says there are more. Every browser answers this the
/// same way, with a chevron at the end that drops the rest into a menu, and that needs one thing
/// the panel is the only place to know: <b>where the row ran out</b>.</para>
/// <para>Hidden children are arranged to a zero rect rather than collapsed. Collapsing changes
/// their desired size, which changes what fits, which changes what is collapsed; arranging them
/// away leaves the measure pass reading the same natural widths every time, so the answer is
/// stable.</para>
/// <para><see cref="VisibleCountChanged"/> fires from the layout pass, so a handler must not force
/// another one synchronously. The bar's handler only sets a button's visibility, which WinUI
/// coalesces into the next pass.</para>
/// </remarks>
public sealed class OverflowStripPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(
            nameof(Spacing),
            typeof(double),
            typeof(OverflowStripPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    private int _visibleCount = -1;

    /// <summary>Raised when the number of children that fit changes. The argument is that count.</summary>
    public event Action<int>? VisibleCountChanged;

    /// <summary>Gap between adjacent children.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>How many children are currently on the row. The rest belong in the overflow menu.</summary>
    public int VisibleCount => _visibleCount < 0 ? Children.Count : _visibleCount;

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            SetVisibleCount(0);
            return new Size(0, 0);
        }

        // Measured unconstrained: a bookmark is its icon and its name, and squeezing one is not an
        // option the way it is for a tab. It either has room or it goes in the menu.
        foreach (var child in Children)
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));

        double budget = availableSize.Width;
        double used = 0;
        double height = 0;
        int fitting = 0;

        for (int i = 0; i < count; i++)
        {
            double width = Children[i].DesiredSize.Width;
            double next = used + (i == 0 ? 0 : Spacing) + width;

            // No budget at all means an unconstrained parent, where everything fits by definition.
            if (!double.IsInfinity(budget) && next > budget) break;

            used = next;
            height = Math.Max(height, Children[i].DesiredSize.Height);
            fitting++;
        }

        SetVisibleCount(fitting);
        return new Size(used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        int visible = VisibleCount;

        for (int i = 0; i < Children.Count; i++)
        {
            if (i >= visible)
            {
                // Off the row, but still measured and still a child, so the next pass reads the
                // same natural width and cannot oscillate.
                Children[i].Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            double width = Children[i].DesiredSize.Width;
            Children[i].Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + Spacing;
        }

        return finalSize;
    }

    private void SetVisibleCount(int count)
    {
        if (_visibleCount == count) return;

        _visibleCount = count;
        VisibleCountChanged?.Invoke(count);
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is OverflowStripPanel panel) panel.InvalidateMeasure();
    }
}
