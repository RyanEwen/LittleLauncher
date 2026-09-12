# LittleLauncher/Windows

`FlyoutWindow` is the per-launcher popup **and the only place launcher items are edited** — the former in-settings items page was removed. Edit mode lives in the partial class `FlyoutWindow.EditMode.cs`.

Read this before touching drag/drop, edit mode, column logic, or flyout sizing. It covers the geometry contract (edit mode may grow height, never width or item size), the `_launcher.Items` vs `_columnLists` source-of-truth rule, and why height is computed arithmetically rather than measured:

Read and follow [drag-drop.md](../../.codex/docs/drag-drop.md) before changing the code described above.

It uses a transparent backdrop and custom drag handlers. Follow the WinUI 3 XAML conventions — including the "owned windows, not ContentDialog" rules that govern `ItemEditorWindow`, `TextPromptWindow`, and `LauncherSettingsWindow`:

Read and follow [xaml.md](../../.codex/docs/xaml.md) before changing the code described above.

Flyout item rendering, favicon/app-icon fetching, and `InvalidateItems()` are part of the icon pipeline — read [.codex/docs/icons.md](../../.codex/docs/icons.md) (and `FlyoutConverters.cs` guidance there) when changing how items or icons render.

`WebFlyoutWindow` is the other kind of flyout: a WebView2 on a launcher's URL, routed to by
`LauncherPanels`. Read this before changing its lifecycle — the browser is deliberately built late
and torn down early, and several WinUI WebView2 limits are worked around there:

Read and follow [web-launchers.md](../../.codex/docs/web-launchers.md) before changing the code described above.

The same guide governs `WebFlyoutWindow.FullscreenTitleBar.cs`, including its fullscreen
header overlay and hover lifecycle.
