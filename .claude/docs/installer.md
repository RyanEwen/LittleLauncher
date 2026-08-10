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

1. `CheckForUpdateAsync()` calls `GetAppAndOptionalStorePackageUpdatesAsync()` to detect Store updates. That list also contains framework/dependency packages, so `CheckForStoreUpdateAsync` filters to the main app package by `FamilyName` — and the presence of that entry **is** the update signal (see the trap below).
2. The version number to display comes from the Store's public display-catalog endpoint (`TryGetPublishedVersionAsync`), not from the update list. It is best-effort: `null` means "cannot say", and the Store's list is then trusted on its own rather than vetoed by a failed lookup. When it *does* answer and the published version is not newer than what's installed, the update is suppressed — that is the guard against a stale list offering an update to the running version. `LatestVersion` is left **empty** when an update exists but its number is unknown, and Home/About word that case without a version rather than inventing one.
3. Home/About pages reuse the same cached result shape as the MSI path and keep the same single-action UI
4. Clicking `Download & Install` calls `RequestDownloadAndInstallStorePackageUpdatesAsync()` on the UI thread
5. The `StoreContext` is associated with the Settings window handle via `InitializeWithWindow.Initialize(...)` so Store consent dialogs are correctly owned in the desktop app
6. After the Store API reports success, `UpdateService` writes a small `.cmd` helper that waits for the current process to exit and then launches `explorer.exe shell:AppsFolder\<PackageFamilyName>!App`
7. The app exits, the helper relaunches the packaged app, and the normal default launch path reopens Settings

Only unpackaged installs show the custom update toast on startup. Packaged installs still prefetch update state at startup so Home/About can immediately surface available Store updates.

When a packaged copy reports **no** update, About offers **Restart Now** rather than a bare "up to date". "Nothing to download" and "already staged, waiting for every process in the package to exit" are indistinguishable from the Store APIs, and Little Launcher lives in the tray and starts with Windows — so it is the app most likely to sit on a staged update indefinitely while the Store reports it as current. `RestartToApplyPackagedUpdate()` sets up the return trip (`RegisterApplicationRestart` + the relaunch helper) and the caller exits; applying a staged update needs no update API and no way to detect one, which is just as well since `Package.CheckUpdateAvailabilityAsync` only covers `.appinstaller` installs. The sibling apps (Drive for Immich, Repilot) do the same thing through their own `RestartToApplyUpdates()`.

### The trap: a pending update reports the version you already have

**`StorePackageUpdate.Package` describes the package as *installed*, so `Package.Id.Version` is the
version already on the machine — never the version being offered.** There is no WinRT API that
reports a pending update's version.

Measured against a live Store update: installed 1.27.1.0, published 1.28.0.0, and
`GetAppAndOptionalStorePackageUpdatesAsync` returned exactly one entry — the app's own family —
reporting **1.27.1.0**.

This bit once already. An earlier version required the listed version to be *strictly newer* than
the installed one, meaning to stop the UI offering an update to the running version. Because that
comparison can never be true, the Store path reported "You're up to date" permanently while the
Store itself showed the update sitting there ready to install, and it did so **silently** — the
check succeeded, so nothing was logged. `CheckForStoreUpdateAsync` now logs the list contents,
the installed version and the published version on every check, so a repeat is visible in
`logs.*.txt` rather than needing to be re-derived.

**Do not reintroduce that comparison.** Presence in the list is the signal; the catalog supplies
the number.

### Verifying Store update behaviour without shipping a build

Two things make this testable in minutes instead of via a Store submission round-trip:

- **What is actually published**, from the same public endpoint the Store client reads:

  ```bash
  curl -s "https://displaycatalog.mp.microsoft.com/v7.0/products/9P3ZZBDQ6PJF?market=US&languages=en-us&fieldsTemplate=Details"
  ```

  The human-readable version is in each `PackageFullName` (`…_1.28.0.0_arm64__hash`) — the
  numeric `Version` field beside it is a packed 64-bit value, not a version string.

