# LittleLauncher/Services

`UpdateService.cs` drives both update flows: the portable build's GitHub version check (which links out and installs nothing) and the MSIX/Store update. When changing update, install, or packaging behavior, follow the packaging conventions:

@../../.claude/docs/installer.md

Global launcher sync (`LauncherSyncService`, `SftpSyncService`, `FolderSyncService`, `CloudFolderService`, `LauncherPayload`, `AutoSyncService`) is transport-pluggable. Before changing any of it — including adding a trigger — follow the sync conventions:

@../../.claude/docs/sync.md

Other services in this folder (`FaviconService`, `AppCatalog`, `BookmarkImport`, …) are described in the root [CLAUDE.md](../../CLAUDE.md) architecture section.
