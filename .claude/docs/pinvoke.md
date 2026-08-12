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
- `SetWindowRelaunchProperties(hwnd, icon, command, displayName)` — sets all three relaunch PKEYs (currently unused — kept for future use)
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

## IShellItemImageFactory COM Section

The `#region shell32.dll` section includes `SHCreateItemFromParsingName` and the `IShellItemImageFactory` COM interface for extracting app icons from `shell:AppsFolder` items (used for PWA and Store app icons). The `#region gdi32.dll` section provides `DeleteObject` (HBITMAP cleanup), `GetObject` / `GetObjectDibSection` (reading bitmap metadata), `CreateCompatibleDC` / `DeleteDC` / `SelectObject` / `BitBlt` (blitting source HBITMAPs into controlled DIBs), and `CreateDIBSection` with `BITMAPINFO` (creating a top-down 32bpp DIB section with known pixel layout for reliable icon extraction).

### DIB structs

| Struct | Purpose |
|---|---|
| `BITMAP` | Basic bitmap dimensions and pixel pointer from `GetObject` |
| `BITMAPINFOHEADER` | Extended header with signed `biHeight` (positive = bottom-up, negative = top-down) |
| `DIBSECTION` | Full DIB info from `GetObjectDibSection`, contains both `BITMAP` and `BITMAPINFOHEADER` |
