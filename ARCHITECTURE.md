# Architecture — Little Launcher

## High-level flow

```
App.xaml  →  MainWindow (invisible, owns tray icon)
                ├── LauncherPanels (routes a tray click by Launcher.Kind)
                │     ├── FlyoutWindow (launcher popup — items)
                │     └── WebFlyoutWindow (WebView2 popup — a web page)
                └── SettingsWindow (WinUI 3 + NavigationView)
                      ├── HomePage
                      ├── LaunchersPage
                      ├── SyncPage
                      ├── SystemPage
                      └── AboutPage
```

## Flyout edit mode

All launcher item editing happens in the flyout. There is no in-settings items editor; the
former `LauncherItemsPage` was removed and its logic consolidated into `FlyoutWindow`.

A hover-revealed pencil in the flyout enters edit mode (`FlyoutWindow.EditMode.cs`), which:

- reveals a toolbar row (add item, add group, add/remove column via overflow, done),
- tints group containers and gives empty groups a drop target,
- enables drag-and-drop reordering (disabled outside edit mode),
- shows a pencil overlay on the hovered item/group,
- restricts item context menus to launcher-level entries outside edit mode,
- pins the flyout open so it cannot dismiss itself while editing.

**Geometry contract:** edit mode may grow the flyout''s *height* but must never change its
width or the size of any item or group. Width is derived arithmetically by `GetFlyoutWidth()`
from fixed column widths and is never measured, so chrome wider than the content is clipped —
hence icon-only toolbar buttons sized for the narrowest case (small-icon mode). Per-item
affordances are `Background`/`CornerRadius` only, which have no layout effect, so
`PackedIconPanel` cannot repack rows.

Height is normally computed arithmetically, because forcing a layout pass on a *hidden* WinUI
window is a hard crash (see the comment in `MeasureContentHeight`). Edit-mode toggles are the
exception: the window is visible then, so `ResizeForEditChrome()` measures `ContentStack`
directly and sizes to the real value.

**Source of truth:** `_launcher.Items` is authoritative and `_columnLists` is a derived view.
Structural edits (add/remove/rename) mutate the flat list and let the rebuild regenerate
columns — they must *not* call `SyncColumnsToFlatList()`, which clears `_launcher.Items` and
would resurrect anything just removed. Drag-and-drop is the opposite: it mutates `_columnLists`
and flushes back via `PersistFlyoutReorder()`.

Drag-and-drop supports cross-column and cross-group moves. All ListViews use
`CanDragItems="True"` with custom `DragOver`/`Drop` handlers — WinUI 3''s `CanReorderItems` is
intentionally avoided because it takes full internal control of drag events and cannot support
cross-collection moves. See [.claude/docs/drag-drop.md](.claude/docs/drag-drop.md).

The same handlers also accept drags from **outside** the app (`FlyoutWindow.ExternalDrop.cs`):
a drag arriving with no `_dragItem` set came from File Explorer, the desktop, the Start Menu or
a browser, and `DroppedItemFactory` maps its payload to launcher items. This is edit-mode-only
out of necessity rather than symmetry — the flyout dismisses on `Deactivated`, so only a pinned
flyout survives the user switching to Explorer to pick something up. The Windows 11 Start Menu
is *not* a usable drag source: its data package exposes only the app name as text, with the
shell item hidden in a clipboard format WinUI 3''s `DataPackageView` does not surface. Dragging
the same apps from their `.lnk` files under `Start Menu\Programs` in Explorer works.


## Web launchers

`Launcher.Kind` decides which window a tray click opens. `Windows/LauncherPanels.cs` is the only
place that resolves it — tray clicks, the companion exe's `PostMessage`, launcher deletion and
sync-driven removal all route through `Toggle` / `Dispose` / `SyncKind` / `WarmUp` rather than
naming a window class.

`WebFlyoutWindow` presents exactly like `FlyoutWindow` — borderless, always on top,
`WS_EX_TOOLWINDOW`, anchored above the taskbar, the same slide-and-fade, dismissed on focus loss —
but its **resource model is the inverse**, and that is the feature rather than an implementation
detail:

| | FlyoutWindow | WebFlyoutWindow |
|---|---|---|
| At startup | Warmed up and pre-rendered | Nothing exists |
| Dismissed | Parked off screen, fully resident | Parked, browser collapsed + suspended, memory target `Low` |
| Idle | Stays resident forever | Browser closed after `WebIdleUnloadMinutes` (default policy) |

