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

Little Launcher lives in the Windows system tray and/or taskbar. Clicking its icon opens a flyout — either a grid of app and website shortcuts, or a web page in its own right. It also synchronises your settings — through OneDrive, Google Drive, WebDAV, a network file share, another folder, or your own SSH/SFTP server — so you can keep your launcher configuration in sync across machines.

**Key features:**

- **Multiple launchers** — define multiple named launchers, each with its own icon and items.
- **Web launchers** — point a launcher at a web page instead of a list of shortcuts, and its tray icon opens that page itself: a Home Assistant dashboard, webmail, Teams, WhatsApp, a status board, one click away. Each keeps its own sign-in between restarts, so two launchers can be signed in to the same site as different people — or share one profile if you would rather sign in once.
- **A bar of pages, or a set of tabs** — a web launcher can hold several bookmarks instead of one address, shown as a browser-style bar along the bottom that expands onto whichever you pick. Turn on **Treat as Tabs** and each one keeps its own live page, so switching never loses your scroll position, your place in a thread, or a half-typed message.
- **Costs nothing while you are not looking at it** — a dismissed web launcher stops rendering, is suspended, and is then unloaded entirely, so a hidden dashboard uses no CPU, network or memory. Launchers you want to keep receiving are the exception, and they say so.
- **Real Windows notifications, with buttons and replies** — a web launcher's notifications arrive as ordinary Windows notifications rather than being lost inside a page nobody is looking at. Action buttons work, and a site that offers a reply box gives you one in the notification itself — typing a reply there does exactly what typing it in the app does. Clicking a notification opens the launcher it came from.
- **Set up once, not every morning** — launchers set to keep running are opened quietly in the background when Little Launcher starts, so their notifications work from the moment you sign in, without you clicking each tray icon to wake it up.
- **Site permissions asked in the flyout** — a page that wants your camera, microphone or location asks in a bar inside the flyout rather than a browser-sized prompt, and the answer is remembered for that launcher alone. A launcher you trust can skip the asking entirely.
- **Sized and placed how you want it** — drag the edges to resize, pin it open, anchor it to a corner or edge instead of the tray, set a zoom level, or lock a size in so a stray drag cannot change it. The header's maximize fills the screen for as long as the flyout is open, then goes back to its usual size next time.
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
- **Shared launchers** — share a single launcher with someone else: create a **OneDrive link** to send them, publish it to a **WebDAV** server, put it on **SFTP**, or drop it in a shared folder. The owner chooses whether subscribers can edit it or only receive it, and that choice travels with the share rather than being something the subscriber has to guess.
- **Export & import** — back up and restore items locally via JSON.
- **Bookmark import** — import bookmarks directly from Chrome, Edge, Firefox, or any browser's exported HTML bookmarks file into a launcher.
- **Pick a web address from your bookmarks** — a web launcher's address, and the bookmarks in its bar, can be chosen from a searchable list of what you have already bookmarked in Chrome, Edge or Firefox, rather than typed from memory.
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
