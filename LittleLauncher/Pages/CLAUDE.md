# LittleLauncher/Pages

Settings pages are WinUI 3 `Page` objects navigated via `NavigationView`. Follow the XAML conventions for all `.xaml` here.

@../../.claude/docs/xaml.md

**There is no item editor here.** All launcher item editing lives in the flyout's edit mode (`Windows/FlyoutWindow.EditMode.cs`); the former `LauncherItemsPage` was removed. `LaunchersPage` keeps only launcher-level concerns — cards, sharing, and the per-launcher bulk operations in `LauncherBulkOps` (export, import, bookmark import). Per-launcher settings open in `LauncherSettingsWindow`, shared with the flyout.

`LaunchersPage` drives per-launcher tray/pin icons. When editing it, follow the icon system conventions: [.claude/docs/icons.md](../../.claude/docs/icons.md).

## Bookmark UI

Two surfaces read browser bookmarks, sharing `BookmarkImport.ReadBookmarks` / `.Flatten` and
`BrowserCatalog` but nothing of their presentation:

| | `LauncherBulkOps.ImportBookmarksAsync` | `BookmarkPicker.PickAsync` |
|---|---|---|
| Answers | "which of these do I want?" | "where is the one I already know?" |
| Shape | folder tree, multi-select | flat list, single-select |
| Search | filters the tree, keeping folders with a matching descendant | filters the flat list |

**In the tree selector, selection lives in a `HashSet<BookmarkNode>`, not in the `TreeView`.**
Filtering rebuilds the nodes, so a selection held only by the control is destroyed on the next
keystroke — tick three bookmarks, search for something else, and those three never get imported.
The tree is authoritative only for the rows it is currently showing; `SyncChosenFromTree` merges
that back without touching anything filtered out. Verified: select 4 matches of one search, switch
the search, select 1 more, clear the search → 5 selected.