The flyout keeps everything because its content is cheap to hold and expensive to re-rasterise. A
browser is the opposite: a dashboard of camera cards keeps decoding video and polling for as long as
its renderer lives, so it is built late and torn down early. Suspension (`TrySuspendAsync`) is
best-effort — it declines during media capture or downloads — so the idle unload, not the suspend,
is what makes "costs nothing while hidden" true.

Measured end to end: opening the flyout started six `msedgewebview2` processes, dismissing left them
suspended, and the idle timer returned the process count to exactly its pre-open baseline. Reopening
after that rebuilds from nothing and lands at the same anchored position.

A web launcher holds a list of bookmarks; the first is the page it opens and the rest show as a
browser-style bar along the bottom — several web launchers in one tray icon. See
[.claude/docs/web-launchers.md](.claude/docs/web-launchers.md) for the single-source rule that keeps
"which URL is showing" answerable in one place.

The flyout is resized by dragging its edges — invisible XAML grips, since a window with no
non-client area has no system sizing border to grab — and the dragged size is persisted onto the
launcher. Its header gear opens `LauncherSettingsWindow` under the same modal contract the item
flyout uses (pin open, drop always-on-top, restore activation on close).

The header also **maximizes the flyout to the monitor's work area, temporarily**: unlike a drag, it
writes nothing to the launcher and is dropped when the flyout is dismissed, so the next open is at
the configured size again. That is a different thing from page fullscreen, which the page enters,
which takes the whole monitor over the taskbar, and which hides the chrome for the duration.

The header's **"…" menu** carries the per-launcher options that are per-moment decisions — window
mode, whether it closes on focus loss, the pin, bookmarking the page on screen, the tab and address
bars, where it opens (including "where you last dragged it") and whether a resize is remembered —
with launcher settings one item below them. Reload on open and whether links go to the real browser
were here and are now Advanced-only: how a launcher treats a reload or a link is settled once, not
reconsidered while looking at it.

An **address bar** can sit under the header (`Launcher.WebShowAddressBar`, off by default). It is
switched on from that menu; there is no separate reveal-for-this-visit button, because one
affordance beats two that differ only in how long they last.

**A web launcher is a list of bookmarks whose first entry is the address it opens.** "A single web
address" is simply a launcher with one bookmark; add a second — in settings, or with the star in the
flyout — and they appear as a bar along the bottom. There is no mode switch between the two and no
setting for the bar: one page has nothing to pick between, two does. A launcher written before
the two merged is brought forward by `Launcher.MigrateWebModel`, which runs on load *and* on every
sync merge — the older build goes on sending the legacy fields for as long as another machine has
not been upgraded.

The bar behaves like a browser's and holds no state of its own
(`Windows/WebFlyoutWindow.Bookmarks.cs`): clicking a bookmark loads it in the tab in front, a
middle-click or a Shift/Ctrl-click opens it in a new one, clicking the bookmark for the page already
showing loads it again, and nothing in the bar is highlighted — what is showing is the tab's
business. Extra browsers are only ever made by a gesture that asks for one.

That bar is also **edited from itself**, not only from settings: a star at the end of the address
bar adds or removes whatever the address box shows — the page you are on, or an address you have
typed but not yet visited — and the "…" menu carries the same action, since the address bar is off
by default. Right-clicking a bookmark opens it here or in a new tab, renames it, points it somewhere
else, copies or opens its address, makes it the one that opens by default, moves it or removes it;
and dragging one along the bar reorders it, with an accent caret marking where it will land. Neither
editing nor deleting a bookmark disturbs the page on screen. Launcher settings still holds the full
list, which is the right shape for a launcher being set up rather than one being used. See
[.claude/docs/web-launchers.md](.claude/docs/web-launchers.md).

**Every browser a web launcher owns is a tab** (`Windows/WebFlyoutWindow.Tabs.cs`), and a link
asking for a new window becomes one instead of leaving for the real browser — using the browser's
own `NewWindowRequested.NewWindow`, so an OAuth popup keeps its opener and can hand its result back.
A strip between the header and the address bar switches between them, appearing on its own once
there are two. The load-bearing distinction is **who chose the address**: tabs the launcher owns
(its own page) may write its icon and be re-navigated when its settings change; a tab opened from a
link — or from a middle-clicked bookmark — is the user's own place and nothing configured may move
it. See [.claude/docs/web-launchers.md](.claude/docs/web-launchers.md).

