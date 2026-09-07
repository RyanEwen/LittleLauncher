> **Scope:** Use when adding P/Invoke declarations, Win32 interop, or native method signatures. Covers DllImport conventions, struct layouts, and safety patterns for this project.
> **Governs:** `**/NativeMethods.cs` (`LittleLauncher/Classes/NativeMethods.cs`).

# P/Invoke Conventions

## Declaration Style

- All P/Invoke declarations go in `NativeMethods.cs`
- Use `[LibraryImport]` (source-generated) for new declarations when possible
- Existing legacy declarations use `[DllImport]` — match the surrounding style
- Group by DLL: user32.dll, shcore.dll, etc.

## Handles and pointer-sized parameters

**Any parameter that is a handle (`HWND`, `HANDLE`, `LPARAM`, `WPARAM`) must be declared
`IntPtr`, never `int`.** These are 64-bit on x64/ARM64, and an `int` parameter silently works
for as long as every caller passes `0` — then fails the moment someone passes a real value or
a negative sentinel, because it cannot sign-extend into the 64-bit slot.

This bit us once: `SetWindowPos`'s `hWndInsertAfter` was declared `int`. Every existing call
passed `0`, so nothing broke until the first caller passed `HWND_TOPMOST` (`-1`) — Windows
received a bogus handle, the call returned `false`, and the window silently never moved.

Sentinel handle values must therefore also be pointer-sized:

```csharp
internal static readonly IntPtr HWND_TOPMOST = new(-1);
```

If a call inexplicably returns `false` or does nothing, check the signature's parameter widths
before anything else, and log `Marshal.GetLastWin32Error()`.

## DWM window attributes

`DwmSetWindowAttribute` covers window chrome that XAML cannot reach:

| Attribute | Use |
|---|---|
| `DWMWA_WINDOW_CORNER_PREFERENCE` | Windows 11 rounded corners on borderless windows |
| `DWMWA_BORDER_COLOR` | Border tint (COLORREF `0x00BBGGRR` — reverse of RGB); `DWMWA_COLOR_DEFAULT` restores the system colour |

Prefer these over XAML borders when a window must not change layout: a `BorderThickness` insets
content, whereas the DWM border costs nothing.

**A window that fills the screen must turn its corners off** — `DWMWCP_DONOTROUND` while it does,
`DWMWCP_ROUND` again afterwards. Rounded corners are a flyout affordance; on a screen-filling
window they cut the corners off the content, which on a fullscreen video is very visible.
`WebFlyoutWindow.ApplyFullScreen` does this alongside clearing its content inset, because both
otherwise read as a border around the page.

## Sizing to a monitor

`MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)` + `GetMonitorInfo` is how a window grows to
the screen it is already on rather than the primary one. Take the bounds from the right field:
`rcWork` excludes the taskbar and is what a flyout should be clamped into, while `rcMonitor` is
the whole screen and is what fullscreen means — a fullscreen video covers the taskbar.

## Off-screen parking

`GetSystemMetrics(SM_XVIRTUALSCREEN / SM_YVIRTUALSCREEN)` gives the top-left of the box around
every monitor. Park a window at that origin *minus its own size* when it must be visible to
Windows but not to the user — `FlyoutWindow.PreRenderOffScreen` uses it to compose a window's
first frame, and `FlyoutWindow.ParkOffScreen` uses it for every dismissal, so the flyout keeps
its composition surfaces instead of re-rasterising on the next open. Never use a hard-coded
negative coordinate for this: monitors can be arranged to the left of or above the primary, so
`-9999` is not reliably off screen.

## Recognising a double-click by hand

`GetDoubleClickTime()` plus `GetSystemMetrics(SM_CXDOUBLECLK / SM_CYDOUBLECLK)` are how a XAML
surface counts a double-click itself. `WebFlyoutWindow.IsCaptionDoubleClick` needs them because the
strips standing in for that window's title bar mark their own `PointerPressed` handled to start a
window move, and XAML raises no `Tapped` / `DoubleTapped` for a pointer whose press was taken.

Both are **user settings**, so read them rather than picking numbers: a hard-coded 500ms and 4px
is a second gesture that nearly matches the one the user configured. The two metrics are the full
width and height of the box the second click must land in, so the tolerance either side of the
first click is half of each. Compare positions in **screen** coordinates, since the first click may
have moved the window out from under the second.

## Per-window opacity

`SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA)` fades a whole window, but only once
`WS_EX_LAYERED` is on its ex-style. `FlyoutWindow.SetFadeAlpha` / `ClearFade` use it for the
hide animation.

Use this rather than XAML opacity whenever a window has a `SystemBackdrop`: the backdrop is
composited *behind* the XAML tree, so `RootGrid.Opacity` fades the content but leaves the
acrylic pane behind as a solid rectangle. Verified working alongside `DesktopAcrylicBackdrop` —
the acrylic fades with the rest of the window.

