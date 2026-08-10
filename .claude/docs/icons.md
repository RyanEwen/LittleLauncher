> **Scope:** Use when working with app icons, tray icons, shortcut icons, or window icons. Covers which icon files exist, where each icon surface pulls from, and how to update them correctly.
> **Governs:** `**/MainWindow.xaml.cs`, `**/SettingsWindow.xaml.cs`, `**/LaunchersPage.xaml*`, `**/LauncherShortcut/**`, `**/HomePage.xaml.cs`, `**/FlyoutConverters.cs`, `**/FlyoutWindow.xaml*`, `**/IconGallery.cs`.

> **Note on glyph code points:** Segoe Fluent Icons glyphs below are written in `U+XXXX` code-point notation. In C# they are string escapes — e.g. `U+E840` is written `""`.

# Icon System

Little Launcher uses a flat upright rocket as its identity icon. The **app identity** (Start Menu, settings window, Home page) always shows the blue rocket. Users can change each launcher's **tray icon** and **pinned taskbar icon** independently.

## Icon Surfaces

| Surface | Source | User-configurable? |
|---|---|---|
| **System tray (per launcher)** | `ResolveTrayIcon(Launcher)` → preset PNG, glyph, composite, or custom image | Yes (`Launcher.TrayIconMode`) |
| **Pinned taskbar shortcut** | Per-launcher `app-icon-{id}.ico` in `<AppDataDir>` via `.lnk` IconLocation | Yes (per launcher `TrayIconMode`) |
| **Settings window titlebar** | `settings-icon.ico` (blue rocket + gear overlay) | No — always Blue rocket |
| **Settings window taskbar entry** | `settings-icon.ico` via `WM_SETICON` + `AppWindow.SetIcon(IconId)` | No — always Blue rocket |
| **Settings window Alt-Tab** | `settings-icon.ico` via `AppWindow.SetIcon(IconId)` | No — always Blue rocket |
| **Home page app icon** | `Resources/AppIcons/Blue.png` loaded directly | No — always Blue rocket |
| **Start menu shortcut** | `exe,0` (embedded icon) | No — always Blue rocket |
| **Exe embedded icon** | `Resources/LittleLauncher.ico` (compiled into exe) | No — always Blue rocket |
| **Pin-to-taskbar dialog** | Per-launcher `app-icon-{id}.ico` loaded via `WM_SETICON` in companion exe | Yes (per launcher `TrayIconMode`) |
| **Windows notifications (per launcher)** | `toast-icon-{id}.png` written by `SaveToastIconToAppData(Launcher)` | Yes (per launcher `TrayIconMode`) |

## Key Files

- **`Resources/LittleLauncher.ico`** — Multi-resolution Blue rocket (16–256px). Embedded into the exe at build time. This is the fallback icon for all surfaces. Generated from `Resources/AppIcons/Blue.png`.
- **`Resources/AppIcons/*.png`** — Preset icon PNGs (Blue, Green, Teal, Red, Orange, Purple). Flat upright rockets stretched 20% horizontally for a wider profile. Copied to output at build time. Loaded at runtime by `ResolveBaseIconBitmap()`.
- **`<AppDataDir>/app-icon-{launcherId}.ico`** — Per-launcher runtime icon. Written by `SaveResolvedIconToAppData(Launcher)`. The first launcher's icon is also copied to `app-icon.ico` by this method.
- **`<AppDataDir>/app-icon-{launcherId}-pin{tick}.ico`** — Timestamped copy created at pin time. Used by the companion exe for both `LoadImage` (WM_SETICON) and `RelaunchIconResource`. Windows caches icon bitmaps per file path, so each pin gets a unique filename. `CleanUpStaleIconFiles()` keeps only the most recent per launcher.
- **`<AppDataDir>/toast-icon-{launcherId}.png`** — Per-launcher notification icon, 256px PNG.
  Written by `SaveToastIconToAppData(Launcher)` from the same `ResolveBaseIconBitmap` every
  other surface uses. **PNG, not `.ico`:** the notification platform does not render
  multi-image icon files, so pointing `SetAppLogoOverride` at one shows nothing at all.
