> **Scope:** Use when changing how Little Launcher is packaged, installed or updated. Covers the two channels it ships through — the portable zip and the Microsoft Store MSIX — the Start Menu shortcut lifecycle, and the update flow behind each.
> **Governs:** `**/UpdateService.cs`, `**/build-msix.ps1`, `**/Package.appxmanifest`.

# Packaging and updates

Little Launcher ships through **two channels, and neither of them is an installer of ours**:

| Channel | Artifact | Put in place by | Updates |
|---|---|---|---|
| **Portable** | `LittleLauncher-{x64,ARM64}-portable.zip`, attached to the GitHub release | the user, unzipping it wherever they like | in-app check against GitHub Releases; the app opens the release page and the user replaces the folder |
| **Microsoft Store** | MSIX, built locally and uploaded in Partner Center | the Store | in-app through the Store APIs, or silently by the Store itself |

**There is no MSI.** A per-user WiX installer shipped up to v1.35.1 and was retired: it duplicated
what the Store already does properly — managed install, silent update, clean uninstall — while
carrying its own upgrade code, custom actions, uninstall script and signing step. Releases from
before the cut keep their `.msi` assets, so old download links still resolve, and an installed MSI
copy keeps working: its updater finds no `.msi` asset on a newer release and falls back to opening
the release page, which is what the portable build now does too.

## Install layout

| What | Portable | MSIX |
|---|---|---|
| App files | wherever the user unzipped it | `%ProgramFiles%\WindowsApps\{PFN}\`, managed by Windows |
| Settings/data | `%AppData%\LittleLauncher\` | the same path, VFS-redirected into `%LocalAppData%\Packages\{PFN}\` |
| Start Menu shortcut | `%AppData%\...\Start Menu\Programs\Little Launcher.lnk`, written by the app | the package's own manifest entry |

Anything an *external* process opens by path — shell `.lnk` files, the companion exe — has to go
through `MainWindow.GetPhysicalAppDataDir()` rather than raw `%AppData%`, which MSIX redirects.

## Start Menu shortcut lifecycle

1. **On first launch** `EnsureStartMenuShortcuts()` in `MainWindow.xaml.cs` writes
   `Programs\Little Launcher.lnk` pointing at the running exe. Nothing creates it beforehand: a
   portable build has no installer to do it, and a packaged one already has its own entry, so
   `EnsureStartMenuShortcuts()` stops early when `IsPackaged`.
2. **On icon change** `UpdateShortcutIcons()` re-stamps the shortcut with the new icon.
3. **On uninstall** — MSIX takes its entry with the package; a portable copy leaves the `.lnk`
   behind until `cleanup-uninstall.ps1` is run (see below).

**Critical:** the shortcut sits directly in `ProgramMenuFolder`, not a subfolder, so the path the
app writes and the path it later updates are the same one. `RemoveLegacyMsiSubfolderShortcut()`
still deletes the stale `Little Launcher\` subfolder shortcut the MSI used to create — keep it,
that is the only thing tidying up after a machine that once had the MSI.

## Portable update flow

`CheckForGitHubUpdateAsync()` reads the latest release tag and compares it against the running
assembly version. **That is all it does — nothing is downloaded and nothing is installed.** Home
and About show the new version behind a **View Release** button that opens the release page, and
unpackaged builds additionally raise the startup toast (packaged ones do not — Store updates
arrive on their own, so there is nothing to interrupt anyone about).

Deliberate: a portable copy is a directory the user chose, may have put somewhere unwritable, and
is executing out of at that moment. Self-replacing it means surviving locked WebView2 and
companion-exe handles and stripping Mark-of-the-Web from every extracted file, to save a drag and
drop. If it is ever reconsidered, that is the work involved.

## MSIX / Store update flow

For packaged installs, `UpdateService` takes a separate path through `Windows.Services.Store.StoreContext` instead of GitHub Releases:

1. `CheckForUpdateAsync()` calls `GetAppAndOptionalStorePackageUpdatesAsync()` to detect Store updates. That list also contains framework/dependency packages, so `CheckForStoreUpdateAsync` filters to the main app package by `FamilyName` — and the presence of that entry **is** the update signal (see the trap below).
2. The version number to display comes from the Store's public display-catalog endpoint (`TryGetPublishedVersionAsync`), not from the update list. It is best-effort: `null` means "cannot say", and the Store's list is then trusted on its own rather than vetoed by a failed lookup. When it *does* answer and the published version is not newer than what's installed, the update is suppressed — that is the guard against a stale list offering an update to the running version. `LatestVersion` is left **empty** when an update exists but its number is unknown, and Home/About word that case without a version rather than inventing one.
3. Home/About pages reuse the same cached result shape as the GitHub path, but only the Store one gets an install button — see the portable flow above
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

**Portable:** deleting the folder removes the app but not what it wrote outside it, and there is
no uninstaller left to tidy that up. `cleanup-uninstall.ps1` ships in the build output for the
user to run by hand, with the app closed — it used to be an MSI custom action and is now just a
script:

| What | Where |
|---|---|
| App data folder | `%AppData%\LittleLauncher\` (settings, companion exe, icons, web profiles) |
| Start Menu shortcuts | `Programs\Little Launcher.lnk`, the legacy `Little Launcher Flyout*.lnk`, and the `Programs\Little Launcher\` folder of per-web-launcher shortcuts |
| Startup registry entry | `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Little Launcher` |
| Pinned taskbar shortcuts | Any `.lnk` in `User Pinned\TaskBar\` targeting `LittleLauncherFlyout.exe` |

Run it *before* deleting the folder — it lives in that folder, and it does not remove the folder
itself.

**MSIX limitation:** MSIX has no custom uninstall actions. When an MSIX package is removed, Windows deletes the package files, its own Start Menu entry, **and all VFS-redirected data** (settings, cached icons, companion exe) because the entire `%LocalAppData%\Packages\{PFN}\` tree is removed. Pinned taskbar shortcuts survive as dead `.lnk` files — Windows 11 eventually detects and offers to remove stale pins. Settings **do** survive MSIX upgrades — Windows preserves package data during version updates, including updates initiated through the Store API path above.

## Building the MSIX (`build-msix.ps1`)

`LittleLauncherMSIX/build-msix.ps1 -Platform {x64|ARM64}` publishes the app (self-contained) + the AOT companion, assembles the layout, runs `makepri`/`makeappx`, and signs. Toolchain requirements (see also the local-build memory):

- **Windows SDK** packaging tools (`makeappx`/`makepri`/`signtool`). The script auto-detects the newest installed `C:\Program Files (x86)\Windows Kits\10\bin\10.*` that has them — don't hardcode a version.
- **VS C++ build tools** (incl. `VC.Tools.ARM64`) for the Native AOT companion. The script prepends the **VS Installer dir to `PATH`** when `vswhere.exe` isn't resolvable, because the ILCompiler targets shell out to `vswhere`; without it the native link fails with **exit code 123**.
- **`-NoSign`** is Store mode: skips signing and leaves the Store `Identity`/`Publisher` intact for the Store to re-sign on ingestion. Without it, the manifest publisher is rewritten to the dev cert subject so `signtool` can sign locally.

### Sideloading a build over the installed package

**The output filename carries the version** (`LittleLauncher-1.33.0-ARM64.msix`), so `msix-output`
accumulates builds instead of overwriting the same two names. It used to be
`LittleLauncher-ARM64.msix`, which meant every build replaced the last one with nothing on disk to
say which was which — and the file you hand to Partner Center is chosen by eye. Partner Center reads
the version from the manifest either way; the name is so the human cannot pick the wrong one. The
same change is in the CopilotRekey and ImmichDrive copies of the script — see the sibling-app note
in the repo memory. CI globs `LittleLauncher-*.msix`, so it was unaffected.

**Plain `build-msix.ps1` (no arguments) is the wrong tool for "put my build on this machine".** It
stamps the *dev* cert's subject (`CN=RyanEwen`) as the publisher, and the publisher is part of the
package identity — so the result installs as a **second, separate package** with its own family
name and its own VFS-redirected `settings.json`. It comes up with no launchers at all, and both
copies then write to the same *physical* AppData for icons, web profiles and the companion exe.

To update the package that is already installed, sign with a cert whose subject **is** the Store
publisher, so the identity is unchanged and Windows treats it as an upgrade:

```powershell
.\LittleLauncherMSIX\build-msix.ps1 -Platform ARM64 `
    -TrustedPfxPath .\LittleLauncherMSIX\LittleLauncher-store-identity.pfx `
    -TrustedPfxPassword LittleLauncher
