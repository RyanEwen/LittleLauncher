# LittleLauncher/Services

`UpdateService.cs` drives the MSI auto-update and MSIX/Store update flows. When changing update, install, or packaging behavior, follow the installer conventions:

@../../.claude/docs/installer.md

Global launcher sync (`LauncherSyncService`, `SftpSyncService`, `FolderSyncService`, `CloudFolderService`, `LauncherPayload`, `AutoSyncService`) is transport-pluggable. Before changing any of it — including adding a trigger — follow the sync conventions:

@../../.claude/docs/sync.md

Other services in this folder (`FaviconService`, `AppCatalog`, `BookmarkImport`, …) are described in the root [CLAUDE.md](../../CLAUDE.md) architecture section.