The alpha is window state and **survives being parked off screen**, so any path that puts the
window back on screen has to clear it first or the flyout returns invisible. `ParkOffScreen`
and `ShowAnimated`/`ShowWithoutAnimation` all call `ClearFade` for that reason.

## Focus loss with a hosted browser

A window hosting WebView2 gets `Deactivated` when the user merely clicks *into the page*, because
the browser's HWNDs are children of the host. A panel that dismisses on focus loss must therefore
confirm the user actually left: `GetForegroundWindow()` compared against the window itself and
`IsChild(hwnd, foreground)`. `WebFlyoutWindow` does this on the next dispatcher turn rather than
inside the event, so the foreground switch has settled before it is read.

**`IsChild` is not enough on its own — an owned window is not a child.** A file picker, the Windows
Security passkey prompt and a print dialog are top-level windows *owned by* the window that raised
them, and they belong to whoever raised them, which for anything a hosted browser opens is another
process. Both tests miss them, and the panel dismisses itself in the middle of the operation the
dialog exists for. Walk the owner chain with `GetWindow(hwnd, GW_OWNER)` — bounded, since a
malformed chain must not spin — and, for dialogs the browser owns itself, compare the foreground
window's process (`GetWindowThreadProcessId`) against `CoreWebView2.BrowserProcessId`. Identify
these by process, never by window class or title.

**A window only deactivates once**, so declining to dismiss is not free: nothing will raise the
event again when the dialog closes and the user moves to a different app. Whatever declines has to
take responsibility for finishing the job — `WebFlyoutWindow` starts a short polling timer that
applies the deferred dismissal once the foreground is no longer its own.

## Constants & Enums

- Win32 constants as `internal const int` or `internal const uint`
- Related constants grouped in comment-delimited sections
- Enums for flag sets with `[Flags]` attribute where appropriate
- Handle-valued sentinels are `static readonly IntPtr`, not `const int` (see above)

## Structs

- `[StructLayout(LayoutKind.Sequential)]` or `[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]`
- String fields use `[MarshalAs(UnmanagedType.ByValTStr, SizeConst = N)]`
- `RECT`, `POINT`, `MONITORINFOEX` are already defined — reuse them

## Consuming P/Invoke

- Import via `using static LittleLauncher.Classes.NativeMethods;`
- Never scatter P/Invoke declarations across multiple files

## IPropertyStore COM Section

The `#region IPropertyStore (COM)` section provides shell property access via `SHGetPropertyStoreForWindow`. Key PKEYs (all share GUID `{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}`):

| PKEY | pid | Purpose |
|---|---|---|
| `PKEY_AppUserModel_RelaunchCommand` | 2 | Exe path for taskbar relaunch |
| `PKEY_AppUserModel_RelaunchIconResource` | 3 | Icon for pinned taskbar entry (`"path.ico,0"`) |
| `PKEY_AppUserModel_RelaunchDisplayNameResource` | 4 | Display name for pinned entry |
| `PKEY_AppUserModel_ID` | 5 | AppUserModelID for taskbar grouping |

Helpers:
- `SetWindowAppUserModelId(hwnd, appId)` — sets AUMID on a window (used by SettingsWindow)
- `SetWindowRelaunchProperties(hwnd, icon, command, displayName)` sets all three relaunch PKEYs. Used by
  `WebFlyoutWindow.ApplyRelaunchProperties` in regular-window mode: an AUMID names a taskbar group but does
  not say how to start it again, so without these three the button offers no working **Pin to taskbar**. The
  icon must be a resource reference (`"path,0"`); a bare path is unparseable and the taskbar silently uses
  the generic document icon.
- `SetPropertyStoreString(store, key, value)` — low-level VT_LPWSTR setter (private)

## ITaskbarList COM Section

`GetTaskbarList()` caches the shell's `ITaskbarList` (CLSID `56FDF344-…`), used by
regular-window mode to add and drop a web launcher's taskbar button.

**`AddTab` is neither sufficient nor redundant, and both halves of that were measured:**

- It returns `S_OK` and does **nothing** for a `WS_EX_TOOLWINDOW` window, even with a matching
  `PKEY_AppUserModel_ID` stamped. It is not a way to give a tool window a taskbar button, however
  much it looks like one.
- It *is* required for a window that has just had the tool bit cleared — without it the window is
  correctly restyled and still has no button, because the shell has not re-evaluated. The
  documented alternative (hide, restyle, show) is unavailable in this app: `SW_HIDE` makes WinUI
  release the composition surfaces that off-screen parking exists to preserve.

Pair it with `SetWindowPos(… SWP_FRAMECHANGED)` after the style change. A null return from
`GetTaskbarList()` is a normal outcome to handle — a taskbar button must never stop a flyout
opening.

