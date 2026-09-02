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
| Dismissed under `KeepRunning` | Collapsed only — **not** suspended, memory target left alone | Awake but not drawn — rendering stops, script, timers and sockets do not |
| Dismissed past `WebIdleUnloadMinutes` (default policy) | Window only | Nothing |

- **Collapsing the control is load-bearing, not cosmetic.** WebView2 refuses to suspend a visible
  browser, and the WinUI `WebView2` control drives `CoreWebView2Controller.IsVisible` from its XAML
  `Visibility`. Collapsing is therefore both what stops the rendering and what makes
  `TrySuspendAsync` legal.
- **`KeepRunning` still collapses, and that is the half that must not change.** A collapsed page
  reports `visibilityState: 'hidden'`, and an app that decides whether to raise a desktop
  notification from visibility concludes the user is looking at it otherwise. Leaving the view
  visible was tried and turned "no sound at all" into "sound but no notification" - the page received
  again and then declined to notify. Messenger and Teams both behave that way.
- **It no longer drops the memory target, and background throttling is off for the whole
  environment.** Those are the two things that stopped a hidden page receiving at all. `Low` is a
  hint to shed memory, which on an already-hidden page means reclaiming the renderer out from under
  a client holding a connection; and hidden is what Chromium throttles - background timers first,
  then intensive throttling at a tick a minute - so a client whose delivery rides on a timer simply
  stops. The symptom was a launcher that took no notice of a message until it was opened and then
  played the whole backlog at once, which reads as "notifications are broken" and is not.
  `--disable-background-timer-throttling`, `--disable-renderer-backgrounding` and
  `--disable-backgrounding-occluded-windows` go on the environment; see `WebViewEnvironments`.
  `Page.setWebLifecycleState('active')` was tried first and is **not** the answer: it addresses
  freezing rather than throttling, and a one-shot call does not survive Chromium re-evaluating the
  page. A build carrying only that received nothing at all.
- **Suspension is best-effort; the unload is the guarantee.** `TrySuspendAsync` declines in cases
  like active media capture or a download in flight. Treat a `false` return as normal (it is logged
  at debug) — the idle timer is what makes the promise true, because after it fires there is no
  browser process left to consume anything.
- **`WarmUp` deliberately skips web launchers.** The flyout is pre-rendered at startup because its
  content is expensive to re-rasterise; doing that for a browser would boot a renderer for a page
  the user may never open, which is exactly the cost this feature exists to avoid.
- **The staggering timer must be a field.** A `DispatcherQueueTimer` lives only as long as
  something references it, so the local one this shipped with was collectable the moment
  `PreloadKeepRunning` returned — and ten seconds is ample. Preload silently never ran: no log line,
  no browser, nothing to see. Every other timer in the class is a field for the same reason.
- **`KeepRunning` launchers are the exception, and are preloaded at startup**
  (`PreloadKeepRunning`). The rule above is about pages the user may never open; this policy is the
  user saying the opposite outright, and the only reason to say it is notifications. Without the
  preload the promise held only for launchers that happened to have been opened by hand since the
  last restart, so every reboot silently switched notifications off until the user remembered to
  click each tray icon in turn. A preload is exactly what opening and dismissing one by hand does:
  parked off the virtual screen, loaded normally — *visible*, so the page defers nothing a
  background tab would — then put through `ParkOffScreen` once it settles. It is **staggered**
  (10s, then one every 6s) because this runs at sign-in, and a settle timeout collapses a page that
  never finishes loading. Launchers are skipped when: until a bookmark is picked there is no
  page to keep running. `NeedsPreload` also skips anything already loaded, because warm-up runs
  again on every launcher change — including every auto-sync.
- The **window** does outlive a dismissal, parked off the virtual screen like the flyout. An empty
  WinUI window costs nothing, and it keeps the reopen a pure move.

## WinUI WebView2 limits worked around here

- **No controller access.** WinUI's `WebView2` never exposes `CoreWebView2Controller`, so
  `ZoomFactor` — which lives there — is unavailable, unlike in the WPF and WinForms wrappers. Zoom
  is applied as a CSS `zoom` on the document element and **re-applied on every
  `NavigationCompleted`**, because it lives in the document rather than on the control.
  - **Which is why the browser's own zoom is switched off** (`IsZoomControlEnabled = false`). The
    two zooms are not interchangeable: a Ctrl+wheel zoom is applied to a controller nothing here can
    read, so it survives the "…" menu, Ctrl+0 and the settings dialog alike: all three write the
    launcher's CSS zoom, and none of them can undo it. Left on, the app has a zoom the user can set
    and nobody can reset. `AreBrowserAcceleratorKeysEnabled = false` covers only the *keys*; the
    wheel needs this flag.
  - **The gesture is kept, not dropped.** The shortcut bridge takes Ctrl+wheel in the page (capture
    phase, `passive: false`) and posts `page.zoomIn` / `page.zoomOut`, so it moves one rung of
    `Launcher.WebZoomLevels` and is persisted like any other zoom change. It accumulates `deltaY` to
    a threshold of 100 first, because a trackpad pinch arrives as a stream of small deltas and
    would otherwise cross the whole ladder in one gesture.
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

**Remember Size** (beside Opens At) is on by default and is what makes a resize stick. It stays a
toggle where position became a value in a list, because size has no equivalent of "top left" to
collide with — there is nothing for it to be mutually exclusive *with*. Turned off, the flyout can still be dragged to any size and stays there for as long as it is
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
- **(Historical) the root grid used to be pinned to a fixed height** so that growing a collapsed
  bar into a page was a pure reveal rather than a reflow. There is no collapsed state now, so
  `ApplyRootAnchor` and its maximize/fullscreen escape hatches are gone with it.
- It overrides `ButtonForegroundPointerOver` / `ButtonForegroundPressed` on the **button's own**
  `Resources`, the same mechanism the disabled state above uses — the templated parent is in the
  lookup chain, so a `ThemeResource` in the default template resolves there first, and the
  `FontIcon` inherits it because it sets no `Foreground` of its own. Pressed matches pointer-over
  or the colour drops away at the moment of committing to it.
- The brush is read with `TryGetValue`, not the indexer. A missing key throws, and this runs while
  the window is being constructed, so an absent theme brush would take the launcher out rather
  than merely lose a hover colour. WinUI theme dictionaries ship compiled to `.xbf`, so a key's
  existence cannot be confirmed by grepping the SDK — assume nothing and fall back.

**Passkeys work** — verified against a real sign-in. WebAuthn with a Windows Hello platform
authenticator completes inside the flyout; credentials belong to the OS, so the per-launcher user
data folder does not isolate them.

## The header's More menu

`WebFlyoutWindow.MoreMenu.cs`. The header's "…" replaced the gear: it opens a menu of the
per-launcher options whose right value is a **per-moment judgement** — Regular window, Close when
focus is lost (window mode only), the pin, add/remove this page from the bookmarks, **Tab bar**,
**Address bar**, **Zoom**, **Opens at** (a submenu of radio items, being an eleven-way choice rather
than a toggle — "Where you last dragged it" is one of its values, not a separate toggle), Remember
size changes, an **Advanced** submenu — with **Keyboard shortcuts**, **Launcher settings…** and **App settings…** at its
foot.

App settings dismisses a flyout on its way, as the item flyout’s context menu does: the flyout is
always-on-top and would otherwise sit over the window it just opened. A regular window is left
alone — it is not topmost unless pinned, and closing the page the user still has open would be a
steep price for reaching a settings window.

**Per-moment is the whole membership test, and two items failed it.** Reload when opened and Open
links in the browser were here and are now Advanced-only: how a launcher treats a reload or a link
is a property of the launcher, settled once when it is set up, not something to reconsider while
looking at it. Open links in the browser had no settings row at all before it was demoted, so it was
unreachable for anyone who had never opened this menu — worth checking when moving an item *out* of
here, because the menu is where several of these were born.

Tab bar is named for what turning it *on* does, not for the default it switches off: it reads as
"keep it", because the strip already appears on its own with a second tab.

**Open in browser moved here, and the header slot it vacated became the address bar's toggle.**
Handing the page to the real browser is a once-in-a-while action, which is what this menu is for;
showing the address is the per-moment decision, and the one worth a button — it is how you see where
you are, type somewhere else, and reach the bookmark star. The button now *sets*
`Launcher.WebShowAddressBar` rather than revealing the bar for one visit: an earlier version had
both a permanent setting and a temporary reveal, and one affordance beats two that differ only in
how long they last, which is a distinction the header had no room to explain. The menu carries the
same toggle, since a button that is only in the header is a button nobody finds.

Anything configured once and forgotten (address, profiles, browsing data)
deliberately stays in the settings window; putting it all here would be a second settings window in
a worse place. The "…" is the same idiom the item cards use, so the affordance is not new.

**Zoom went the other way, and is the clearest case of an item the membership test read wrong.** It
looks like a set-once setting and sits in Advanced accordingly, but the gestures that change it
(Ctrl+wheel and Ctrl+plus/minus) are ones a user hits *by accident*, and nothing on screen then
admits why the page is the wrong size. That makes it two things at once: a value worth **reading**
off the menu, which is why the current level is in the parent item's own text (`Zoom (125%)`) rather
than only in the checked rung, and a value worth putting back from there. The submenu leads with
**Reset to 100%** for that reason, and lists `Launcher.WebZoomLevels` below it: the same rungs the
settings dialog offers and the zoom keys step through, plus the launcher's current level when it
holds one that is not on the ladder. There is deliberately no Zoom in / Zoom out item: a
`MenuFlyoutItem` closes the menu, so nudging from here would cost one trip per rung, and nudging is
what the keyboard and the wheel are for.

Three rules, and the first two are the traps this window has hit before:

- **`ShouldConstrainToRootBounds = false`.** A flyout can be 400px wide with 34px of chrome, and a
  constrained menu is clipped exactly like the `ContentDialog` the item editors had to stop using.
- **`_isMenuOpen` pins the flyout while the menu is up.** Once unconstrained the menu is hosted in
  its own popup, so opening it deactivates the flyout — which would dismiss it and take the menu
  down in the same motion. Same reason `_isModalOpen` exists, and it must clear on `Closed`,
  including the dismissed-by-clicking-away case.
