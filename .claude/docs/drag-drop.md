> **Scope:** Use when modifying drag-and-drop or edit mode in the flyout. Covers the custom drag-drop system, cross-list moves, insertion indicators, the edit-mode geometry contract, and known WinUI 3 limitations.
> **Governs:** `**/FlyoutWindow.xaml*`, `**/FlyoutWindow.EditMode.cs`.

# Drag-and-Drop & Edit Mode Conventions (FlyoutWindow)

All launcher item editing lives in the flyout. The former in-settings `LauncherItemsPage` was
deleted; do not reintroduce a second editor.

## Why custom drag-drop (not CanReorderItems)

WinUI 3's `CanReorderItems` takes full internal control of `DragOver` and `Drop` events. Its
built-in handlers evaluate the drag source at a low level and **cannot be reliably overridden**
— even `AddHandler` with `handledEventsToo` doesn't work consistently. This makes cross-list
drag-drop (between column lists and group child lists) impossible with `CanReorderItems`.

**Solution:** All ListViews use `CanDragItems="True"` (not `CanReorderItems`) with fully custom
`DragOver`, `DragLeave`, `Drop`, `DragItemsStarting`, and `DragItemsCompleted` handlers.

## Edit mode

Dragging is an **edit-mode-only** affordance. Both `DragItemsStarting` handlers cancel when
`_isEditMode` is false. Entry is the hover-revealed pencil overlay; exit is the toolbar's Done
button, Escape (first press exits edit mode, second dismisses), or hiding the flyout.

### Geometry contract

Edit mode may grow the flyout's **height**. It must never change its **width**, or the size of
any item or group.

- The **toolbar floats in its own window** (`EditToolbarWindow`) above the flyout, so it
  contributes no height at all and cannot crowd the launcher's content. It is
  `WS_EX_NOACTIVATE` so clicking it does not steal focus, and the flyout drops its
  always-on-top flag while an owned editor is open — ownership alone does not beat a topmost
  owner. Reposition it **after** the flyout settles: `ResizeWindowToCurrentContent` moves the
  window with `SWP_ASYNCWINDOWPOS`, so reading its rect immediately returns the *old* bounds.
- The **edit-mode border tint** uses the DWM border colour (`DWMWA_BORDER_COLOR`), not a XAML
  border — a `BorderThickness` on `RootGrid` would inset the content.
- `GetFlyoutWidth()` sums fixed column widths arithmetically and is never measured, so anything
  wider than the content is **clipped**.
- Per-column headers and the empty-launcher placeholder *do* add height, and both are counted
  in the arithmetic (`CurrentColumnHeaderHeight`, `CurrentEmptyPlaceholderHeight`). Anything
  that reserves space must be added there, or the content overflows the window — an empty
  column's drop-target `MinHeight` is skipped when the whole launcher is empty precisely
  because only the placeholder is accounted for.
- Per-item/group affordances must be **non-reflowing**: use `Background` and `CornerRadius`
  only, which have no layout effect. A border was tried and rejected — compensating for it with
  negative padding only works when the container already has padding on every side to give
  back, which the icon-mode container style does not.
- Empty groups get a `MinHeight` drop target in edit mode. This grows height only. In icon mode
  it costs nothing extra: `MeasureIconModeHeight` already reserves one row for an empty group
  via `Math.Max(1, …)`.

### Height measurement

`MeasureContentHeight` / `MeasureIconModeHeight` compute height **arithmetically** because
forcing a layout pass on a window hidden via `ShowWindow(SW_HIDE)` while another WinUI 3 window
is active causes a fatal `ExecutionEngineException`. Do not "fix" this by calling
`UpdateLayout()`.