`IsIconic` + `SW_RESTORE` belong to the same feature: **restore before foregrounding**.
`SetForegroundWindow` on a minimized window raises it still minimized, which is
indistinguishable from the call having done nothing — and minimized is exactly where a
regular-window launcher lands after its taskbar button is clicked.

## Jump list COM section

The `#region Jump lists (COM)` section is what publishes the menu behind a pinned taskbar button:
`ICustomDestinationList` (CLSID `77F10CF0-…`), `IObjectCollection`, `IShellLinkW`, and `PKEY_Title`.
`Services/JumpListService.cs` is the only caller.

- **The sequence is fixed and silent when broken.** `SetAppID` → `BeginList` → `AddUserTasks` →
  `CommitList`. Nothing appears until the commit, and every one of those returns an HRESULT rather
  than throwing, so a skipped check looks exactly like a list nobody opened.
- **`BeginList` hands back the destinations the user removed by hand.** It must be read and
  released even though user tasks can never be among them, and the slot count it reports is the
  number of entries the user's settings will show.
- **The heading cannot be ours, and this was measured rather than assumed.** Only `AppendCategory`
  takes a name; `AddUserTasks` always produces Windows' localized "Tasks". But a category is made of
  *destinations*, and the shell requires those to be file types the publishing app is registered to
  open, checked against the AppUserModelID being published for. A per-launcher AUMID is registered
  for nothing at all: it is minted at pin time with a tick in it, so it is a fresh identity on every
  pin with nothing to hang a file association on. `AppendCategory` returns **`E_ACCESSDENIED`
  (0x80070005)** - verified on three launchers, freshly published, with no removed destinations
  outstanding. The property that makes one list per launcher possible is the same one that makes
  naming it impossible. `JumpListService.Publish` attempts the category and falls back to user
  tasks, and the first refusal is remembered so nothing pays for the attempt twice.
- **"Remove from this list" comes with the category, not with user tasks.** Destinations are
  removable and user tasks are not, so the fallback above is also a menu nothing can be taken out
  of. Reaching it would mean a real file-type registration and one file per entry on disk, which
  changes what a jump list entry *is* - see the note in `JumpListService`.
- **A removed destination is remembered forever.** If categories ever do work here, `AppendCategory`
  fails for the whole list if it names one, so anything acting on a removal must call `DeleteList`
  in the same pass. That is also the only reason an entry the user removed can come back after they
  add it to the launcher again.
- **`PKEY_Title` is not optional.** A shell link with no title shows its own path, so every task in
  the list reads `LittleLauncherFlyout.exe`. `SetShellLinkTitle` takes the link as `object` because
  reaching its `IPropertyStore` face is a QueryInterface, which is what casting the RCW does.
- **A task's icon is a path and an index, never a bitmap.** Only files carrying icon resources
  qualify (`.exe`, `.dll`, `.ico`), which is why an app item points at its own executable and
  everything else is rasterised to a cached `.ico` first.
- **`IShellLinkW`'s getters take raw buffers.** Nothing calls them; only the vtable order matters,
  and a wrong marshalling attribute on a method that is never invoked is a trap for the first caller.
- **Interfaces that derive from another COM interface repeat the base methods.** C# cannot express
  COM inheritance, so `IObjectCollection` restates `IObjectArray`'s two slots to keep the vtable
  order right. Getting this wrong calls the wrong function, not a compile error.

**MSIX note, measured:** the shell's own write lands in the **real** user profile
(`%AppData%\Microsoft\Windows\Recent\CustomDestinations`), not in the package's redirected
AppData, so jump lists work from the packaged build with nothing special done. Do not "fix" this by
routing it through `GetPhysicalAppDataDir()` - nothing here writes a path of ours.

## IShellItemImageFactory COM Section

The `#region shell32.dll` section includes `SHCreateItemFromParsingName` and the `IShellItemImageFactory` COM interface for extracting app icons from `shell:AppsFolder` items (used for PWA and Store app icons). The `#region gdi32.dll` section provides `DeleteObject` (HBITMAP cleanup), `GetObject` / `GetObjectDibSection` (reading bitmap metadata), `CreateCompatibleDC` / `DeleteDC` / `SelectObject` / `BitBlt` (blitting source HBITMAPs into controlled DIBs), and `CreateDIBSection` with `BITMAPINFO` (creating a top-down 32bpp DIB section with known pixel layout for reliable icon extraction).

### DIB structs

| Struct | Purpose |
|---|---|
| `BITMAP` | Basic bitmap dimensions and pixel pointer from `GetObject` |
| `BITMAPINFOHEADER` | Extended header with signed `biHeight` (positive = bottom-up, negative = top-down) |
| `DIBSECTION` | Full DIB info from `GetObjectDibSection`, contains both `BITMAP` and `BITMAPINFOHEADER` |
