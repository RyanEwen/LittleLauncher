---
description: "Use when modifying the MSI installer (WiX), MSIX packaging, changing install paths, shortcuts, or upgrade behavior. Covers per-user install, Start Menu shortcut lifecycle, MSIX Store builds, and common pitfalls."
applyTo: "**/Package.wxs,**/LittleLauncherSetup.wixproj,**/UpdateService.cs,**/build-msix.ps1,**/Package.appxmanifest"
---
> **Scope:** Use when modifying the MSI installer (WiX), MSIX packaging, changing install paths, shortcuts, or upgrade behavior. Covers per-user install, Start Menu shortcut lifecycle, MSIX Store builds, and common pitfalls.
> **Governs:** `**/Package.wxs`, `**/LittleLauncherSetup.wixproj`, `**/UpdateService.cs`, `**/build-msix.ps1`, `**/Package.appxmanifest`.

# MSI Installer (WiX)

Little Launcher ships as a **per-user MSI** built with WiX Toolset 5. No elevation is required.

## Install layout

| What | Where |
|---|---|
| App files | `%LocalAppData%\Little Launcher\` |
| Start Menu shortcut | `%AppData%\Microsoft\Windows\Start Menu\Programs\Little Launcher.lnk` |
| Settings/data | `%AppData%\LittleLauncher\` (created by the app, not the MSI) |

## Start Menu shortcut lifecycle

1. **MSI creates** `Programs\Little Launcher.lnk` at install time using the embedded `LittleLauncher.ico` (always Blue rocket). This gives users something to click before the app ever runs.
2. **On first launch** `EnsureStartMenuShortcuts()` in `MainWindow.xaml.cs` overwrites the same shortcut with the user's chosen icon (`app-icon.ico` from AppData).
3. **On icon change** `UpdateShortcutIcons()` re-stamps the shortcut with the new icon.
4. **On uninstall** the MSI removes the shortcut via the component's registry key.

**Critical:** The MSI shortcut must be placed directly in `ProgramMenuFolder` (not a subfolder), so its path matches what the app writes at runtime. If the MSI uses a subfolder, you get duplicate shortcuts — one stale (MSI's) and one current (app's).

## Version injection

The installer version comes from `Directory.Build.props` → `LittleLauncherSetup.wixproj` passes `ProductVersion=$(Version).0` via `DefineConstants`. CI also injects it. A fallback `<?define ProductVersion = "X.Y.Z.0" ?>` exists in `Package.wxs` for local builds — **keep it in sync** when bumping versions.

## Upgrade behavior

`MajorUpgrade` with `AllowSameVersionUpgrades="yes"` handles upgrades and reinstalls — the old version is uninstalled before the new one is installed, even when the version number is unchanged. `UpgradeCode` must never change.

## Auto-launch after install

A `CustomAction` in `Package.wxs` launches `LittleLauncher.exe` after `InstallFinalize` (condition `NOT REMOVE`). It uses `asyncNoWait` so the installer doesn't block. This ensures the app is running in the tray immediately after a fresh install or upgrade.

## Per-user install notes

- `Scope="perUser"` means no elevation, installs to `LocalAppDataFolder`
- WiX ICE validations ICE38, ICE64, ICE91 are suppressed in `.wixproj` — these fire for per-user installs writing to profile directories, which is expected
- The update service (`UpdateService.cs`) launches `msiexec /i` without elevation (`-Verb RunAs` is NOT used)

## Auto-update flow

`UpdateService` downloads the MSI to a temp folder, removes the Zone.Identifier ADS (Mark of the Web), then spawns a `.cmd` helper script:

1. Script waits for the current app process to exit
2. Runs `msiexec /i <path> /passive` — installs silently with progress bar (no user interaction; they already consented in-app)
3. MSI's `CustomAction` auto-launches the app in the tray
4. Script launches `LittleLauncher.exe --settings` — the single-instance mutex detects the running app and sends `LittleLauncher_ShowSettings`, re-opening the Settings window

## MSIX / Store update flow

For packaged installs, `UpdateService` takes a separate path through `Windows.Services.Store.StoreContext` instead of GitHub Releases:

1. `CheckForUpdateAsync()` calls `GetAppAndOptionalStorePackageUpdatesAsync()` to detect Store updates
2. Home/About pages reuse the same cached result shape as the MSI path and keep the same single-action UI
3. Clicking `Download & Install` calls `RequestDownloadAndInstallStorePackageUpdatesAsync()` on the UI thread
4. The `StoreContext` is associated with the Settings window handle via `InitializeWithWindow.Initialize(...)` so Store consent dialogs are correctly owned in the desktop app
5. After the Store API reports success, `UpdateService` writes a small `.cmd` helper that waits for the current process to exit and then launches `explorer.exe shell:AppsFolder\<PackageFamilyName>!App`
6. The app exits, the helper relaunches the packaged app, and the normal default launch path reopens Settings

Only unpackaged installs show the custom update toast on startup. Packaged installs still prefetch update state at startup so Home/About can immediately surface available Store updates.

## Uninstall cleanup

Before file removal, WiX `util:CloseApplication` targets `LittleLauncher.exe` on `REMOVE="ALL"`. It sends a normal close message, then an end-session message, waits 5 seconds, and force-terminates the process if it is still running. This keeps explicit uninstalls and major-upgrade removal from racing the running tray process.

On `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`, a `CustomAction` runs `cleanup-uninstall.ps1` (shipped in the install folder) via `powershell.exe -File`. The `NOT UPGRADINGPRODUCTCODE` condition ensures settings and data survive upgrades. It cleans up:

| What | Where |
|---|---|
| App data folder | `%AppData%\LittleLauncher\` (settings, companion exe, icons) |
| Flyout Start Menu shortcut | `%AppData%\...\Start Menu\Programs\Little Launcher Flyout.lnk` |
| Startup registry entry | `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Little Launcher` |
| Pinned taskbar shortcuts | Any `.lnk` in `User Pinned\TaskBar\` targeting `LittleLauncherFlyout.exe` |

The action uses `Return="check"` so the uninstall does not report completion until the cleanup script has finished.

**MSIX limitation:** MSIX has no custom uninstall actions. When an MSIX package is removed, Windows deletes the package files, its own Start Menu entry, **and all VFS-redirected data** (settings, cached icons, companion exe) because the entire `%LocalAppData%\Packages\{PFN}\` tree is removed. Pinned taskbar shortcuts survive as dead `.lnk` files — Windows 11 eventually detects and offers to remove stale pins. Settings **do** survive MSIX upgrades — Windows preserves package data during version updates, including updates initiated through the Store API path above.

## Building the MSIX (`build-msix.ps1`)

`LittleLauncherMSIX/build-msix.ps1 -Platform {x64|ARM64}` publishes the app (self-contained) + the AOT companion, assembles the layout, runs `makepri`/`makeappx`, and signs. Toolchain requirements (see also the local-build memory):

- **Windows SDK** packaging tools (`makeappx`/`makepri`/`signtool`). The script auto-detects the newest installed `C:\Program Files (x86)\Windows Kits\10\bin\10.*` that has them — don't hardcode a version.
- **VS C++ build tools** (incl. `VC.Tools.ARM64`) for the Native AOT companion. The script prepends the **VS Installer dir to `PATH`** when `vswhere.exe` isn't resolvable, because the ILCompiler targets shell out to `vswhere`; without it the native link fails with **exit code 123**.
- **`-NoSign`** is Store mode: skips signing and leaves the Store `Identity`/`Publisher` intact for the Store to re-sign on ingestion. Without it, the manifest publisher is rewritten to the dev cert subject so `signtool` can sign locally.

## CI state: MSI/GitHub Release automated, Store submission manual

The `external/promo` private-submodule checkout failure is **fixed** — `build-msix.yml`
(MSI + GitHub Release) runs clean on every `v*` tag again. Confirmed: v1.24.0 and v1.24.1
both completed successfully and published GitHub Releases with the four artifacts
(`LittleLauncher-{x64,ARM64}-Setup.msi` + `-portable.zip`). So a tag push automatically ships
the MSI auto-update path; **nothing manual is needed for MSI users.**

**The Store, however, still can't be submitted from CI** — not because of checkout, but because
the Azure AD credentials that gate `store-publish.yml`'s submission step are not currently
working. So Store packages are built **locally** and uploaded by hand:

```powershell
.\LittleLauncherMSIX\build-msix.ps1 -Platform x64   -NoSign
.\LittleLauncherMSIX\build-msix.ps1 -Platform ARM64 -NoSign
# then zip both .msix into LittleLauncher.msixupload and upload via Partner Center
```

`msstore publish` takes a **single** package, which is why multi-arch must be bundled into one
`.msixupload` — the same shape the workflow produces.

## Runbook: creating the Store publishing credentials

The four secrets `store-publish.yml` needs come from a Microsoft Entra **app registration** that
Partner Center has been told to trust. Walk this once to enable automation, and again whenever
the client secret expires (Entra caps them at 24 months).

**Step 0 — do you have a tenant?** Partner Center → gear icon → **Account settings** →
**Tenants**. Store dev accounts opened with a personal Microsoft account often have **none**,
and nothing else works without one.

- No tenant → **Create a new Microsoft Entra ID tenant** right there (free, and the button is on
  that same page). This becomes the tenant that owns the app registration.
- Tenant already listed → note its domain and continue.

**Step 1 — register the app.** [entra.microsoft.com](https://entra.microsoft.com) →
**Entra ID** → **App registrations** → **New registration**.

- Name: anything (e.g. `LittleLauncher Store Publisher`).
- Supported account types: **Single tenant**.
- **Redirect URI: leave blank.** This is a daemon/service credential — there is no interactive
  sign-in, so a redirect URI is not used.
- **Do not add any API permissions.** This is the step people over-do: publishing rights do *not*
  come from Graph scopes, they come from the Partner Center role in step 3. An app with Graph
  permissions and no Partner Center role still gets denied.

From the app's **Overview**, copy **Application (client) ID** → `AZURE_AD_APPLICATION_CLIENT_ID`,
and **Directory (tenant) ID** → `AZURE_AD_TENANT_ID`.

**Step 2 — client secret.** In that app → **Certificates & secrets** → **New client secret**.
Copy the **Value** column (not "Secret ID") **immediately** — it is never shown again. Set a
calendar reminder for the expiry date; an expired secret fails the publish step with an auth
error and nothing else explains why. → `AZURE_AD_APPLICATION_SECRET`.

**Step 3 — authorize it in Partner Center.** This is the step that actually grants publishing
rights, and the one most often missed. Partner Center → **Account settings** →
**User management** → **Microsoft Entra applications** → add the app registration from step 1 and
assign it the **Manager** role. Without this, authentication succeeds and submission is refused.

**Step 4 — seller ID.** Partner Center → **Account settings** → **Identifiers** (or Developer
settings). Copy **Seller ID** / **Publisher ID** → `SELLER_ID`.

**Step 5 — load the secrets** (values go over stdin, never into shell history):

```powershell
.\LittleLauncherMSIX\set-store-secrets.ps1
```

**Step 6 — verify with a draft before trusting it.** Run the workflow manually with
`noCommit=true`; it stages a draft submission in Partner Center rather than shipping one. Only
after that looks right should the `v*` tag trigger be restored (see the workflow header).

## Microsoft Store auto-publish (CI — blocked on Store credentials)

The submission step below is gated on the `AZURE_AD_*` / `SELLER_ID` secrets, and those
credentials are **not currently working**, so the automated Store submission does not happen —
build the `.msixupload` locally and upload via Partner Center (see the section above).

`.github/workflows/store-publish.yml` runs on every `v*` tag (and `workflow_dispatch`): it builds both `-NoSign` MSIX packages, zips them into a single `LittleLauncher.msixupload`, and submits a package update with the [Microsoft Store Developer CLI](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/overview) (`microsoft/microsoft-store-apppublisher@v1.1` → `msstore reconfigure` → `msstore publish <upload> -id 9P3ZZBDQ6PJF`). `msstore publish` takes a **single** package, so multi-arch must be bundled into one `.msixupload`. The submission step is gated on the `AZURE_AD_TENANT_ID` / `AZURE_AD_APPLICATION_CLIENT_ID` / `AZURE_AD_APPLICATION_SECRET` / `SELLER_ID` secrets (skipped if unset; the `.msixupload` is still uploaded as an artifact). Microsoft supports this GitHub Actions update path for **free products only**. To stage a draft instead of committing, add `-nc`/`--noCommit` to the publish command. This is separate from `build-msix.yml`, which builds the MSI + GitHub Release.
