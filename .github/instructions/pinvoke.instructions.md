---
description: "Use when adding P/Invoke declarations, Win32 interop, or native method signatures. Covers DllImport conventions, struct layouts, and safety patterns for this project."
applyTo: "**/NativeMethods.cs"
---
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

## Off-screen parking

`GetSystemMetrics(SM_XVIRTUALSCREEN / SM_YVIRTUALSCREEN)` gives the top-left of the box around
every monitor. Park a window at that origin *minus its own size* when it must be visible to
Windows but not to the user — `FlyoutWindow.PreRenderOffScreen` uses it to compose a window's
first frame, and `FlyoutWindow.ParkOffScreen` uses it for every dismissal, so the flyout keeps
its composition surfaces instead of re-rasterising on the next open. Never use a hard-coded
negative coordinate for this: monitors can be arranged to the left of or above the primary, so
`-9999` is not reliably off screen.

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

## IShellItemImageFactory COM Section

The `#region shell32.dll` section includes `SHCreateItemFromParsingName` and the `IShellItemImageFactory` COM interface for extracting app icons from `shell:AppsFolder` items (used for PWA and Store app icons). The `#region gdi32.dll` section provides `DeleteObject` (HBITMAP cleanup), `GetObject` / `GetObjectDibSection` (reading bitmap metadata), `CreateCompatibleDC` / `DeleteDC` / `SelectObject` / `BitBlt` (blitting source HBITMAPs into controlled DIBs), and `CreateDIBSection` with `BITMAPINFO` (creating a top-down 32bpp DIB section with known pixel layout for reliable icon extraction).

### DIB structs

| Struct | Purpose |
|---|---|
| `BITMAP` | Basic bitmap dimensions and pixel pointer from `GetObject` |
| `BITMAPINFOHEADER` | Extended header with signed `biHeight` (positive = bottom-up, negative = top-down) |
| `DIBSECTION` | Full DIB info from `GetObjectDibSection`, contains both `BITMAP` and `BITMAPINFOHEADER` |
