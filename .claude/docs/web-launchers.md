> **Scope:** Use when working on web launchers — the WebView2 flyout, its resource policy, or the
> per-launcher web settings. Covers why the browser is torn down rather than kept warm, the
> WebView2 APIs that make that work, and the WinUI-specific limits worked around here.
> **Governs:** `**/WebFlyoutWindow.cs`, `**/LauncherPanels.cs`, the `Web*` properties on `Models/Launcher.cs`.

# Web Launchers

A launcher whose `Kind` is `LauncherKinds.Web` opens `WebFlyoutWindow` instead of `FlyoutWindow`:
a tray-anchored WebView2 on `Launcher.WebUrl`, or on a bar of bookmarks (see below). The motivating case is a Home Assistant dashboard —
camera cards and an agenda, one tray click away, and costing nothing while it is not being looked at.

## Routing — never branch on kind at the call site

`Windows/LauncherPanels.cs` is the only place that decides which window a launcher opens. Tray
clicks, the companion exe's `PostMessage`, launcher deletion and sync-driven removal all go through
`LauncherPanels.Toggle` / `.Dispose` / `.WarmUp`. Add a new entry point there, not another
`FlyoutWindow.Toggle` call.

`LauncherPanels.SyncKind` runs from `MainWindow.RefreshTrayIcons` and destroys the window a
launcher no longer uses — without it, a launcher switched from items to web keeps a warmed-up
flyout (or a loaded browser) it can never show again.

## The resource contract

This is the whole point of the feature, so it is the thing not to regress.

| State | What exists | Cost |
|---|---|---|
| Never opened since app start | No window, no browser | Nothing |
| Open | Window + browser, visible | A browser tab's worth |
| Dismissed | Window parked off screen, browser collapsed + suspended, `MemoryUsageTargetLevel = Low` | Minimal — rendering, video and timers stop |
| Dismissed past `WebIdleUnloadMinutes` (default policy) | Window only | Nothing |

- **Collapsing the control is load-bearing, not cosmetic.** WebView2 refuses to suspend a visible
  browser, and the WinUI `WebView2` control drives `CoreWebView2Controller.IsVisible` from its XAML
  `Visibility`. Collapsing is therefore both what stops the rendering and what makes
  `TrySuspendAsync` legal.
- **Suspension is best-effort; the unload is the guarantee.** `TrySuspendAsync` declines in cases
  like active media capture or a download in flight. Treat a `false` return as normal (it is logged
  at debug) — the idle timer is what makes the promise true, because after it fires there is no
  browser process left to consume anything.
- **`WarmUp` deliberately skips web launchers.** The flyout is pre-rendered at startup because its
  content is expensive to re-rasterise; doing that for a browser would boot a renderer for a page
  the user may never open, which is exactly the cost this feature exists to avoid.
- The **window** does outlive a dismissal, parked off the virtual screen like the flyout. An empty
  WinUI window costs nothing, and it keeps the reopen a pure move.

## WinUI WebView2 limits worked around here

- **No controller access.** WinUI's `WebView2` never exposes `CoreWebView2Controller`, so
  `ZoomFactor` — which lives there — is unavailable, unlike in the WPF and WinForms wrappers. Zoom
  is applied as a CSS `zoom` on the document element and **re-applied on every
  `NavigationCompleted`**, because it lives in the document rather than on the control.
- **Escape only works while the XAML tree has focus.** Once the page takes focus the browser owns
  the key, which is why the header keeps a close button. Both the `KeyboardAccelerator` and the
  `WM_KEYDOWN` subclass are best-effort.
- **Focus loss must be verified, not trusted.** The browser's HWNDs are children of the window, so
  clicking into the page raises `Deactivated` without the user having gone anywhere. The handler
  re-checks `GetForegroundWindow()` against the window and `IsChild` on the next dispatcher turn,
  and only then dismisses. **Every dismissal condition is re-checked in that callback**, not just
  the ones read when the event fired — pin, modal and resize state can all change in the turn
  between deciding and acting, and a condition evaluated at one moment and acted on at another is
  how a pinned flyout gets dismissed anyway. There is a standing report of exactly that which
  neither of us could reproduce afterwards; this closes the only gap the code actually had.
