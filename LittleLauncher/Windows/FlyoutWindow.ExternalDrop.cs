using LittleLauncher.Models;
using LittleLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using DataPackageOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using DataPackageView = global::Windows.ApplicationModel.DataTransfer.DataPackageView;

namespace LittleLauncher.Windows;

/// <summary>
/// Dropping files, shortcuts and links into the flyout from File Explorer, the desktop,
/// the Start Menu or a browser.
/// </summary>
/// <remarks>
/// <para>This is edit-mode-only, and not merely for symmetry with dragging: the flyout
/// dismisses on <c>Deactivated</c>, so outside edit mode it is already gone by the time the
/// user has switched to Explorer to pick something up. <c>SuppressDismiss</c> is what keeps
/// it on screen long enough to be a drop target at all.</para>
/// <para>The per-list handlers in <c>FlyoutWindow.xaml.cs</c> route here whenever a drag
/// arrives with no <c>_dragItem</c> set — that is exactly the case of a drag that did not
/// start inside this flyout.</para>
/// </remarks>
public partial class FlyoutWindow
{
    /// <summary>Read-only shared launchers have no editing affordances at all, including this one.</summary>
    private bool AcceptsExternalDrops => _isEditMode && !IsReadOnlyLauncher;

    /// <summary>
    /// Picks a drop operation the source is willing to perform. Link is the honest one — a
    /// launcher item references its target, it doesn't take a copy — but a source that only
    /// offers Copy would reject it, and an operation the source refuses reads to the user as
    /// "this app won't accept the drop" with no visible reason.
    /// </summary>
    private static DataPackageOperation ExternalDropOperation(DragEventArgs e)
    {
        if (e.AllowedOperations.HasFlag(DataPackageOperation.Link))
            return DataPackageOperation.Link;

        return e.AllowedOperations.HasFlag(DataPackageOperation.Copy)
            ? DataPackageOperation.Copy
            : DataPackageOperation.Link;
    }

    // ── Drag over ───────────────────────────────────────────────────

    /// <summary>
    /// Accepts an external payload over a list, showing the same insertion indicator a
    /// reorder would, so the drop position is as precise as an internal move.
    /// </summary>
    /// <param name="group">The group being hovered, or null for a top-level column.</param>
    private void ExternalDragOver(ListViewBase listView, DragEventArgs e, LauncherItem? group)
    {
        if (!AcceptsExternalDrops || !DroppedItemFactory.CanAccept(e.DataView))
            return;

        e.AcceptedOperation = ExternalDropOperation(e);

        ShowInsertionIndicator(listView, GetDropIndex(listView, e));

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = group != null
            ? $"Add to {GetItemDisplayName(group)}"
            : "Add to launcher";
        e.Handled = true;
    }

    /// <summary>
    /// Accepts an external payload over the flyout's empty space. This is the only drop
    /// surface an empty launcher has — with no items there is no list big enough to hit.
    /// </summary>
    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        // An internal reorder that reaches the root has missed every list; leaving it
        // unaccepted lets it fall back to the source list's own handling.
        if (_dragItem != null || !AcceptsExternalDrops || !DroppedItemFactory.CanAccept(e.DataView))
            return;

        e.AcceptedOperation = ExternalDropOperation(e);
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = "Add to launcher";
        e.Handled = true;
    }

    // ── Drop ────────────────────────────────────────────────────────

    /// <summary>
    /// Drops an external payload into a column or group at the hovered position.
    /// </summary>
    /// <remarks>
    /// <paramref name="target"/> is a derived collection (a column list, or a group's
    /// children reached through one), so this persists via <c>PersistFlyoutReorder</c> —
    /// the same flush direction a drag reorder uses.
    /// </remarks>
    private void ExternalDrop(ListViewBase listView, DragEventArgs e, ObservableCollection<LauncherItem> target)
    {
        if (!AcceptsExternalDrops || !DroppedItemFactory.CanAccept(e.DataView))
            return;

        // e.GetPosition is only meaningful while the event is on the stack, so the
        // insertion point has to be resolved before the deferral takes this async.
        int dropIndex = GetDropIndex(listView, e);

        BeginExternalDrop(e, target, dropIndex, PersistFlyoutReorder);
    }

    /// <summary>
    /// Drops an external payload that landed on the flyout's empty space. It appends to the
    /// flat list, which lands in the last column — the same place the toolbar's add button
    /// puts a new item.
    /// </summary>
    private void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (_dragItem != null || !AcceptsExternalDrops || !DroppedItemFactory.CanAccept(e.DataView))
            return;

        // Appending to _launcher.Items is a structural edit, so it must NOT persist via
        // PersistFlyoutReorder: that regenerates the flat list from the column lists,
        // which were built before these items existed and would drop them again.
        var target = GetAddTarget();
        BeginExternalDrop(e, target, target.Count, PersistStructuralChange);
    }

    private void BeginExternalDrop(
        DragEventArgs e,
        ObservableCollection<LauncherItem> target,
        int dropIndex,
        Action persist)
    {
        var deferral = e.GetDeferral();
        e.Handled = true;
        _ = CompleteExternalDropAsync(e.DataView, target, dropIndex, persist, deferral);
    }

    /// <summary>
    /// Reads the payload, inserts the items, then fills in titles and icons in the
    /// background so the drop itself stays instant.
    /// </summary>
    private async Task CompleteExternalDropAsync(
        DataPackageView data,
        ObservableCollection<LauncherItem> target,
        int dropIndex,
        Action persist,
        DragOperationDeferral deferral)
    {
        List<LauncherItem> added;

        try
        {
            added = await DroppedItemFactory.CreateItemsAsync(data);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not read the dropped payload");
            added = [];
        }
        finally
        {
            // Release the source app before any of the slower work below — Explorer
            // keeps its drag loop spinning until the deferral completes.
            deferral.Complete();
        }

        if (added.Count == 0)
        {
            Logger.Info("External drop carried nothing that maps to a launcher item");
            return;
        }

        int index = Math.Clamp(dropIndex, 0, target.Count);
        foreach (var item in added)
            target.Insert(index++, item);

        persist();

        // Website titles and icons need the network or the shell, so they land on the
        // already-visible items rather than stalling the drop behind them.
        await DroppedItemFactory.EnrichAsync(added);
        PersistFlyoutItemChanges();

        Logger.Info($"Added {added.Count} item(s) to {_launcher.Name} by drag-and-drop");
    }
}
