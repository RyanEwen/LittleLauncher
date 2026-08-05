<p align="center">
  <img src="LittleLauncher/Resources/AppIcons/Blue.png" alt="Little Launcher" width="128">
</p>

<h1 align="center">Little Launcher</h1>

<p align="center">
  A Windows launcher with system-tray and taskbar icon support, settings-sync capability, built with WinUI 3, Windows App SDK, WebView2, and SSH.NET.
</p>

<p align="center">
  <a href="https://apps.microsoft.com/detail/9p3zzbdq6pjf">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Download Little Launcher from the Microsoft Store" height="56">
  </a>
</p>

<p align="center">
  <img src="LittleLauncher/Resources/AppIcons/Blue.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Green.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Teal.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Red.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Orange.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Purple.png" width="48">
</p>

<p align="center">
  <img src="docs/icon-launcher.png" alt="Flyout — icon view" width="300">
  &nbsp;&nbsp;
  <img src="docs/list-launcher.png" alt="Flyout — list view" width="300">
</p>

<p align="center">
  <img src="docs/icon-launcher-multiple-cols.png" alt="Flyout — icon view with multiple columns" width="720">
</p>

<p align="center">
  <img src="docs/list-launcher-multiple-cols.png" alt="Flyout — list view with multiple columns" width="720">
</p>

<p align="center">
  <img src="docs/launchers.png" alt="Launchers page" width="720">
</p>

<p align="center">
  <img src="docs/icon-launcher-settings.png" alt="Launcher settings dialog" width="720">
</p>

<p align="center">
  <img src="docs/icon-launcher-items.png" alt="Launcher items — icon mode" width="720">
</p>

<p align="center">
  <img src="docs/list-launcher-items.png" alt="Launcher items — list mode" width="720">
</p>

## Overview

Little Launcher lives in the Windows system tray and/or taskbar. Clicking its icon opens a flyout with app and website shortcuts. It also synchronises your settings — through OneDrive, Google Drive, a network file share, another folder, or your own SSH/SFTP server — so you can keep your launcher configuration in sync across machines.

**Key features:**

- **Multiple launchers** — define multiple named launchers, each with its own icon and items.
- **Web launchers** — point a launcher at a URL instead of a list of shortcuts, and its tray icon opens that page itself: a Home Assistant dashboard, a webmail inbox, a status board, one click away. It stays signed in between restarts, and while it is closed the page is suspended and then unloaded entirely, so a hidden dashboard uses no CPU, network or memory.
- **Application & website shortcuts** — launch any executable or URL with one click from the flyout.
- **Manual icon picker** — choose item icons from Fluent glyphs, emojis, bundled app icons, uploaded images, or the selfh.st icon catalog.
- **View modes** — choose between list view, icon grid view, or a compact tray-sized small-icon grid with no labels.
- **Groups & columns** — organise items into groups and multi-column layouts.
- **Direct flyout reordering** — drag items in the live flyout to reorder them, with the same insertion-indicator style used in the editor.
- **Drag in from anywhere** — while editing a launcher, drag files, folders, shortcuts or browser links straight from File Explorer or the desktop into the flyout to add them, complete with names and icons. (Windows' own Start Menu can't act as a drag source, but its shortcuts can be dragged from `Start Menu\Programs` in Explorer.)
- **Direct flyout item actions** — right-click items in the live flyout to move, edit, or remove them without opening the settings editor.
- **System-tray icons** — a tray icon that opens a flyout popup for shortcuts.
- **Taskbar icons** — a companion helper exe (`LauncherShortcut`) can be pinned to the taskbar so one click opens the flyout without needing to find the tray icon.
- **Settings sync** — keep all your launchers in sync across machines through **OneDrive**, **Google Drive**, **WebDAV** (Nextcloud, ownCloud, a NAS), a **network file share**, any other folder, or your own **SSH/SFTP** server. OneDrive and Google Drive sign in through your browser and use their own APIs, so no sync client has to be installed and an upload is confirmed by the service rather than handed to a background app. Each is granted only a private folder of its own — nothing else in your drive is visible to Little Launcher.
- **Shared launchers** — share individual launchers through a OneDrive, Google Drive or network-share folder, any other file path, or per-launcher SFTP. Owners publish items; subscribers receive read-only copies, or 2-way sharing lets everyone edit.
- **Export & import** — back up and restore items locally via JSON.
- **Bookmark import** — import bookmarks directly from Chrome, Edge, Firefox, or any browser's exported HTML bookmarks file into a launcher.
- **Update paths per install type** — unpackaged/WiX installs update via GitHub Releases + MSI, while Microsoft Store installs can check for and apply updates through the Store from inside the app.

## Install

**Microsoft Store (recommended):** [Get Little Launcher from the Microsoft Store](https://apps.microsoft.com/detail/9p3zzbdq6pjf) — installs and updates automatically through the Store.

**Direct download:** Grab the latest MSI installer or portable ZIP for your architecture (x64 or ARM64) from the [Releases page](https://github.com/RyanEwen/LittleLauncher/releases/latest). The MSI is a per-user install (no admin required) and keeps itself up to date via GitHub Releases.

**Requirements:** Windows 10 or 11 (build 22000 or later).

## Building from source

Little Launcher is a .NET 10 WinUI 3 app. See **[DEVELOPMENT.md](DEVELOPMENT.md)** for build prerequisites, the architecture overview, and the project layout.

## License

Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md): free for any
**personal and other noncommercial use**, including modifying and redistributing it.
**Commercial use is not permitted.** Copyright © 2024-2026 Ryan Ewen.
