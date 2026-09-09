# LittleLauncher/Services

`UpdateService.cs` drives both update flows: the portable build's GitHub version check (which links out and installs nothing) and the MSIX/Store update. When changing update, install, or packaging behavior, follow the packaging conventions:

Read and follow [installer.md](../../.codex/docs/installer.md) before changing the code described above.

Global launcher sync (`LauncherSyncService`, `SftpSyncService`, `FolderSyncService`, `CloudFolderService`, `LauncherPayload`, `AutoSyncService`) is transport-pluggable. Before changing any of it — including adding a trigger — follow the sync conventions:

Read and follow [sync.md](../../.codex/docs/sync.md) before changing the code described above.

Other services in this folder (`FaviconService`, `AppCatalog`, `BookmarkImport`, …) are described in the root [AGENTS.md](../../AGENTS.md) architecture section.
