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
  and only then dismisses.
- **A dialog the page raises is *owned*, not a child** — and `IsChild` misses it entirely. A file
  picker (uploading to Discord or WhatsApp), the Windows Security passkey prompt and a print dialog
  are all top-level windows owned by this one, in another process, so the flyout used to vanish the
  instant the picker appeared and take the upload with it. `IsForegroundStillOurs` therefore tests
  three relationships: child, the **owner chain** (bounded, walked with `GW_OWNER`), and the
  foreground window's process against `CoreWebView2.BrowserProcessId` for the dialogs the browser
  owns itself. Process identity is exact — never match on window class or title.
- **A window deactivates once.** Declining to dismiss because a picker had focus would pin the
  flyout open for good: the user closes the picker, clicks another app, and no second `Deactivated`
  arrives to reconsider. `StartForegroundWatch` polls (400ms) only while such a dialog is up and
  applies the deferred dismissal as soon as the foreground is no longer ours. It stops on
  re-activation, on `HideFlyout`, and whenever anything else has taken over pinning the flyout. **Every dismissal condition is re-checked in that callback**, not just
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

### Remember Size — locking a size in

**Remember Size** (Advanced, beside Remember Position) is on by default and is what makes a drag
stick. Turned off, the flyout can still be dragged to any size and stays there for as long as it is
open, but nothing is written: the next open is at `WebFlyoutWidth` × `WebFlyoutHeight` as set in
launcher settings. That is how a size is pinned down — set it in the form, switch this off, and no
amount of dragging can drift it afterwards.

It is the same bargain maximize makes, and it is implemented the same way rather than a second way:
`CompleteResize` flags `_hasTemporaryResize` instead of writing, and `ParkOffScreen` undoes it
alongside `_isMaximized`. Two details follow from that shared path:

- **Nothing snaps back mid-session.** Reverting on pointer release would read as the resize being
  *refused* rather than being temporary, and the user would keep trying.
- **The park size is reset, not just the model.** `ParkOffScreen` parks at the window's current
  rect because that is what the next open's first frame is drawn at — so a dragged size left in
  place would flash for a frame before the placement code moved it back.

The stored property is `Launcher.WebLockSize`, the **inverse** of the toggle, because a `bool`
defaulting to `true` cannot be turned off in this settings file — see "Settings that must default
to zero / false" below.

`_isResizing` pins the flyout open, exactly like `_isModalOpen` — a drag that strays outside the
window must not read as "the user clicked elsewhere".

## Maximize is temporary, and that is the whole design

The header's maximize (`EnterMaximized` / `ExitMaximized`) fills the **work area** of the monitor
the flyout is on. It is a state of the window, never of the launcher: **nothing on this path
writes `WebFlyoutWidth`, `WebFlyoutHeight` or `WebFlyoutPosition`**, and `ParkOffScreen` drops
`_isMaximized` on dismissal so the next open is at the configured size. A launcher that should
always be big is resized by dragging or in its settings; this is "let me look at the whole
dashboard for a minute".

Three things follow from that, and each is a guard rather than a convention:

- **The grips and the header drag are inert while maximized.** Both would otherwise persist the
  maximized geometry — `CompleteResize` writes the size, `EndWindowMove` writes the position —
  which is exactly the state that is meant not to outlive the dismissal.

  The grips are also **collapsed** in that state, not merely refused (`UpdateResizeGripVisibility`,
  called from `EnterMaximized` / `ExitMaximized` and both branches of `ApplyFullScreen`). They are
  transparent, so all a grip shows is its resize cursor — and a resize cursor is a promise, the
  same rule the external-drop code follows for drop cursors. Refusing the drag in the handler left
  the edges advertising a resize that silently did nothing. The handler guard stays as the backstop
  for a drag already in flight when the state changes.
- **`ApplyLauncherChanges` must not resize while maximized.** It runs on *anything* touching the
  launcher, a bookmark's favicon fetch completing included, so without the guard a maximized
  flyout snapped back to its normal size with no user action at all. Same trap as
  `CurrentTargetUrl` below, one field over.
- **`ApplyRootAnchor` releases the bar-mode fixed height.** That height exists to make expansion a
  pure reveal; held at the launcher's configured size while the window is screen-sized, it clips
  the page to the size the flyout used to be. `CollapseToBar` exits maximize first, geometry
  included — the collapse keeps the current width, and a bar as wide as the screen is not a bar.