- **Every item applies immediately**, via `ApplyWindowMode` for the mode switch. The menu is opened
  while looking at the thing it changes, so a toggle that only took effect on the next open would
  be indistinguishable from one that did nothing. `ApplyWindowMode` has to push *all* of it —
  taskbar/switcher eligibility, always-on-top (which the pin's meaning depends on), and
  `IsMinimizable`, without which the taskbar button's click does nothing.

The pin item names what it does in the current mode ("Always on top" vs "Stay open when focus is
lost"), since `WebPinFlyout` is one flag with two readings and the header button only has a glyph.

### Advanced is buried on purpose

Three things a launcher can do to its own browser that nothing else in the app exposes, behind a
submenu so they sit off the path somebody walks to change their zoom:

- **Developer tools.** Nothing in the app opened them. A web launcher is a browser with no menu bar,
  so the way in was a right-click hoping the default context menu was still there - and every
  diagnosis inside a launcher began by finding that out.
- **Reload, ignoring cache.** The header's reload is the ordinary one, answered from cache, which is
  no help against the thing people reload for. There is no WebView2 API for it; the DevTools
  protocol has `Page.reload` with `ignoreCache` and the runtime speaks it. It falls back to an
  ordinary reload rather than doing nothing.
- **Rebuild notification bridge.** Resets the launcher's notification permission to "not asked",
  unregisters the site's service worker and reloads, so the site asks again, registers again, and
  that fresh install is wrapped. It also clears the once-ever adoption marker. The permission half
  is what makes the rest work: a site sets push up *during* its permission flow, so one already
  holding the grant never runs that code again and comes back up with no worker. This replaces the
  manual dance of logging out, resetting site permissions, clearing workers in DevTools,
  hard-reloading and logging back in, and it is also **how a launcher picks up a changed wrap**,
  since an already-registered worker will not on its own.
- **Rebuild every launcher's bridge.** The same across every loaded web launcher, because the reason
  to reach for it is rarely one launcher: the wrap changes for all of them together.

The last one is **the thing the bridge cannot do on anybody's behalf**, which is why it is a menu
item rather than automatic. Adoption needs a script URL, and a site enforcing Trusted Types accepts
only one minted by a policy its own CSP names - Teams refuses to let us create a policy at all, so it
can never be adopted. Teams mints its own on every load that finds nothing registered, and this
creates that state. The cost is the registration's push subscription, which unregistering throws
away; every site tested subscribes again on its next load, but that is its behaviour and not a
promise this can make. Doing the same act automatically to every launcher was built twice and
withdrawn twice, and that history is what "Advanced" is carrying.

Nothing already reachable is duplicated in it. Reload, zoom, open-in-browser and the bars are on the
menu above; browsing data is in launcher settings.

## The address bar

Off by default (`Launcher.WebShowAddressBar`, Advanced → **Address Bar**), because a flyout is a
small window and a launcher usually opens one known page. Off does not mean unreachable: the
header carries a button that turns it on and off, tooltipped for whichever it will do next.

- **It is a row of chrome, not an overlay.** Same reasoning as the permission prompt bar and the
  resize grips: anything floating over a hosted browser depends on how WebView2 routes input, and
  an address bar that cannot be clicked into is worse than one that costs a little height.
- **It travels with the header, in a `StackPanel` sharing root row 0.** That keeps the root's rows —
  and the resize grips' row spans — exactly as they were. Its visibility is driven off the header's
  via `RegisterPropertyChangedCallback`, so everything that already hides the header (collapsing to
  a bookmark bar, a page going fullscreen) carries the address bar with it without a matching line
  at each of those call sites.
- **It sizes to its content.** A fixed height clips the box at any scale or font where the default
  `TextBox` is taller than the number picked. It eats into the page, not the window, so no geometry
  arithmetic depends on how tall it comes out.
- **The header button writes the setting; it is not window state.** It used to be the latter —
  a reveal for the current visit, dropped by `ParkOffScreen` like maximize — and having both that
  and a persistent setting meant two controls whose only difference was how long they lasted. Now
  `ToggleAddressBar` saves, notifies sync, and re-applies, exactly as the "…" menu's twin does.
- **A blank new tab shows the bar whatever the setting says** (`IsActiveTabBlank`). A tab opened
  with the "+" has no address yet, so it is a place to type; hiding the box would leave the user an
  empty window and no way to use it.
- **"Blank" means "has nowhere to go", not "has not arrived yet"**, and the difference is the whole
  reason a tab is built knowing its address. A browser reports an empty `Source` until its first
  response commits, so a tab that learned where it was going only on arrival was indistinguishable
  from an empty one for the length of the load: the bar slid in for every cold open and every link
  opened in a tab, on launchers that have it switched off, and the "type an address" notice flashed
  up over a page that was loading. `CreateTabAsync` therefore seeds `WebTab.NavigatedUrl` before the
  tab goes on screen — including for a tab handed to `NewWindowRequested.NewWindow`, which WebView2
  navigates itself (`adopted: true` says so, so it is recorded without being navigated twice).
- **`IsBlankAddress` is the single test** for "this address is not a place", used on the seed, on the
  live `Source` and by `ShowEmptyTabStatus`: nothing at all, `about:blank`, or a new-tab page. The
  last two matter because `window.open()` with no address arrives as `about:blank` and a new-tab
  request as `edge://newtab/` — recording either as a destination would take the address bar away
  from the one tab that has nothing else in it. The new-tab test is gated on `IsBrowserPage` for the
  same reason the interception is: on its own it matches any URL containing "newtab".
- **It never becomes a fourth answer to "which URL".** `GoToTypedAddress` drives the browser that
  is already there and never creates one. Falling back to `PrepareContentAsync` looks helpful and
  is not — that path navigates to `CurrentTargetUrl()`, so an address typed with no live browser
  would silently load the launcher's configured page instead of the one asked for.
- **Escape is checked in the accelerator, not left to the `TextBox`.** An accelerator and a
  bubbling `KeyDown` do not resolve in a fixed order, so `CancelAddressEditing` runs first and
  reports whether it consumed the key. That makes moving focus *off* the box load-bearing: Escape
  is swallowed while the box has focus, so a failure to move it would swallow every later Escape
  too and leave the flyout with no keyboard dismissal at all. Hence the fallback to the pin
  button — always present and always enabled while the header is showing, unlike Back/Forward
  (disableable) or the reveal button (hidden once the bar is permanent).
- The box is refreshed from `UpdateNavigationButtons`, so it rides the same `HistoryChanged` +
  `NavigationCompleted` pair as Back and Forward — and the tab switch in `ActivateTabAsync`, which
  already calls it. **Never while the box has focus**: `HistoryChanged` fires freely on a
  single-page app, and overwriting a half-typed address is indistinguishable from a bug.

## Tabs

`WebFlyoutWindow.Tabs.cs`. **Every browser this flyout owns is a tab** — its own page included —
and `_webView` always points at the active one. That is what lets zoom, navigation, the header, the
address bar, permissions, notifications and the worker bridge carry on operating on "the browser"
without any of them learning about tabs.

There are two kinds, and the difference is **who chose the address**:

| | Home tab (`HomeKey` non-null) | Link tab (`HomeKey` null) |
|---|---|---|
| Stands for | The launcher's own address | A page the user or the page asked for |
| Key | `PrimaryTabKey` | — |
| May write the launcher's / a bookmark's icon | Yes | **Never** |
| Re-navigated by `ApplyLauncherChanges` | Yes | **Never** |
| Reload On Open applies | Yes (single-address only) | No |

That table is the whole safety story, and both "never"s are the same trap `CurrentTargetUrl` exists
for, one tab over. A favicon fetch completing runs `ApplyLauncherChanges`, so without the guard a
page the user opened from a link would be yanked to the launcher's own address **with no user action
at all** — and a site they merely passed through would rewrite the tray icon and the taskbar pin.

### Opening a link in a tab

**A middle-click opens behind, and the gesture does not arrive with the request.**
`CoreWebView2NewWindowRequestedEventArgs` carries `Uri`, `IsUserInitiated` and `WindowFeatures` —
no modifier or button state — so nothing on the event separates "middle-clicked this link" from
"clicked this link", which a browser treats oppositely. The page reports the gesture instead: the
shortcut bridge posts `bgIntent` on a mousedown that is a middle button or a Ctrl/Shift-click, and
`WantsBackgroundWindow` pairs it to the next new-window request within a second. Nothing is
prevented in the page — it does exactly what it would have done, and only where the result lands
changes.

A **sized** window is never sent behind, whatever the intent flag says: an OAuth popup is a
`window.open` with width and height, so it arrives with `WindowFeatures.HasSize`, and opening one
behind the page that raised it leaves the user waiting on a window they cannot see. It follows an
ordinary left-click and so fails the gesture test anyway — the features check is the belt to that
pair of braces, because this failure is invisible until someone cannot sign in.

`NewWindowRequested` used to be answered with `Handled = true` + `OpenExternally`, because a flyout
had nowhere to put a second page. It now becomes a tab, unless `Launcher.WebLinksInBrowser`
("Open links in the browser", in the "…" menu) restores the old behaviour.

- **The browser's own `e.NewWindow` is used, not `e.Uri` + `Navigate`.** Handing WebView2 the new
  browser keeps the opener relationship, so `window.opener.postMessage` works and an **OAuth sign-in
  popup can hand its result back to the page that raised it**. Reading the URI and navigating a tab
  by hand severs that, and the sign-in simply never completes — which is the failure that makes
  this worth the extra code.
- **The request is held open by a deferral**, because building a browser is asynchronous and the
  answer *is* the browser. Every path completes it, including both failure paths, which fall back to
  the external browser — a deferral never completed leaves the page waiting forever, the same rule
  the permission prompts follow.
- **An extension asking for a window gets a window, not a tab.** `chrome-extension://` new-window
  requests go to `ExtensionPopupWindow.AdoptAsync`. Bitwarden opens its passkey and two-factor
  prompts this way, and as a tab the prompt was a dark rectangle with no UI and no way to finish
  signing in. The window is on the launcher's own user-data folder, since an extension's pages only
  work in the profile it was installed into, and this path deliberately runs *before*
  `WebLinksInBrowser`: the real browser has no access to this profile's extensions and would answer
  the address with an error page.

  **Two things are needed and each fixes a different failure.** The browser must be *adopted*:
  `e.NewWindow` is how WebView2 is told the window was made, and answering `Handled` with no window
  reports failure to the extension, which gives up on the spot, so the site reported the sign-in as
  failed before the window had been looked at. And the window must be *navigated if WebView2 does
  not navigate it*, because adoption alone opened a window that stayed empty.
  `EnsureExtensionWindowNavigatedAsync` waits out a short grace period, asks what the browser
  actually did, and loads the address only when the answer is nothing, so a window WebView2 did
  drive is left alone rather than sent somewhere twice.
- **An extension's passkey prompt cannot complete here, and the reason is one missing return
  value.** Measured against Bitwarden and GitHub, with the extension's own console:

  ```
  [Fido2Client] Aborted by user: TypeError: Cannot read properties of undefined (reading 'id')
  ```

  That is the extension reading `.id` off the window object `chrome.windows.create` should have
  resolved with. WebView2 raises `NewWindowRequested`, the host builds a real window and adopts the
  browser into it, and the page loads, but the extension is handed nothing with an `id` on it, so
  it aborts the ceremony before the popout can claim its session. The window is then a correctly
  loaded page with no session to render, and the site reports the sign-in as failed before the user
  has touched anything.

  **The APIs are not the gap, which is worth knowing because it is the obvious wrong guess.** A
  probe inside a live extension page returned `chrome.runtime`, `chrome.windows`, `chrome.tabs` and
  `chrome.extension.getViews` all present, with `chrome.runtime.id` set. Bitwarden's *ordinary*
  popup renders perfectly in `ExtensionPopupWindow` on the same profile, so the window, the profile
  and the extension loading are all fine.

  **Leaving the request unhandled does not help, and this was measured rather than reasoned.** The
  obvious suspicion is that the host is the problem: `HandleNewWindowRequested` sets
  `e.Handled = true` and supplies its own window, so perhaps WebView2 only builds the object
  `chrome.windows.create` resolves with when it creates the window itself. It was tried. The window
  came up as a plain unowned Edge popup, address bar and all, and Bitwarden aborted on the same
  missing id. Handing the request back gains nothing and costs the window's appearance, so the
  launcher keeps it. **Do not re-run this experiment.**

  So the missing value is produced by WebView2's extension implementation and handed straight to the
  extension, with no seam in between.

  **The answer is the extension's own switch, and it works.** In Bitwarden that is
  **Settings, Notifications, "Save to vault options", "Ask to save and use passkeys"** (`enablePasskeys`;
  not under Autofill, which is where it sounds like it should be). It is not merely an offer to
  decline: the background registers and unregisters the interception with it.

  ```js
  (yield this.isPasskeySettingEnabled())
      ? SN.registerContentScriptsMv3([{ id: 'fido2-page-script-registration',
                                        js: ['content/fido2-page-script.js'], world: "MAIN" }, ...])
      : SN.unregisterContentScriptsMv3({ ids: ['fido2-page-script-registration',
                                               'fido2-content-script-registration'] })
  ```

  Off, the MAIN-world script is never registered, `navigator.credentials.get` is never patched, and
  the request reaches Windows Hello, which works in the flyout. **Verified end to end against a real
  GitHub sign-in.** Each launcher has its own WebView2 profile, so this is set per launcher and the
  user's real browser is untouched, which costs nothing: Bitwarden passkeys cannot work in a
  launcher either way.

  **Two ways of forcing it from the host were considered and rejected.** Patching the extension's
  background script as it is fetched (the trick the notification bridge uses) means forging window
  identities inside a password manager, and would break on every Bitwarden release. Declining to
  serve `content/fido2-page-script.js` into launcher profiles is narrower but is still the app
  reaching into an extension's files to disable one of its features. Neither is worth it when the
  extension ships the switch.

  **What must not be done is wrapping `navigator.credentials.get`.** It was tried: capture the
  native implementation at document start, re-install as the outermost wrapper, and on rejection
  offer Windows Hello through a prompt. It was reverted the same day, because the first observable
  effect was **GitHub no longer offering its passkey option at all**: a site decides what to offer
  partly from that API, and standing in the middle of it on every page of every web launcher, to
  work around one extension, broke a path that had been working.

  **Sharing one environment per profile was tried too, and changed nothing.** The console also shows
  `Uncaught (in promise) Error: Duplicate script ID 'fido2-page-script-registration'` during
  background startup, and the suspicion was that this app caused it: it built a new
  `CoreWebView2Environment` for every tab and every popup window on one profile, restarting the
  extension's service worker around them, while its content-script registrations persisted. If that
  rejection aborted the rest of the FIDO2 setup, the undefined would have been a consequence rather
  than the cause. `Services/WebViewEnvironments` now shares one environment per folder, and the
  duplicate registration still appears and the sign-in still fails, so the worker churn was not ours.
  The sharing is kept anyway: it is what the WebView2 documentation recommends, and it is less work
  per browser.

  **What the undefined most likely is.** `Could not establish connection. Receiving end does not
  exist.` fires immediately before the abort, every time, which is a `sendMessage` to a receiver that
  is not there, and the address carries `senderTabId=1549898803`, far too large to be a real Chrome
  tab id, so WebView2 is synthesising them. So the `.id` being read off undefined is more likely a
  **tab** lookup than the window object. It makes no difference to the outcome: `chrome.tabs` is the
  platform's implementation, with nothing between it and the extension.

  **Every host-side variable has now been tested**, which is the reason to stop: the launcher builds
  the window, and WebView2 builds the window; one environment per browser, and one shared per
  profile. The failure is identical in all four.

  Unrelated noise in the same log, so it is not mistaken for a cause: `Specified native messaging
  host not found` is Bitwarden failing to reach its desktop app, whose manifests are registered per
  browser. That affects biometric unlock, not passkeys.
- **`window.close()` closes the tab, not the flyout** — for a link tab. Only a page the launcher
  itself opened speaks for the whole window. OAuth popups close themselves, so without this
  splitting the handler, signing in dismissed the launcher at the moment it succeeded.

### The strip

A row of chrome between the header and the address bar, for the reason both of those are rows and
not overlays: anything floating over a hosted browser depends on how WebView2 routes input, and a
tab you cannot click is worse than one that costs a little height. It eats into the page rather
than the window, so no geometry arithmetic depends on how tall it comes out.

- **It appears on its own** once there is a second tab and goes away again when there is not, so a
  launcher that never opens one never pays for it. `Launcher.WebAlwaysShowTabs` pins it on — which
  is also the only way to reach the "+" without following a link first.
- **Its visibility is gated on the header's**, exactly like the address bar's, so collapsing to a
  bookmark bar and a page going fullscreen carry it with them without a line at each call site.
- **Tabs squeeze to fit before the strip scrolls.** `Controls.TabStripPanel` gives every chip its
  natural width while the row has room, then takes width back from all of them together down to a
  floor, and only past that does the strip scroll, with `BringActiveTabIntoView` keeping the tab in
  front reachable. A `StackPanel` could not do this at all: inside a horizontal scroller it is
  measured with an infinite width, so a fourth tab simply grew past the edge with nothing on screen
  saying it was there. The give-up is **max-min fair** rather than an equal share, so a short "Gmail"
  keeps its own width and hands the difference to the long titles being trimmed. The panel is told
  the width to work within (`AvailableWidth`), because the scroller's viewport is the number that
  decides all of this and the measure pass cannot see it.
- **The close button is drawn over the title, not beside it.** A reserved column is what a browser
  gives a comfortable tab, and it is wrong for a strip that squeezes: at the floor it spent a quarter
  of the chip on a button that is invisible most of the time, and titles came out trimmed to three
  characters to pay for it. Overlaid, the title always gets the whole chip. It is revealed on hover
  with the `Opacity` + `IsHitTestVisible` pair the item cards use (`IsHitTestVisible` being the half
  that matters, or a click on what looks like empty chip silently closes the tab), and takes a fill
  of its own while it is up so a long title does not read through it. Collapsing its column instead
  was rejected: it would resize every chip after it as the pointer crossed the strip.
- **Chips are built once per tab and kept on the tab**, never rebuilt per refresh — a rebuild would
  throw away a decoded favicon and a measured label on every switch, which is the mistake the
  bookmark bar's rebuild signature exists to avoid. `DestroyTab` drops them, so nothing survives its
  browser (see the container-leak note in [drag-drop.md](drag-drop.md) for why that matters in a
  window that lives for the whole session).
- **A chip's favicon is never written to disk.** It is read into a byte array and handed to the
  bitmap through a stream of our own — nothing that came out of the browser is left for the
  finalizer — and lives exactly as long as the tab. Only home tabs write a file, and only onto the
  launcher or the bookmark they stand for.
- **The strip is the only thing that says which page is in front.** The bookmark bar used to tint
  the active bookmark and had to suppress it whenever a link tab was showing; that whole rule is
  gone with the tint, because the bar was claiming to know something only the tab knew. Clicking a
  bookmark now loads it wherever you are, so there is nothing for the bar to be right or wrong
  about.
- **The active chip is marked the Fluent way** — a quiet raised surface
  (`LayerFillColorDefaultBrush`), a 2px accent underline, and the inactive chips at 0.6 opacity.
  A solid `AccentFillColorDefaultBrush` was tried first and is wrong here: across a strip of tabs it
  is a block of colour sitting directly above the page it belongs to, and it shouts. Dimming carries
  the favicon with it, which is the point — an inactive tab should read as further away rather than
  merely as a different colour, and that is what lets the underline be a hairline. The underline
  lives **inside**
  the button's content, in a fixed 2px row, so appearing cannot widen the chip or shift the strip —
  the same non-reflowing rule the flyout's edit-mode affordances follow.

### Every shared-chrome handler asks `IsActiveCore` first

A background tab navigates on its own — a chat app pushing history, a dashboard refreshing — so
`NavigationStarting`, `HistoryChanged`, `NavigationCompleted`, `ProcessFailed` and
`ContainsFullScreenElementChanged` all check whether the browser raising them is the one on screen
before touching the status overlay, the back/forward buttons, the address box or the window's
geometry. Without it a hidden tab drives the header of a page nobody is looking at — and a hidden
tab going fullscreen would resize the window.

The two things that are *per tab* and so run either way: the chip's title and icon, and
`ApplyZoom(core)`, which takes its browser explicitly so a background tab re-applies its own
document's zoom rather than the active tab's.

### Closing

Closing the last tab is the same gesture as closing a browser's last tab: the launcher goes
back to just its bar, anything else dismisses the flyout. Both leave the launcher with no browser at
all, which the next open rebuilds — so closing a **home** tab is simply "unload this", and the
bookmark or the tray icon brings it straight back.

## Several launchers on top at once

Always-on-top is a **band**, not a place, and more than one launcher can be in it: every pinned
flyout, plus every regular-window launcher whose pin is on. Within the band the order is whatever
the last `HWND_TOPMOST` call left, so "always on top" alone does not say which of them the user is
actually looking at. Two rules keep the focused one in front, and they are opposite halves of the
same problem.

- **The focused launcher re-asserts its place in the band** (`RaiseWithinTopmostBand`, from
  `Activated`). `SetWindowPos(HWND_TOPMOST)` on a window *already* topmost moves it to the head of
  the band and touches nothing further down the desktop, so this is not the same thing as making a
  window topmost, and a launcher outside the band is left alone rather than promoted into it. It is
  needed because the click that activates a launcher usually lands on the hosted browser's own
  child HWND, so the raise a plain top-level click would have brought with it does not reliably
  arrive. Gated on `_isOpen`: a parked window is topmost too (the park is a move off the virtual
  screen, not a hide), as is every launcher preloaded under `KeepRunning`.
- **`SetTopmost` returns early when the value is unchanged.** Setting `IsAlwaysOnTop` re-applies
  `HWND_TOPMOST` whether or not the window was already in the band, and `SetTopmost(true)` runs from
  `ApplyLauncherChanges`, which fires on *any* launcher edit, a background favicon fetch included.
  A second launcher therefore jumped in front of the one being looked at with no user action at all.
  Same trap as the maximize and `CurrentTargetUrl` guards above, one field over: **`ApplyLauncherChanges`
  is not a user gesture and must not behave like one.** `IsTopmost` reads the window's own
  `WS_EX_TOPMOST` rather than a cached flag or the launcher's settings, because a modal drops the
  band for its duration and the answer has to follow the window, not the intent.

Item flyouts are not part of this: they are always topmost but dismiss on focus loss, so only one is
normally on screen.

## Regular-window mode, and why the taskbar and Alt-Tab are one switch

`Launcher.WebRegularWindow` (Advanced → **Regular Window**, off by default) presents a web launcher
as an ordinary app window: it drops always-on-top and dismiss-on-focus-loss, appears in the taskbar
and both switchers, and — because its window carries the pinned shortcut's AUMID — lights the
**running indicator on its own pinned button**. Clicking that button closes it again.

**The setting names a window kind rather than a symptom, and that is not cosmetic.** The obvious
label would be "show in taskbar", and it would be a promise the shell cannot keep: the running
indicator is derived purely from whether the launcher's AUMID group owns a *taskbar-eligible*
window, and eligibility is `WS_EX_TOOLWINDOW`, which governs the taskbar, Alt-Tab and Win+Tab
together. Four ways round that were measured on Windows 11 and every one failed:

| attempt | taskbar button | switcher |
|---|---|---|
| `ITaskbarList.AddTab` on the flyout, tool bit kept, AUMID stamped | ✗ — returns `S_OK`, does nothing | clean |
| flyout clears `WS_EX_TOOLWINDOW` | ✓ | cluttered |
| 1×1 off-screen proxy window, `WS_EX_APPWINDOW \| WS_EX_NOACTIVATE` | ✓ | cluttered — blank thumbnail |
| the same proxy **owned** by a hidden tool window | ✓ | cluttered — `APPWINDOW` forces inclusion regardless of ownership |

So a flyout cannot light its pin without also appearing in the switcher. Under this setting that
stops being clutter and becomes correct: the window is no longer always-on-top, so there is finally
a reason to Alt-Tab *to* it. **Do not re-litigate this by adding a taskbar-only toggle** — the
proxy-window route is the one that looks like it should work and does not.

**Dismissal is a separate axis from presentation.** `WebRegularWindow` decides whether the shell
sees a window; `WebWindowAutoHide` ("Close On Focus Loss") decides whether it survives being clicked
away from. Setting both gives a launcher that is Alt-Tab-able and shows a running indicator while
still getting out of the way on its own — a combination neither mode offers alone. Default is off,
which is both what regular-window mode shipped with and what an ordinary app window does. The
dismissal guards read `StaysOpenAsWindow`, not `WebRegularWindow`.

Four things follow, each a guard rather than a convention:

- **The tool bit is toggled on show and park, never dropped once at construction.** A dismissed
  flyout is parked off the virtual screen, not hidden, so it stays visible in the Win32 sense for
  the life of the app — made switcher-eligible once, it would sit in Alt-Tab forever, including for
  every launcher preloaded at startup under `KeepRunning` that has never been opened.
- **`ITaskbarList.AddTab` is required and is not redundant with the style change.** It does nothing
  for a tool window, and *everything* for one that has just stopped being one: dropping the call
  left a correctly-restyled window with no button at all, because the shell had not looked again.
  The hide/restyle/show cycle the documentation suggests instead is unavailable here — `SW_HIDE` is
  what makes WinUI drop the composition surfaces the park exists to protect.
- **The window must be told it is minimizable, or the taskbar click does nothing at all.** A
  flyout's `CreateForContextMenu` presenter is not minimizable, and the shell will not send
  `SC_MINIMIZE` to a window that says it cannot be — so both possible behaviours, minimize *and*
  the close that intercepts it, silently did nothing. `presenter.IsMinimizable` in regular-window
  mode is what makes the click reach the window; it is not visible in `HandleTaskbarMinimize`,
  which simply never ran. (`WS_SYSMENU` was tried alongside it and is **not** needed — measured.)
- **What that click does is `Launcher.WebTaskbarClickCloses`** (Advanced → **Taskbar Click**, shown
  only while Regular Window is on, since a flyout has no button to click). Default is minimize,
  because that is what an ordinary app window does and this mode exists to be one; closing is the
  option, because a launcher is cheap to reopen from the same button. Minimize is simply the
  message reaching `DefWindowProc` unconsumed.
- **The pin button changes meaning with the mode, deliberately reusing `WebPinFlyout`.** A flyout's
  risk is vanishing when you click away, so pinning stops the dismissal; a regular window never
  dismisses itself, so its risk is being buried and pinning keeps it on top. Each reading is
  meaningless in the other mode, so a second flag would just be a setting that does nothing
  wherever the launcher actually is. `SetTopmost` resolves it centrally — every caller is either
  dropping topmost for a modal or restoring "the default", and the default is exactly what differs.
- **The header carries a minimize button, and only in this mode.** A flyout has no taskbar button,
  so minimizing one would put it somewhere with nothing to bring it back from; Close is what gets a
  flyout off the screen. A regular window has the button, the switcher entry and
  `presenter.IsMinimizable` already, so this was the one window control the header was visibly
  missing. It is built once and collapsed outside the mode, so the setting can be flipped while the
  window is alive (`ApplyLauncherChanges` re-applies it beside `SetMinimizable`).

  Two things had to follow it, both because **minimized is a third state next to open and
  dismissed** and nothing previously distinguished them:

  - **It minimizes with `ShowWindow`, never `OverlappedPresenter.Minimize()`.** Nothing documents
    the presenter route as bypassing `WM_SYSCOMMAND`, and `HandleTaskbarMinimize` is sitting on
    that message: under `WebTaskbarClickCloses` it turns `SC_MINIMIZE` into a dismissal, so a
    presenter call that happened to go that way would make the minimize button close the launcher.
  - **`IsMinimized` joins the dismissal guards.** Minimizing deactivates the window, so under
    `WebWindowAutoHide` the focus-loss dismissal fired and parked the launcher off screen, taking
    its taskbar button with it. The new button was a close in disguise for exactly the launchers
    that had opted into being closable. A minimized window is not one the user clicked away from.
  - **A tray click on a minimized launcher restores it rather than toggling it off.** `_isOpen` is
    still true, so `Toggle` read it as "on screen, dismiss it" and answered a click asking to *see*
    the launcher by removing the other way back to it. It goes through `BringToFront` (renamed from
    `ActivateForNotification`, which now has two callers): restore first, then foreground, because
    a minimized window ignores `SetForegroundWindow` on its own.
- **The window needs an icon it never needed as a flyout.** A flyout has no title bar and appears
  nowhere an icon is drawn; a regular window appears in two such places, and without
  `ApplyWindowIcon` both show a blank placeholder. It uses the per-launcher `app-icon-{id}.ico` —
  the same file the pinned shortcut points at, so the button and its pin agree.
- **The button is pinnable, and that needs more than an AUMID.** An AUMID names a group; it does
  not say how to start the group again, so `Pin to taskbar` had nothing to write down and the pin
  either was not offered or opened nothing. `ApplyRelaunchProperties` stamps the same three
  properties the companion exe sets on its own message box for the settings window's **Pin to
  taskbar** button: `RelaunchCommand` (the companion with this launcher's id),
  `RelaunchIconResource` and `RelaunchDisplayNameResource`. That flow only ever existed
  for want of a pinnable window, and this mode is one.

  The values have to match that flow rather than merely resemble it. The icon must be an icon
  *resource* reference (`"path,0"`); a bare path is unparseable and the taskbar falls back to the
  generic document icon. The display name keeps the `Little Launcher - {name}` format, because
  Windows caches pin display names **per AUMID** in CloudStore and a launcher pinned both ways
  would otherwise show one name from the cache and one from the window.

  It uses the base `app-icon-{id}.ico`, not the timestamped `-pin{tick}.ico` copy the settings flow
  makes. That copy exists to bust Windows' per-*path* icon cache across repeated pin attempts, which
  is what a user re-pinning to fix a wrong icon needs; a window merely advertising itself should not
  write a file on every open for a pin that may never happen.

  **The minted fallback identity is now recorded on the launcher.** `PinAppUserModelId`'s last
  resort (`LittleLauncher.Launcher.{guid}`, no tick) becomes a real pin's identity the moment the
  user pins this button, and `JumpListService.ResolvePinAumid`'s own fallback scan only matches the
  *ticked* shape a settings-window pin produces. Without the write, a launcher pinned from its
  window got a working button and a permanently empty jump list. This is not the registry guess
  being promoted (the warning below still stands): it is a string this app minted and stamped on
  the window itself, which is exactly what `PinAumid` is defined to mean.
- **The pin's AUMID is recorded when the pin is made** (`Launcher.PinAumid`), minted by
  `LauncherSettingsWindow.PinToTaskbar_Click` and passed to the companion as `--aumid`. The
  companion used to mint it and then exit, so nothing remembered it.

  **Two ways of recovering it afterwards were tried and both fail — do not go back to either.**
  Reading the pinned `.lnk`'s property store is not possible at all: Windows 11 does not keep these
  pins as shortcut files, and `User Pinned\TaskBar` held three unrelated apps on a machine showing
  eleven Little Launcher pins. Scraping `HKCU\…\Explorer\Taskband\Favorites` /
  `FavoritesResolve` works for *some* pins: on that same machine the two blobs between them held
  eight of the eleven, with WhatsApp, Messenger and Web Launcher in neither, and re-pinning did not
  add them. That scan survives only as a fallback for pins made before the app started recording.

  **A mismatch is loud, not silent** — a window whose AUMID does not match its pin raises a
  *second* taskbar button beside it. That is the symptom to recognise; it means the identity is
  wrong, never that the feature is off.

  Consequence for existing pins: a launcher pinned before this must be **re-pinned** once, unless
  it happens to be one the registry scan can still find.

## Start Menu shortcuts

`Services/StartMenuShortcutService.cs` keeps one shortcut per **web** launcher in a
`Programs\Little Launcher\` group, so they can be opened from Start search, PowerToys Command
Palette, and anything else that indexes the Start Menu. A web launcher is an application in
everything but installation; reaching it only by tray click or taskbar pin was the odd part.

Driven from `MainWindow.RefreshTrayIcons` — already the one place that runs on every launcher add,
remove, rename, icon change and sync-driven replacement — plus once at startup, *after*
`EnsureFlyoutShortcut` deploys the companion exe the shortcuts point at. Pruning is by what
*should* exist rather than by remembering what was written, so renames, deletions, a launcher
switched away from Web, and files left by a crash all resolve themselves.

Four things this has to get right, three of them landmines:

- **Nothing here writes an AUMID.** Per-launcher Start Menu shortcuts existed before and were
  removed precisely because they set `PKEY_AppUserModel_ID` and acted as a pin identity source:
  Windows saw two identities for one pin — the shortcut's and the companion's relaunch properties —
  and produced duplicate "(2)" pins. These are plain shortcuts running the same command the pin's
  `RelaunchCommand` runs, so they cannot compete with anything. **Do not add one.**
- **They must not live loose in `Programs`.** `MainWindow.CleanUpStaleFlyoutShortcuts` still sweeps
  the old `Little Launcher - *.lnk` naming there on every startup.
- **The MSI-era cleanup had to stop deleting the folder.** It removed a `Little Launcher` subfolder
  recursively — the same folder this now uses — so it would have silently emptied the group on
  every startup and the shortcuts would have looked like they were never created.
  `RemoveLegacyMsiSubfolderShortcut` deletes the known MSI filenames and removes the folder only if
  it is then empty.
- **The physical Start Menu path is required** (`MainWindow.GetPhysicalStartMenuProgramsDir`).
  `SpecialFolder.StartMenu` is VFS-redirected under MSIX, so shortcuts written through it land
  where only the packaged app can see them and the shell never indexes them — the feature would do
  nothing on exactly the build most people run. Same rule as everything else the shell must read.

Opening a launcher this way is the *same code path* as its taskbar pin, so a launcher in
regular-window mode lights its pinned button either way. A launcher with no pin now falls back to a
stable `LittleLauncher.Launcher.{guid}` identity rather than none, so its window gets its own
correctly-named and correctly-iconed taskbar button instead of joining a generic "LittleLauncher"
one.

The group is removed entirely when the feature is switched off
(`UserSettings.DisableWebLauncherShortcuts`) or when no web launchers remain — an empty group is
still a visible entry in All apps.

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

## One kind of web launcher

A web launcher is a **list of bookmarks whose first entry is the address it opens**. "A single
address" is a launcher with one bookmark. There is no mode switch, and no `WebUseBookmarks`.

| Concept | Where it lives |
|---|---|
| The page the tray icon opens | `Launcher.WebAddress` — `WebBookmarks[0].Url` |
| Whether the strip is drawn | `Launcher.ShowsBookmarkBar` — `WebShowBookmarkBar`, or the old rule when unset |
| Whether it stands for several sites | `Launcher.HoldsSeveralSites` — more than one bookmark |
| What is on screen right now | The tab. Never the launcher, never the bar |

**First place is a rule the user can see and can drag.** A stored "which one opens" pointer
(`WebDefaultBookmarkUrl`) was tried and is redundant once the two modes are one: it could disagree
with the order on screen, so reordering the bar changed nothing while a hidden field decided the
answer. The bar's context menu therefore carries **Set as default page**, which is a move to the
front, rather than a checkbox. It is named for the consequence rather than for the move that
implements it: as "Open the launcher here" it sat directly under Open and Open in new tab and read
as a third way to open the page, next to the two that actually do.

**The bar appears at the second bookmark, and there is no setting for it.** With one there is
nothing to pick between, and the strip would hold the address the launcher already opened at.
Adding the second page — in settings, or with the star in the flyout — *is* "turn the bar on"; a
toggle beside it would be a second step that does nothing without the first, and a first step that
does nothing without the second.

This is not the mode-inferred-from-count trap the old `WebUseBookmarks` guarded against. That flag
existed because crossing the threshold changed **what the tray icon opened**; now it changes only
what is drawn under the page, and the page is the same either way.

### Migration

`Launcher.MigrateWebModel()` brings a launcher written before the merge onto this model: `WebUrl`
becomes the first bookmark, then whichever bookmark `WebDefaultBookmarkUrl` named is moved to the
front. Both legacy fields are cleared. It is idempotent.

It runs from **two** places, and needs both:

- `SettingsManager.NormalizeAllGlyphs` — after a JSON load and after the legacy XML migration.
- `LauncherPayload.MergeInto` — after **every** sync merge. A launcher edited on a machine still
  running the older build arrives over the wire carrying `WebUrl` and no bookmarks; a
  once-at-startup migration would miss it and the launcher would land here with no address at all.
  This is why `WebUrl` and `WebDefaultBookmarkUrl` are still synced.

### Every browser is asked for

There is no setting that gives each bookmark its own browser. `WebBookmarksAsTabs` ("Treat as Tabs")
is gone: it made a plain click on a bookmark mean "switch to its tab", so the bar could not do the
one thing a bookmarks bar does. Extra browsers are now made by the gestures that ask for one — a
middle-click, a Shift/Ctrl-click, **Open in new tab**, the "+", or a page opening a window — which
puts the cost and the gesture in the same place, one page at a time.

### The bar holds no state


It opens pages. It does not own the one that is open, mark it, or close it — the same contract a
browser's bookmarks bar has, and the reason there is no active-bookmark field anywhere in the class.

| Gesture | What happens |
|---|---|
| Click | Loads it in the tab in front — *whichever* tab that is, a link tab included |
| Click the bookmark for the page already showing | Loads it again. It does **not** collapse or close anything |
| Middle-click, Shift-click, Ctrl-click, **Open in new tab** | Opens it in a tab of its own |
| Remove the bookmark for the page showing | Nothing happens to the page. It does change where the launcher opens if it was the first one |

An earlier version toggled: clicking the active bookmark collapsed the flyout back to a strip, and
the bar tinted whichever bookmark was showing. Both are gone. They made the bar a mode switch that
happened to navigate, and the tint had to be suppressed whenever a link tab was in front — a rule
that only existed because the bar was claiming to know something the tab actually knew.

**The bar is chrome, so it follows the header.** `ApplyBookmarkBarVisibility` is the single answer
to "should the bar be visible": bar mode, and the header not hidden. Nothing else writes
`_bookmarkBar.Visibility`, and that is the fix for a bug rather than tidiness. `ApplyFullScreen`
used to collapse the bar itself, which held only until the next `RebuildBookmarkBar`, and that
runs from `ApplyLauncherChanges`, so a background favicon fetch or a periodic sync put a row of
bookmarks back over a fullscreen video with no user action at all. **The skip path re-showed it
too**: "these are the bookmarks the bar already holds" is a rebuild that was not needed, not a
decision that the bar should be on screen. The address bar and the tab strip were already gated on
the header for the same reason; the bar is a row of its own below the page, so it had to be gated
explicitly rather than by sitting in the same `StackPanel`.

**There is no collapsed state.** A launcher used to open as a bare 34px strip and grow when a
bookmark was clicked; with one mode and an address that always exists, nothing could ever put it
back, so `_isExpanded`, `ExpandToContent`/`CollapseToBar`, `ApplyRootAnchor`,
`ApplyExpansionGeometry`, `StretchRootDuringResize` and `PreRenderBarOffScreen` are all gone. The
window is one size — the launcher's — and the bar is a row inside it. The strip's own settings and
close buttons went with it: they existed because a collapsed bar was the whole window and had no
header.

**What opens on show**, in priority order: a tab that is still alive (which settles it outright —
returning a live page to the launcher's address would throw away where the user was), then
`_rememberedUrl`, then `Launcher.WebAddress`. A launcher with no bookmarks at all has nothing to
open and is told so.

`_rememberedUrl` is where the launcher's **own tab was last explicitly sent** — by a bookmark click
or the address box, and by nothing else. It is deliberately *not* written from inside `Navigate`,
which is what keeps "empty" meaning **the user has not steered this launcher anywhere**: that is the
state in which a settings change to the launcher's address may still move the page, while a page the
user chose is left alone. Session state, not settings — it survives an idle unload, which is the
point, and not a restart. Closing the last tab clears it.

### Editing the bar from the flyout

`Windows/WebFlyoutWindow.Bookmarks.cs` owns the bar and every way it is edited from inside the
flyout. Launcher settings still holds the full list, and for a launcher being set up that is the
right place — but the moments that *produce* a bookmark all happen with the page on screen, and
walking to a settings window and back to act on one is the whole cost.

| Gesture | Where | What it does |
|---|---|---|
| Star at the end of the address bar | Address bar (off by default) | Adds or removes whatever the **address box shows** |
| "Add to / Remove from the bookmarks bar" | The header's "…" menu | The same action, without needing the address bar on |
| Right-click a bookmark | The bar | Open, Open in new tab, Rename, Edit address, Copy address, **Icon only**, Open in browser, **Set as default page**, Move left/right, Remove |
| Right-click the bar's empty space | The bar | **Add bookmark…** (type an address) and **Add from browser…** (pick one out of an installed browser) |
| The chevron at the end | The bar | Whatever did not fit, as a menu, aligned to the chevron's right edge. A click opens the bookmark here, a middle-click or Shift/Ctrl-click in a new tab, and a right-click turns the list over into that bookmark's own menu |
| Drag a bookmark | The bar | Reorders it, with an accent caret marking where it lands |

Rules worth not rediscovering:

- **The star acts on the address box, not on the live page.** The two differ only while the box is
  being typed into, and at that moment what is on screen is no longer what the user means — so
  typing an address and starring it without visiting it first works, and is the same gesture. The
  box's `TextChanged` keeps the glyph honest as it is typed; `UpdateNavigationButtons` re-asks
  after every navigation *and* every tab switch, because switching back to a tab already on that
  address writes the same string and raises no `TextChanged` at all.
- **The star is always offered.** Every web launcher is a list of bookmarks, so there is always
  somewhere for it to write. It gated on `WebUseBookmarks` while two modes existed, which meant the
  most obvious way to *get* a second page was missing from exactly the launchers that had one.
- **Starring appends; it never inserts.** The first bookmark is the launcher's address, and starring
  the page you happen to be on is not a request to change what the tray icon opens. Dragging it to
  the front is — a gesture with the consequence visible in it.
- **A bookmark's address is a key.** The cached icon is filed under the URL
  (`GetBookmarkIconPath`), so re-addressing one drops the icon and re-fetches, or the bookmark wears
  another site's logo. `_rememberedUrl` follows the edit when it pointed at that bookmark.
- **Neither editing nor removing a bookmark moves the page on screen.** Re-addressing one changes
  where it goes next time it is clicked; deleting one is a change to the bar. Both would be
  statefulness sneaking back in — the bar deciding what the window shows.
- **The drag draws a caret; it does not shuffle the buttons.** Moving them as the pointer passes is
  the more literal preview and cannot be done here — the element that would move is the drag
  source, and taking it out of the panel and putting it back unloads it mid-gesture. The caret
  lives in a `Canvas` overlay in the bar's own cell, so it adds nothing to the strip's layout,
  which is what its position is measured from; a caret that reflowed the row would oscillate
  between two slots. Move left / Move right stay in the context menu beside it, because a bar with
  more bookmarks than fit ends in the chevron's menu, where there is nothing to drag.
- **`_isBookmarkDragging` pins the flyout**, alongside `_isModalOpen`, `_isMenuOpen` and the resize
  and move flags. The drag carries the address as text so it can end in another application, and
  that deactivates this window — which would dismiss the flyout mid-gesture and take the bar being
  reordered with it. (Dropping a bookmark into a browser or an editor is the payoff for carrying
  the text at all.)
- **The bar overflows into a chevron; it does not scroll.** `Controls.OverflowStripPanel` lays the
  bookmarks out left to right, shows as many as fit and reports where it ran out, and the chevron
  beside it drops the rest into a menu. A horizontal scroller was the previous answer and is the
  wrong one for a 34px strip with no visible scrollbar: the bookmarks past the edge were not merely
  off screen, nothing on the bar said they existed. The hidden buttons stay children and are
  *arranged to a zero rect* rather than collapsed: collapsing changes their desired size, which
  changes what fits, which changes what is collapsed. The drag code skips them by their width for
  the same reason, or the caret anchors on one and draws at the left edge with the pointer at the
  right.
- **Right-clicking a row in the overflow menu replaces the list with that bookmark's own menu, in
  the same place.** It is the same menu the bar itself opens, so a bookmark answers a right-click
  the same way wherever it is sitting, and the chevron brings the list straight back. The new menu
  is shown *without* hiding the list first: opening it light-dismisses the list by itself, so there
  is never a moment with no menu under the pointer. There was one when the list was hidden first,
  and the cursor fell through to the flyout's 6px bottom resize grip and stayed a resize arrow,
  since nothing re-asks for a cursor until the pointer moves.
- **Keeping the list up underneath cannot be done, and both ways of trying it are traps that spring
  later.** WinUI keeps one `MenuFlyout` up at a time, so a second menu dismisses the first by
  definition. *Editing the open menu* to hold the actions (swapping the row for a
  `MenuFlyoutSubItem`, or inserting them under it) leaves a menu that never light-dismisses again,
  so every right-click stranded one on screen; assigning over an entry (`Items[at] = ...`) removes
  before it adds, which empties a single-row menu for an instant and closes it outright; and
  emptying `Items` with `Clear` takes the presenter down mid-gesture, dropping the right-click onto
  the page underneath, which answers with WebView2's own menu. *A submenu built in before the menu
  is shown* survives all of that and then cannot be opened: `MenuFlyoutSubItemAutomationPeer`'s
  `Expand` is the only public way in and it goes around the framework's cascading-menu bookkeeping,
  putting the submenu in the corner of the window and breaking dismissal again. Left to open itself
  it waits for the pointer to move, and a right-click that visibly does nothing is worse than one
  that answers with a different menu shape.
- **A right-click in that menu is answered on the pointer press, with `AddHandler(...,
  handledEventsToo: true)`**, which is why it is *not* wired to `ContextRequested` the way the bar's
  own buttons are. A `MenuFlyoutItem` marks **every** pointer press handled for its own visual
  states, whichever button it was, where a `Button` takes only the left one and leaves a right press
  to become the right-tap that raises `ContextRequested`. Inside a menu that event is never raised
  at all, so the actions were unreachable for anything that had overflowed.
- **Middle-click is taken from the press; Shift/Ctrl-click is taken from `Click`, reading the
  keyboard.** On the bar and in the menu alike, and for the same reason in both: the control claims
  the left press before any instance handler runs, so a press handler wired the plain way
  (`button.PointerPressed +=`) never saw a modified click and the gesture silently did nothing for
  as long as the bar has existed. `Click` always arrives, and `WantsNewTab()` asks
  `InputKeyboardSource.GetKeyStateForCurrentThread` whether Shift or Ctrl is down rather than
  reading a `KeyModifiers` off an event that may never reach us. Middle-click cannot use `Click`,
  which it never raises, so it keeps the press handler, and that one does need
  `handledEventsToo: true`.
- **The bar checks that its buttons still hold the launcher's *own* bookmark objects, not just
  equal ones.** `RebuildBookmarkBar` skips the rebuild when its signature of names, addresses and
  icons is unchanged, and a sync download defeats exactly that: `LauncherPayload.Merge` empties
  `WebBookmarks` and refills it with new objects carrying identical values, so the signature matches
  to the character while every button's `Tag` points at a bookmark the launcher no longer holds.
  Everything the bar does starts by asking the launcher where a bookmark *is*, with `IndexOf`: the
  actions, the moves, the removes. All of it returned -1 and did nothing at all, on the bar and
  in the overflow menu, with nothing thrown and nothing logged, until the app was restarted. It read
  as "right-click stopped working". `BarHoldsLiveBookmarks` is the reference check that closes it,
  and `WebFlyoutWindow.InvalidateBookmarks` is what tells a flyout that is already open to run it.
- **The menu is aligned to the chevron's right edge (`TopEdgeAlignedRight`), not centred on it.**
  The chevron is the last thing on the bar, so a centred menu straddles the window's edge and comes
  up half outside the flyout, around the pointer rather than under the button it came from.
- **The bar's own context menu is where a bookmark for a page you are not looking at comes from.**
  The star adds the page that is *on screen*, which is the common case and worth its one click, but
  it was the only way in: anything else meant loading the page first or walking to launcher
  settings. Right-clicking the bar's empty space offers **Add bookmark…** and **Add from browser…**,
  which is where every browser puts it. Typing an address asks one question, not two: the name
  follows the host exactly as the star's does, and the bar renames in place from the same menu.
- **"Add from browser…" needs a window, not a `ContentDialog`.** `BookmarkPicker` was written as a
  dialog for launcher settings, which is full size; the flyout is not, and a dialog cannot overflow
  its HWND. The chooser is now `BookmarkPickerView`, with two hosts: the existing `ContentDialog`
  and `Windows/BookmarkPickerWindow`, an owned window on the same rules as `TextPromptWindow`.
- **The strip accepts nothing else.** `BookmarkStrip_DragOver` returns before setting
  `AcceptedOperation` when the drag did not start on a bookmark, so a file or a link dragged over
  the bar never shows a drop cursor. A drop cursor is a promise — the same rule the item flyout's
  external drops follow.
- **Icon-only is per bookmark as well as per launcher**, and the two are not redundant: a bar of
  familiar sites wants every label gone (`Launcher.WebBookmarkIconsOnly`, in settings), while a bar
  with one awkwardly long name wants that one collapsed and the rest readable
  (`WebBookmark.IconsOnly`, from the bar's context menu). `ShowsIconOnly` resolves them in one
  place and the launcher-wide setting wins — a bookmark still showing its label under "icons only"
  would be that setting quietly failing, so the per-bookmark flag only ever adds to what is
  collapsed. While the launcher-wide one is on, the menu item shows checked but disabled: already
  the case, and not because of this.
- **`PersistBookmarks` is the one way out.** Save, `AutoSyncService.NotifyLaunchersChanged`, rebuild
  the bar, re-ask the star. A launcher change saved without telling the sync service is reverted by
  the next periodic download.

### `CurrentTargetUrl` is the only answer to "which URL"

Bar mode added a second possible answer beside `Launcher.WebUrl`, and **three** separate places
navigate. Two of them were missed when the bar was added, and both produced the same confusing pair
of symptoms: the wrong page opened, *and* the bookmark that was clicked took the wrong page's icon,
because the arriving page's favicon is adopted onto whichever bookmark the page belongs to.

- `ShowHomeContentAsync` — every show; it resolves the address once and hands it to
  `CreateTabAsync`, which no longer reads it again
- `ApplyLauncherChanges` — anything that touches the launcher, **including a bookmark's own
  favicon fetch completing**, which is how it recurred with no user action at all

Both call `CurrentTargetUrl()`. An empty result means a launcher with no bookmarks at all, which is
not an instruction to navigate anywhere. If another navigation path is ever added, it must use the
same helper.

**`CurrentTargetUrl` answers "where does this launcher open", not "what is it showing".** Once a
page is up, the tab owns that — and `_rememberedUrl` is what keeps the two from fighting. It is
written by exactly the two gestures that mean *go here*, a bookmark click and the address box, and
deliberately **not** from inside `Navigate`. So `CurrentTargetUrl` already equals what the home tab
is on whenever the user has steered it, and `ApplyLauncherChanges` re-navigating on a mismatch fires
only when the launcher's own address has genuinely changed under a tab still sitting on the old one.
Writing `_rememberedUrl` from `Navigate` instead would look equivalent and is not: the initial load
navigates too, so "the user has not steered this anywhere" would stop being a state the flyout could
recognise, and editing a launcher's address would no longer move its page.

`OpenBookmark` drives the browser directly rather than routing through `CurrentTargetUrl` — the bar
names the page, so there is nothing to resolve.

Tabs added a **second half** to the same rule: `CurrentTargetUrl` answers "which URL", and
`HomeKey` answers "may this tab be sent there". `ApplyLauncherChanges` checks both, or a favicon
fetch completing drags a link tab to the launcher's address. `PrepareContentAsync` is where the two
meet — a reopen returns to an active link tab, and only falls through to `ShowHomeContentAsync`
when the launcher's own page is what should be showing.

Icon adoption finds its bookmark by **matching the page's own address** against the bar
(`FindBookmark`) — never by remembering which bookmark was clicked, which the bar no longer tracks.
The **tray** icon is written only when the page is the launcher's own address, so a launcher holding
six sites does not end up wearing whichever one was looked at last. Matching is the better answer regardless: it also declines
to write an icon onto a bookmark the user has since navigated away from. It then independently
checks that the loaded page's host matches the bookmark's before writing (`SameHost`). A wrong page
is obvious; a wrong icon persists and looks like data corruption.

### Geometry

The window is always the launcher's configured size, so there is no expansion to animate and no
fixed root height to keep in step with a drag. Both were needed only while a bar could grow into a
page, and both are gone with it — see the note above.

The open/close slide is untouched: that moves a fixed-size window, which never had the problem.

### Warm-up

Launchers that **show a bar** are warmed up (`WebFlyoutWindow.WarmUp`), parked off screen so WinUI
composes the strip before it is ever shown — otherwise the first open showed buttons measuring and
favicons decoding on screen. This does not weaken the resource promise: what is built is a strip of
XAML and an empty window, and no browser is created until the flyout is opened.

Launchers with one bookmark are excluded — their first frame *is* the page, so there would be
nothing to pre-render but an empty window.

The bar is also only rebuilt when the bookmarks actually change, keyed on a signature of their
names, URLs and icon paths **in order** — so a drag that reorders the launcher invalidates it
exactly as an edit does. Rebuilding per open threw away laid-out buttons and decoded icons every
time.

**Every rebuild detaches what the old buttons were listening to** (`ClearBookmarkButtons`). A
`WebBookmark` lives in settings and outlives every bar built from it, so a `PropertyChanged`
handler left attached pins that button's `Image`, its `TextBlock` and the bitmap it decoded for the
life of the app. That cost little while the bar was rebuilt once per open; editing it from the bar
itself rebuilds far more often, which is the shape of the item flyout's container leak — see
[drag-drop.md](drag-drop.md).

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

`BuildForm` lays the dialog out as **three questions and then the fold**, and the grouping is the
point — a row read beside the wrong neighbours is a row the user misreads:

| Group | Rows |
|---|---|
| What it is | Name, Type, Web Address, Bookmarks |
| How it shows that content | View Mode / Icons Per Row / Show Title (shortcut launchers), Tab Bar, Address Bar, Flyout Size, Opens At, Remember Size |
| How it appears in the shell | Icon, custom icon path, Show In Tray, Show In Taskbar |
| Everything else | **Advanced** |

- **Advanced is last in the whole dialog, not last among the web rows.** It is the fold for what a
  working launcher does not need, so anything below it is something the user has to scroll past a
  collapsed section to reach. `BuildWebRows` therefore returns it separately from its option rows.
- **The icon sits with the rows that decide where it is seen.** It used to sit directly under the
  launcher's content, where "Icon" read as the icon of the thing being configured; beside Show In
  Tray and Show In Taskbar it reads as what it is — the tray icon, and the only place it appears.
  The "address before icon" rule below still holds, and now holds by a wide margin.
- **Bookmarks is a collapsed `Expander`**, like Advanced. Most launchers hold one page, so laid out
  flat the list was a second copy of the address already in the field above, taking up most of the
  dialog. Its header carries the count and the sentence that has to survive being folded away —
  that a *second* bookmark is what produces the bar. Every edit inside it moves that count
  (`_bookmarksListChanged`), or the header describes the list as it was when the window opened.
- **Tab Bar and Address Bar are promoted out of Advanced.** Both change what the flyout *is* — a
  page versus a small browser — rather than tuning one that already works, and Address Bar is where
  the star for bookmarking the current page lives. Tab Bar was reachable only from the flyout's "…"
  menu, which is the wrong place to be the *only* place: that menu is for changing your mind while
  looking at a launcher, not for discovering that an option exists.
- **They are listed tabs-then-address, in the dialog and in the "…" menu both**, because that is the
  order the strips appear in on the flyout: header, then tabs, then the address of whichever tab the
  tabs chose. Two toggles for two adjacent rows of chrome read as mislabelled when the list
  disagrees with the window.
- **Opens At and Remember Size were promoted too**, for the same kind of reason: they answer the
  question Flyout Size does, and they are what a user reaches for straight after dragging a flyout
  and finding the change did not stick. Opens At used to sit beside a Remember Position toggle whose
  subtitle it had to describe; that toggle is now one of its own values, so there is nothing to
  explain across two rows.

Zoom, When Hidden, Unload After, Reload On Open, Open Links In Browser, Pin Open, Regular Window,
Close On Focus Loss, Taskbar Click, Site Permissions, Profile and Browsing Data stay in Advanced: they tune a launcher
that already works, and all of them on one surface made the common case (paste a URL, pick a size)
read as a form to fill in. Pin is safe to demote twice over — the flyout's header has a button for it.
Zoom is here *and* on the "…" menu rather than promoted out of Advanced, because it is genuinely both:
the level a launcher is set up with, and the one to put back after an accidental Ctrl+wheel.

New per-launcher web settings should default to Advanced unless a launcher is unusable without
them, or unless the setting changes what the flyout is rather than how well it is tuned.

**A promoted toggle applies to the open flyout immediately.** Address Bar and Tab Bar both call
`WebFlyoutWindow.ApplyLauncherChanges`, so they behave like their "…" menu twins rather than taking
effect on the next open — a toggle whose effect is invisible until the window is reopened is
indistinguishable from one that did nothing.

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

**Trusted grants are also seeded up front** (`SeedTrustedPermissionsAsync`), for the launcher's own
origins only. Saving on request is not enough on its own, because a well-built app *checks before it
asks*: Teams reads `Notification.permission` on load, finds `default`, shows "Stay in the know. Turn
on desktop notifications." and then renders its own in-page banners — because as far as it can tell
the desktop cannot show them. Nothing is ever requested, so nothing is ever saved, and the prompt
returns on every load forever. Seeding breaks the loop, so the first read already says `granted`.

**There was a "Trust This Site" toggle here, and removing it was the fix.**
`Launcher.WebAllowAllPermissions` granted camera, microphone, location and notifications without
asking, and seeded those grants into the profile so a site that checks before asking would see them.

It broke the thing it was meant to help. **A site sets a permission up during its own permission
flow** — it asks, and on being granted it does the setup that grant implies. For notifications that
setup is *registering the push worker*. A site handed the grant before it asks never runs that code,
so it ends up holding a permission it cannot use, with nothing to deliver through. WhatsApp behaved
as though it had no notification permission while the toggle was on, and Messenger's own permission
dialog never resolved, leaving the page dimmed behind it.

Notifications were taken out of the seeding first, which was not enough: the request handler still
answered Allow silently, so the site still never ran its flow. The toggle is gone entirely. Every
request is answered once in the flyout's prompt bar and remembered per profile, exactly like a
browser — which is also the only behaviour that can be reasoned about, since "granted" now always
means the site asked and was answered.

## Browser extensions

WebView2's whole extension API is `AddBrowserExtensionAsync(folder)`, `GetBrowserExtensionsAsync()`
and the `AreBrowserExtensionsEnabled` option. There is **no store integration, no `.crx` handler and
no browser-action UI** — so `Services/BrowserExtensionService.cs` and
`WebFlyoutWindow.Extensions.cs` supply the parts a browser would do for itself.

- **The option goes on for every launcher, unconditionally.** The options must match across every
  environment on a user-data folder — connecting to an already-running one with a different value
  fails with `ERROR_INVALID_STATE` — and the shared profile puts several launchers on one folder. Set
  it only where an extension exists and shared-profile launchers start failing to initialise while
  private ones carry on fine.
- **Extensions belong to a profile, so the list is app-wide.** `UserSettings.BrowserExtensionFolders`
  is applied to whichever profile is starting (`ApplyAsync`, called as each browser is created), which
  is what makes one install cover every launcher sharing a profile and gives a private launcher its
  own copy. Matching is by declared `name`: the id comes back from the *install*, so there is nothing
  to compare a folder against beforehand.
- **The store's button is intercepted, not replaced.** The Chrome Web Store renders and offers to
  install, because WebView2 presents as Chrome; it cannot finish, because the last step is a private
  API WebView2 does not implement. Where that degrades into an ordinary package download,
  `HandleDownloadStarting` takes it: a CRX3 is `Cr24`, a version, a header length, that many header
  bytes, then a plain ZIP — so skipping to the ZIP and extracting produces exactly the folder the API
  wants. Anything that is not an extension package is left alone, default download UI included.
- **Archive extraction is path-checked.** A zip entry may name `..\..nything`, so each entry's
  resolved path is required to be under the target. The file came from the internet even though the
  user asked for it.
- **One button that opens a list, plus whichever are pinned beside it** — the way every browser
  does it, and not one button per extension: a flyout header is a handful of slots wide, so four
  extensions would spend them all and leave nothing for the window controls. The list is also the
  only place an extension with *no* popup can be seen at all, which for an MV3 blocker is every
  time. They sit **immediately after the address-bar toggle** (`ExtensionSlot`), with the page
  controls rather than among the window controls further right — the same reasoning that keeps
  Back and Reload on the left. The slot is computed from the address button's index rather than
  written as a constant, so adding a header button later cannot silently move them.
- **`BrowserExtension.Folder` must be persisted, and is kept out of the payload by projection.**
  It carried `[JsonIgnore]` briefly, to keep it from syncing — and since local settings use the
  same class, that dropped it from disk too: every extension came back after a restart with an
  empty path, loaded into nothing, and vanished from the header with no error anywhere.
  `Portable()` is what keeps it out of the sync payload, by projecting id and name into fresh
  objects. An attribute cannot express "persist here, not there"; a projection can.
  `RepairMissingFolders` re-pairs saved names with folders still on disk, for the settings files
  that were written during that window.
- **The popup is the toolbar that does not exist.** `CoreWebView2BrowserExtension` carries an id, a
  name and an enabled flag and nothing about browser actions, so `action.default_popup` and its icon
  are read from the extension's own `manifest.json` — which the host has, because the host unpacked
  it — and shown by `ExtensionPopupWindow`, a WebView2 on `chrome-extension://{id}/{page}`
  **using the launcher's own user-data folder**. On any other profile the popup loads, looks right,
  and behaves as though the extension had never run. Extensions with no popup get no button; an MV3
  blocker does its whole job through `declarativeNetRequest` and content scripts with no UI at all.
- **Manifest V3 only**, since WebView2 tracks Chromium's extension platform. uBlock Origin proper is
  MV2 and cannot load; uBlock Origin Lite is the MV3 product and does.

## Continue where you left off

`WebFlyoutWindow.Session.cs`. The addresses a launcher had open are written to
`Launcher.WebSessionTabs` and put back the next time it is **opened**.

- **Restored on open, never at startup.** That is the whole of how it coexists with the resource
  contract: a launcher nobody opens still builds nothing, and one that is opened pays exactly what
  its tabs cost — which is what they were already costing before the restart. The active tab is
  created first and in the foreground; the rest follow behind it, so only the page being looked at
  renders.
- **Once per run** (`_sessionRestored`). After the first open the tabs are live and are themselves
  the session; re-reading the stored list would resurrect tabs the user has since closed.
- **The save is guarded while a restore is mid-flight** (`_restoringSession`), or the half-built
  list overwrites the stored one as each tab is created.
- **Saved from `NavigationCompleted`, not only from `RefreshTabBar`.** This was the bug that made
  the feature look absent: `RefreshTabBar` catches opens, closes and switches, so a launcher opened
  once and then browsed within recorded the address it started at — or, far more often, nothing at
  all. `NavigationCompleted` is the moment a tab's address is finally real, and it runs **per tab**,
  not per active tab.
- **A browser that has not navigated yet reports `Source` as the empty string, not `null`.** So
  `Source ?? NavigatedUrl` never fired, and the save that runs during tab creation found nothing to
  record. Test the string, not the reference.
- Each save compares against what is stored and writes nothing when the set has not moved — it runs
  on every navigation of every tab, and each write is the whole settings file.
- **Closing the last tab clears the session.** It is an explicit "I am done with this", not a place
  to come back to.
- **No tabs is never a session, and this one bit.** A launcher reaches zero tabs two ways and only
  one of them is the user saying so. The other is teardown, and the *default* hidden policy performs
  one on a timer: `UnloadWebView` closes every browser and then refreshes the strip, which reached
  `SaveSession` with an empty list and wrote the session away. So a launcher left alone for
  `WebIdleUnloadMinutes` silently forgot every tab it had, which is exactly what the feature exists
  to prevent. `SaveSession` now returns on an empty list; `ClearSession` is the only thing that may
  forget one.
- **Not synced**, for the reason `WebFlyoutPosition` is not: a set of open tabs is what one machine
  was doing, not a preference about the launcher.
- Addresses only. Scroll position, form state and history live in the browser that was torn down,
  and promising them would mean keeping it. Same bargain `Ctrl+Shift+T` makes.

## The addresses a real browser answers and WebView2 does not

`WebFlyoutWindow.BrowserPages.cs` intercepts `chrome://`, `search://` and friends in
`NavigationStarting`. WebView2 ships without Chrome's built-in pages, so an extension or a page
linking to one navigates to a 404 — which is what sent Bitwarden's "unlock with biometrics" setup
to `search://local-ntp/local-ntp.html` and left it dead.

The new-tab page becomes a **blank tab with the address bar showing**, which is what a new tab is
for; the rest are answered or refused rather than navigated to. A new tab deliberately does not
assume the launcher's address: an empty tab the user asked for is a place to type, not another copy
of the page they already have.

**An empty tab has to settle the status overlay itself.** `CreateTabAsync` raises "Loading…" on the
way in, because a tab being built is nearly always about to load something, and `NavigationCompleted`
is what takes it down again. A tab with no address never navigates, so nothing was ever going to:
the "+" opened a spinner that span for the life of the tab. `ShowEmptyTabStatus` replaces it with the
one line saying what an empty tab is for, and it is not "nothing", because an unnavigated browser
paints nothing at all and the tab would be a bare rectangle of window background. It runs from three
places, which is the whole set of ways a blank tab ends up in front: creation with no address,
`ActivateTab` switching back to one, and `NavigationCompleted` for the `about:blank` a new-tab
request is answered with.

**Answering a new-tab request clears the tab's `NavigatedUrl`.** The navigation is cancelled and the
tab blanked, so the address it was built with — or was last sent to — is no longer where it is going,
and leaving it there would leave the tab claiming a destination it has abandoned. That claim is what
decides whether the tab gets the address bar and the notice, which is exactly what a new tab needs
and what the interception exists to give it. It is why `TryHandleBrowserPageNavigation` takes the
`WebTab` as well as the browser.

## Saved logins are scoped to the profile

`UserSettings.ProfilesWithoutPasswordManager`, surfaced as **Save Logins** beside Sign-ins in
Advanced. Turning it off stops WebView2 offering to save logins and filling them in, which is what
a password manager extension needs — two of them competing means the built-in one keeps proposing
its own older saved logins over the manager's.

- **Per profile, not per launcher**, and the row says so when the launcher is on the shared one.
  Saved passwords live in the profile, so the switch governing them has to be scoped the same way.
  The platform scopes it neither way (`IsPasswordAutosaveEnabled` is per browser instance), so this
  is what decides.
- **Read when a browser is created**, so `ReloadProfile` is what makes the change immediate rather
  than "next time this launcher is opened".
- **Forget** clears the saved logins for the whole profile, because WebView2 exposes no way to
  enumerate them — only to clear the category. It needs a live browser, and says so instead of
  reporting a success that did not happen.

## Profiles

Each web launcher gets `%AppData%\LittleLauncher\WebProfiles\{launcherId}` as its WebView2 user-data
folder (via `MainWindow.GetPhysicalAppDataDir()`, so it survives MSIX VFS redirection). That is what
keeps a dashboard signed in across restarts, and keeps two launchers signed in as different users.

**Cookies and sessions are per launcher, not per bookmark.** Every bookmark in one
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
| 1 | `WebFlyoutPosition` | `WebAnchor` is `WebAnchors.LastPosition` *and* the flyout has been dragged |
| 2 | `WebAnchor` (`WebAnchors`) | A corner, edge or centre has been chosen |
| 3 | The tray icon | The default, and the fallback for `LastPosition` with nothing dragged yet |

**`WebAnchor` is the whole answer**, `LastPosition` included. It used to be an anchor plus a
`WebRememberPosition` flag, and that pair had a dead cell: with remembering on, nine of the ten
anchors decided only where the *very first* open landed — before the drag that is the next thing the
user does. Follow the tray, sit in a fixed spot, and stay where I put it are three mutually exclusive
answers to one question, so they are three values of one setting. `MigrateWebModel` turns the old
flag into the new value.

Rank 1 is why **changing the anchor clears `WebFlyoutPosition`** — otherwise picking a corner on a
flyout that had been dragged would appear to do nothing, the remembered position silently outranking
the choice just made. The exception is picking `LastPosition` itself, which *is* that position, and
switching away and back deliberately does not resurrect it.

`RememberFlyoutPosition` gates on the same value: under any other anchor a drag is not written
anywhere, so it holds while the flyout stays open and the next open goes back to the tray or the
corner. That is the entire difference between `LastPosition` and the other ten.

An anchored flyout is placed on the **work area of the monitor whose tray icon was clicked**, not
the primary monitor: a corner should mean a corner of the screen being worked on. It also slides in
from the nearer edge — down from a top anchor, up from anything else — rather than travelling
across the screen from wherever the tray happens to be.

### The shared profile

`Launcher.WebSharedProfile` (Advanced → **Sign-ins**) points a launcher at `WebProfiles\Shared`
instead of its own folder, pooling it with every other launcher that sets it. **New launchers are
created shared**: several launchers onto one system otherwise means signing in to that system once
per launcher, and again every time a session expires. Private is still there for the case that
argued for isolation originally — two launchers as two accounts on the same site.

**It is `true` at creation, never a model default.** `WhenWritingDefault` omits a property holding
the CLR default, so a `= true` initialiser would read as `true` for every launcher that never stored
the field. That is not a cosmetic mistake here: it decides which folder a launcher's cookies live in,
so it would silently move every existing launcher onto a profile it had never signed in to, and
`false` could never be persisted afterwards. `ShowTitle` is set at creation for the same reason, one
consequence less severe.

**Existing launchers are deliberately untouched**, and there is no migration. Profiles cannot be
merged — cookies are one encrypted SQLite database per profile and local storage one leveldb, so
N private profiles cannot become one shared one without signing each site in again. Switching a
launcher across is reversible, though: nothing deletes a profile folder except the explicit
**Clear** button (`ClearBrowsingDataAsync`), so a launcher flipped to shared can be flipped back and
its old session is still in place.

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

## The address is a setting, not the first bookmark

`Launcher.WebHomeUrl` is where a web launcher opens. It is **not** a pointer into the bookmark bar,
and a bookmark holding the same URL is an ordinary bookmark that nothing tries to merge or notice.

This reverses an earlier design in which the address *was* the first bookmark. That design's
objection is recorded on `Launcher.WebAddress` and was sound: a hidden "which one opens" field could
disagree with the order on screen, so reordering the bar appeared to do nothing. A home URL does not
re-open it, because it never claims to be one of the bar's entries and so has no order to disagree
with. It is the same split every browser makes between a home page and a bookmarks bar.

What forced the change is folders: position zero can hold something with no address at all.

Consequences worth knowing:

- **Empty means "the first bookmark", and `MigrateWebModel` writes it down.** The fallback keeps
  every settings file written before this opening where it did, so the CLR default is the compatible
  direction - but a launcher left on the fallback is still one whose first bookmark *is* its home
  page, which reordering the bar would move. The migration seeds `WebHomeUrl` from the first
  bookmark once, which changes nothing (the value is identical) and is what actually separates the
  two. The legacy `WebDefaultBookmarkUrl` now becomes `WebHomeUrl` rather than being converted into
  position.
- **Editing the address in launcher settings no longer rewrites a bookmark.** The one exception is a
  launcher with no bookmarks at all, which is one being set up: it gets a bookmark for the address
  too, so it arrives with something in the bar.
- **"Set as home page" on the bar moves nothing, and links nothing.** It used to drag the bookmark
  to the front, because the front was the address. It now copies the bookmark's URL and that is all
  that happens: the bookmark is not remembered, marked or referred to, so renaming it, re-addressing
  it or removing it later leaves the home page where it was. The row is a convenient way to type an
  address the user already has, not a way to make one bookmark special.
- Removing or reordering bookmarks no longer changes where the launcher opens, and the settings
  list says so by saying nothing: the first row used to carry an "Opens with the launcher" badge,
  because being first was the only thing that made it the address. Editing that row's address now
  edits the bookmark like any other rather than being routed into the launcher's address.
- **The address field seeds a bookmark only for a launcher being set up** - no bookmarks *and* no
  home page. It commits on lost focus, so without the second half of that test, deleting a
  launcher's last bookmark and then clicking anywhere near the field re-created it, which reads as
  a bookmark that refuses to be removed.
- **Home is `GoHome`, not `ShowHomeContentAsync`.** Those answer different questions, and wiring the
  button to the wrong one made it do nothing at all: `ShowHomeContentAsync` asks "what should this
  launcher be showing", whose answer is `CurrentTargetUrl` - the *remembered* address once the user
  has clicked a bookmark. Click a bookmark, click Home, and it navigated to the page already on
  screen. `GoHome` aims at `WebAddress`, and overwrites the remembered address on the way, because
  going home is itself a steer.
- **The header carries a Home button**, beside back, forward and reload. It is not decoration: once
  the home page is a setting of its own it need not be in the bar at all, so there may be no other
  way back to it. `Alt+Home` (`nav.home`) already did this, and a gesture with no visible control is
  a feature only its author knows about.

## Folders in the bookmark bar

A `WebBookmark` with `IsFolder` and `Children` is a folder, the same shape a group takes in an item
launcher (`LauncherItem.IsGroup`). It draws with Segoe Fluent's Folder glyph and never a favicon,
because it stands for a group of pages rather than any one of them, and clicking it opens a menu of
what it holds - which is what a bookmarks bar folder is in every browser, so it costs no new
affordance to learn.

- **Folders nest, without a depth limit.** A folder inside a folder is a submenu, built recursively.
  Importing a browser's bookmarks brings whatever arrangement the user already had, and flattening
  or refusing that on the way in would be losing their arrangement rather than honouring it.
- **A folder opens a popup, not a `MenuFlyout`** (`WebFlyoutWindow.FolderPopup.cs`). A menu was the
  first answer and cannot do the one thing a bookmarks bar folder has to: a `MenuFlyoutItem` is not
  an element that can be given `CanDrag`, menu rows mark every pointer press handled for their own
  click bookkeeping, and a menu light-dismisses on pointer activity outside itself - so a drag that
  starts in a folder and ends on the bar closes the menu half way through. No browser uses the
  platform menu here either. The popup is assembled from what the window already had: the rows are
  the bar's own buttons, dragging through `WireStripDragSource`, and the drop index is the
  Y-midpoint scan the flyout's edit mode uses. `ShouldConstrainToRootBounds` is what lets it escape
  a window that may be a 34px strip; an owned window would put the rows in a different XAML tree and
  the drag between them out of reach.
- **The feedback is a line and a tint, never a caption.** A 2px accent caret marks the gap a
  reorder would land in, and hovering a nested folder tints that row instead. `DragUIOverride`'s
  caption is switched off in the list: it is 260px wide and the caption box covered most of the rows
  the user was reading to decide where to drop. The bar keeps its caption, where there is room.
- **A nested list cascades off its parent's right edge, overlapping it slightly** (32px) and lifted
  a little (9px) so its first row lines up with the row that opened it. Both are what a cascading
  menu does everywhere else, and both matter more in a flyout than in a full window: the overlap
  shortens the pointer's trip from row to child, and it buys back width, which is the difference
  between two levels fitting and one. It folds across the parent's left edge when there is no room
  to the right. Offsetting from the row's own position instead - the first attempt - put the child
  exactly where the row was, so it covered the list it came from and read as the subfolder being
  replaced by its contents.
- **A list sizes to its rows**, between 150 and 300 DIPs. A fixed width was tried first and made a
  folder of short names take a third of the flyout for nothing; the cap stops one long bookmark
  claiming the window. Everything that positions a cascade therefore reads the list's *own* width,
  never a constant, or a narrow list is placed as though it were the widest possible.
- **A row stretches its content.** `HorizontalContentAlignment` on a `Button` defaults to sizing the
  content to itself, so the row's star column had nothing to expand into and the chevron sat against
  the name rather than at the row's right edge. Stretch is what makes the right-aligned column mean
  anything.
- **They cannot leave the window, and that is the price of the drag.** The overlay is inside the
  flyout's root grid because that is the only tree XAML will route a drop into, so a cascade is
  clamped to the window. If deeper folders become common the answer is drill-down navigation - one
  list at a time with a back row - not a wider cascade.
- **Placement waits for a real height.** The list's height is what decides how far *up* from the bar
  it starts, so a first pass before layout put its top edge level with the bar and hung the whole
  list off the bottom of the window. It measures if it must, refuses to place at zero, and re-places
  on `SizeChanged` rather than only on `Loaded`.
- **A bookmark is filed by dropping it on a folder** - on the bar or on a row inside a popup - or
  through *Move to folder* on its own menu, which lists every folder by path (`Work / Dashboards`)
  because two folders at different depths may share a name. The drop counts only the middle half of
  a folder, so a bookmark can still be dropped *beside* one; without that margin a folder would
  swallow every nearby drop and reordering past it would be impossible.
- **Reordering works at every level**, by drag inside the popup and by *Move up / Move down* on the
  row's menu. The menu resolves the collection that actually holds the bookmark (`OwnerOf`) rather
  than the launcher's top level, which returned -1 for anything inside a folder and handed back an
  empty menu - so a right-click inside a folder opened nothing at all. The rows are named for the
  direction they travel: left/right on the bar, up/down in a popup.
- **A bookmark dragged out of a folder onto the bar is detached and inserted**, rather than moved:
  the bar's drop used to look the dragged bookmark up in the top-level list and give up when it was
  not there.
- **A folder cannot be moved into itself or into anything it contains.** That would take the whole
  branch off the bar with no way back to it.
- **Removing a folder keeps what was in it**, returning its contents to *the collection that held
  the folder* - the bar for a top-level one, the parent folder for a nested one - at the position it
  occupied. A remove that quietly deletes bookmarks the user never selected is not what anyone means
  by "remove folder", and there is no undo here to lean on. Like every other action on a bookmark it
  resolves that collection with `OwnerOf`: reading the launcher's top level directly is what made
  this, the right-click menu and the drag out of a folder each silently do nothing in turn.
- **The bar's signature includes folder contents.** A folder's menu is built from its button, so a
  bookmark added or renamed inside one changes what the bar can show while every top-level entry
  stays identical - without it the rebuild is skipped and the folder keeps opening a menu of what it
  used to hold.
- **Launcher settings is the full tree**, indented one level per depth, and each row acts on the
  collection that actually holds it. A row moving itself against the launcher's top-level list would
  silently jump out of its folder.
- **The jump list flattens them.** A jump list is one flat menu with no submenus, so a folder can
  only appear as its contents, in the position the folder held.
- `Launcher.WebAddress` skips folders when falling back to the first bookmark: a folder has no
  address to open.

## The bar is a setting, and settings no longer lists bookmarks

**Launcher settings has no bookmark list.** It was a second editor for something the bar does better
and in front of the pages being bookmarked - adding by address or from a browser, renaming,
re-addressing, foldering, reordering by drag, removing - and keeping both meant two places to learn,
two to keep in step, and a folder tree rendered twice. The copy that went is the one that could not
see what page you were on. What is left is a **Bookmarks Bar** toggle, which is the one thing the
bar cannot do for itself.

`Launcher.WebShowBookmarkBar` is a `bool?`: on, off, or never chosen. Null falls back to the rule
the bar had before it was a setting - a second bookmark shows it - so nothing changes for a launcher
whose owner has never said. A plain `bool` seeded once by a migration was tried first and is what
this replaces; see the note in [user-settings.md](user-settings.md) for why that could not survive a
sync download.

**Bar visibility and "holds several sites" are now different questions, and conflating them would
break two rules quietly.** While the bar appeared only once there was a second bookmark, visibility
*was* that fact, and both of these leaned on it:

- a launcher of several sites must not adopt one page's icon as the launcher's own;
- its jump list lists its bookmarks rather than repeating what the button already does.

Both now ask `Launcher.HoldsSeveralSites`. Reading visibility instead would strip the icon from every
launcher the moment the bar defaulted on, and would put a single-address launcher's one bookmark
back on its jump list.

## The tab menu

Right-clicking a tab opens a menu (`WebFlyoutWindow.Tabs.cs`): reload, duplicate, copy address, open
in browser, add/remove bookmark, close, close others, reopen closed. It follows the bookmark menu's
shape exactly - the same local `Item`/`Divide` helpers, `IsEnabled` rather than hidden rows, and the
More menu's convention of a row whose *text* says which way it will go for the bookmark toggle.

**Every entry acts on the tab it was opened on, not the tab in front.** That is the point of having
it on a strip where any chip can be right-clicked without being switched to, and it is why `ReloadTab`
and `TabUrl` exist alongside the header's `ReloadPage`, which acts on `_webView`. It opens *below* the
chip where the bookmark bar's menus open above theirs, because both open into the window rather than
off the edge of it.

Both flyout-window menu rules apply, for the reasons in [xaml.md](xaml.md):
`ShouldConstrainToRootBounds = false`, and `_isMenuOpen` set from `Opened`/`Closed`.

## The launcher icon, and why it may not shrink

A web launcher's tray icon is `web-favicon-{launcherId}.png`, written by two paths that disagree about
quality: `AdoptPageIconAsync` takes `CoreWebView2.GetFaviconAsync`, which is the browser-tab favicon
and commonly 16 or 32px, while `AdoptHighResPageIcon` takes what the in-page probe found in the web
app manifest or as an `apple-touch-icon`, commonly 512px or more.

**The favicon path refuses to shrink what is already stored** (`WouldShrinkIcon`). Discord is the case
that forced it, and it was failing in both directions at once: its declared icon is 16px, which a tray
icon and far more a taskbar pin upscale into a blur, and it swaps that icon for an unread-badge
variant as messages arrive - so the launcher's identity flickered with the message count. Measured on
the dev machine: `web-favicon-{discord}.png` was 16x16 while another launcher's was 1024x1024.

Sizes are measured, not inferred from which path produced them: a page is free to declare a good
`link rel=icon`, and a site that genuinely changes its logo should still be able to. Only shrinking is
refused. Anything unreadable answers "no opinion" rather than blocking, so a launcher can never be
stranded on an icon nothing may replace.

### Three things had to be true before any of it worked

The high-resolution path existed long before it ever ran. Each of these failed silently, and the only
symptom was a launcher that stayed blurry, which is indistinguishable from a site that offers nothing
better. The probe now reports what it found at every step (`pageIconProbe`), which is how they were
finally told apart.

- **The bridge must know its own tab.** `AdoptHighResPageIcon` used to find the tab by matching the
  `CoreWebView2` an event handed back against the one held on each tab. That comparison never
  succeeded, so every icon the probe found was declined as "not the launcher's own tab". The tab is
  now captured when the bridge is installed, which is what every other per-tab handler on that
  browser already did.
- **An `.ico` is a candidate worth fetching.** It declares no `sizes`, so it scored 32 and lost to the
  96px floor. It is a container, and a plain favicon.ico routinely holds a 256px frame beside the 16
  the tab uses. It now scores 128: high enough to be fetched, low enough that a site declaring a real
  manifest icon still wins.
- **The frame inside it has to be read by hand.** Since Vista the large frame is usually an embedded
  PNG while the small ones stay uncompressed, and `System.Drawing.Icon` does not see the PNG ones -
  asked for 256 it returned Discord's 48. `LargestIcoFrame` reads the directory itself.

Measured on Discord end to end: 16x16 before, 48x48 with only the first two fixed, 256x256 with all
three. A site whose manifest fetch is blocked by its own CSP still gets nothing, and Teams is
currently in that group.

## Regular-window mode and the pin identity

A launcher under `Launcher.WebRegularWindow` gets a real taskbar button, and it lights the launcher's
pin only while the window carries the same AppUserModelID the pin does. **A re-pin mints a fresh
AUMID**, so the identity moves while the presentation does not, and a window still carrying the
previous one does not fail quietly: it raises a *second* taskbar button beside the pin the user just
clicked.

`ApplyTaskbarButton` therefore re-stamps on every show, ahead of its own "nothing changed" return -
that early exit is what let a live window keep a dead identity for as long as it stayed open. The
window is torn off the taskbar and re-added when the identity changes, because the shell reads the
AUMID when it creates the button and groups from that; rewriting the property under a live button
leaves the grouping where it was.

## The taskbar jump list

A pinned web launcher's right-click menu lists its bookmarks (`Services/JumpListService.cs`, which
also does the same for an item launcher's items). Three decisions are specific to web launchers:

- **A launcher with a single address publishes nothing.** It shows no bookmark bar, and its one
  entry would go exactly where clicking the button already goes. A menu whose only line repeats the
  button is worse than no menu, so Windows shows the plain right-click menu instead. The gate is
  `Launcher.ShowsBookmarkBar`, the same test the bar itself uses.
- **A bookmark opens in a new tab, in front.** This is the only place a bookmark is reached without
  the launcher being on screen, so there is no "tab in front" the user chose to replace - only
  whatever the launcher was left on, possibly days ago. In front rather than behind, because unlike
  a middle-click the gesture says nothing but "show me this". The exception is a launcher with
  nothing loaded at all, where the bookmark simply becomes its first tab.
- **The show is awaited before the tab is added.** A show may be restoring the tabs the launcher had
  open last time, which builds tabs and activates one; a tab added alongside that races it.
  `ShowFlyout` keeps its `PrepareContentAsync` task in `_contentPreparation` for exactly this.

A task carries the bookmark's position **and a token hashed from its name and URL**, because a
published list is a snapshot that can outlive an edit. The token decides; the position only saves a
search. When neither finds a bookmark the launcher is opened instead, so a stale task is never a
dead click and never opens the wrong page.
