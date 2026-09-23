> **Scope:** Use when changing how Little Launcher is packaged, installed or updated. Covers the two channels it ships through — the portable zip and the Microsoft Store MSIX — the Start Menu shortcut lifecycle, and the update flow behind each.
> **Governs:** `**/UpdateService.cs`, `**/build-msix.ps1`, `**/Package.appxmanifest`.

# Packaging and updates

Little Launcher ships through **two channels, and neither of them is an installer of ours**:

| Channel | Artifact | Put in place by | Updates |
|---|---|---|---|
| **Portable** | `LittleLauncher-{x64,ARM64}-portable.zip`, attached to the GitHub release | the user, unzipping it wherever they like | in-app check against GitHub Releases; the app opens the release page and the user replaces the folder |
| **Microsoft Store** | MSIX, submitted by CI to Partner Center | the Store | in-app through the Store APIs, or silently by the Store itself |

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

**The output filename carries the version** (`LittleLauncher-1.33.0-ARM64.msix`). After an unsigned
Store package has been created successfully, the script removes MSIX files for older releases while
preserving both architecture packages for the current version. This leaves one upload-ready pair
after sequential x64 and ARM64 builds, keeps the previous release intact when a new build fails,
and prevents a signed fourth-component local-test build from deleting the Store upload artifacts.
It used to write `LittleLauncher-ARM64.msix`, which left nothing on disk to identify the version,
and later accumulated every version indefinitely. Partner Center reads the version from the
manifest either way; the name is so the human cannot pick the wrong file. CI globs
`LittleLauncher-*.msix`, so it is unaffected.

The cleanup candidate list must remain an array even when empty or containing one package,
so strict-mode PowerShell can read its count after sequential architecture builds.

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

## CI Store publishing

`build-msix.yml` publishes portable ZIPs and the GitHub Release on `v*` tags.
`store-publish.yml` builds unsigned x64 and ARM64 MSIX packages on one runner,
zips them into `LittleLauncher.msixupload`, and submits directly to Partner Center for
product `9P3ZZBDQ6PJF`. No Store package is uploaded as a public Actions artifact.

