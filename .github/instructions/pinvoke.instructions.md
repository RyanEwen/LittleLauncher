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

- Import via `using static SelfHostedHelper.Classes.NativeMethods;`
- Never scatter P/Invoke declarations across multiple files
- Wrap complex interop sequences in helper methods in `WindowHelper.cs`