It is *not* the same thing as page fullscreen (`ApplyFullScreen`), which is entered by the page,
takes the whole monitor over the taskbar, hides the chrome and squares the corners. Maximize keeps
the header, the corners and the taskbar — the tray icon it was opened from stays reachable. The
two compose in the obvious direction: a page going fullscreen from a maximized flyout restores to
maximized afterwards.

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
launcher settings / reload / open in browser / pin / maximize / close — the page actions first,
then the two that decide how the flyout behaves as a window. Back is driven by `HistoryChanged`
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

**The address commits as soon as it is entered** — on Enter and on losing focus, not only when the
window closes — because the icon is fetched from it and everything else in the window is downstream
of that. Committing only on close meant the icon row still showed a globe for the whole time the
launcher was being set up, and **Pin to Taskbar pinned that globe**: a pinned icon is baked once and
Windows never re-reads it, so the launcher stayed wrong until the user unpinned and pinned again.
Pinning additionally waits out an in-flight fetch (`WaitForIconAdoptionAsync`, bounded by
`IconAdoptionWait`), and the icon row refreshes itself when one lands.

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

## Site permissions and page notifications

`WebFlyoutWindow.Permissions.cs` holds both. They are one topic: WebView2 raises
`PermissionRequested` and `NotificationReceived` and leaves each to the host, so **what the host
does not handle does not happen** — an unhandled notification is simply dropped, and an unhandled
permission request falls back to WebView2's own prompt, which is drawn for a browser window rather
than a 400px tray flyout.

### Asking

Requests are answered in a **prompt bar in row 0 of the content host** — camera, microphone,
location, notifications, clipboard, sensors, and the rest of `CoreWebView2PermissionKind`, each
worded in `DescribePermission`.

- **A row, not an overlay.** Buttons floating over a hosted browser depend on how WebView2 routes
  input — the same uncertainty that made the resize grips physically disjoint from the browser
  (above). A permission prompt that cannot be clicked is worse than one that costs a little height,
  and this is what a browser does with an infobar anyway. The `Auto` row costs nothing while the bar
  is collapsed. The WebView2 is inserted into row 1 for this reason; anything else added to
  `_contentHost` must set its row too.
- **The request is held open by a deferral**, not by the handler returning. Two consequences:
  several requests queue (`_prompts`), because a page asking for camera *and* microphone raises two;
  and **every deferral must be completed**, which is why `CancelPendingPermissions` runs from both
  `HideFlyout` and `UnloadWebView`. A deferral that is never completed leaves the page waiting
  forever. Cancelling denies without saving, so the page may ask again.
- **An open question pins the flyout**, exactly like an owned window (`IsPromptOpen` sits alongside
  `_isModalOpen` in both dismissal checks). A prompt that vanishes on focus loss is a request the
  page never gets an answer to.
- **Answers are saved into the launcher's WebView2 profile** (`SavesInProfile`), so they are per
  launcher like its cookies, and launchers on the shared profile answer once between them.

`Launcher.WebAllowAllPermissions` ("Trust This Site" in Advanced) grants everything without asking.
It deliberately sets `SavesInProfile = false`: the toggle *is* the decision, so turning it off must
return the launcher to asking rather than leave a profile full of silent grants behind.

**Reset Site Permissions** undoes stored answers — without it, a mistaken Block could only be
cleared by wiping the whole profile, which also signs the launcher out.
`CoreWebView2Profile` is only reachable through a live browser, so a reset asked for while the
launcher is unloaded is queued (`_pendingPermissionResets`) and applied by `ConfigureCore` on the
next create. The button says which of the two happened rather than reporting a success that has not
occurred yet.

### Notifications

`NotificationReceived` is marked handled and the notification is shown as a Windows toast through
`AppNotificationManager`, the same channel the app's own notices use. The page's callbacks are
driven from it — `ReportShown` when Windows accepts the toast, `ReportClicked` when it is activated
— or the page believes its notification never appeared.

- **The toast carries its launcher** (`AddArgument("launcher", id)`), and
  `MainWindow.OnNotificationInvoked` routes a click to `WebFlyoutWindow.HandleNotificationActivation`
  before falling back to opening the Home page. That opens the flyout via
  `MainWindow.OpenLauncherPanel`, which anchors on the taskbar button if there is one and the cursor
  otherwise.