- **What the WinRT API reports for the real install**, by running any process under the installed
  package's identity (`StoreContext` and `Package.Current` need it, so a plain script cannot call
  them):

  ```powershell
  Invoke-CommandInDesktopPackage -PackageFamilyName '27766TechnicallyReal.LittleLauncher_gfb69tsnc4jnp' -AppId 'App' -Command 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -Args '-NoProfile -ExecutionPolicy Bypass -File <script>'
  ```

  Use Windows PowerShell 5.1, not `pwsh` — the WinRT projection needed to call
  `GetAppAndOptionalStorePackageUpdatesAsync` is only there. The launched process is detached, so
  have the script write its output to a file outside the package's redirected AppData
  (e.g. `C:\Users\Public\`).

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

## Toast notifications in MSIX

Packaged builds register for notifications like unpackaged ones. This needs two manifest
extensions on the `<Application>`, which are easy to miss because their absence fails at
*runtime*, not at packaging time:

- `<com:Extension Category="windows.comServer">` with an `ExeServer` whose `Arguments` are
  `----AppNotificationActivated:` and a `<com:Class Id="…">`
- `<uap:Extension Category="windows.toastNotificationActivation">` with the same
  `ToastActivatorCLSID`

Plus `xmlns:com` and `com` in `IgnorableNamespaces`.

Without them a clicked toast has no activator and `AppNotificationManager.Register()` can throw,
which is why registration used to be skipped for packaged builds entirely — a workaround that
read like a platform limitation and was not one.

**The CLSID must stay stable.** Changing it orphans the activator for any toast already sitting
in the Action Center.

## CI state: MSI/GitHub Release automated, Store submission manual

The `external/promo` private-submodule checkout failure is **fixed** — `build-msix.yml`
(MSI + GitHub Release) runs clean on every `v*` tag again. Confirmed: v1.24.0 and v1.24.1
both completed successfully and published GitHub Releases with the four artifacts
(`LittleLauncher-{x64,ARM64}-Setup.msi` + `-portable.zip`). So a tag push automatically ships
the MSI auto-update path; **nothing manual is needed for MSI users.**

**The Store submission itself cannot be automated at all** — see the next section; it is a
paid-product / Pricing Version 2 restriction, not a credential problem. **Store packages are
built locally and never published from CI:**

```powershell
.\LittleLauncherMSIX\build-msix.ps1 -Platform x64   -NoSign
.\LittleLauncherMSIX\build-msix.ps1 -Platform ARM64 -NoSign
# then upload the two .msix files individually in Partner Center
```

**Upload the individual `.msix` files, not a `.msixupload`.** The `.msixupload` container exists
to give `msstore publish` a single file; it does not upload reliably through the Partner Center
UI. Since `msstore` cannot be used for this product at all, there is no reason to produce one.

**Never publish the Store build from CI.** This repository is public, and GitHub Actions
artifacts only require *read* access to download — on a public repo, that is everyone. An earlier
version of `store-publish.yml` uploaded the `.msixupload` as an artifact, which published the
Store build of a paid app; those artifacts were deleted on 2026-07-25 and the step removed. The
workflow now only *validates* that packaging still succeeds and produces no downloadable output.

## Automated Store submission is not possible for this product

**Settled — do not re-litigate without new information from Microsoft.** Little Launcher is a
**paid** Store product on **Pricing Version 2** (confirmed: the "Review price per market" button
is present under Pricing and availability; base price $0.99 across 240 markets). That rules out
both submission automation paths:

| Path | Blocked by |
|---|---|
| `msstore` CLI / `microsoft-store-apppublisher` action | [Free products only](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/github-actions) — paid explicitly unsupported |
| Store submission API (`manage.devcenter.microsoft.com`) / StoreBroker | [Unusable on Pricing Version 2](https://learn.microsoft.com/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services) — returns an *unknown tier* for pricing |

A third API (`api.store.microsoft.com`) *does* support `PAID` pricing, but covers **MSI/EXE
installers only, not MSIX**, so it does not apply. It is easy to land on that doc and conclude
automation is available — it is not, for this product.

The Pricing Version 2 hazard is not theoretical: submission flows clone the previous submission
and re-commit it, so an unknown pricing tier would be committed against a paid app in 240
markets. **`store-publish.yml` therefore only validates that packaging still works** — it runs on
`v*` tags, builds both architectures, and produces no downloadable output (see the public-artifact
warning above). Build and upload the packages yourself.

Re-evaluate only if Microsoft adds paid-product support to the msstore CLI, or the product moves
off Pricing Version 2.

The Entra credentials described below are consequently **unused**. They are kept because they
cost nothing and would be needed on a re-enable; delete the app registration's client secret if
you would rather not leave a dormant credential.

## Runbook: creating the Store publishing credentials

The secrets come from a Microsoft Entra **app registration** that Partner Center has been told to
trust. Currently unused (see above), but this is the procedure if the restriction ever lifts —
and the client secret expires after 24 months regardless.

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

Note there are three distinct "roles" here and they are easy to conflate — the one that matters
is the middle row:

| Role | Where | Governs |
|---|---|---|
| Your user's Partner Center role | Account settings → User management → **Users** | your own dashboard access |
| **The app registration's role** | Account settings → User management → **Microsoft Entra applications** | **API publishing rights — must be Manager** |
| Your directory role | entra.microsoft.com | whether you can create app registrations at all |

**Step 4 — seller ID.** Partner Center → **Account settings** → **Identifiers** (or Developer
settings). Copy **Seller ID** / **Publisher ID** → `SELLER_ID`.

**Step 5 — load the secrets** (values go over stdin, never into shell history):

```powershell
.\LittleLauncherMSIX\set-store-secrets.ps1
```

**Step 6 — if the restriction ever lifts**, verify with a draft before trusting it: run the
submission with `msstore publish ... -nc`, which stages a draft in Partner Center rather than
shipping one, and confirm it in Partner Center before allowing an unattended run to commit.

## If Microsoft adds paid-product support

Microsoft's docs say paid products "will be supported in a future release" of the msstore CLI
path. When that lands, re-enabling is small — the credentials and
`LittleLauncherMSIX/set-store-secrets.ps1` already exist. Restore into
`.github/workflows/store-publish.yml`:

1. `SELLER_ID` (never set — the only missing secret).
2. A bundling step: `msstore publish` takes a **single** file, so the two `.msix` must be zipped
   into one `LittleLauncher.msixupload`. (That container is only for `msstore`; do not use it for
   manual Partner Center uploads.)
3. The submission steps: [`microsoft/microsoft-store-apppublisher@v1.1`](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/overview)
   → `msstore reconfigure --tenantId … --sellerId … --clientId … --clientSecret …`
   → `msstore publish <upload> -id 9P3ZZBDQ6PJF`.
4. Confirm the Entra app registration still holds the **Manager** role in Partner Center and the
   client secret has not expired.

Verify with `-nc` first (step 6 above). Still do **not** add an `upload-artifact` step — the
public-repo exposure problem is independent of the submission question.

This is separate from `build-msix.yml`, which builds the MSI + GitHub Release and is fully
automated today.
