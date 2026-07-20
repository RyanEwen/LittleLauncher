# LittleLauncher/Windows

`FlyoutWindow` is the per-launcher popup **and the only place launcher items are edited** — the former in-settings items page was removed. Edit mode lives in the partial class `FlyoutWindow.EditMode.cs`.

Read this before touching drag/drop, edit mode, column logic, or flyout sizing. It covers the geometry contract (edit mode may grow height, never width or item size), the `_launcher.Items` vs `_columnLists` source-of-truth rule, and why height is computed arithmetically rather than measured:

@../../.claude/docs/drag-drop.md

It uses a transparent backdrop and custom drag handlers. Follow the WinUI 3 XAML conventions — including the "owned windows, not ContentDialog" rules that govern `ItemEditorWindow`, `TextPromptWindow`, and `LauncherSettingsWindow`:

@../../.claude/docs/xaml.md

Flyout item rendering, favicon/app-icon fetching, and `InvalidateItems()` are part of the icon pipeline — read [.claude/docs/icons.md](../../.claude/docs/icons.md) (and `FlyoutConverters.cs` guidance there) when changing how items or icons render.