- **The first show of a window's life is not animated.** WinUI has never drawn it, so it would
  present its extended frame — a black rectangle — for the frames XAML takes to paint. The flyout
  hides this by pre-rendering off screen at startup; a web launcher must not, so it takes the plain
  show once and slides on every open after that.

## Manual resize on a borderless window

The flyout has **no non-client area at all**, which means there is no system sizing border to
drag — and `WM_NCHITTEST` never reaches this window for a point over the XAML island or the hosted
browser, so the usual `WS_THICKFRAME` + `WM_NCCALCSIZE` trick has nothing to hit-test either.

Resizing is therefore done in XAML: `AddResizeGrips` overlays four transparent edge strips and four
corner squares (`Grid.RowSpan` across the whole window), each carrying its `ResizeEdges` in `Tag`.
A press captures the pointer and records the cursor and window rects in *screen* coordinates; each
move recomputes the rect from the raw cursor delta and calls `SetWindowPos`. Two details matter:

- **A transparent `Background` is required.** A `null` background is not hit-testable, so the grips
  would be invisible to the pointer as well as to the eye.
- **The content is inset by `GripThickness`**, so the browser never physically occupies the strip
  the grips need. They sit above it in z-order regardless, but a hosted browser is not an ordinary
  XAML sibling, and disjoint rectangles make the edges grabbable no matter how WebView2 routes its
  own input.

The size is persisted **only on pointer release**, never from the placement code: every open clamps
the flyout to the work area, and writing that back would silently shrink the launcher the first
time it opened on a smaller screen.

`_isResizing` pins the flyout open, exactly like `_isModalOpen` — a drag that strays outside the
window must not read as "the user clicked elsewhere".

## Opening launcher settings from the flyout

The header's gear runs `OpenLauncherSettingsAsync`, which follows the item flyout's `RunModalAsync`
contract: pin the flyout (`_isModalOpen`), **drop always-on-top** — owner relationship alone does
not beat a topmost owner, so the settings window would open behind the flyout that spawned it —
then restore both, re-apply the launcher's settings, and hand activation back so the flyout can
dismiss itself again.

Everything after the `await` must tolerate the window being gone: switching `Kind` to Shortcuts in
that window disposes this very flyout.

## The header

Back (left, where a browser puts it — it acts on the page, not the flyout) and, on the right,
pin / launcher settings / reload / open in browser / close. Back is driven by `HistoryChanged`
rather than `NavigationCompleted`: a dashboard is usually a single-page app, so most of its
navigation is history pushed by script with no document load to hang the update off.

**Passkeys work** — verified against a real sign-in. WebAuthn with a Windows Hello platform
authenticator completes inside the flyout; credentials belong to the OS, so the per-launcher user
data folder does not isolate them.

## Discoverability

Two mechanisms, doing different jobs:

- **`+ Add Web Launcher` on the Launchers page** — permanent, and the one that matters long term.
  The kind is otherwise only discoverable by creating an ordinary launcher and noticing the Type
  dropdown, which nobody does unprompted. It creates the launcher and opens its settings with the
  caret already in the address field, which is the only thing left to supply.
- **A one-time Home page notice for upgraders** (`UserSettings.ShowWebLauncherNotice`, raised by
  `SettingsManager.RaiseUpgradeNotices`). Store updates install silently, so this is the only
  channel that reaches those users; a Windows toast would not, because `AppNotificationManager`
  is registered for unpackaged builds only.

## Bookmark bars

A web launcher shows either **one address** or **a bar of bookmarks**, chosen explicitly with
`Launcher.WebUseBookmarks`. It is a stored choice, not one inferred from how many bookmarks exist:
inferring it meant adding a second bookmark silently changed what the tray icon did, and deleting
one changed it back.

