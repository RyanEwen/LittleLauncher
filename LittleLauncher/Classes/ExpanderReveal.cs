// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace LittleLauncher.Classes;

/// <summary>
/// Scrolls a collapsed section's start into view when it is expanded.
/// </summary>
/// <remarks>
/// <para>An <see cref="Expander"/> near the bottom of a scrolling form reveals its content
/// <em>below the fold</em>: the header stays where it was and everything the click produced is off
/// screen, so it reads as having done nothing until the user thinks to scroll. Advanced in launcher
/// settings is the worst case — it is deliberately last in the dialog, so expanding it always
/// happens at the bottom of the scroller.</para>
/// <para><b>Aligned to the top rather than merely brought into view.</b>
/// <c>VerticalAlignmentRatio = 0</c> puts the header at the top of the viewport, which is the only
/// answer that works for a section taller than the viewport — the default "scroll the least amount
/// that makes it visible" would show its *end* for exactly the long sections that need it most.
/// </para>
/// <para><b>Twice, because the extent is not final on the first pass.</b> The section's rows are
/// still being realised while <c>Expanding</c> runs, so the scroller cannot yet scroll far enough
/// to put a bottom-most header at the top. The second pass runs at
/// <see cref="DispatcherQueuePriority.Low"/>, after that layout, and is the one that usually lands
/// it; the first is what keeps the movement from arriving a beat late when the extent was already
/// sufficient.</para>
/// </remarks>
internal static class ExpanderReveal
{
    /// <summary>Reveals this expander's header whenever the user opens it.</summary>
    internal static void Attach(Expander expander)
    {
        expander.Expanding += (sender, _) =>
        {
            Reveal(sender);
            sender.DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () => Reveal(sender));
        };
    }

    /// <summary>Reveals several at once, for a page built from a fixed set of sections.</summary>
    internal static void Attach(params Expander[] expanders)
    {
        foreach (var expander in expanders)
            Attach(expander);
    }

    private static void Reveal(Expander expander)
    {
        try
        {
            expander.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = 0,
                AnimationDesired = true,
            });
        }
        catch (Exception ex)
        {
            // Nothing here is worth taking a settings window out for: the section is expanded
            // either way, and the cost of failing is a scroll the user has to do themselves.
            NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Scrolling an expanded section into view failed");
        }
    }
}