Add-AppxPackage -Path .\LittleLauncherMSIX\bin\msix-output\LittleLauncher-1.33.0-ARM64.msix -ForceUpdateFromAnyVersion
```

`LittleLauncher-store-identity.pfx` is a self-signed cert whose subject is
`CN=C21E6CEF-D0D1-4497-93F9-3718D054DA0E` — the publisher Partner Center assigned this app. Because
the subject matches, `build-msix.ps1` takes its `-TrustedPfxPath` branch and stamps a publisher
identical to the one already in the manifest, i.e. changes nothing. Settings, sign-ins and web
profiles all survive, and the Store can still update the package later since the identity matches.

- **The `.pfx`/`.cer` are gitignored and exist only on the dev machine** (as `*.pfx` / `*.cer`
  patterns; nothing of the sort is tracked). Regenerate with `New-SelfSignedCertificate -Subject
  "CN=C21E6CEF-D0D1-4497-93F9-3718D054DA0E"` and import the `.cer` into
  **`LocalMachine\TrustedPeople`** (needs elevation) — without that trust the install is refused.
- **`0x80073CFB` "already installed … contents are different"** means the manifest version matches
  the installed one exactly. `-ForceUpdateFromAnyVersion` does not cover this: it allows a *lower*
  version, not an identical one with different bytes. Bump a fourth component in
  `Directory.Build.props` for the test install (`1.34.0` → `1.34.0.1`); `build-msix.ps1` passes a
  four-part version through untouched. Put it back afterwards. **Do not `Remove-AppxPackage`
  instead:** that deletes the package's `LocalCache`, which is where the real `settings.json`, the
  web profiles and every sign-in live.
- **`Get-AppxPackage … | Select SignatureKind` tells you which you are on.** `Developer` means a
  sideloaded build is installed, `Store` means the shipped one.
- **`0x80073D02` "resources … currently in use"** means the app is running — including a copy
  started by clicking a tray icon or pinned shortcut a moment earlier. Kill `LittleLauncher` and
  retry; the other packages Windows lists alongside it are usually not the real blocker.
- **This is for testing your own build, not for shipping.** Store users get the package through the
  Store; a sideloaded build simply sits there until the next Store update replaces it.

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

## CI state: portable/GitHub Release automated, Store submission manual

`build-msix.yml` (badly named — it builds the app, the portable zips and the GitHub Release, not
the MSIX) runs clean on every `v*` tag. It attaches two artifacts,
`LittleLauncher-{x64,ARM64}-portable.zip`, and that is the whole GitHub side: nothing manual is
needed for portable users, and nothing installs itself for them either.

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

This is separate from `build-msix.yml`, which builds the portable zips + GitHub Release and is
fully automated today.
