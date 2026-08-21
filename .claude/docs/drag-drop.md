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

> The flyout is no longer hidden at all — dismissing it now parks it off the virtual screen
> (`ParkOffScreen`, see `ARCHITECTURE.md`) so its composition surfaces survive. That removes the
> crash *hazard*, but the arithmetic stays: the reasons below are about correctness, not the
> crash, and a dismissed window is still the wrong thing to measure.

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

**Identity matters as much as content.** Every operation in the table finds a row's item **by
reference** (`FindParentCollection`), and `_columnLists` holds the very objects that were in
`_launcher.Items` when the panel was last built. Anything that swaps those objects for
equal-valued copies breaks all of them at once: remove, move up/down, move to and edit find no
parent and return silently, and a drag reorder flushes the stale set back over whatever replaced
it. A sync download used to do exactly that on every tick, which disabled item editing in every
flyout until the app was restarted; see [sync.md](sync.md) for the guard that stops it.
`FindParentCollection` logs a warning when a row's item is missing, because the only other
symptom is a menu entry that does nothing.

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

## External drops (Explorer, desktop, Start Menu, browsers)

`FlyoutWindow.ExternalDrop.cs` accepts drags that did **not** start inside the flyout, turning
files, shortcuts and links into new items. `Services/DroppedItemFactory.cs` does the payload →
`LauncherItem` mapping.

**Routing:** every per-list `DragOver`/`Drop` handler treats `_dragItem == null` as "this drag
came from outside" and forwards to `ExternalDragOver` / `ExternalDrop`. There is no separate
event wiring for the lists — the same `AllowDrop` surfaces serve both.

`RootGrid` has its own `AllowDrop` + `RootGrid_DragOver` / `RootGrid_Drop`. It is not redundant:
an **empty launcher has no list big enough to hit**, since the empty-column `MinHeight` drop
target is skipped when the whole launcher is empty. Root handlers bail out when `_dragItem` is
set, and only ever run for drags that missed every list (the lists mark their events handled).

**Edit-mode-only, for a load-bearing reason.** The flyout dismisses on `Deactivated`, so outside
edit mode it is already gone by the time the user has switched to Explorer to pick something up.
`SuppressDismiss` is what makes it a drop target at all.

**Persist direction matters** — the same trap as everywhere else in this file:

| Drop lands on | Target collection | Persist via |
|---|---|---|
| A column or group list | `_columnLists[i]` / `group.Children` (derived) | `PersistFlyoutReorder()` |
| Flyout empty space | `_launcher.Items` (source of truth) | `PersistStructuralChange()` |

Using `PersistFlyoutReorder` for the root case would regenerate the flat list from column lists
built *before* the drop and silently discard the new items.

**Deferral and ordering.** `Drop` is synchronous but reading the payload is not, so it takes an
`e.GetDeferral()`. Two rules: resolve `GetDropIndex` **before** going async (`e.GetPosition` is
only meaningful while the event is on the stack), and complete the deferral as soon as the
payload is read — Explorer spins its drag loop until then. Website titles and icons are fetched
*after* the deferral completes and after the items are already visible, then persisted again;
`LauncherItem` is observable, so they fill in live.

**Payload mapping** (`DroppedItemFactory`):

| Dropped | Becomes |
|---|---|
| `.lnk` | The resolved target + its arguments, so it matches the same app picked from the item editor and survives the shortcut being deleted. Unresolvable shortcuts (Store apps, control-panel targets) keep the `.lnk`, which still shell-executes. |
| `.url` | A website item; the filename wins over the page `<title>`, since the user named it. |
| `.exe` / `.com` / `.bat` / `.cmd` | An application item, named from the exe's product name. |
| Folder | An application item launched through `explorer.exe`. |
| Any other file | An application item launched via `ShellExecute`. |
| Browser link or tab | A website item named from the page title (host until the fetch lands). |

**The Windows 11 Start Menu cannot be a drag source here — confirmed, not assumed.** Its data
package carries no `StorageItems` at all (not even for plain Win32 apps): the shell item lives in
the `Shell IDList Array` clipboard format, which WinUI 3's `DataPackageView` does not surface.
All that reaches `DataPackageView` is the app's *name* as text.

This is why `CanAccept` refuses text-only payloads. An earlier version accepted them, and the
result was the worst possible behaviour: Start Menu drags showed a confident "Add to launcher"
drop cursor and then silently added nothing. **A drop cursor is a promise — do not accept a
format you cannot turn into an item.** `CreateItemsAsync` still reads text as a *fallback* for a
source that advertises a web link but fails to produce it; text alone never triggers acceptance.

Dragging from `%AppData%\Microsoft\Windows\Start Menu\Programs` in an Explorer window works
fine — those are real `.lnk` files. Supporting the Start Menu itself needs the raw OLE
`IDataObject`, below the XAML layer.

Icon fetching goes through `FaviconService.FetchMissingItemIconsAsync` like every other import
path — see [icons.md](icons.md). Do not add a parallel fetch here.

## Visual feedback

`ShowInsertionIndicator(ListView, int)` sets an accent-coloured border on the target container.
In list mode this is a horizontal line; in icon-grid mode it's a vertical line **compensated by
subtracting the border's width from the container's own padding on the same side**, so the
container's outer dimensions stay constant and the grid doesn't reflow.
`ClearInsertionIndicator()` restores border, padding and margin from the saved `_lastIndicator*`
fields.

**Always clamp the compensation at zero** (`Math.Max(0, padding.Left - 3)`), never write a bare
negative `Thickness`. WinUI **throws** `ArgumentException` on a negative `Control.Padding` rather
than clamping it, and the icon-mode container style has no padding to give back — so the grid
branch used to kill the entire drag with an unhandled exception on every `DragOver` tick. Where
the container has no padding, the indicator costs 3px of reflow; that is the accepted trade.

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
8. **`_dragItem == null` means an external drag**, not a bug — don't "fix" a drag handler by
   early-returning on it without routing to the external-drop path.
9. **Anything that tracks realised containers in a field must be cleared on rebuild.** The
   `FlyoutWindow` instances are **permanent** (one per launcher in the static `_instances`
   map), so a per-container collection that only ever grows pins every container — and its
   native composition surface — for the life of the app. `_hoverWiredContainers` did exactly
   this and leaked gigabytes over a day (the periodic auto-sync rebuilds the flyout every few
   minutes). `RebuildColumnsPanel` now calls `ClearHoverWiring()` alongside
   `_editStyledContainers.Clear()` / `_loadedIconChildLists.Clear()`; detach the pointer
   handlers too, not just clear the set, so the WinRT event registrations release as well.
