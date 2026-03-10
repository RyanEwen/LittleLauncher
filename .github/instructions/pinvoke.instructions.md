---
description: "Use when adding P/Invoke declarations, Win32 interop, or native method signatures. Covers DllImport conventions, struct layouts, and safety patterns for this project."
applyTo: "**/NativeMethods.cs"
---

# P/Invoke Conventions

## Declaration Style

- All P/Invoke declarations go in `NativeMethods.cs`
- Use `[LibraryImport]` (source-generated) for new declarations when possible
- Existing legacy declarations use `[DllImport]` — match the surrounding style
- Group by DLL: user32.dll, shcore.dll, etc.

## Constants & Enums

- Win32 constants as `internal const int` or `internal const uint`
- Related constants grouped in comment-delimited sections
- Enums for flag sets with `[Flags]` attribute where appropriate

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

The `#region shell32.dll` section includes `SHCreateItemFromParsingName` and the `IShellItemImageFactory` COM interface for extracting app icons from `shell:AppsFolder` items (used for PWA icons). The `#region gdi32.dll` section provides `DeleteObject` (HBITMAP cleanup) and `GetObject` (reading DIB pixel data via the `BITMAP` struct).