In bar mode the flyout opens as just the bar — a strip along the bottom, browser-style: 16px icon,
label beside it, centred, scrolling horizontally when there are more than fit. Clicking a bookmark
expands the flyout onto that page; clicking the one already showing collapses it again.

| State | Window | Browser |
|---|---|---|
| Collapsed | Bar height only | None — nothing loads until a bookmark is picked |
| Expanded | Full configured height | Loaded, showing the active bookmark |

**What opens on show**, in priority order: the bookmark that was open when it was last dismissed,
then `WebDefaultBookmarkUrl`, then nothing (just the bar). Collapsing clears the remembered
bookmark — that is an explicit "close this page", and reopening onto something just closed is the
wrong kind of memory. The default is stored as a URL rather than an index, so reordering the bar
cannot silently change which page opens, and empty naturally means "none".

Collapsing is treated as hidden for the browser: it gets the same `ApplyHiddenPolicy` as a
dismissal rather than being left running behind a bar.

### `CurrentTargetUrl` is the only answer to "which URL"

Bar mode added a second possible answer — the active bookmark — beside `Launcher.WebUrl`, and
**three** separate places navigate. Two of them were missed when the bar was added, and both
produced the same confusing pair of symptoms: the wrong page opened, *and* the bookmark that was
clicked took the wrong page's icon, because the arriving page's favicon is adopted onto whatever
bookmark is active.

- `CreateWebViewAsync` — the first click after the browser has been torn down
- `PrepareContentAsync` — every subsequent click
- `ApplyLauncherChanges` — anything that touches the launcher, **including a bookmark's own
  favicon fetch completing**, which is how it recurred with no user action at all

All three now call `CurrentTargetUrl()`. An empty result means the bar is collapsed with nothing
open, which is not an instruction to navigate anywhere. If a fourth navigation path is ever added,
it must use the same helper.

Icon adoption independently checks that the loaded page's host matches the bookmark's before
writing (`SameHost`). A wrong page is obvious; a wrong icon persists and looks like data
corruption.

### Geometry: reveal, do not animate

Expansion **snaps**. Two attempts at animating it were removed, for a reason worth not
rediscovering: a window hosting a browser cannot be smoothly resized frame by frame, because the
window frame, the XAML island's surface and WebView2's composition surface are resized by
different parts of the system and do not land on the same frame. The content lags the frame and
appears to drift downwards while the window grows upwards, however the geometry is eased.

Two things make the snap look deliberate:

- `ApplyRootAnchor` pins the root grid to the anchored edge (`Bottom`, or `Top` for a flyout under
  a top taskbar) and gives it a **fixed height equal to the expanded size**. The layout is then
  computed once and never reflows; the window simply uncovers more of it. Re-applied after a
  manual resize, which changes that height.
- The anchored edge never moves, so the bar stays exactly under the pointer that clicked it.

The open/close slide is untouched — that moves a fixed-size window, which has none of this
problem.

### Warm-up

Bar-mode launchers **are** warmed up (`WebFlyoutWindow.WarmUp`), parked off screen at bar height
so WinUI composes their first frame before they are ever shown — otherwise the first open showed
buttons measuring and favicons decoding on screen. This does not weaken the resource promise: what
is built is a strip of XAML, and no browser is created until a bookmark is clicked.

Single-address web launchers are still excluded — their first frame *is* the page, so there would
be nothing to pre-render but an empty window.

The bar is also only rebuilt when the bookmarks actually change, keyed on a signature of their
names, URLs and icon paths. Rebuilding per open threw away laid-out buttons and decoded icons
every time.

### Resuming shows a stale frame

