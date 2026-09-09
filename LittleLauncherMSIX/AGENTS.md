# LittleLauncherMSIX (MSIX / Store package)

`build-msix.ps1` stamps `Package.appxmanifest` (replacing `VERSION_PLACEHOLDER`) and builds the packaged app. The Store MSIX and the portable zip are the only two channels Little Launcher ships through. MSIX has VFS redirection and no custom uninstall actions — see the packaging guide for the implications:

Read and follow [installer.md](../.codex/docs/installer.md) before changing the code described above.

The MSIX manifest version is derived from `Directory.Build.props` at build time. Release process and version sources:

Read and follow [versioning.md](../.codex/docs/versioning.md) before changing the code described above.