**Compute, don't measure.** Three separate height bugs came from measuring: a "learned" chrome
constant that fed its own output back into the arithmetic and grew without bound; a
`ContentStack` measure taken straight after a rebuild, before the new containers were laid out;
and a double-counted margin (`DesiredSize` already includes the element's own margin). Edit
chrome heights are now derived from geometry that is known up front — toolbar rows × slot,
header height × 1.

Where a real measurement genuinely is needed, take it **after layout**, never during
construction or mid-animation. `LauncherSettingsWindow` sizes itself on `Loaded` for this
reason; sizing during the constructor read a `DesiredSize` of zero and produced a full-height
window. Likewise, entering edit mode from the settings app waits out the open animation:
`ShowAnimated` drives geometry on a rendering loop, and a resize partway through is overwritten
by the animation's next frame.

## Source of truth

`_launcher.Items` is authoritative. `_columnLists` is a **derived view**, rebuilt from the flat
list by `BuildColumnLists()`.

| Operation | Mutates | Persists via |
|---|---|---|
| Add / remove / rename | `_launcher.Items` (or `group.Children`) | `PersistStructuralChange()` |
| Drag reorder | `_columnLists` | `PersistFlyoutReorder()` |

**Critical:** `PersistStructuralChange()` must **not** call `SyncColumnsToFlatList()`. That
method clears `_launcher.Items` and regenerates it from `_columnLists`, so calling it after a
structural edit resurrects anything just removed — this is exactly how remove was once broken.
Drag-drop is the opposite case and must flush the other way.

## Architecture

### Multi-column layout

The flat `Items` collection is split at `IsColumnBreak` sentinel items into per-column
`ObservableCollection<LauncherItem>` lists. Each column gets its own `ListView` (list mode) or
`GridView` (icon mode) created in `CreateColumnListView()`.

### Synthetic groups

In icon mode, consecutive ungrouped items are wrapped into **ephemeral** groups
(`WrapUngroupedItemsIntoSyntheticGroups`) so loose icons pack into a wrapping grid. These are
tracked in `_syntheticGroups`, unwrapped again by `SyncColumnsToFlatList()`, and must **never**
be renamed, removed, reordered, or persisted. Guard with `IsEditableGroup(item)`.

### Drag surfaces

| Surface | Tag | Source collection | Notes |
|---|---|---|---|
| Column items | column index (`int`) | `_columnLists[idx]` | Cross-column drag-drop |
| Group children | the group `LauncherItem` | `group.Children` | Rejects `IsGroup`/`IsColumnBreak` drops (no nesting) |

### Shared state fields

- `_dragItem` — the `LauncherItem` being dragged
- `_dragSourceCollection` — the collection it came from
- `_lastIndicatorContainer` / `_lastIndicatorListView` — insertion-indicator restore state

### Drop index calculation

`GetDropIndex(ListViewBase, DragEventArgs)` dispatches to `GetDropIndexGrid` when the panel is
an `ItemsWrapGrid` or `PackedIconPanel` (row-band hit test, then X-midpoint); otherwise it scans
Y-midpoints.

**Critical:** When reordering within the same collection, removing the dragged item shifts
subsequent items up by one. The drop handlers must adjust: if the original index was before the
drop index, decrement `dropIndex` by 1 after removal. This applies to both
`ColumnListView_Drop` and `GroupChildList_Drop`.

## Visual feedback

`ShowInsertionIndicator(ListView, int)` sets an accent-coloured border on the target container.
In list mode this is a horizontal line; in icon-grid mode it's a vertical line **with a
compensating negative padding on the same side**, so the container's outer dimensions stay
constant and the grid doesn't reflow. `ClearInsertionIndicator()` restores border, padding and
margin from the saved `_lastIndicator*` fields.

`DragUIOverride.Caption` shows contextual text ("Move above X", "Move into X", "Move to end").

## Container item lookup

These lists are populated via `ItemsSource`, which puts the item in each container's
**`Content`** and leaves `DataContext` **null**. Use `GetContainerItem(container)` (Content
first, DataContext as fallback) — checking `DataContext` silently rejects every item. Always
re-read on each event rather than capturing, since containers are recycled.

## Pointer events

`ListViewItem` marks `PointerMoved` as **handled** for its own hover visuals, so a handler
attached to `RootGrid` never fires over an actual item — only over empty space. Attach hover
handlers to the **containers** (via `ContainerContentChanging`, plus an immediate pass over
`ContainerFromIndex` for already-realised ones). In nested lists the child's handler runs first,
so set `e.Handled = true` to stop the enclosing group claiming the hover.

## Common pitfalls

1. **Never use `CanReorderItems`** for ListViews that need cross-list drag-drop.
2. **Always adjust drop index** when removing from the same collection before inserting.
3. **`RebuildColumnsPanel()` re-creates all containers** — any visual state must be model-backed
   or re-applied in `Loaded` / `ContainerContentChanging`.
4. **Groups cannot be dropped into other groups** — `GroupChildList_DragOver` rejects `IsGroup`.
5. **Never persist synthetic groups.**
6. **Never call `SettingsManager.SaveSettings()` alone** for an item change — always pair it with
   `AutoSyncService.NotifyItemsChanged()` (use `PersistFlyoutItemChanges` / `PersistFlyoutReorder`),
   or a periodic sync download will silently revert the edit.
7. **Column breaks are invisible** — they exist only as sentinel items in the flat list.
