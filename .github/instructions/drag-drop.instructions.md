---
description: "Use when modifying drag-and-drop behavior in the Launcher Items settings page. Covers the custom drag-drop system, cross-list moves, insertion indicators, and known WinUI 3 limitations."
applyTo: "**/LauncherItemsPage.xaml*"
---

# Drag-and-Drop Conventions (LauncherItemsPage)

## Why custom drag-drop (not CanReorderItems)

WinUI 3's `CanReorderItems` takes full internal control of `DragOver` and `Drop` events. Its built-in handlers evaluate the drag source at a low level and **cannot be reliably overridden** — even `AddHandler` with `handledEventsToo` doesn't work consistently. This makes cross-list drag-drop (between top-level and group ListViews) impossible with `CanReorderItems`.

**Solution:** All ListViews use `CanDragItems="True"` (not `CanReorderItems`) with fully custom `DragOver`, `DragLeave`, `Drop`, `DragItemsStarting`, and `DragItemsCompleted` handlers.

## Architecture

### Three drag surfaces

| Surface | ListView | Source collection | Notes |
|---|---|---|---|
| Top-level items | `ItemsList` | `SettingsManager.Current.LauncherItems` | Handles all item types including groups |
| Group children | `GroupChildList` (inside DataTemplate) | `group.Children` | Rejects `IsGroup` drops (groups can't nest) |
| Top-level drop zone | `TopLevelDropZone` (Border) | — | Appears only when dragging FROM a group; drops append to top-level |

### Shared state fields

- `_dragItem` — the `LauncherItem` being dragged
- `_dragSourceCollection` — the `ObservableCollection<LauncherItem>` the item came from
- `_lastIndicatorContainer` — the last `ListViewItem` with an insertion indicator border

### Drop index calculation

`GetDropIndex(ListView, DragEventArgs)` iterates item containers, comparing the cursor Y position against each container's vertical midpoint. Returns the index where the item should be inserted (Count = append to end).

**Critical:** When reordering within the same collection, removing the dragged item shifts subsequent items up by one. The drop handlers must adjust: if the original index was before the drop index, decrement `dropIndex` by 1 after removal. This applies to both `ItemsList_Drop` and `GroupChildList_Drop`.

## Visual feedback

### Insertion indicators

`ShowInsertionIndicator(ListView, int)` sets an accent-colored 2px border (top or bottom) on the `ListViewItem` at the target position. `ClearInsertionIndicator()` resets the last indicator via `_lastIndicatorContainer`.

### Drag captions

`DragUIOverride.Caption` shows contextual text:
- Top-level/group: "Move above {targetItem.Name}" or "Move to end"
- Top-level drop zone: "Move to top level"

### TopLevelDropZone

A `Border` with `AllowDrop="True"` that sits between `ItemsList` and the button bar. It collapses by default and becomes `Visible` in `GroupChildList_DragItemsStarting` (only when dragging from a group). Hidden again in `DragItemsCompleted` and `Drop` handlers.

## Group collapse state

Groups use a custom expand/collapse StackPanel (Tag `"GroupRoot"` / `"GroupChildren"`), not WinUI Expanders. `LauncherItem.IsExpanded` (`[XmlIgnore]`, defaults `true`) preserves collapse state across `RefreshList()` re-renders. The `GroupRoot_Loaded` handler reads `IsExpanded` and restores the collapsed visual + chevron glyph.

## Button order (item action buttons)

Left to right: **Move to…** → **Move up** → **Move down** → **Edit** → **Remove**. This order is consistent across LauncherItemTemplate and HeadingItemTemplate.

## Common pitfalls

1. **Never use `CanReorderItems`** for ListViews that need cross-list drag-drop. It swallows drag events.
2. **Always adjust drop index** when removing from the same collection before inserting — classic off-by-one.
3. **`RefreshList()` re-creates all containers** — any visual state (borders, expanded/collapsed) must be model-backed or restored in `Loaded` handlers.
4. **Groups cannot be dropped into other groups** — `GroupChildList_DragOver` rejects `IsGroup` items.
