// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LittleLauncher.Windows;

/// <summary>
/// What a bookmark folder opens: a hanging list of what it holds, which can be dragged into, out
/// of, and rearranged at any depth.
/// </summary>
/// <remarks>
/// <para><b>Neither a <see cref="MenuFlyout"/> nor a <see cref="Popup"/>, and both were tried.</b>
/// A menu cannot do it: a <c>MenuFlyoutItem</c> is not an element that can be given <c>CanDrag</c>,
/// and menu rows mark every pointer press handled for their own click bookkeeping. A popup renders
/// and clicks perfectly well and <b>never receives a drag at all</b> - measured twice, with the
/// panel's <c>DragOver</c> not firing once, first with light dismiss on and then with it off. A
/// drag *out* of the popup worked throughout, because the bar receiving it is in the window's own
/// tree, which is the tell: XAML registers the window as the drop target, and an unconstrained
/// popup is hosted outside it.</para>
/// <para><b>So the list is an overlay in the flyout's own root grid</b>, spanning its three rows.
/// That puts the rows in the same tree as the bookmark bar, which is what makes every direction of
/// the gesture work: reordering inside a folder, dropping onto a nested folder, and dragging back
/// out onto the bar. It also restores click-outside-to-close, as a backdrop of our own rather than
/// a behaviour of the host.</para>
/// <para><b>Assembled from what the window already had.</b> The rows are the bar's own buttons, so
/// they drag through <c>WireStripDragSource</c> (a <c>Button</c> captures the pointer for its click
/// handling, so <c>CanDrag</c> alone never fires) and carry the same context menu. The drop index is
/// the Y-midpoint scan the flyout's edit mode uses for its column lists, and a drop crossing from
/// one collection to another is the same shape as dragging an item between a column and a group's
/// children - see drag-drop.md, which is also where the rule against <c>CanReorderItems</c> comes
/// from: it takes over <c>DragOver</c> and <c>Drop</c> and cannot be overridden, which makes
/// dragging *out* of a folder impossible by construction.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>The open folder lists, outermost first, so a nested one closes with its parent.</summary>
    private readonly List<Border> _folderPanels = [];

    /// <summary>The folder each open list is showing, so clicking it again can close it.</summary>
    private readonly List<WebBookmark> _openFolders = [];

    /// <summary>The narrowest a folder list may be, in DIPs.</summary>
    /// <remarks>
    /// Wide enough that a two-word bookmark does not come out as a sliver, and no wider. A fixed
    /// width was tried first and is what made a folder of short names take a third of the flyout
    /// for nothing.
    /// </remarks>
    private const double MinFolderPanelWidth = 150;

    /// <summary>The widest, so one long name cannot claim the whole window.</summary>
    private const double MaxFolderPanelWidth = 300;

    /// <summary>How far a nested list sits *over* its parent rather than beside it.</summary>
    /// <remarks>
    /// A cascading menu overlaps its parent by a little instead of clearing it entirely. Two
    /// reasons, and both matter more here than in a full-size window: the pointer travels from the
    /// row to the child across a shorter gap, so the child is easier to reach without straying onto
    /// a sibling; and every level costs less width, which in a flyout only a few hundred pixels
    /// wide is the difference between two levels fitting and one.
    /// </remarks>
    private const double FolderNestOverlap = 32;

    /// <summary>How far a nested list starts above the row that opened it.</summary>
    /// <remarks>
    /// Enough to take the list's own padding and border out of the reckoning, so its first row
    /// lines up with the row it came from rather than sitting a few pixels below it. Level, the
    /// two read as one continuous step; low, the child looks like it belongs to the row underneath.
    /// </remarks>
    private const double FolderNestLift = 9;

    /// <summary>
    /// Opens a folder, showing what it holds.
    /// </summary>
    /// <remarks>
    /// Anchored to the left edge of whatever was clicked: hanging up from a bar button, and stepped
    /// right of a row inside another list so both stay readable.
    /// </remarks>
    private void ShowFolderPopup(WebBookmark folder, FrameworkElement anchor, int depth = 0)
    {
        if (_folderOverlay == null) return;

        // Clicking the folder that is already open closes it again.
        bool alreadyOpen = depth < _openFolders.Count && ReferenceEquals(_openFolders[depth], folder);

        CloseFolderPopups(depth);
        if (alreadyOpen) return;

        var panel = new StackPanel { Spacing = 2 };

        // The same caret the bar draws, turned on its side: a 2px accent line marking where the
        // row will land. In a layer of its own over the list, so it adds nothing to the layout the
        // caret's own position is measured from - the trap recorded on the bar's version.
        var caret = new Border
        {
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(1),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var layers = new Grid();
        layers.Children.Add(panel);
        layers.Children.Add(caret);

        var surface = new Border
        {
            Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = MinFolderPanelWidth,
            MaxWidth = MaxFolderPanelWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = layers,
        };

        FillFolderPanel(panel, folder, caret, depth);

        _folderOverlay.Children.Add(surface);
        _folderPanels.Add(surface);
        _openFolders.Add(folder);

        _folderOverlay.Visibility = Visibility.Visible;
        _isMenuOpen = true;

        // Re-placed whenever its size changes, not only on Loaded. Its height is what decides how
        // far *up* from the bar it starts, and a first pass with a height of zero puts its top edge
        // level with the bar - so the whole list hangs downwards off the bottom of the window, which
        // is exactly what it did. SizeChanged is the event that fires once there is a real height,
        // and again if the contents ever change.
        surface.SizeChanged += (_, _) => PlaceFolderPanel(surface, anchor, depth);
        PlaceFolderPanel(surface, anchor, depth);
    }

    /// <summary>Builds the rows for one folder, and makes the list a drop target for them.</summary>
    private void FillFolderPanel(StackPanel panel, WebBookmark folder, Border caret, int depth)
    {
        if (folder.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Empty",
                Opacity = 0.5,
                FontSize = 12,
                Margin = new Thickness(8, 6, 8, 6),
            });
        }

        foreach (var child in folder.Children)
            panel.Children.Add(BuildFolderRow(child, depth));

        Button? tinted = null;

        void Tint(Button? row)
        {
            if (ReferenceEquals(tinted, row)) return;

            if (tinted != null)
                tinted.Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];

            tinted = row;

            // Background only, never a border: a border insets the content and reflows the row.
            if (tinted != null)
                tinted.Background = (Brush)Application.Current.Resources["AccentFillColorSelectedTextBackgroundBrush"];
        }

        void ClearFeedback()
        {
            caret.Visibility = Visibility.Collapsed;
            Tint(null);
        }

        panel.AllowDrop = true;
        panel.DragOver += (_, e) =>
        {
            if (_draggingBookmark == null) return;

            e.Handled = true;
            e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

            // No caption at all. The list is 260px wide and the caption box covered most of what
            // the user was reading to decide where to drop - which is the one thing they need to
            // see. The line and the tint say the same two things without standing on the rows.
            if (e.DragUIOverride != null) e.DragUIOverride.IsCaptionVisible = false;

            double y = e.GetPosition(panel).Y;

            if (RowFolderAt(panel, y, _draggingBookmark) is { } nested)
            {
                caret.Visibility = Visibility.Collapsed;
                Tint(RowFor(panel, nested));
                return;
            }

            Tint(null);
            ShowFolderCaret(panel, caret, FolderDropIndex(panel, y, _draggingBookmark));
        };

        panel.DragLeave += (_, _) => ClearFeedback();
        panel.Drop += (_, e) =>
        {
            ClearFeedback();
            FolderPanelDrop(panel, folder, e);
        };
    }

    /// <summary>One row: a bookmark to open, or a folder that opens a list of its own.</summary>
    private Button BuildFolderRow(WebBookmark bookmark, int depth)
    {
        var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (!bookmark.IsFolder && !string.IsNullOrEmpty(bookmark.IconPath) && System.IO.File.Exists(bookmark.IconPath))
            icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(bookmark.IconPath));

        var fallback = new FontIcon
        {
            Glyph = bookmark.IsFolder ? "\uE8B7" : "\uE774",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = icon.Source == null ? Visibility.Visible : Visibility.Collapsed,
        };

        var iconSlot = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        iconSlot.Children.Add(icon);
        iconSlot.Children.Add(fallback);

        string caption = string.IsNullOrWhiteSpace(bookmark.Name) ? bookmark.Url : bookmark.Name;

        // A Grid rather than a stack, so the chevron sits at the *right end of the row* instead of
        // trailing the name. Beside the text it reads as punctuation on the name; at the edge it
        // reads as the row leading somewhere, which is what a cascading menu looks like everywhere
        // else and where the eye is already pointed when the next list opens over there.
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(iconSlot, 0);
        content.Children.Add(iconSlot);

        var label = new TextBlock
        {
            Text = caption,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        // U+E76C, Segoe Fluent's ChevronRight - what every browser uses for "there is more this
        // way", and the only thing distinguishing a nested folder from a bookmark once both are a
        // row with an icon and a name.
        if (bookmark.IsFolder)
        {
            var chevron = new FontIcon
            {
                Glyph = "\uE76C",
                FontSize = 10,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(chevron, 2);
            content.Children.Add(chevron);
        }

        var button = new Button
        {
            Content = content,
            Tag = bookmark,
            HorizontalAlignment = HorizontalAlignment.Stretch,

            // Stretch, not Left. A Button that aligns its content left sizes that content to
            // itself, so the row's star column had nothing to expand into and the chevron sat
            // against the name instead of at the row's right edge.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            MinWidth = 0,
            MinHeight = 0,
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, bookmark.IsFolder ? caption : bookmark.Url);

        button.Click += (_, _) =>
        {
            if (bookmark.IsFolder)
            {
                ShowFolderPopup(bookmark, button, depth + 1);
                return;
            }

            CloseFolderPopups(0);
            OpenBookmark(bookmark, newTab: WantsNewTab());
        };

        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsMiddleButtonPressed) return;

            e.Handled = true;
            if (bookmark.IsFolder) return;

            CloseFolderPopups(0);
            OpenBookmark(bookmark, newTab: true);
        }), handledEventsToo: true);

        button.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowBookmarkMenu(bookmark, button, FlyoutPlacementMode.Right);
        };

        // The same drag source the bar uses, for the same reason: CanDrag alone never fires on a
        // Button, which captures the pointer for its own click handling.
        button.CanDrag = true;
        WireStripDragSource(button);

        button.DragStarting += (_, e) =>
        {
            _draggingBookmark = bookmark;
            _isStripDragging = true;

            e.Data.SetText(bookmark.Url);
            e.Data.RequestedOperation =
                global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move |
                global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        };

        button.DropCompleted += (_, _) =>
        {
            _draggingBookmark = null;
            _isStripDragging = false;
            CloseFolderPopups(0);
        };

        return button;
    }

    /// <summary>
    /// Answers a drop inside a folder: into a nested folder if it landed on one, otherwise into
    /// this folder at the row it was dropped between.
    /// </summary>
    private void FolderPanelDrop(StackPanel panel, WebBookmark folder, DragEventArgs e)
    {
        var dragged = _draggingBookmark;
        Logger.Info("Folder drop on {Folder}, dragging {Dragged}", folder.Name, dragged?.Name ?? "(nothing)");

        if (dragged == null) return;

        e.Handled = true;
        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        double y = e.GetPosition(panel).Y;

        // Onto a nested folder rather than between two rows, judged by the middle of the row so a
        // bookmark can still be dropped *beside* a folder. Same rule the bar uses.
        if (RowFolderAt(panel, y, dragged) is { } nested)
        {
            MoveIntoFolder(dragged, nested);
            CloseFolderPopups(0);
            return;
        }

        if (ReferenceEquals(dragged, folder) || (dragged.IsFolder && Contains(dragged, folder))) return;

        int to = FolderDropIndex(panel, y, dragged);
        int from = folder.Children.IndexOf(dragged);

        if (from >= 0)
        {
            // Reordering inside this folder. The index already counts only the others, which is the
            // list that exists after the removal, so it needs no adjustment - the same trap the
            // bar's drop records.
            to = Math.Clamp(to, 0, folder.Children.Count - 1);
            if (to == from) return;

            folder.Children.Move(from, to);
        }
        else
        {
            // Arriving from the bar or from another folder.
            if (!DetachBookmark(dragged)) return;

            folder.Children.Insert(Math.Clamp(to, 0, folder.Children.Count), dragged);
        }

        PersistBookmarks();
        CloseFolderPopups(0);
    }

    /// <summary>The row carrying a bookmark within one list.</summary>
    private static Button? RowFor(StackPanel panel, WebBookmark bookmark) =>
        panel.Children.OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.Tag, bookmark));

    /// <summary>Puts the caret at the gap a drop would land in.</summary>
    /// <remarks>
    /// Above the row it would land before, or below the last one when it goes on the end - the same
    /// two cases the bar's caret handles along its own axis.
    /// </remarks>
    private void ShowFolderCaret(StackPanel panel, Border caret, int index)
    {
        var rows = panel.Children
            .OfType<Button>()
            .Where(b => b.ActualHeight > 0
                     && b.Tag is WebBookmark bookmark
                     && !ReferenceEquals(bookmark, _draggingBookmark))
            .ToList();

        if (rows.Count == 0)
        {
            caret.Visibility = Visibility.Collapsed;
            return;
        }

        bool atEnd = index >= rows.Count;
        var anchor = atEnd ? rows[^1] : rows[index];
        double top = RowTop(anchor, panel) + (atEnd ? anchor.ActualHeight : 0);

        caret.Margin = new Thickness(0, Math.Max(0, top - 1), 0, 0);
        caret.Visibility = Visibility.Visible;
    }

    /// <summary>The nested folder a drop at <paramref name="y"/> lands on, or null.</summary>
    /// <remarks>
    /// Positions are read off the rows themselves rather than added up from zero: the panel spaces
    /// its rows, so an accumulated total drifts and the bands stop matching what is on screen - the
    /// same mistake that stopped a drop landing on a folder on the bar.
    /// </remarks>
    private static WebBookmark? RowFolderAt(StackPanel panel, double y, WebBookmark? dragged)
    {
        foreach (var child in panel.Children)
        {
            if (child is not Button { Tag: WebBookmark bookmark } row) continue;
            if (!bookmark.IsFolder || ReferenceEquals(bookmark, dragged)) continue;

            double height = row.ActualHeight;
            if (height <= 0) continue;

            double top = RowTop(row, panel);
            double inset = Math.Min(height * 0.25, 10);
            if (y >= top + inset && y <= top + height - inset) return bookmark;
        }

        return null;
    }

    /// <summary>
    /// Where in the folder a drop at <paramref name="y"/> falls, counting only the rows that are
    /// not being dragged.
    /// </summary>
    private static int FolderDropIndex(StackPanel panel, double y, WebBookmark? dragged)
    {
        int index = 0;

        foreach (var child in panel.Children)
        {
            if (child is not Button { Tag: WebBookmark bookmark } row) continue;
            if (ReferenceEquals(bookmark, dragged)) continue;

            double height = row.ActualHeight;
            if (height <= 0) continue;

            if (y <= RowTop(row, panel) + (height / 2)) break;
            index++;
        }

        return index;
    }

    /// <summary>A row's arranged top edge within its list.</summary>
    private static double RowTop(FrameworkElement row, StackPanel panel) =>
        row.TransformToVisual(panel).TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;

    /// <summary>Places a list against whatever opened it, kept inside the window.</summary>
    private void PlaceFolderPanel(Border surface, FrameworkElement anchor, int depth)
    {
        if (_folderOverlay == null) return;

        try
        {
            var origin = anchor.TransformToVisual(_folderOverlay)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));

            // Measured rather than trusted: before the first layout pass both ActualHeight and
            // DesiredSize are zero, and a zero here is what put the list below the bar instead of
            // above it.
            if (surface.ActualHeight <= 0)
            {
                surface.Measure(new global::Windows.Foundation.Size(
                    MaxFolderPanelWidth, double.PositiveInfinity));
            }

            double height = surface.ActualHeight > 0 ? surface.ActualHeight : surface.DesiredSize.Height;
            if (height <= 0) return;

            // Its own width, now that it sizes to its rows: a constant here would place a narrow
            // list as though it were the widest one possible.
            double width = surface.ActualWidth > 0 ? surface.ActualWidth : surface.DesiredSize.Width;
            if (width <= 0) width = MinFolderPanelWidth;

            double maxLeft = Math.Max(0, _folderOverlay.ActualWidth - width);
            double maxTop = Math.Max(0, _folderOverlay.ActualHeight - height);

            double left;
            double top;

            if (depth == 0)
            {
                // Up from the bar button that opened it, because the bar is along the foot.
                left = origin.X;
                top = origin.Y - height - 4;
            }
            else
            {
                // **Beside the parent list, not on top of it.** Offsetting from the row's own
                // position put a nested list exactly where its row was, so it covered the list it
                // came from and read as the subfolder being replaced by its contents. A submenu
                // hangs off the parent's edge, level with the row that opened it - which is what
                // says it belongs to that row rather than replacing it.
                var parent = _folderPanels.ElementAtOrDefault(depth - 1);
                double parentLeft = parent?.Margin.Left ?? origin.X;

                double parentWidth = parent?.ActualWidth > 0 ? parent.ActualWidth : width;
                left = parentLeft + parentWidth - FolderNestOverlap;
                top = origin.Y - FolderNestLift;

                // No room to the right: fold back across the parent's left edge, overlapping it by
                // the same amount, as a cascading menu does at the edge of a screen.
                if (left > maxLeft)
                    left = Math.Max(0, parentLeft - width + FolderNestOverlap);
            }

            surface.Margin = new Thickness(
                Math.Clamp(left, 0, maxLeft),
                Math.Clamp(top, 0, maxTop),
                0, 0);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Placing a bookmark folder list failed");
        }
    }

    /// <summary>Closes every folder list at or below <paramref name="depth"/>.</summary>
    internal void CloseFolderPopups(int depth)
    {
        for (int i = _folderPanels.Count - 1; i >= depth && i >= 0; i--)
        {
            _folderOverlay?.Children.Remove(_folderPanels[i]);
            _folderPanels.RemoveAt(i);
            if (i < _openFolders.Count) _openFolders.RemoveAt(i);
        }

        if (_folderPanels.Count > 0) return;

        _isMenuOpen = false;
        if (_folderOverlay != null) _folderOverlay.Visibility = Visibility.Collapsed;
    }
}