The workflow pins [Microsoft Store CLI v0.4.3](https://github.com/microsoft/msstore-cli/releases/tag/v0.4.3).
The published submission reports `PriceId: "Base"`, which the CLI cannot round-trip.
Tag runs now supply `Tier1012`, the verified US $0.99 tier, and may change converted
prices in other markets. The user accepted the initial 36-market comparison below.
Manual runs default to a draft; a different `price_id` can only be tested in a draft.
The workflow checks for a pending submission before invoking the CLI, because the
CLI would otherwise delete an existing draft. See [Microsoft PR #175](https://github.com/microsoft/msstore-cli/pull/175).

The first live test, [v1.40.2 on September 22, 2026](https://github.com/RyanEwen/LittleLauncher/actions/runs/35759458540),
built both packages and authenticated successfully. The CLI created a draft, retrieved it,
and stopped because the API returned `Base`. No package update was committed and pricing
was left unchanged. Paid-app support in v0.4.3 therefore does not unblock this product's
current per-market pricing configuration. The CLI attempts to delete its temporary draft
on this pricing failure. Do not assume that failed run left a draft available.

The initial Tier2 test hit the listing's 20-feature limit; a corrected draft then
passed upload validation but failed API commit ingestion with `Price Tier is not
supported`. That failed draft was deleted and replaced by a Tier1012 submission.
The workflow checks for pending submissions before invoking the CLI, so a retry
cannot silently overwrite an existing draft or in-flight submission.

Manual dispatch defaults `no_commit` to true, uploading a draft without submitting it.
Use that first to verify credentials, both architectures and pricing in Partner Center.
Tag pushes and manual runs with `no_commit` disabled commit the submission; certification
and the submission's publishing settings determine when it becomes available.

All four required repository secrets were present when checked on September 22, 2026:
`AZURE_AD_TENANT_ID`, `AZURE_AD_APPLICATION_CLIENT_ID`, `AZURE_AD_APPLICATION_SECRET`,
and `SELLER_ID`. Presence does not verify expiry or the app registration's Manager role.

For manual fallback, build both architectures with `build-msix.ps1 -Platform x64 -NoSign`
and `-Platform ARM64 -NoSign`, then upload the individual `.msix` files in Partner Center.
Do not upload the `.msixupload` container through the web UI.

## Runbook: creating the Store publishing credentials

The secrets come from a Microsoft Entra **app registration** that Partner Center has been told to
trust. CI uses these credentials; renew the client secret before its configured expiry.

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

**Step 6: validate a draft.** Manually dispatch `store-publish.yml` with `no_commit` enabled,
then inspect the draft's packages and pricing in Partner Center before committing it.
The workflow supplies Tier1012 by default; review the resulting regional prices.

### Repairing the existing pricing test draft

The manual Store workflow accepts an existing_draft ID with no_commit enabled and an explicit
price_id. The selected draft must match the current pending API submission and be PendingCommit.
The helper in .github/scripts/update-store-test-draft.ps1 combines the approved Web Launcher
feature bullets to meet the API's 20-feature limit, sets the requested tier, replaces packages,
and uploads the bundle. It never creates, deletes, or commits submissions. Review the resulting
regional prices and packages in Partner Center before any separate submission decision.


The September 22 Tier2 draft retry (Actions run 35766179842) uploaded both 1.40.2
packages and reduced the English features to 20. It failed at API commit ingestion
and has since been replaced. Draft prices shown before ingestion did not establish
effective regional pricing.


### Committing with publication held

After approval, dispatch store-review.yml with the existing submission_id. Its helper
commit-store-review.ps1 changes only targetPublishMode to Manual, reads it back before
committing, and checks ingestion status. It does not release the app. Re-running after
commit reads status without committing again. Inspect ingested prices and packages before
separately authorizing publication.


The earlier Tier2 held commit was attempted in Actions run 35767396785. Microsoft confirmed
targetPublishMode Manual and accepted the commit request, then returned CommitFailed with
InvalidOperation: Price Tier is not supported. Tier2 is therefore NOT a verified usable
price for this app; accepting the initial PUT did not establish ingestion compatibility.
No publication occurred from that attempt.


Use the inspect_only option of store-review.yml to read the submission's pricing
fields without changing it. Microsoft documents Tier1012-Tier1424 for advanced
pricing and Tier2-Tier96 for the original model, but the msstore-cli maintainers
measured the isAdvancedPricingModel flag flipping after a PUT and both ranges being
accepted on the same product. Do not select a tier based on this flag. The Partner
Center price-tier table or a Microsoft maintainer's mapping is needed to identify a
US price; regional prices still require review in Partner Center.


Read-only API inspections (Actions runs 35783439136 and 35783542100) showed that
the published submission has priceId Base with isAdvancedPricingModel true, whereas
the failed Tier2 draft reports isAdvancedPricingModel false. The differing flags
do not establish a valid tier range for this app. The current Partner Center
'view conversion table' link failed to load for both live and draft submissions.

On September 23, 2026, a Microsoft maintainer clarified in
[PR #175](https://github.com/microsoft/msstore-cli/pull/175#issuecomment-5791491206)
that Tier1012 is US $0.99. The reply lists the US price ranges and increments for
the current Tier1012-Tier1424 sequence. The
[replacement draft run](https://github.com/RyanEwen/LittleLauncher/actions/runs/35877319754)
used Tier1012, 20 English features, and both 1.40.2 packages. The
[held commit run](https://github.com/RyanEwen/LittleLauncher/actions/runs/35878083671)
passed price-tier ingestion and entered `PreProcessing` with `targetPublishMode:
Manual`. Partner Center showed the update in certification, and says it will
publish only after **Publish now** is selected. The U.S. price remains $0.99,
but **36 of 240 market prices changed** compared with published Submission 41.
See the [complete regional comparison](store-pricing-comparison-2026-09-23.md).
The user accepted those regional price changes on September 23, 2026.
Submission 42 still needs certification to finish before manual publication.

