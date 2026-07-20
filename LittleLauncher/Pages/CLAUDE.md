# LittleLauncher/Pages

Settings pages are WinUI 3 `Page` objects navigated via `NavigationView`. Follow the XAML conventions for all `.xaml` here.

@../../.claude/docs/xaml.md

**There is no item editor here.** All launcher item editing lives in the flyout's edit mode (`Windows/FlyoutWindow.EditMode.cs`); the former `LauncherItemsPage` was removed. `LaunchersPage` keeps only launcher-level concerns — cards, sharing, and the per-launcher bulk operations in `LauncherBulkOps` (export, import, bookmark import). Per-launcher settings open in `LauncherSettingsWindow`, shared with the flyout.

`LaunchersPage` drives per-launcher tray/pin icons. When editing it, follow the icon system conventions: [.claude/docs/icons.md](../../.claude/docs/icons.md).
