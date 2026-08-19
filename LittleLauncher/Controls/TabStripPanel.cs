// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace LittleLauncher.Controls;

/// <summary>
/// The row a browser's tabs sit in: natural widths while they fit, and a squeeze down to
/// <see cref="MinItemWidth"/> once they do not.
/// </summary>
/// <remarks>
/// <para>A <c>StackPanel</c> cannot do this, and the reason is the scroller it sits in: a
/// horizontally scrolling <c>ScrollViewer</c> measures its content with infinite width, so every
/// chip takes the width its title wants and the row simply grows past the edge. A fourth tab ended
/// up off screen with nothing on screen saying it was there.</para>
/// <para>What a browser does instead, and what this does: tabs keep their natural width until the
/// row is full, then they give width up together, and only once they hit a floor does the strip
/// start scrolling. The floor matters as much as the squeeze — a tab narrower than its favicon and
/// a character or two of title is not a smaller tab, it is an unidentifiable one.</para>
/// <para>The give-up is <b>max-min fair</b> rather than a flat equal share: a chip already narrower
/// than an even share keeps its own width and hands the difference back to the ones that need it,
/// so a short "Gmail" stays short instead of being padded out beside three long titles that are
/// being trimmed. It costs a few lines over dividing by the count, and it is the difference between
/// reading two titles and reading five.</para>
/// <para><see cref="AvailableWidth"/> exists because of the infinite measure above: the width that
/// decides all of this is the scroller's own, which only the host can supply. Left at zero the
/// panel lays out at natural widths, which is the right answer for a strip that is not in a
/// scroller.</para>
/// </remarks>
public sealed class TabStripPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(
            nameof(Spacing),
            typeof(double),
            typeof(TabStripPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(
            nameof(MinItemWidth),
            typeof(double),
            typeof(TabStripPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty AvailableWidthProperty =
        DependencyProperty.Register(
            nameof(AvailableWidth),
            typeof(double),
            typeof(TabStripPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    /// <summary>The width each child was allocated, in child order. Written by measure, read by arrange.</summary>
    private readonly List<double> _widths = [];

    /// <summary>Gap between adjacent tabs.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>How narrow a tab may be squeezed before the strip scrolls instead.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>
    /// The width to lay out within when the measure pass is handed an infinite one — the viewport
    /// of the scroller this panel is inside. Zero means "lay out at natural widths".
    /// </summary>
    public double AvailableWidth
    {
        get => (double)GetValue(AvailableWidthProperty);
        set => SetValue(AvailableWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _widths.Clear();

        int count = Children.Count;
        if (count == 0) return new Size(0, 0);

        double gaps = Spacing * (count - 1);
        double budget = double.IsInfinity(availableSize.Width) ? AvailableWidth : availableSize.Width;

        // Natural widths first. They are both the answer when the row fits and the cap when it does
        // not: a squeeze must never leave a chip wider than its own content asked for.
        var natural = new double[count];
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            natural[i] = Children[i].DesiredSize.Width;
            total += natural[i];
        }

        if (budget <= 0 || total + gaps <= budget) _widths.AddRange(natural);
        else AllocateWidths(natural, Math.Max(0, budget - gaps));

        double width = 0;
        double height = 0;
        for (int i = 0; i < count; i++)
        {
            // Re-measured at the width it will actually get, so its label trims to the room it has
            // rather than to the room it wanted. Only the ones that lost width need it.
            if (_widths[i] < natural[i])
                Children[i].Measure(new Size(_widths[i], availableSize.Height));

            width += _widths[i];
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }

        return new Size(width + gaps, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            double width = i < _widths.Count ? _widths[i] : Children[i].DesiredSize.Width;
            Children[i].Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + Spacing;
        }

        return finalSize;
    }

    /// <summary>
    /// Shares <paramref name="room"/> out across the children, capped at what each actually wants.
    /// </summary>
    /// <remarks>
    /// Repeats until a pass settles nothing: each round hands anything already narrower than the
    /// even share its own width and takes it out of the division, which is what lets the width it
    /// did not need reach the chips that are being trimmed. Whatever is still unsettled splits the
    /// remainder, floored at <see cref="MinItemWidth"/> — past that the strip scrolls, which is the
    /// honest answer, rather than going on shaving tabs down to slivers.
    /// </remarks>
    private void AllocateWidths(double[] natural, double room)
    {
        int count = natural.Length;
        var settled = new bool[count];
        int unsettled = count;
        double left = room;

        bool changed = true;
        while (changed && unsettled > 0)
        {
            changed = false;
            double share = left / unsettled;

            for (int i = 0; i < count; i++)
            {
                if (settled[i] || natural[i] > share) continue;

                settled[i] = true;
                unsettled--;
                left -= natural[i];
                changed = true;
            }
        }

        double squeezed = unsettled > 0 ? Math.Max(MinItemWidth, left / unsettled) : 0;

        // Capped at natural even here. The floor can exceed what a very short title asked for, and
        // padding a chip out past its own content is the one way this can produce a gap rather than
        // close one.
        for (int i = 0; i < count; i++)
            _widths.Add(settled[i] ? natural[i] : Math.Min(natural[i], squeezed));
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TabStripPanel panel) panel.InvalidateMeasure();
    }
}
