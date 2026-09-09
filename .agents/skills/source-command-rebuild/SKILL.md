---
name: source-command-rebuild
description: Build, sideload, and launch Little Launcher as a Release MSIX for local testing. Use when rebuilding or launching the app after changes.
---

# Rebuild and launch Little Launcher

The user's local testing preference is the sideloaded Release MSIX. Use Debug or an unpackaged
executable only when explicitly requested. Read [the packaging guide](../../../.codex/docs/installer.md)
before building; it contains the signing command and installed-package identity.

1. Inspect `Get-AppxPackage '*LittleLauncher*'` and running `LittleLauncher` process paths in the
   desktop user's context. A sandbox can return an empty package list; use the permitted desktop
   context before concluding the app is absent. `SignatureKind = Developer` identifies sideloading.
2. Use the installed package's architecture, or the host architecture if not installed. Record
   the original version in `Directory.Build.props`. Temporarily choose a four-part test version
   newer than the installed package and any local package being reused. Windows refuses different
   package contents with the same version. Restore the original version after packaging, including
   on failure; a local test must not become an accidental release bump.
3. Build with `LittleLauncherMSIX/build-msix.ps1 -Configuration Release` and the matching
   platform, using the existing Store-identity signing certificate as documented in the guide.
   If the Windows shell has lost `OS=Windows_NT`, restore it for that build process: Native AOT
   otherwise reports that cross-OS compilation is unsupported. Do not bypass Native AOT checks.
4. After a successful build, stop Little Launcher, update with `Add-AppxPackage -Path <built-msix>
   -ForceUpdateFromAnyVersion`, and launch `explorer.exe shell:AppsFolder\<PackageFamilyName>!App`.
   Never uninstall to resolve an update error: uninstalling deletes package settings and profiles.
   On build or install failure, report the error without claiming the fix is installed.
5. Verify the installed version and `SignatureKind`, and verify that the sole running app comes
   from its `InstallLocation`. Report the version actually running. Do not launch from `bin/Debug`.

Run commands from the repository root. Launch desktop applications in the user's desktop context,
outside a tool sandbox when needed for normal access to package registration and app data.