- **The launcher's adopted page icon is used, not `notification.IconUri`** — the latter is a page URL
  Windows would have to fetch itself, and for a dashboard behind a login it would fetch a redirect.
- `_liveNotifications` is **capped**, not trusted to drain: a toast the user simply ignores reports
  nothing back, so entries would otherwise accumulate for the life of the app.

#### WebView2 only reports *non-persistent* notifications — hence the bridge

This is the single fact that decides whether any of this works, and it is easy to miss because
nothing fails loudly.

`ICoreWebView2_24::add_NotificationReceived` fires for `new Notification(...)` **only**. A
notification raised through `ServiceWorkerRegistration.showNotification()` — the *persistent* API —
is created inside Chromium, resolves its promise, is returned by the page's own
`getNotifications()`, and **is never surfaced to the host at all**. It is displayed nowhere and
dropped in silence. Measured against a probe host on WebView2 151.0.4129.72: page-context
`new Notification` raised the event, `registration.showNotification` raised nothing, and the page
could not tell the difference.

That would leave every messaging launcher permanently silent, because the persistent API is the one
real apps use — WhatsApp Web, Discord, Messenger, Google Messages, Teams and Home Assistant all
register service workers that call `showNotification`. So `NotificationBridgeScript`
(`InstallNotificationBridgeAsync`) rewrites `ServiceWorkerRegistration.prototype.showNotification`
in the page to construct a non-persistent notification instead, which the host does see.

- **It is awaited before the first `Navigate`.** A document-created script added after the
  navigation has started misses the one page the flyout was opened to show.
- **It reimplements the two behaviours a page can observe**: a repeated `tag` closes the earlier
  notification, and `getNotifications()` returns what is still on screen. Chat apps use both to keep
  one entry per conversation and to clear it when the thread is read.
- **It falls back to the original method on any error**, so a future WebView2 that supports this
  natively is left alone.
- **The toast honours the tag too** — `SetTag` plus `SetGroup` on the launcher id, so Windows
  replaces rather than stacks, and two launchers on the same site cannot collide on a thread id.
  `ToastIdentifier` hashes anything over the 64-character limit.

**What the bridge cannot reach is the service worker itself.** Document-created scripts run in
document contexts only, so a `showNotification` called from inside a worker — a push handler,
typically — still has no host-visible path in this SDK, and there is nothing above the WebView2
layer that can add one. That is an acceptable line to stop at: a page-raised notification is exactly
the case that matters for a launcher that is kept running, and a push arriving while nothing is
loaded was never going to work anyway, because the resource model has already closed the browser.

One behaviour does change and is worth knowing: a bridged notification is clicked in the *page*, so
the site's service-worker `notificationclick` handler does not run. The toast still opens the
launcher it came from, which is the useful half of what that handler would have done.

### Notifications need the browser alive — which the resource contract does not

This is the tension worth understanding before changing either side. A dismissed flyout suspends its
browser and then unloads it, and a page that is not running raises nothing, so **notifications only
work under `WebHiddenPolicies.KeepRunning`**. There is no push channel to fall back on: this
WebView2 SDK exposes no Push API, only page-raised notifications.

Granting the Notifications permission therefore **offers** the switch (`OnPermissionGranted`) rather
than making it. The policy is what decides whether a hidden launcher costs anything at all, so
changing it silently would undo the promise the feature is built on. Declined once, it is not asked
again that session (`_keepRunningOffered`).

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
  auto-hide flag, and `WebLockSize` rather than the "Remember Size" the UI actually shows. A bool
  defaulting to `true` **cannot be turned off**: `false` is dropped on write and the initialiser
  puts it back on load. **A setting that reads better in the positive is still stored in the
  negative** — invert it in the one line that builds the toggle, not in the model.
- `WebHiddenPolicies.UnloadWhenIdle` is `0` for the same reason: the cheapest behaviour is the one
  that survives being omitted.

## Vocabulary

The window is a *flyout*, not a *panel* — matching `FlyoutWindow`, the `Flyout*` settings, and the
sibling Persistent app it takes its conventions from (`AppFlyout`, `FlyoutWidth`/`FlyoutHeight`,
`PinFlyout`, `SuspendWebViewAsync`). Keep new names in that family.