- **`<AppDataDir>/app-icon.ico`** — Canonical icon for shortcuts (always mirrors first launcher's icon). Used by `.lnk` shortcuts and the Settings window.
- **`<AppDataDir>/settings-icon.ico`** — Runtime-generated icon: the current app icon composited with a gear glyph overlay (dark circle + white gear in bottom-right corner). Written by `SaveSettingsIconToAppData()`. Used by the Settings window.
- **`<AppDataDir>/LittleLauncherFlyout.exe`** — Copy of the companion exe deployed by `EnsureFlyoutShortcut()` for all build types. Pinning uses this copy.
- **`<AppDataDir>/main-exe-path.txt`** — Breadcrumb file containing the main exe path. Read by the companion exe as a fallback when `FindWindow` fails.
- **`%AppData%\LittleLauncher\icons\`** — User-chosen gallery images (`custom-*`, `selfhst-*`). The path stored on the item/launcher points here.
- **`%AppData%\LittleLauncher\icons\selfhst\`** — The selfh.st **browsing** cache: `index.json` plus one `{reference}.png` per icon the gallery has drawn. Never referenced by settings — see [The selfh.st tab](#the-selfhst-tab).

> **`<AppDataDir>`** = the path returned by `MainWindow.GetPhysicalAppDataDir()`. See [MSIX VFS Redirection](#msix-vfs-redirection) below.

## TrayIconMode Values

`Launcher.TrayIconMode` is a **string** property. Values are defined as constants in `TrayIconModes` (in `Launcher.cs`). A `TrayIconModeJsonConverter` handles reading legacy integer values from older settings files.

| Mode string | Type | Source |
|-------------|------|--------|
| `"Composite"` | Composite (default) | 2×2 grid of first 4 item icons |
| `"Blue"` | Blue rocket | `AppIcons/Blue.png` |
| `"Green"` | Green rocket | `AppIcons/Green.png` |
| `"Teal"` | Teal rocket | `AppIcons/Teal.png` |
| `"Red"` | Red rocket | `AppIcons/Red.png` |
| `"Orange"` | Orange rocket | `AppIcons/Orange.png` |
| `"Purple"` | Purple rocket | `AppIcons/Purple.png` |
| `"Pin"` | Pin glyph | Segoe Fluent Icons `U+E840` |
| `"Star"` | Star glyph | Segoe Fluent Icons `U+E734` |
| `"Heart"` | Heart glyph | Segoe Fluent Icons `U+EB51` |
| `"Lightning"` | Lightning glyph | Segoe Fluent Icons `U+E945` |
| `"Search"` | Search glyph | Segoe Fluent Icons `U+E721` |
| `"Globe"` | Globe glyph | Segoe Fluent Icons `U+E774` |
| `"Custom"` | Custom | User-provided file |
| `"Glyph:X"` | Gallery glyph/emoji | Arbitrary Fluent icon or emoji from the icon gallery |
| `"Glyph:#RRGGBB:X"` | Colored gallery glyph/emoji | Gallery glyph with a custom color |

Preset icons are full-color PNGs — they do **not** change with OS theme.
Glyph presets render in black (light theme) or white (dark theme) and update automatically on theme change.
Gallery-chosen glyphs (stored as `"Glyph:X"` or `"Glyph:#RRGGBB:X"`) are resolved by `ResolveBaseIconBitmap()` using `TrayIconModes.IsGlyphMode()`, `GetGlyphCharacter()`, and `GetGlyphColor()`, then rendered via `RenderGlyphBitmap()` with "Segoe Fluent Icons" or "Segoe UI Emoji" font depending on `IconGallery.IsFluentGlyph()`. When a color is encoded, it overrides the theme-dependent black/white.

## How Icon Updates Flow

All icon surfaces derive from a single source of truth: `ResolveBaseIconBitmap(Launcher)`, which returns a 256×256 `System.Drawing.Bitmap`.

1. User changes `TrayIconMode` in LaunchersPage → `Launcher.PropertyChanged` fires
2. `CreateTrayIconForLauncher` subscription → `MainWindow.UpdateTrayIcon(Launcher)`
3. `UpdateTrayIcon(Launcher)` calls `ResolveTrayIcon(Launcher)` → `ResolveBaseIconBitmap(Launcher)` → `BitmapToIcon()` → updates that launcher's native tray icon
4. `UpdateTrayIcon(Launcher)` calls `SaveResolvedIconToAppData(Launcher)` → writes `app-icon-{id}.ico` (and `app-icon.ico` for first launcher)
5. `UpdateTrayIcon(Launcher)` calls `CleanUpStaleIconFiles()` → removes old versioned/pin icon copies
6. `UpdateTrayIcon(Launcher)` calls `SaveSettingsIconToAppData()` → first launcher's bitmap + gear overlay → `BitmapToIcon()` → writes `settings-icon.ico`
7. `SettingsWindow.RefreshIcon()` reloads `settings-icon.ico` into titlebar, taskbar, and overlay

Tray icons are registered with Shell_NotifyIcon using a stable `guidItem` derived from `Launcher.Id`. This keeps Windows' per-icon tray visibility/pin preference stable across app restarts, Store updates, and icon/name changes. Only changing a launcher's `Id` causes Windows to treat it as a new tray icon identity.

**Pinned taskbar icons are NOT updated at runtime.** Windows 11's taskbar caches the icon bitmap per AUMID at pin time and does not re-read it. Changing a launcher's icon requires unpinning and re-pinning. The Launcher Settings dialog shows a note about this near the "Pin to Taskbar" button.

### Key rendering methods

| Method | Purpose |
|---|---|
| `ResolveBaseIconBitmap(Launcher)` | Single source of truth — returns 256×256 bitmap for a launcher's `TrayIconMode`. Handles Composite, Custom, named glyph presets, `Glyph:` gallery modes, and color presets (with fallback to Blue) |
| `RenderGlyphBitmap()` | Renders a Segoe Fluent Icons or Segoe UI Emoji glyph to bitmap at specified size. Accepts optional `fontName` parameter (called by `ResolveBaseIconBitmap` for glyph modes and composite sub-icons) |
| `RenderCompositeIconBitmap()` | Renders a 2×2 grid composite from first 4 launchable items (`"Composite"` mode) |
| `CollectLaunchableItems()` | Collects first N launchable items from a launcher, flattening groups, skipping headings/column breaks |
| `RoundedRectPath()` | Creates a `GraphicsPath` for a rounded rectangle (used by composite background) |
| `TrimAndResizeTo256()` | Trims transparent padding, centers on 256×256 canvas (called for presets + custom images) |
| `BitmapToIcoBytes(Bitmap)` | Converts bitmap to raw multi-resolution ICO byte array (16–256px). Bypasses `System.Drawing.Icon.Save()` which loses multi-resolution data on .NET |
| `BitmapToIcon(Bitmap)` | Calls `BitmapToIcoBytes()` then wraps in `System.Drawing.Icon` |
| `ResolveTrayIcon(Launcher)` | `ResolveBaseIconBitmap(Launcher)` → `BitmapToIcon()` |
| `SaveToastIconToAppData(Launcher)` | `ResolveBaseIconBitmap(Launcher)` → 256px PNG → write `toast-icon-{id}.png` for Windows notifications |
| `EnsureToastIconSaved(Launcher)` | Static. Returns that path, writing it first if absent — the notification path calls this |
| `SaveResolvedIconToAppData(Launcher)` | `ResolveBaseIconBitmap(Launcher)` → `BitmapToIcoBytes()` → write `app-icon-{id}.ico`; copies to `app-icon.ico` for first launcher |
| `SaveSettingsIconToAppData()` | First launcher's `ResolveBaseIconBitmap()` → gear overlay → `BitmapToIcon()` → write file |
| `RefreshLauncherIcon(Launcher)` | Batch-friendly: updates tray HICON + writes .ico to disk only (no settings save, no cleanup, no settings window refresh) |
| `UpdateTrayIcon(Launcher)` | Single-launcher convenience: calls `RefreshLauncherIcon` + `SaveSettingsIconToAppData` + `CleanUpStaleIconFiles` + `SettingsWindow.RefreshIcon()` |
| `EnsureLauncherIconSaved(Launcher)` | Static. Ensures `app-icon-{id}.ico` exists; called by pin flow before creating timestamped copy |
| `MigrateStaleIconPaths()` | Clears dead `IconPath`/`CustomTrayIconPath` at startup (handles MSIX→WiX path migration) |

## Settings Window Icon Strategy

WinUI 3 has a known bug (WindowsAppSDK#2730) where the taskbar ignores `AppWindow.SetIcon()` for
windows in the same process as the exe's embedded icon. The workaround uses three layers:

1. **`SetWindowAppUserModelId(hwnd, "LittleLauncher.Settings")`** — gives the Settings window its own
   taskbar group via the Shell `IPropertyStore` COM API, so the taskbar treats it independently.
2. **`AppWindow.SetIcon(IconId)`** via `GetIconIdFromIcon` interop — sets the app-level icon for
   Alt-Tab and the window's identity.
3. **`WM_SETICON`** (ICON_SMALL + ICON_BIG) — sets the Win32 window icon, re-sent on `Activated`
   to counteract WinUI's framework overrides.

## Pinned Taskbar Icon Strategy

The companion exe is deployed to `<AppDataDir>` by `EnsureFlyoutShortcut()` for **all build types** (WiX, MSIX, unpackaged). This gives it a consistent, non-packaged location so the shell treats it like a normal app:

1. **`EnsureFlyoutShortcut()`** copies `LittleLauncherFlyout.exe` from the app directory to `<AppDataDir>`.
2. Writes a `main-exe-path.txt` breadcrumb so the companion exe can launch the main app if it's not running.
3. The companion exe loads `app-icon-{launcherId}.ico` from `AppContext.BaseDirectory` (same `<AppDataDir>`).

### Pin identity (all builds)

Pin identity comes **solely from relaunch properties** set on the companion exe's MessageBox HWND via a CBT hook:
- `PKEY_AppUserModel_ID` = `LittleLauncher.Launcher.{guid}.{TickCount64}` — unique per pin attempt to bust Windows' per-AUMID icon cache
- `PKEY_AppUserModel_RelaunchCommand` = the companion exe path + `--launcher {guid}`
- `PKEY_AppUserModel_RelaunchIconResource` = timestamped `app-icon-{id}-pin{tick}.ico` path (same path as `LoadImage` uses), written as an **icon resource reference — `"path,0"`, never a bare path**
- `PKEY_AppUserModel_RelaunchDisplayNameResource` = `"Little Launcher - {name}"`
- CBT hook also calls `SetWindowTextW()` to set the MessageBox title (taskbar reads this for the button tooltip)

The pin flow (in `LauncherSettingsWindow.PinToTaskbar_Click`):
1. `CommitName()` + `CommitWebUrl()` — flushes the two fields the window defers to close
2. `WaitForIconAdoptionAsync()` — waits out a site-icon fetch the address just started
3. `EnsureLauncherIconSaved(launcher)` — ensures `app-icon-{id}.ico` exists
4. Creates a timestamped copy `app-icon-{id}-pin{tick}.ico`
5. Minimizes the Settings window (prevents focus stealing that dismisses taskbar context menu)
6. Launches companion exe with `--pin --launcher {guid} --name "{name}" --icon "{pinnedPath}"`
7. Restores Settings window + `SetForegroundWindow` after companion exe exits

> **Pinning bakes whatever the launcher looks like at that instant**, and Windows never re-reads it
> (see the warning below), so steps 1–2 exist because the window's *own* pending state used to be
> excluded from it. A web launcher pins the generic app icon if it is pinned before its favicon has
> been fetched — and since the address only committed on close, that was every launcher created and
> pinned in one sitting. Both text fields now also commit on Enter and on losing focus, so the icon
> arrives while the window is still open and is visible in the icon row before the user pins.
> `IconAdoptionWait` (8s) bounds the wait: an unreachable host must not leave the button dead.

> **`RelaunchIconResource` must carry a resource index.** It is an icon *resource* string
> (`"C:\...\app-icon-{id}-pin{tick}.ico,0"`), the same format the Start Menu shortcut's
> `IconLocation` uses — not a bare file path. Windows cannot parse a path on its own and silently
> falls back to the generic document icon, so the pinned taskbar button comes up **blank**. This is
> easy to misdiagnose, because the window's *title bar* icon still looks right: that one comes from
> `WM_SETICON`, which is unaffected. If a pin is blank, check this string before anything else.

Both `LoadImage` (WM_SETICON on the MessageBox HWND) and `RelaunchIconResource` use the **same timestamped path**. Windows caches icon bitmaps per file path in its icon cache DB, so reusing a stable path across pin attempts would serve stale cached bitmaps.

`CleanUpStaleIconFiles()` keeps the **most recent** `-pin*.ico` per launcher and deletes older ones. This ensures the pinned shortcut's `RelaunchIconResource` still points to a valid file after app restart.

> **WARNING:** Windows 11's taskbar caches the pin icon bitmap per AUMID at pin time. Changing the icon on disk does NOT update the pinned icon. The only way to update is to unpin and re-pin. The stamped AUMID ensures each re-pin sees a fresh identity.

### Diagnosing a blank pin icon

**`SHGetPropertyStoreForWindow` does not round-trip across processes.** Reading another process's
window property store returns `<EMPTY>` for every key, and *writing* to it returns `S_OK` from both
`SetValue` and `Commit` and still reads back empty. An external tool therefore cannot tell whether
the companion's relaunch properties were applied — do not conclude "the properties are unset" from
an outside read, as that is wrong. To actually check, log the HRESULTs and read the values back
**inside** the companion, in `--pin` mode (which already shows a modal, so it is not on the
latency-critical flyout path).

What is known good: `WM_SETICON` lands (both icon handles are set, which is why the *title bar*
icon looks right — it does not come from the relaunch properties), and the per-launcher `.ico`
exists and loads at the taskbar's requested size. A blank pin was last seen only in a dev
environment that had been run from several paths and switched between packaged and unpackaged
builds; treated as dev-only rather than chased further.

### Testing pin behaviour

Pin identity is **path-based and persistent**, so it cannot be tested from a throwaway directory:
`EnsureStartMenuShortcuts()` re-points the Start Menu shortcut at whatever exe is running, and a
pin records the path it was created from. Run the build from a stable location when testing this
flow. (A temp-folder test run left the Start Menu shortcut aimed into `%Temp%`, which is what
"Windows is searching for LittleLauncherFlyout.exe" looks like afterwards.)

Debug builds also deploy the **framework-dependent** companion (~175 KB apphost + its `.dll`,
`.deps.json`, `.runtimeconfig.json`) where Release deploys the self-contained AOT binary (~1.7 MB).
Both embed the app icon via `<ApplicationIcon>`, so the icon itself is not the difference — but
prefer a Release build when validating anything about pinning.

**Per-launcher Start Menu shortcuts are NOT created.** Previous versions used AUMID-stamped `.lnk` files in the Start Menu as the primary pin identity source. This was removed because combining shortcuts with relaunch properties caused Windows to see two identity sources and create duplicate "(2)" pins. `CleanUpStaleFlyoutShortcuts()` removes any leftover per-launcher shortcuts from previous versions on startup.

> **WARNING:** Windows caches pin display names per AUMID in CloudStore. Changing the display name format requires changing the AUMID format to bust the cache. See `ARCHITECTURE.md` and repo memory `msix-taskbar-pinning.md`.

## Adding a New Preset Icon

1. Add the PNG file to `Resources/AppIcons/` (transparent background, square)
2. Add entry to `PresetIcons` dictionary in `MainWindow.xaml.cs` with the next mode number
3. Add a `ComboBoxItem` with colored `Ellipse` + `TextBlock` in `LaunchersPage.xaml.cs` (`BuildIconModeCombo`)
4. Bump the Custom mode number in: `ResolveBaseIconBitmap()` and `BuildCustomIconRow` in `LaunchersPage.xaml.cs`
5. Add `<Content Include="Resources/AppIcons/NewColor.png">` to `.csproj` (or use the existing `*.png` glob)

## The selfh.st tab

`IconGallery`'s fourth tab lists the [selfh.st](https://selfh.st/icons/) icon set, fetched from
GitHub (`index.json`) and jsDelivr (the PNGs). **Everything it touches is cached on disk**, in
`icons\selfhst\` — a folder of its own so that `icons\` stays what it has always been, the icons
actually in use.

- **The index** resolves memory → disk → network, with `SelfhStCatalogTtl` (6h) measured on the
  cached file's write time. The in-memory copy dies with the process, and the tab cannot draw
  anything until the whole index has arrived, so without the disk copy every first visit after a
  restart sat on a spinner. If the fetch fails, a **stale** disk copy is served rather than failing
  the tab, and is treated as current for the rest of the session — otherwise every keystroke in the
  search box re-attempts a download that has just timed out.
- **The icons** are downloaded by `LoadSelfhStThumbnailAsync` and written to
  `icons\selfhst\{reference}.png`, at most `SelfhStDownloadGate` (8) at a time. Binding
  `BitmapImage.UriSource` straight to the CDN URL — which is what this replaced — kept nothing
  between visits, so the same hundred icons came over the wire again on every open.
- **Selecting an icon copies** the browse-cache file to `icons\selfhst-{reference}.png` rather than
  downloading it again. That path is what gets stored, and its `selfhst-` prefix is what
  `IsSelfhStImagePath` / `IsMatchingSelfhStImagePath` (pre-selecting the current icon) and
  `IsAutoFetchedIconPath` (leaving user-chosen icons alone) both key off — do not change the shape
  of that filename without those three.
- Reference names come from a remote index, so they go through `SelfhStFileName` before being used
  as a filename.
- Nothing prunes the browse cache. It only grows by what the user has actually looked at (≤120
  icons per view, a few KB each), which is why that is acceptable and a stored icon is kept
  outside it.

## Regenerating `LittleLauncher.ico`

`Resources/LittleLauncher.ico` is the embedded exe icon and the MSI installer shortcut icon. It **must be regenerated** whenever `Resources/AppIcons/Blue.png` changes. The `.ico` is not auto-generated by the build — it's a committed binary.

To regenerate, create a multi-resolution ICO (16, 24, 32, 48, 64, 256px) from `Blue.png` using System.Drawing or any ICO tool, and overwrite `Resources/LittleLauncher.ico`. If the `.ico` is stale, the installer and exe will show an outdated icon.

## MSI Installer Shortcut

The MSI (`Package.wxs`) creates an initial Start Menu shortcut at `Programs\Little Launcher.lnk` using `Resources/LittleLauncher.ico` as the icon. On first launch, `EnsureStartMenuShortcuts()` in `MainWindow.xaml.cs` overwrites this shortcut with the user's chosen icon from `app-icon.ico`. The shortcut must be placed directly in `ProgramMenuFolder` (not a subfolder) so the app's runtime management can find and update it.

## Launcher Item Icons (Favicons & App Icons)

`FaviconService.FetchMissingItemIconsAsync(IEnumerable<LauncherItem>)` is the **unified pipeline** for fetching launcher item icons. It handles both websites (favicon download) and apps (exe icon extraction). All bulk import paths must use this single method — do not duplicate the fetch logic.

Entry points that call the pipeline:
- **Startup**: `MainWindow.FetchMissingIconsOnStartupAsync()` — fire-and-forget, covers settings-import-then-restart and machine migration scenarios. Also runs the stale-icon refresh (below).
- **Sync download**: `SftpSyncService.DownloadLauncherItemsAsync()` — awaited before save.
- **File import**: `LauncherBulkOps.ImportItemsAsync()` — awaited before save.
- **Manual add/edit**: calls `FaviconService.FetchAndCacheAsync()` / `GetApplicationIcon()` directly for the single item in the dialog.
- **Drag-and-drop into the flyout**: `DroppedItemFactory.EnrichAsync()` — runs *after* the items are inserted and visible, so the drop itself never waits on the network. Dropped items carry a Segoe Fluent placeholder glyph (open / globe / folder / page) until the fetch lands.

### Getting a *good* icon, not just an icon

Two paths fetch a site's icon, and both must aim higher than the browser favicon:

- **Launcher items and bookmarks** — `FaviconService` reads the page HTML, prefers the **web app
  manifest**'s largest icon, then `<link rel="icon|apple-touch-icon">` picking the largest declared
  `sizes`, then the Google favicon service, then `/favicon.ico`. An `apple-touch-icon` with no
  `sizes` attribute is scored **180** rather than 0 — it is 180px by convention, and scoring it zero
  let a 16px shortcut icon that merely appeared first in the HTML win.
- **Web launchers** — `CoreWebView2.GetFaviconAsync` returns what the page declared for a *browser
  tab*, commonly 32 or 64px, and a tray icon (and far worse, a taskbar pin) is drawn much larger
  than that. Upscaling it is why a freshly created web launcher looked soft enough that picking a
  replacement by hand was the obvious workaround. `AdoptHighResPageIcon` takes a better one instead:
  the page bridge finds the best icon the site declares — manifest, then apple-touch-icon, then
  sized link icons — and **fetches it in the page**, because those assets sit behind the same login
  as the page and a host-side fetch gets a redirect. Anything under 96px is ignored, since it is not
  worth replacing Chromium's favicon with something no bigger. It still only ever replaces an icon
  that is ours to replace (`MayAdoptPageIcon`), and never in bar mode, where the tray icon stands
  for several sites rather than one.

### Stale icon refresh

`FaviconService.RefreshStaleItemIconsAsync()` re-fetches **auto-fetched** item icons whose cached file is older than `FaviconService.IconMaxAge` (7 days), so favicons, extracted app icons, and PWA icons don't rot as sites and apps update. Rules:

- **Custom icons are never touched.** Auto-fetched vs custom is decided by `IsAutoFetchedIconPath()`: auto-fetched icons all live in the `favicons` cache folder (`{host}.png`, `app_{name}.png`, `pwa_{aumid}.png`); user-chosen images live in the separate `icons` folder (`custom-*`, `selfhst-*`) or an arbitrary path.
- **Failures keep the existing file** — `FetchAndCacheCoreAsync` only writes on success, so icons only ever improve. A failed fetch leaves the old timestamp, so it's retried on the next check.
- Items sharing a cache file (same host) are deduped per pass via a shared visited-path set.
- Triggered from `MainWindow`: on startup (inside `FetchMissingIconsOnStartupAsync`) and by `_iconRefreshTimer`, a daily `System.Threading.Timer` (the tray app stays resident for weeks, so launch-only would not be enough). The timer callback marshals to the UI thread via `App.MainDispatcherQueue` before touching `IconPath`. After any change, `ApplyItemIconChanges()` saves settings, invalidates flyouts, and re-renders tray/settings icons (Composite mode embeds item icons).
- **Cache-busting:** the refresh rewrites cache files in place (same path), so the item-icon `BitmapImage` loads in `IconPathToImageConverter` and `FlyoutWindow.CreateIconTile` set `BitmapCreateOptions.IgnoreImageCache` — without it, WinUI's per-URI decoded-image cache keeps showing the old bitmap for the rest of the session.

The add/edit dialog's unified app/PWA picker shows its own **transient list icons** (not the persisted item icon): `AppPickerService.LoadIcons` extracts them via `ShellIcons.Extract` (`IShellItemImageFactory`, 32px BGRA → `WriteableBitmap`) on a background STA thread. These are display-only — the item's saved `IconPath` is still produced by the `FaviconService` calls above when a row is selected.

**Gotcha:** `IShellItemImageFactory.GetImage` returns `E_PENDING` (0x8000000A) the first time it's asked for an icon the shell hasn't rasterized into its cache yet — common for a freshly-seen PWA/Store tile. `ShellIcons.Extract` **must retry** on `E_PENDING` (short sleeps) or those icons silently come back blank while already-cached ones (e.g. a PWA you've launched before) work. This is why a PWA can show no list icon for no obvious reason.

**Raster-only:** `FetchAndCacheCoreAsync` saves whatever it downloads to a `.png` file rendered by WinUI `BitmapImage`, which **cannot decode SVG**. A site whose favicon/manifest icon is an SVG would otherwise be cached as a `.png` that displays blank. `RasterOrNull`/`IsRasterImage` (magic-byte check for PNG/JPEG/GIF/BMP/ICO/WEBP) rejects SVG and other non-raster payloads at each fetch step, so websites continue down the favicon fallback chain and **PWAs fall back to the shell-extracted icon** (`GetPwaIconFromShell`) — which is why a PWA could show its list icon (shell) but a blank selection/preview icon (the rejected SVG web icon). `GetPwaIconFromShell` requests 64px and, like the 32px list path, must retry on `E_PENDING`.

For Chromium PWAs, prefer the site's own icon/manifest asset when the AUMID encodes a domain (for example `example.com-HEX_hash!App`). The shell image factory often returns a softer rasterized bitmap than the original web icon. `GetBestPwaIconAsync()` handles this preference, but it must reject off-origin login redirects and skip generic Google favicon-service fallbacks for PWAs; when the site does not expose a real app icon, it should fall back to `GetPwaIconFromShell()`.

After any bulk icon change, call `FlyoutWindow.InvalidateItems()` so the flyout rebuilds its item containers on the next toggle. The flyout's `RebuildItemsIfNeeded()` nulls `ItemsSource` before reassigning to force full container recreation.

## Gotchas

- **MSIX VFS redirection** — see the [dedicated section below](#msix-vfs-redirection). All file paths referenced by external processes (shell `.lnk` files, companion exe) **must** use the physical path from `MainWindow.GetPhysicalAppDataDir()`, never raw `Environment.GetFolderPath(ApplicationData)`. The latter is VFS-redirected inside MSIX and invisible to the shell.
- **`LauncherItem.IconGlyph` must be a Unicode character**, not a text name. Use `""` (globe, `U+E774`) for websites and `""` (open, `U+E8E5`) for apps. Text strings like `"Globe24"` render as rectangle tofu in `FontIcon`.
- **`LauncherItem.IconGlyph` can be an emoji character** (e.g. `"🚀"`, `"💻"`). Use `IconGallery.IsFluentGlyph()` to determine whether a glyph is a Segoe Fluent icon (PUA range U+E000–U+F8FF) or an emoji. Fluent glyphs render via `FontIcon`; emojis render via `TextBlock`. The `IsFluentGlyphConverter` XAML converter handles this in data templates. In `System.Drawing` code (composite tray icon), use `"Segoe UI Emoji"` font for emoji glyphs instead of `"Segoe Fluent Icons"`.
- **Icon Gallery** (`Classes/IconGallery.cs`) provides a gallery-style Flyout for choosing glyphs, emojis, bundled app color icons, selfh.st catalog icons, or custom images. It is shown from the item edit dialog via a "Choose" button and from the launcher icon chooser. The item gallery has tabs (Glyphs, Emoji, App Icons, selfh.st) plus a color palette for choosing glyph colors; the launcher tray icon gallery now exposes presets, glyphs, emojis, and a selfh.st tab that feeds image-based selections into `TrayIconModes.Custom`. Selected colors are returned in `IconResult.Color` and stored as `LauncherItem.IconColor` (hex string) or encoded in the launcher `TrayIconMode` string (`"Glyph:#RRGGBB:X"`). selfh.st icons are fetched manually from the public catalog/index and cached locally as `AppData\LittleLauncher\icons\selfhst-{reference}.png` when selected. When a selfh.st tab is active, the gallery shows attribution links for selfh.st and the CC BY 4.0 license in the footer. When opened, the gallery pre-selects the current icon: it opens the correct tab, highlights the matching color swatch, and highlights + selects the matching icon button so the user can immediately Confirm or change just the color. Pre-selection is driven by `currentGlyph`/`currentColor`/`currentImagePath` params on `CreateFlyout()` and by parsing `currentMode` in `CreateLauncherIconFlyout()`.
- **A notification's icon comes from `toast-icon-{id}.png`, not the adopted page icon.** It used
  to read `web-favicon-{id}.png` directly, which is written *only* for a web launcher nobody
  has chosen an icon for — so a launcher wearing a custom image or a glyph had no file at
  that path, `SetAppLogoOverride` was skipped, and Windows fell back to Little Launcher's own
  logo. Every toast showed the app's icon rather than the site's, upscaled from the small
  manifest logo and blurry with it. Resolving through `ResolveBaseIconBitmap` fixes both at
  once: the right icon, at 256px.
- The bundled `.ico` is the Blue rocket only — it's the exe identity icon and fallback.
- Tray visibility state is now tied to each launcher's stable GUID identity via `NOTIFYICONDATA.guidItem`, not the runtime-assigned `uID`. Launcher reordering, name changes, and icon changes do not reset the user's tray preference; deleting/recreating a launcher still will.
- `SaveResolvedIconToAppData()` always writes an `.ico` for all modes (including mode 0). There is no "delete and fall back to exe icon" path.
- The companion exe (`LauncherShortcut/Program.cs`) loads `app-icon.ico` from `AppContext.BaseDirectory` for the pin dialog via `LoadImage` + `WM_SETICON`.
- `BitmapToIcoBytes()` / `BitmapToIcon()` produce multi-resolution ICO (16, 24, 32, 48, 64, 256) so tray icons render correctly at all DPI scales. `BitmapToIcoBytes()` writes raw ICO bytes directly — do NOT use `System.Drawing.Icon.Save()` which loses multi-resolution data on .NET.
- `MainWindow` listens for `UISettings.ColorValuesChanged` and refreshes the tray icon, `app-icon.ico`, and SettingsWindow icon when the OS theme changes. Uses `RefreshLauncherIcon()` (batch-friendly) for all launchers, then does a single `SaveSettingsIconToAppData` + `CleanUpStaleIconFiles` + `RefreshIcon` pass.
- `MigrateStaleIconPaths()` runs at startup before `SetupTrayIcons()` to clear dead `IconPath`/`CustomTrayIconPath` values (e.g. from MSIX→WiX path changes).
- When the user changes `TrayIconMode`, `UpdateTrayIcon()` also calls `SettingsWindow.RefreshIcon()` to update the settings window titlebar immediately.

## MSIX VFS Redirection

MSIX packages use **filesystem virtualization (VFS)**. When the packaged app calls `Environment.GetFolderPath(ApplicationData)`, it returns the standard `%AppData%` path, but writes are **silently redirected** to a package-specific location:

```
%LocalAppData%\Packages\<PackageFamilyName>\LocalCache\Roaming\
```

Files written to this redirected path are **invisible** to external processes (the Windows shell, the companion exe, any other non-packaged process) that read from the standard `%AppData%` path.

### The Problem

Icon files (`app-icon.ico`, `settings-icon.ico`) and the companion exe (`LittleLauncherFlyout.exe`) are written by the MSIX-packaged app. The shell reads `.lnk` shortcut `IconLocation` fields and the companion exe loads icon files — both operate outside the VFS.

### The Solution: `GetPhysicalAppDataDir()`

`MainWindow.GetPhysicalAppDataDir()` returns the **real filesystem path** that external processes can see:

- **MSIX**: `ApplicationData.Current.RoamingFolder.Path` → resolves to the `LocalCache\Roaming` path
- **Unpackaged/WiX**: `Environment.GetFolderPath(ApplicationData)` → standard `%AppData%` (no redirection)

### Rules

1. **Any path that will be referenced by the shell or external processes** (`.lnk` IconLocation, companion exe file reads, Start Menu shortcuts) → use `GetPhysicalAppDataDir()`.
2. **Internal-only paths** (settings.xml read/written only by the main app, favicon cache) → `Environment.GetFolderPath(ApplicationData)` is fine because VFS redirects reads too within the same package context.
3. **Companion exe** should use `AppContext.BaseDirectory` to find `app-icon.ico` — since the exe resides in the physical AppData dir, this resolves correctly for both MSIX and unpackaged.
4. **Never hardcode `%AppData%\LittleLauncher`** in any code path that writes files referenced externally. Always call `GetPhysicalAppDataDir()`.