Suspending does not discard the rendered page: resuming puts the last painted frame straight back
on screen. With **Reload On Open** set, that meant watching the old page appear and then be
replaced. When a reload or navigation is queued the browser is therefore kept hidden and the
loading state shown until `NavigationCompleted` — hiding stops rendering, not loading, so the
navigation still runs. Failed navigations, `ProcessFailed` and unloading all clear that pending
reveal, or the flyout would sit on a spinner over a hidden page.

With nothing queued the page is shown immediately. A dashboard repainting a moment later is the
page updating itself, not a stale frame.

## Settings layout

Launcher settings show only **Web Address** and **Flyout Size** for a web launcher. Zoom, When
Hidden, Unload After, Reload On Open, Pin Open and Browsing Data live in a collapsed **Advanced**
expander: they tune a launcher that already works, and putting all eight fields on one surface made
the common case (paste a URL, pick a size) read as a form to fill in. Pin is safe to demote twice
over — the flyout's own header has a pin button.

New per-launcher web settings should default to Advanced unless a launcher is unusable without
them.

**The window does not resize when Advanced expands.** Growing it to fit was tried and rejected:
the window sizes to its form once on `Loaded`, so re-sizing afterwards lands a beat *after* the
expander has already animated and reads as a jolt. The form already lives in a `ScrollViewer`
with the button row pinned below it, so expanding simply scrolls.

**The page's own icon is the default tray icon.** `CoreWebView2.FaviconChanged` gives whatever the
page declares, which is the only source that works for a dashboard behind a login —
`FaviconService` fetches over plain HTTP with no session and gets a redirect instead of an icon.
Both that provisional fetch and the page icon write the same managed path
(`web-favicon-{launcherId}.png`), which is what lets `MayAdoptPageIcon` tell an icon *we* adopted
from one the user chose: the former is upgraded freely, the latter is never touched.

**That has to be visible in the form, or it reads as the opposite.** The address rows are laid out
*before* the icon row for exactly this reason — asked the other way round, the form made the user
choose an icon for a page it had not been told about yet, which presents an automatic behaviour as
a required decision. The icon row also names the state rather than the mode: while
`MayAdoptPageIcon` holds, the button shows the adopted image (or a globe before anything has
loaded) labelled **From the page**, not `Composite` — which composes item icons a web launcher
hasn't got — or `Custom`, which names a file the user never picked. The custom-path row stays
hidden until they actually choose one.

**The address can be chosen from browser bookmarks.** `Pages/BookmarkPicker.PickAsync` shows a
searchable, flattened list of the bookmarks in a chosen browser profile — a dashboard URL is
miserable to type from memory and is invariably already bookmarked. It shares its reading half
with the multi-select import flow (`BookmarkImport.ReadBookmarks` / `.Flatten`,
`BrowserCatalog`) but not its presentation: that flow is a tree you browse to tick a set, this one
answers "which single page?" for a user who already knows and just needs to find it. Search is
term-wise across name, URL *and* folder path, because titles and URLs rarely order the words the
way the user remembers them.

## Profiles

Each web launcher gets `%AppData%\LittleLauncher\WebProfiles\{launcherId}` as its WebView2 user-data
folder (via `MainWindow.GetPhysicalAppDataDir()`, so it survives MSIX VFS redirection). That is what
keeps a dashboard signed in across restarts, and keeps two launchers signed in as different users.

**Cookies and sessions are therefore per launcher, not per bookmark.** Every bookmark in one
launcher's bar shares that launcher's profile — they are tabs of the same browser, so signing in
on one is a sign-in for all of them, and a site that both use sees a single session. Two launchers
share nothing by default: separate cookies, storage and cache, which is what makes two accounts on
the same site possible. Nothing is shared with the user's real Edge or Chrome either, so a launcher
starts signed out even where the desktop browser is signed in.

### Where it opens

Placement resolves in one order, in `CalculatePlacement`, and each step only runs because the one
above it did not apply:

| Rank | Source | Applies when |
|---|---|---|
| 1 | `WebFlyoutPosition` | `WebRememberPosition` is on *and* the flyout has been dragged |
| 2 | `WebAnchor` (`WebAnchors`) | A corner, edge or centre has been chosen |
| 3 | The tray icon | The default — above a bottom taskbar, below a top one |

So with **Remember Position** on, the anchor decides the *first* open and nothing after it; with it
off, the anchor decides every open. That ranking is why **changing the anchor clears
`WebFlyoutPosition`** — otherwise picking a corner on a flyout that had been dragged would appear
to do nothing at all, the remembered position silently outranking the choice just made. The row's
subtitle states which of the two behaviours is currently in force.

An anchored flyout is placed on the **work area of the monitor whose tray icon was clicked**, not
the primary monitor: a corner should mean a corner of the screen being worked on. It also slides in
from the nearer edge — down from a top anchor, up from anything else — rather than travelling
across the screen from wherever the tray happens to be.

### The shared profile

`Launcher.WebSharedProfile` (Advanced → **Sign-ins**) points a launcher at `WebProfiles\Shared`
instead of its own folder, pooling it with every other launcher that sets it. Isolation stays the
default — it is the setting that cannot surprise anyone, and it is what shipped — but several
launchers onto one system otherwise means signing in to that system once per launcher, and again
every time a session expires.

`GetUserDataFolder(Launcher)` is the resolution point; the `(string launcherId)` overload it calls
still names a private folder and is what makes the shared name safe (launcher ids are GUIDs, so
nothing private can be called `Shared`).

Three things this has to get right:

- **Several environments, one folder.** WebView2 allows it *within a process* only when the
  environments are created with identical options. Ours are (`new CoreWebView2EnvironmentOptions()`
  with no browser-executable folder), so the shared profile works — but anything added to those
  options later has to be added for every launcher, or launchers on the shared profile start
  failing to initialise while private ones carry on fine.
- **Switching profile needs the browser dropped.** The folder binds when the environment is
  created, so a launcher moved on or off the shared profile keeps using the old one until
  `ReloadProfile` discards the browser. It rebuilds only a panel that is actually on screen with
  something loaded — starting a browser for a dismissed panel, or for a collapsed bookmark bar,
  would undo the resource promise.
- **Clearing is per profile, not per launcher.** `ClearBrowsingDataAsync` resolves
  `ProfileSiblings` first: on the shared profile one `ClearBrowsingDataAsync` on any live core
  clears the lot (they are views onto one profile, not copies), and the disk path has to dispose
  every sibling panel before deleting or the delete hits a locked file. The row's subtitle says
  which of the two is about to happen.

"Clear browsing data" in launcher settings calls `CoreWebView2.Profile.ClearBrowsingDataAsync()`
when the flyout is loaded, and deletes the folder when it is not.

## Settings that must default to zero / false

`settings.json` is written with `DefaultIgnoreCondition = WhenWritingDefault`, so a property holding
the CLR default is **omitted from the file**. Two consequences shape these properties:

- Numeric settings (`WebFlyoutWidth`, `WebFlyoutHeight`, `WebZoomPercent`, `WebIdleUnloadMinutes`)
  store `0` for "unset" and resolve their real defaults through the `Resolved*` properties on
  `Launcher`. Do not put the default in the field initialiser.
- Booleans must be phrased so `false` is the default behaviour — hence `WebPinFlyout` rather than an
  auto-hide flag. A bool defaulting to `true` **cannot be turned off**: `false` is dropped on write
  and the initialiser puts it back on load.
- `WebHiddenPolicies.UnloadWhenIdle` is `0` for the same reason: the cheapest behaviour is the one
  that survives being omitted.

## Vocabulary

The window is a *flyout*, not a *panel* — matching `FlyoutWindow`, the `Flyout*` settings, and the
sibling Persistent app it takes its conventions from (`AppFlyout`, `FlyoutWidth`/`FlyoutHeight`,
`PinFlyout`, `SuspendWebViewAsync`). Keep new names in that family.