Web launchers also get a **Start Menu shortcut each**, in a `Programs\Little Launcher\` group kept
in sync by `Services/StartMenuShortcutService`, so they can be opened from Start search, PowerToys
Command Palette or anything else that indexes the Start Menu. The shortcuts run the same command a
taskbar pin does, and deliberately carry **no AUMID** — an earlier version of them did, competed
with the companion's relaunch properties for pin identity, and produced duplicate pins.

A web launcher can also stop being a flyout altogether: `Launcher.WebRegularWindow` (Advanced) drops
always-on-top and dismiss-on-focus-loss and puts it in the taskbar and the task switcher, so its
pinned button shows the running indicator and closes it when clicked. **The switcher entry cannot be
declined** — taskbar eligibility is `WS_EX_TOOLWINDOW`, which governs both, and four ways round it
were measured and failed; see [.claude/docs/web-launchers.md](.claude/docs/web-launchers.md). That
is why the setting names a window kind rather than offering a taskbar-only toggle.

Per-launcher WebView2 profiles live in `%AppData%\LittleLauncher\WebProfiles\{launcherId}`, which is
what keeps a dashboard signed in across app restarts.

**Site permissions and notifications** are the host's job, not the browser's: WebView2 raises
`PermissionRequested` and `NotificationReceived` and does nothing further, so an unhandled
notification is dropped and an unhandled permission falls back to a prompt sized for a browser
window. `Windows/WebFlyoutWindow.Permissions.cs` answers both — camera, microphone, location and the
rest are asked for in a bar above the page and remembered per launcher profile, and page
notifications become Windows toasts that reopen the launcher they came from. Notifications need a
running page, which is exactly what the resource model above takes away, so granting them **offers**
to move the launcher to `KeepRunning` rather than changing it silently.

WebView2 reports only *non-persistent* notifications (`new Notification(...)`); one raised through
`ServiceWorkerRegistration.showNotification()` is created inside Chromium and never reaches the
host, so it is shown nowhere and dropped in silence. Since that persistent API is the one every
messaging site actually uses, a document-created script rewrites it in the page to the flavour the
host can see — tag replacement and `getNotifications()` included.

**Every toast has to be taken back off again**, and nothing here used to. Clicking one withdraws it,
which Windows does itself, and that was the only route — so opening a launcher any other way left
its toasts sitting there, including toasts for messages already read on a phone. A full Action
Center queue does *not* block new notifications (it is per app, FIFO, and a toast expires after
three days), but the queue being per *app* means every launcher splits one budget of twenty evicted
oldest-first, and Windows scores the whole app on how often its notifications get acted on before
offering to switch them off — all of them, since the launchers share one identity. So a toast is taken back off when the page closes its
notification (relayed out of the service worker as well as the page), when the launcher is next
brought to the front, when the launcher is deleted, and by Windows itself on the next reboot. Each
launcher's toasts are filed under a group of their own, so clearing one leaves the others and the
app's own notices alone, and each carries a header with its launcher's name so Notification Center
files them under the launcher rather than under the app. Windows does not clear a header's
notifications when it is clicked — its documentation says so outright — so the app does that itself.
A launcher is also held to five toasts at a time, since twenty is the whole app's budget and every
launcher spends from it.

A notification object is owned by the browser that raised it, and calling into one after that
browser has closed is a fail-fast that kills the app rather than throwing. The handler therefore
finishes with the notification before it builds the toast — `AppNotificationManager.Show()` pumps
the message loop, and the idle unload can run inside it — and tracked notifications are dropped when
the browser unloads.

See [.claude/docs/web-launchers.md](.claude/docs/web-launchers.md) for the WinUI WebView2 limits
worked around (no controller access, so zoom is CSS; focus-loss must be re-verified against
`GetForegroundWindow` because the browser's HWNDs are children of the window).

## Owned editor windows

Editing UI opened from the flyout uses standalone windows, not `ContentDialog`: a dialog
renders inside its host window''s content area and cannot overflow the HWND, and the flyout is
frequently narrower and shorter than the form.

| Window | Purpose |
|---|---|
| `ItemEditorWindow` | Add/edit a launcher item; returns `ItemEditorResult` (Cancelled/Saved/Deleted) |
| `TextPromptWindow` | Single-field prompt (group name) |
| `LauncherSettingsWindow` | Per-launcher settings; opened from both the flyout and the Launchers page |

All three share `WindowChrome` for the app icon and a themed custom title bar (a default WinUI
title bar does not follow `RequestedTheme`). The flyout **drops its always-on-top flag** while
one is open — owner relationship alone does not beat a topmost owner — and closes any open
editor when edit mode ends, so an orphaned window cannot commit into a launcher the user has
navigated away from.
## Launch modes

By default, launching the app opens the Settings window. Silent mode (tray icon only, no Settings window) is used for Windows startup and companion exe cold-starts:

| Scenario | How it launches | Settings window? |
|---|---|---|
| Install / update / Start Menu / double-click exe | No special args | Yes |
| Windows startup (unpackaged) | Registry Run key with `--silent` | No |
| Windows startup (MSIX) | StartupTask → `ExtendedActivationKind.StartupTask` | No |
| Companion exe cold-start | `LittleLauncher.exe --silent` | No |
| Second instance (app already running) | Signals first instance via `PostMessage` → shows Settings | Yes |

`SystemPage` manages startup through the install-type-specific mechanism: unpackaged builds write the HKCU Run key, packaged builds use the manifest `StartupTask`, and packaged startup cleanup removes any stale unpackaged Run entry so the Store app wins after install-type switches.

## Settings persistence

- `UserSettings` (the ViewModel) is an `ObservableObject` with `[ObservableProperty]` attributes.
- `SettingsManager` (fully static) serialises it to JSON at `%AppData%\LittleLauncher\settings.json`.
- On first load, migrates from legacy `settings.xml` (XmlSerializer) to `settings.json` (System.Text.Json), renaming the old file to `.bak`.
- On startup, `RestoreSettings()` deserialises and calls `CompleteInitialization()` to enable change handlers.
- `SaveSettings()` is called on settings window close and after SFTP download.

## Launcher item icons

`FaviconService.FetchMissingItemIconsAsync(items)` is the **single pipeline** for fetching launcher item icons. It iterates the items and, for each one missing a valid local icon, fetches a favicon (websites), extracts a shell icon (PWAs via `IShellItemImageFactory`), or extracts an exe icon (apps). All entry points use this one method:

| Trigger | Caller |
|---|---|
| App startup (missing icons on disk) | `MainWindow.FetchMissingIconsOnStartupAsync()` |
| SFTP sync download | `SftpSyncService.DownloadLaunchersAsync()` |
| File import (Launchers page card menu) | `LauncherBulkOps.ImportItemsAsync()` |
| Manual add/edit | `DoFetch()` in add/edit dialog (calls `FaviconService` directly for the single item) |
| PWA add | PWA combo selection handler (`FaviconService.GetBestPwaIconAsync()`: prefers a real site/manifest icon, rejects off-origin login redirects, then falls back to the installed shell icon) |

After bulk icon changes, callers invoke `FlyoutWindow.InvalidateItems()` so the flyout rebuilds its containers on the next toggle.

`FaviconService.RefreshStaleItemIconsAsync(items)` complements the missing-icon pipeline by **re-fetching auto-fetched icons** whose cached file is older than 7 days (`FaviconService.IconMaxAge`), so favicons, app icons, and PWA icons track upstream changes. Custom user-chosen icons are never touched (auto-fetched icons are identified by living in the `favicons` cache folder), and a failed fetch keeps the existing file. It runs at startup (inside `FetchMissingIconsOnStartupAsync`) and on a daily timer in `MainWindow` while the app stays resident in the tray. Because refreshed files keep the same path, item-icon `BitmapImage` loads use `BitmapCreateOptions.IgnoreImageCache` to bypass WinUI's per-URI decoded-image cache.

## Global launcher sync

All launchers sync between machines as one `launchers.json`. The transport is selected by
`UserSettings.SyncProvider` (`Models/SyncProviders.cs`):

| Value | Provider | Implementation | Auth |
|---|---|---|---|
| 0 | SSH / SFTP server (default) | `SftpSyncService` (SSH.NET) | key or password |
| 1 | OneDrive | `CloudSyncService` -> `OneDriveFileStore` (Microsoft Graph) | OAuth, system browser |
| 2 | Google Drive | `CloudSyncService` -> `GoogleDriveFileStore` (Drive v3) | OAuth, system browser |
| 3 | Network file share (UNC) | `FolderSyncService` | Windows |
| 4 | Any other folder | `FolderSyncService` | none |
| 5 | WebDAV (Nextcloud, ownCloud, NAS) | `CloudSyncService` -> `WebDavFileStore` | Basic auth, typed in |

`LauncherSyncService` is the single entry point and dispatches on the provider; nothing outside
`Services/` names a transport. It exposes `TestAsync()`, `UploadLaunchersAsync()`,
`DownloadLaunchersAsync(force:)`, `IsConfigured`, and `UsesCredentials`.

**OneDrive and Google Drive use their vendor APIs, not synced folders.** They implement the
narrow `ICloudFileStore` (download / upload / remote-modified-time) over the app-private folder
each vendor offers: Graph's app folder under `Files.ReadWrite.AppFolder`, and Drive's hidden
app-data folder under `drive.appdata`. Neither can see anything else in the user's storage. That
buys what a folder cannot — the service confirms the upload, no sync client need be installed,
there is no placeholder to hydrate, and the remote's modified time is readable. OneDrive is
**personal accounts only**; Microsoft has never extended app-folder permission to OneDrive for
Business.

**WebDAV** rides the same `ICloudFileStore` path, but its credentials are typed into the app
rather than obtained in a browser, so `SignInAsync` verifies a URL/username/password instead of
opening one. One implementation reaches every WebDAV server, and it costs no app registration,
consent screen, verification review or client secret — nothing a vendor can revoke. It uses
`PROPFIND` Depth 0 to verify, `HEAD` for the modified time, and pre-emptive Basic auth (some
servers answer an unauthenticated `PROPFIND` with 404 rather than 401, which would otherwise look
like a wrong path).

`OAuthPkceClient` implements Authorization Code + PKCE with a loopback redirect, shared by the two
OAuth providers — hand-rolled rather than pulling in MSAL and the Google SDK for one small file each.
Sign-in happens in the system browser, so the app never sees credentials. `ProtectedStore`
persists every sync credential — OAuth tokens and the WebDAV password — DPAPI-encrypted per
Windows user in `%AppData%\LittleLauncher\cloud-*.dat`, never in settings.json, which this very
feature uploads. `CloudSyncCredentials` holds the client
IDs; until they are filled in the two providers report themselves unconfigured.

Folder providers (3 and 4) cover everything else — Dropbox, Seafile, Syncthing, a USB stick, and
OneDrive for Business. There the sync client, not this app, moves the bytes, so a successful
upload means the file reached disk. `FolderSyncService` writes via `LauncherPayload.WriteAtomic`
(a temp file and a move — clients upload the instant a file changes, so a plain write can be
uploaded half-written), reads whole files on a background thread (they may be online-only
placeholders that block while hydrating), and probes write access with a throwaway file rather
than trusting `Directory.CreateDirectory`.

`CloudFolderService` locates OneDrive and Google Drive *folders* on the machine — now used only
by the shared-launcher dialog, since global sync reaches those two through their APIs.

`LauncherPayload` holds what the transports must not duplicate: the timestamped envelope format
(with a legacy plain-array fallback), the guard that stops an automatic download overwriting
newer local work, and the in-place merge into the live launcher collection. Only an explicit
user-initiated download passes `force: true`.

Full conventions, including how to add a transport: [.claude/docs/sync.md](.claude/docs/sync.md).

### Shared launcher sync

Individual launchers can be shared via files or per-launcher SFTP connections, **independently of `SyncProvider`** — `Launcher.SharedSyncMode` controls the transport: 0 = File, 1 = SFTP. File mode accepts any path, so a OneDrive, Google Drive or UNC folder works: both participants point at their own local copy of the same shared folder. The share dialog offers detected cloud roots as quick-fill buttons (buttons rather than a picker — a `FileSavePicker` raised from inside a `ContentDialog` is a modal-on-modal). File writes go through `LauncherPayload.WriteAtomic` for the same reason global folder sync does.
- **Owner** (`IsSharedOwner = true`): `ShareLauncherAsync()` pushes the launcher's items as `List<LauncherItem>` JSON.
- **Subscriber** (`IsSharedOwner = false`): `SyncSharedLauncherAsync()` pulls items (read-only).
- `VerifySharedLauncherAsync()` — validates the file/remote exists and is parseable (used before subscribing).
- `SyncAllSharedLaunchersAsync()` — batch syncs all shared launchers (owners push, subscribers pull). File-mode always syncs; SFTP-mode skips launchers without an auto-detectable SSH key.

The sharing UI lives in `LaunchersPage.xaml.cs`: "Share" button on unshared launcher cards, "Sync" and "Settings" buttons on shared cards, "Shared"/"Subscribed" badges, "Add Shared Launcher" subscribe dialog (with File/SFTP mode picker), and "Stop Sharing" via the share dialog's secondary button.

`AutoSyncService` manages automatic sync triggers, and is transport-agnostic — everything goes
through `LauncherSyncService`:
- Downloads launchers on startup, then syncs shared launchers.
- Debounced upload (3 s) when items change.
- Periodic download on a configurable interval, followed by shared launcher sync.
- Automatic downloads are skipped while local launcher item edits are pending upload, so a periodic pull cannot overwrite newer flyout/editor changes before the debounced upload completes.
- Every trigger is gated on the auto-sync toggle **and** `LauncherSyncService.IsConfigured`, never on `SftpHost` — which is meaningless under a folder provider.

The SFTP transport supports both private-key (`PrivateKeyFile`) and password-based
authentication. Folder providers need no credentials from the app: OneDrive, Google Drive and
Windows have already authenticated the folder before it is visible.

## Theme system

`ThemeManager` controls the app theme via WinUI 3's `ElementTheme` system:
- Sets `RequestedTheme` on the root `FrameworkElement` of each window.
- `IsDarkTheme()` reads the system foreground colour from a cached `UISettings` instance to detect light/dark mode.
- Theme 0 = system default, 1 = Light, 2 = Dark.

## Backdrop

- **SettingsWindow** uses `MicaBackdrop` (WinUI 3 built-in).
- **FlyoutWindow** uses a transparent backdrop for seamless integration.

## FlyoutWindow

The flyout popup dismisses when focus is lost via the WinUI `Activated` event (`Deactivated` state). It also toggles closed when the user clicks the same tray icon or pinned taskbar shortcut while that launcher's flyout is already open. It uses `WS_EX_TOOLWINDOW` to stay out of Alt-Tab and `OverlappedPresenter.CreateForContextMenu()` for borderless always-on-top presentation. When the `UserSettings.FlyoutAnimationsEnabled` setting is on, the fully rendered flyout window is moved a short distance from its anchored edge with eased `SetWindowPos` steps, so the whole popup slides as one surface instead of revealing its frame and contents in stages.

**Easing must be checked in pixels, not curve shape.** The slide covers only `SlideDistanceDip` (~54px at 150% scale), which is short enough that an aggressive curve quantises to zero movement. The exit used a pure cubic ease-in and its first five frames (~30ms) all rounded to the same pixel, so the close sat still and then lurched away at ~7px/frame before cutting out — choppy, even though the animation loop was ticking cleanly at ~8ms. `EaseOutExit` adds a linear floor (`0.35t + 0.65t²`) so every frame advances at least a pixel while still accelerating away. Anything that changes the easing, duration, or slide distance here should be verified against the **per-frame pixel deltas**.

**The hide also fades** (`SetFadeAlpha` / `ClearFade`). Sliding alone still ended with a fully opaque window that simply stopped existing, which reads as a snap however smooth the travel is. The fade uses per-window alpha (`WS_EX_LAYERED` + `LWA_ALPHA`) rather than XAML opacity, because the acrylic comes from a `SystemBackdrop` that sits behind the XAML tree — `RootGrid.Opacity` would fade the content and leave the backdrop pane as a solid rectangle. It completes at `FadeOutCompleteAt` (0.8) of the hide duration rather than at the very end, so the flyout is **fully transparent before it is parked** and never winks out mid-fade.

Alpha is window-level state that survives parking, so every path back on screen calls `ClearFade` first (`ParkOffScreen`, `ShowAnimated`, `ShowWithoutAnimation`) — otherwise a re-opened flyout comes back invisible.

**Warm-up**: `FlyoutWindow.WarmUp()` creates one window per launcher at startup (and again whenever launchers change) so no flyout is built on the click that opens it. Creating a window is not enough on its own — WinUI only draws a window that has actually been visible, so each warmed-up flyout is parked outside the virtual screen with `SWP_SHOWWINDOW` and given `PreRenderDurationMs` to compose its first frame. Without that first frame the DWM has nothing to present and the flyout's opening frames are a black rectangle. Every path that shows or hides a flyout calls `EndPreRender()` first, so a window that is still warming up is never mistaken for one that is open on screen.

**Dismissal parks, it does not hide** (`ParkOffScreen`). The flyout used to be dismissed with `ShowWindow(SW_HIDE)`, which lets WinUI release the window's composition surfaces. The next open then had to re-rasterise the entire visual tree — every text run and every item icon — before anything could be presented. Measured with timed screen captures, that took ~100ms on a *cold* open, during which the window was already on screen and sliding: the flyout slid in as an empty rectangle and all the items appeared at once near the end of the slide. Re-opening within a second or so looked fine because the surfaces were still resident, which is why this only ever showed up in normal use, where opens are seconds or minutes apart. Keeping the window visible but parked off the virtual screen keeps those surfaces alive, so showing it is a pure move and the first on-screen frame is already fully painted — verified at ~99ms after a 35s idle, painted for the whole slide, with no added latency.

Because the window stays visible in the Win32 sense whenever it is dismissed, **`_isOpen` is what "the flyout is open" means** — every former `WS_VISIBLE` check (in `Toggle`, `HideFlyout`, `ShowAnimated`, `ResizeIfVisible`, `ShowInEditMode`, `RestoreFlyoutActivation`) now tests that flag instead. `WS_EX_TOOLWINDOW` keeps the parked windows out of Alt-Tab and the taskbar.

For pinned taskbar launches, `MainWindow` now tries to resolve the actual taskbar button bounds via UI Automation before showing the flyout. When that succeeds, the flyout anchors to the center of the launcher button instead of the companion exe's fallback cursor coordinates, keeping both mouse and touch launches centered directly over the icon.

**Direct reordering** (edit mode only): FlyoutWindow uses a custom drag-drop implementation instead of WinUI `CanReorderItems`. Each column is rendered as a drag-enabled `ListView`, and grouped sections render nested child `ListView`s. In icon mode, consecutive ungrouped items are wrapped into synthetic groups for display so icon tiles can still be reordered in a wrapping `ItemsWrapGrid`. Drops use custom insertion indicators (horizontal for list sections, vertical for icon grids), then sync the per-column view back into the launcher's flat `Items` collection, save settings, notify auto-sync, and invalidate tray/flyout surfaces.

**Multi-column & multi-view layout**: The flyout renders items into a horizontal `ColumnsPanel` (a `StackPanel`). Each `LauncherItem` with `IsColumnBreak = true` starts a new column. The display mode is controlled by `Launcher.ViewMode`:
- **Icon view** (ViewMode = 0, default): Each column is a `GridView` of grouped sections whose top-level layout is handled by the custom `PackedIconPanel`. Real groups render a heading plus a nested icon-grid child `ListView`, while consecutive ungrouped items are wrapped into synthetic groups so they render in the same wrapping grid surface. `Launcher.IconModeIconsPerRow` controls the maximum icons shown across each row (default 3, configurable from 1 to 12). Top-level groups use their visible child count as a width span so narrower groups can share a row beside wider groups, while the packed panel keeps row heights based on the measured group cards instead of fixed slot heights. Dragging near the flyout's left or right edge snaps the layout wider or narrower by whole icon columns without opening settings.
- **List view** (ViewMode = 1): Each column is a drag-enabled `ListView` of top-level items and groups. Real groups render a heading plus a nested child `ListView`, so items can be dragged within groups, out to the top level, or between columns while keeping the flyout visuals close to the editor.

`RebuildColumnsPanel()` rebuilds all columns (icon grid or ListView) from scratch whenever items change. Window width scales per column: 175 px for list view, or a dynamic icon-mode width derived from the configured icons-per-row value.

**Right-click context menu**: Right-clicking empty space in the flyout shows a `ContextFlyout` with launcher/settings shortcuts. Right-clicking an item opens an item menu with Move up/down, Move to…, Edit, and Remove actions, while in edit mode. Outside edit mode the item menu shows only launcher-level entries.

## Companion exe (`LauncherShortcut`)

`LittleLauncherFlyout.exe` is a tiny companion binary pinned to the taskbar. Clicking it sends a `PostMessage` to the main app to show the flyout, then exits. Because it launches on every click, startup latency is critical:

- **Release builds** use Native AOT (`dotnet publish`) producing a single ~1.6 MB native binary with no .NET runtime dependency.
- **Debug builds** copy the framework-dependent output for fast iteration (startup is slower but build is faster).
- **Never add heavy dependencies** (NuGet packages, large frameworks) to the `LauncherShortcut` project — it must remain a minimal P/Invoke-only program.
- **Never run expensive work** (icon regeneration, shell notifications, file I/O) synchronously in the main app's `_wmShowFlyout` WndProc handler — it blocks the flyout from appearing.

### Companion exe deployment

At startup, `EnsureFlyoutShortcut()` copies the companion exe to the external helper directory returned by `GetPhysicalAppDataDir()` and writes a `main-exe-path.txt` breadcrumb alongside it so the helper can launch the main app with `--silent` if `FindWindow` fails. In packaged builds it also mirrors the helper into the real shared `%AppData%\\LittleLauncher\\` path so old unpackaged launcher pins stop cold-starting a stale debug or portable build after an install-type switch. No Start Menu flyout shortcut is created anymore.

## Distribution

Little Launcher ships through exactly two channels: the **portable zip** attached to each GitHub release, and the **Microsoft Store MSIX**. A per-user WiX MSI was a third until v1.35.1 and was retired — it re-implemented, less well, what the Store already does (managed install, silent update, clean uninstall), and the portable zip covers everyone who wants to stay off the Store. Only the Store build installs its own updates; the portable one checks GitHub and links to the release page. See [.claude/docs/installer.md](.claude/docs/installer.md).

## MSIX packaging

`LittleLauncherMSIX/build-msix.ps1` produces an MSIX package for Microsoft Store distribution or sideloading. Key details:

- **Publishes with `-p:WindowsPackageType=MSIX`** to suppress the unpackaged-only auto-bootstrapper (`MICROSOFT_WINDOWSAPPSDK_BOOTSTRAP_AUTO_INITIALIZE`), which fails in a packaged context.
- **Declares `<PackageDependency>`** on `Microsoft.WindowsAppRuntime.1.8` so the framework package provides WinRT activation factories.
- **Copies compiled XAML (.xbf)** files manually from the RID build directory to the layout — `dotnet publish` omits them.
- **Version and architecture** are stamped from `Directory.Build.props` into the manifest at build time (`VERSION_PLACEHOLDER`, `ARCH_PLACEHOLDER`).
- **Image assets** in `LittleLauncherMSIX/Images/` use standard MRT naming qualifiers (e.g. `.scale-200.`, `.targetsize-48.`) and are indexed into `resources.pri` by `makepri`.
- **Companion exe** is deployed at startup to the external helper directory, and packaged builds also mirror it into the shared raw `%AppData%\\LittleLauncher\\` path for backward compatibility with old launcher pins. See "Companion exe" section above.
- **`-NoSign` flag** skips all signing for Store uploads (Microsoft re-signs during ingestion). Without `-NoSign`, the script signs with a self-signed dev cert or a trusted PFX.
- **Update checks run in MSIX builds too**, against the Store rather than GitHub Releases, and this is the only build that can update *itself*: Home/About offer **Download & Install** and apply a Store update in place, where a portable copy gets **View Release** and a link. About also offers a restart when no update is pending, since a staged one cannot apply while the tray process is alive. The **startup update toast** stays unpackaged-only — Store updates otherwise install silently, so there is nothing to interrupt anyone about. The manifest declares a `windows.toastNotificationActivation` COM activator regardless, so `AppNotificationManager` registers for both install types and packaged builds can still raise one-time upgrade notices. See [.claude/docs/installer.md](.claude/docs/installer.md) for the Store update flow and the version-reporting trap it works around.
