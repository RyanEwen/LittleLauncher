> **Scope:** Use when changing global launcher sync — its transports, its triggers, the
> `launchers.json` format, or the Cloud Sync page. Covers the provider model, what each transport
> may and may not assume, and how to add a new one.
> **Governs:** `**/Services/LauncherSyncService.cs`, `**/Services/SftpSyncService.cs`,
> `**/Services/FolderSyncService.cs`, `**/Services/LauncherPayload.cs`,
> `**/Services/CloudSyncService.cs`, `**/Services/*FileStore.cs`, `**/Services/OAuthPkceClient.cs`,
> `**/Services/ProtectedStore.cs`, `**/Services/CloudFolderService.cs`,
> `**/Services/AutoSyncService.cs`, `**/Pages/SyncPage.xaml*`.

# Global launcher sync

Global sync moves **every** launcher between machines as a single `launchers.json`. It is
separate from **shared launchers**, which publish one launcher's items and have their own
per-launcher transport setting (`Launcher.SharedSyncMode`). The two features share a file
(`SftpSyncService.cs`) and nothing else; a change to `SyncProvider` must not alter sharing.

## The layers

```
AutoSyncService ─┐                        (triggers: startup, debounce, periodic)
SyncPage ────────┴─▶ LauncherSyncService  (fans out over every ENABLED destination)
                             │
                             ▼
                      SyncDestinations     (one ISyncDestination per transport)
                             ├──▶ SftpSyncService    (SSH.NET)
                             ├──▶ FolderSyncService  (a directory on this machine)
                             └──▶ CloudSyncService   (a signed-in account)
                                        └──▶ ICloudFileStore
                                               ├── OneDriveFileStore     (Microsoft Graph)
                                               ├── GoogleDriveFileStore  (Drive v3)
                                               └── WebDavFileStore       (any WebDAV server)

shared by all of the above:
    LauncherPayload   format, download guard, merge, atomic write
    OAuthPkceClient   browser sign-in + refresh
    ProtectedStore    DPAPI-encrypted credentials
```

**`LauncherSyncService` is the only entry point.** Callers outside `Services/` must not name a
transport. Adding one otherwise means auditing every trigger for a case that was missed, which is
how the newer-local guard came to be applied unevenly in the first place (see below).

## Providers

`Models/SyncProviders.cs`. **Which ones are switched on lives in
`UserSettings.EnabledSyncProviders`** (a set); `SyncProvider` now records only which expander was
last open and the value migration reads.

| Value | Provider | Transport | Auth |
|---|---|---|---|
| 0 | SSH / SFTP server | `SftpSyncService` | key or password, typed into the app |
| 1 | OneDrive | `CloudSyncService` → `OneDriveFileStore` | OAuth in the system browser |
| 2 | Google Drive | `CloudSyncService` → `GoogleDriveFileStore` | OAuth in the system browser |
| 3 | Network file share *(folded into 4)* | `FolderSyncService` | Windows |
| 4 | Folder or network share | `FolderSyncService` | none / the folder's own sync client |
| 5 | WebDAV | `CloudSyncService` → `WebDavFileStore` | Basic auth, typed into the app |

Predicates classify them, and code should branch on these rather than on constants:
`SyncProviders.IsCloudAccount` (1, 2, 5), `IsFolderBased` (3, 4), `UsesTypedCredentials` (5), and
none-of-those meaning SFTP.

## Several destinations at once

`UserSettings.EnabledSyncProviders` is the set that actually syncs. `SyncProvider` survives only
as the *migration* source and is no longer consulted at sync time.

- **Upload fans out to every enabled destination.** One failing must not stop the others — that is
  the entire point of enabling several — so it succeeds if any destination took the copy and names
  the ones that did not.
- **Download takes whichever holds the newest copy.** They are replicas of one thing, so the
  question is not "which is authoritative" but "which was written last". Each destination is asked
  for a timestamp first, so only one full download happens.
- **A destination that cannot report a timestamp is skipped**, never assumed empty and never
  assumed newest. Either assumption would let an unreachable server decide the outcome.

That rule is only implementable because *every* transport can report a remote modified time —
`GetRemoteModifiedAsync` exists on the cloud stores, on `FolderSyncService` and on
`SftpSyncService`. **A new transport must provide one**, and it must not throw: returning null
takes it out of the running rather than failing the whole download. The SFTP one deliberately
connects only with a passphrase-free key, because it runs unattended on the sync timer and must
never block on a prompt.

**Migration.** `EnabledSyncProviders` is `null` — not empty — in files written before this
existed, and `CompleteInitialization` seeds it from the old exclusive `SyncProvider`. Null and
empty must stay distinguishable: empty is a real state, meaning the user switched everything off,
and treating it as "migrate me again" would switch a destination back on every launch.

**`NetworkShare` (3) is no longer selectable.** It and `Folder` (4) are the same transport reading
the same `SyncFolderPath`, so once several can be enabled at once, offering both would let a user
switch on two that silently fight over one path. `SyncProviders.Normalize` folds 3 into 4;
`Selectable` lists what the UI offers.

**WebDAV is an account provider with typed credentials.** It goes through `CloudSyncService` like
the OAuth providers — same payload, same guard, same `ICloudFileStore` — but `SignInAsync` verifies
a URL/username/password the user entered instead of opening a browser, so `SyncPage` gives it a
form rather than a Sign in button.

**SFTP is 0 deliberately.** `SettingsManager.JsonOptions` uses `WhenWritingDefault`, so the CLR
default is what every settings file written before this setting existed resolves to — it has to be
the behaviour those files were configured for. See
[user-settings.md](user-settings.md#defaults-vs-whenwritingdefault--the-trap-that-eats-settings).

**Switching a destination off does not clear its settings.** SFTP fields, `SyncFolderPath`,
WebDAV details and the stored cloud tokens all coexist and survive being switched off, so turning
one back on is never a re-entry exercise.

## Cloud accounts use the vendor APIs, not synced folders

OneDrive and Google Drive sign in and talk to Microsoft Graph and Drive v3 directly. They do not
touch the filesystem, and they do not require the vendor's sync client to be installed.

That is the whole reason they are not folder providers:

| | Synced folder | Vendor API |
|---|---|---|
| "Upload succeeded" means | written to disk, handed to a background client | the service accepted it |
| Needs the sync client installed | yes | no |
| Can read the remote's modified time | no | yes (`GetRemoteModifiedAsync`) |
| Online-only placeholder files | must be hydrated, blocks | not applicable |
| Half-written file can be uploaded | yes — hence `WriteAtomic` | no |

**Scopes are deliberately the smallest that work**, because the consent prompt is the product.
Each provider has two: a `PrivateScope` everyone consents to, and a `SharingScope` requested
**only when the user first shares a launcher** to that provider.

| | Private (sync only) | Sharing (incremental) |
|---|---|---|
| OneDrive | `Files.ReadWrite.AppFolder` | `Files.ReadWrite` — the whole drive |
| Google Drive | `drive.appdata` | `drive.appdata` **+** `drive.file` |

**Private launchers never leave private storage.** Broadening the grant does not move them: the
user's own `launchers.json` stays in the app folder / app-data folder either way, and only the
file they explicitly shared becomes a visible object. Do not "simplify" this by moving sync into
the shared area once the wider scope exists.

Google is the cheap case — both its scopes are **non-sensitive**, so sharing adds no verification
burden at all, and holding both keeps private config invisible. OneDrive has no per-file
equivalent of `drive.file`, so sharing there genuinely costs full drive access; that is why it is
asked for late rather than up front.

**Incremental consent is not a nicety.** Demanding "this app wants your entire OneDrive" at first
sign-in, to enable a feature most people never use, is the single biggest reason an OAuth flow
gets abandoned. `HasSharingConsent` / `RequestSharingConsentAsync` exist so the wide prompt
appears at the moment it is justified.

`CloudTokens.GrantedScope` records what a grant actually covers, because a stored refresh token is
only good for the scopes it was issued for. Without it, needing a wider scope surfaces as an
opaque 403 *when the user tries to share* rather than as a prompt. Refresh deliberately re-requests
`GrantedScope`, never the endpoint default — otherwise a token widened for sharing would be
quietly narrowed back on its next refresh. Tokens saved before this field existed have it empty,
which is read as "assume private only": the safe direction, since it triggers a consent that was
needed anyway.

- **OneDrive is personal Microsoft accounts only** — Microsoft has never extended app-folder
  permission to OneDrive for Business, which is why the registration targets the `consumers`
  authority and why work/school accounts must use provider 4 pointed at their synced folder.
- **Google Drive — `drive.appdata`.** A hidden per-app folder. Google classifies it as
  **non-sensitive**, so it needs only basic OAuth verification — no security assessment, no
  restricted-scope review. The trade-off is that the file cannot be seen or recovered from the
  Drive web UI, and revoking the app's access deletes it. Acceptable only because it is a replica;
  never put anything there that is not also stored locally.

### Auth

`OAuthPkceClient` implements Authorization Code + PKCE with a **loopback redirect**, shared by
both providers. Points that are load-bearing:

- **Loopback, not a custom URI scheme.** A registered scheme is global to the machine, so any
  other app can claim it and receive the authorization code. A loopback listener can only be
  reached locally, and the port is picked per attempt because a fixed one may be taken. PKCE then
  makes an intercepted code useless without the verifier.
- **System browser, not a WebView.** Required by both vendors for native apps, and it lets the
  user see a real address bar and reuse sessions and password managers. This app never sees the
  credentials.
- **Hand-rolled rather than MSAL + the Google SDK.** Both flows are plain OAuth 2.0 and the app
  needs one small file from each; two large SDKs with two token-cache and threading idioms would
  be more surface, not less, and this project keeps its dependency list short on purpose.
- **Carry the refresh token forward on refresh.** Google returns one only on the first consent, so
  a refresh response that overwrote it with empty would sign the user out an hour later.
- **A rejected refresh token is terminal** — revoked, expired, password changed. `GetAccessToken`
  clears the stored tokens so later calls report a clean "signed out" instead of retrying an
  endpoint that will keep saying no.

`ProtectedStore` keeps every sync credential — OAuth tokens and the WebDAV password — in
`%AppData%\LittleLauncher\cloud-{name}.dat`, encrypted with DPAPI to the current Windows user.
`CloudTokenStore` is a thin typed wrapper over it. **They must never go in settings.json** — that file is
exported, imported, backed up, and *uploaded by this very feature*, so a refresh token in it would
be copied to every machine and into whatever server or folder is configured.

Signing out is **local only**: it forgets the tokens on this PC and does not revoke the app's
access in the account. The UI says so rather than implying more than happened.

## Folder providers

Provider 3 and 4 remain a plain directory, and are the transport for everything without a
first-class integration — Dropbox, Seafile, iCloud, Syncthing, a USB stick, and OneDrive for
Business. Where a sync client is involved it, not this app, moves the bytes, so:

- **Writes must be atomic.** Cloud clients watch their folders and upload the moment a file
  changes, so a plain write gives them a window in which to upload a half-written file — and the
  other machine then parses truncated JSON over a working configuration. `LauncherPayload.WriteAtomic`
  writes a `.tmp` in the same directory and moves it into place, falling back to a direct write
  if a virtual drive refuses the move.
- **Reads may block.** A file in a cloud folder may be an online-only placeholder; touching it
  makes the client hydrate it. `FolderSyncService` reads the whole file on a background thread
  before parsing, and never parses straight off the filesystem.
- **`Directory.CreateDirectory` succeeding is not write access.** A share can grant traversal and
  refuse writes, so `TestAsync` writes and deletes a probe file rather than trusting the create.

`CloudFolderService` still detects OneDrive and Google Drive *folders*, but only for the
**shared-launcher** dialog now — global sync reaches those two through their APIs.

## `LauncherPayload` — the part both transports must share

`LauncherPayload` owns the wire format (a timestamped envelope, with a legacy plain-array
fallback), the newer-local download guard, and the merge into the live launcher collection.

**Do not reimplement any of it per transport.** Two reasons, both load-bearing:

1. The format has to stay identical, or a user syncing one machine over SFTP and another over
   OneDrive cannot move between them.
2. The guard has a **data-loss history**. It originally ran on the startup download only, so
   periodic syncs overwrote local launchers every few minutes however recently they had been
   edited. Only an explicit user-initiated download passes `force: true`. Duplicating that logic
   is how the bug comes back.

`ShouldSkipDownload` returns true when the caller must abandon the download; the reason it fills
in is user-facing.

`WriteAtomic` lives here too, and **every** folder write goes through it — global sync and shared
launchers both. See "Writes must be atomic" above.

### The envelope carries extensions as well as launchers

`LauncherPayload.Deserialize` returns `(launchers, timestamp, extensions)` and `ApplyAsync` takes
both, because the installed browser extensions are app-wide state a second machine should match.
**Identity travels; the copy does not** — what crosses is the Chrome Web Store id and the name, which
is all another machine needs to fetch its own copy. `BrowserExtensionService.Portable()` does that
projection, and is also what keeps the local-only `Folder` out of the payload; an extension added
from a folder or a zip has no id, so nothing elsewhere can reproduce it and it stays on the machine
it was added to.

The downloaded list is authoritative, the same rule the bookmark collection follows: an extension
missing from it was uninstalled somewhere, so the machine reading it uninstalls too.

### `CopyInto` must list every property that should travel

The merge updates an existing launcher **in place** so `PropertyChanged` subscriptions and the
launcher's open `FlyoutWindow` / `WebFlyoutWindow` survive the download. That means every synced
property is named explicitly in `LauncherPayload.CopyInto`, and **anything not named there never
propagates**.

That failure is unusually quiet, so it is worth knowing the shape: a launcher the other machine
has never seen is added wholesale and therefore carries everything. Only launchers that *already
exist on both sides* lose the missing fields — so it looks like it works when you first set sync
up, and silently stops for exactly the launchers you have had longest. `ViewMode`,
`IconModeIconsPerRow`, `ShowTitle` and the entire bookmark bar were missing on that basis until
they were added.

**When you add a property to `Launcher`, add it to `CopyInto`.**

One property is excluded on purpose, and says so in a comment: `WebFlyoutPosition`. It is a
remembered pixel position on one machine's monitor layout rather than a preference, so copying it
lands the flyout somewhere arbitrary — or off-screen — on a different display arrangement.
`WebAnchor`, the part the user actually chose, does travel. Apply the same test to anything else
that records *where a window ended up* rather than *what the user asked for*.

## Triggers

`AutoSyncService` is transport-agnostic and must stay that way. Every trigger is gated on its
private `IsSyncEnabled`, which is the auto-sync toggle **and** `LauncherSyncService.IsConfigured`.

**Never gate on `SftpHost`.** It is meaningless under a folder provider, and testing it was what
the gates did before providers existed — a machine on a folder provider would have been inert, or
(had only the toggle been checked) would fire failing uploads on every keystroke while the folder
was still unset.

`RestartPeriodicTimer` is reached from `Start()` at launch **and** from the
`OnSftpAutoSyncChanged` / `OnSftpAutoSyncIntervalChanged` handlers in `UserSettings`. Without
those handlers the settings did nothing until the next launch — switching auto-sync on left no
timer running, switching it off left the old one ticking, and a new interval was ignored. Any
future setting that changes the timer's shape needs the same wiring.

## Shared launchers and cloud folders

Shared launchers are **not** affected by `SyncProvider` — each launcher carries its own
`SharedSyncMode` (0 = file path, 1 = SFTP). File mode has always accepted any path, so a OneDrive,
Google Drive or UNC path works and always did; sharing through a cloud folder means both people
point at their own local copy of the same shared folder and let their client do the rest.

Two things were brought in line with the global folder transport:

- **The write is atomic** (`LauncherPayload.WriteAtomic`). It was a plain `FileMode.Create`
  stream, which on a watched cloud folder can be uploaded half-finished and reach a subscriber as
  truncated JSON. The likelihood went up considerably once a cloud folder became the obvious
  place to put a shared launcher.
- **The share dialog offers detected cloud roots** as quick-fill buttons beside the path box.
  They set the text box directly rather than raising a picker: a `FileSavePicker` opened from
  inside a `ContentDialog` is a modal-on-modal, which WinUI handles badly.

### Direction travels with the share

The shared file is an **envelope** (`SharedLauncherPayload`), not the bare `List<LauncherItem>` it
used to be, so it can carry `TwoWay` and the owner's launcher name alongside the items.

**Whether a share is 1-way or 2-way is the owner's decision about their launcher, so the owner
writes it and the subscriber reads it.** The subscribe dialog used to ask — a question the
subscriber has no way to answer and every reason to get wrong, where guessing means either losing
their own edits or pushing into a share meant to be read-only. `ApplyAsync` adopts the published
value on the first pull, and only for non-owners, so an owner's own file never overrides them.

**Bare arrays must keep parsing.** Files published by earlier versions are already on servers and
in shared folders, and a subscriber upgrading must not lose the launcher they subscribed to.
`Deserialize` tries the envelope, then falls back; a JSON array cannot deserialize into the
envelope object, so the fallback is unambiguous. A legacy file reports `TwoWay = null` — "cannot
say" — rather than guessing, and callers leave the launcher's existing setting alone.

Every transport routes through `SharedLauncherPayload` now, including the file and SFTP paths that
previously had their own inline `JsonSerializer` calls. That was the drift the shared payload
exists to prevent.

Shared sync still carries **items only**, not launcher-level presentation — a subscriber names
and styles their own copy. That is by design, not an oversight like `CopyInto` was.
`SharedLauncherPayload` owns that format, the apply, and the feedback-loop suppression, so the
transports cannot drift apart — the same rule as `LauncherPayload` for global sync.

### Sharing transports

`Models/SharedSyncModes.cs`, in `Launcher.SharedSyncMode`. **Independent of `SyncProviders`** — a
shared launcher must keep working when the owner and the subscriber sync their own settings to
completely different places.

| Value | Mode | 2-way | Notes |
|---|---|---|---|
| 0 | File (local, UNC, or a synced cloud folder) | yes | the existing default |
| 1 | SFTP | yes | per-launcher connection settings |
| 2 | WebDAV | yes | per-launcher URL + account; password in `ProtectedStore` |

**The requirement here is stricter than for global sync: the location must be reachable by someone
else.** That is what rules out the private per-app cloud storage global sync uses — OneDrive's app
folder and Google's app-data folder are per-user with no way to grant anyone access, so a launcher
shared from one would be invisible to the person it was shared with.

**WebDAV is the cleanest sharing transport**, and worth reaching for first: the URL is already a
real shared address, each participant authenticates as themselves, and both can write — so 2-way
works with no link to mint, no permission to grant and no subscriber-side resolution.

Details that matter:

- **Per-launcher URL and account, not the global WebDAV settings.** The server a colleague shares
  from is routinely not the one you sync your own launchers to.
- **The password is keyed by launcher id** in `ProtectedStore`, so two shared launchers can use
  different servers, and it never reaches settings.json.
- **An empty password box means "keep what is stored."** Re-opening the dialog to change the
  direction must not silently wipe a working password.
- **Stop Sharing clears the stored password**, and a failed subscribe clears it too — otherwise a
  live credential is orphaned on disk for a launcher that does not exist.
- `HasAutoKeyForShared` now gates every mode, not just SFTP: unattended timer syncs must skip
  anything that would prompt, and for WebDAV that means "is the password actually stored".

**Still to do: OneDrive and Google Drive sharing.** The scope plumbing is in place
(`SharingScope`, `HasSharingConsent`, `RequestSharingConsentAsync`), but the link machinery is
not — Graph `createLink` plus `/shares/{url}/driveItem` resolution, and Drive `permissions.create`.
Note a real constraint on the Google side: `drive.file` only reaches files the app created or the
user opened *through the app*, so a subscriber pasting a file id cannot read it. Google sharing is
therefore 1-way (owner publishes, subscribers fetch the public link) unless the Picker is
adopted.

## The Cloud Sync page

`SyncPage` is one `Expander` per destination: chevron, icon, name, live status, and a toggle in
the header; that destination's settings as the content. Points to keep:

- **Enabled and expanded are separate, and both are visible.** The toggle answers "is this
  syncing?", the chevron answers "do I want to see its settings?". An earlier version used a
  selected-card highlight for the second question and it was not discoverable — a border colour
  reads as state, not as "click me, there is more". Do not reintroduce a hidden selection.
- **Setting `ToggleSwitch.IsOn` from code raises `Toggled` exactly as a click does.** The
  `_initializing` guard therefore wraps `RefreshDestinations`, not just the constructor; without
  it the initial refresh writes the settings back over themselves.
- **Switching on an unconfigured destination auto-expands it**, and **signing in switches the
  destination on**. Both exist because a control that appears to do nothing is worse than no
  control — signing in to OneDrive and having nothing sync was a real trap.
- The password prompt is SFTP-only (`LauncherSyncService.UsesCredentials`).

## WebDAV

The one provider with no vendor relationship at all. A single implementation reaches Nextcloud,
ownCloud, Fastmail Files, Koofr, NAS boxes and anything else speaking the protocol — no app
registration, no consent screen, no verification review, no client secret, no SDK, and nothing a
vendor can revoke or deprecate. For self-hosted users, who are the same audience SFTP already
serves, it is also the case where a synced folder most often is not an option: the server is
reachable but no sync client is installed.

Protocol choices worth keeping:

- **`PROPFIND` with `Depth: 0`** to verify the base URL — asks about the collection itself rather
  than enumerating a folder that may be large.
- **`HEAD` for the modified time**, not `PROPFIND`. Every server serves `Last-Modified` on it, and
  it avoids parsing a multi-status XML body for one timestamp.
- **Basic auth sent pre-emptively**, not after a 401 challenge. It halves the round trips, and some
  servers answer an unauthenticated `PROPFIND` with 404 rather than 401 — which would surface as
  "wrong path" when the real problem is the credential.
- **A failed connect clears the stored password.** Leaving one that was just proven wrong lets
  every later background sync replay it at the server.
- **`http://` is allowed but warned about** on connect, since Basic auth sends the password in the
  clear.

The password goes in `ProtectedStore`, never settings.json — same rule, same reason, as the OAuth
tokens. The URL and username are ordinary settings and stay, so "Forget" is cheap to undo.

## Runbook: registering the OAuth clients

`CloudSyncCredentials.cs` ships with empty client IDs, and until they are filled in both cloud
providers report themselves unconfigured and disable their Sign in button rather than offering
one that can only fail. Registering is a one-time manual step.

**These identifiers are not secrets.** A desktop app cannot keep one — anyone can read it out of
the binary — which is why both vendors define the installed-app flow to be secure without one.
Google still issues a "client secret" for Desktop clients and still requires it on the token call;
it is a client *identifier* in practice. Both can sit in this public repo. Security comes from
PKCE and the loopback redirect.

### OneDrive — Microsoft Entra

1. [entra.microsoft.com](https://entra.microsoft.com) → **App registrations** → **New registration**
2. Name: anything the consent screen should show (e.g. `Little Launcher`)
3. **Supported account types: "Personal Microsoft accounts only"** — the app-folder permission
   does not exist for work/school accounts, and `common` would let a work account reach a consent
   screen it cannot satisfy
4. **Redirect URI:** platform **"Mobile and desktop applications"**, value `http://localhost`.
   Any port is then accepted, which is what lets the loopback listener pick a free one
5. **API permissions** → Microsoft Graph → **Delegated** → `Files.ReadWrite.AppFolder`
6. **No client secret.** A public client must not have one
7. Copy **Application (client) ID** → `CloudSyncCredentials.OneDriveClientIdDefault`

### Google Drive — Google Cloud

1. [console.cloud.google.com](https://console.cloud.google.com) → create a project. **Use a
   project of its own**, not a shared one: the consent screen is *per project*, so a shared
   project shows the wrong app name at sign-in, and its scope list is reviewed as a whole — one
   app needing a sensitive scope would drag Little Launcher's cheap `drive.appdata` review along
   with it.
2. **APIs & Services → Library** → enable **Google Drive API**. **This is a separate step from
   creating the OAuth client and is the easiest one to skip** — miss it and sign-in succeeds while
   every request comes back 403 *"Google Drive API has not been used in project N before or it is
   disabled"*. (`CloudErrors` now surfaces that message verbatim, including the activation URL.)
3. **OAuth consent screen** → External. App name, support email and developer email must match
   the real, public-facing app — reviewers compare the sign-in screen against what was submitted
4. **Scopes** → add `https://www.googleapis.com/auth/drive.appdata`. It is **non-sensitive**, so
   this needs only basic verification; do not add a broader Drive scope or the app lands in
   sensitive or restricted review for no gain
5. **Credentials → Create credentials → OAuth client ID** → application type **Desktop app**.
   Loopback redirects are permitted automatically for this type
6. Copy the **client ID** and **client secret** → `CloudSyncCredentials.GoogleClientIdDefault` /
   `GoogleClientSecretDefault`
7. Publish the consent screen. While it is in *Testing*, only accounts added as test users can
   sign in and refresh tokens expire after 7 days

### Where the credentials live

Client **IDs** are in `CloudSyncCredentials.cs` and committed. They appear in every auth request
URL and are not secrets.

The Google **client secret is not in the repository**, because this repo is public and git history
cannot be unpublished. It comes from an untracked `local.secrets.props` at the repo root:

```xml
<Project>
  <PropertyGroup>
    <GoogleClientSecret>GOCSPX-...</GoogleClientSecret>
  </PropertyGroup>
</Project>
```

`Directory.Build.props` imports it when present, `LittleLauncher.csproj` turns each property into
an `AssemblyMetadata` attribute, and `CloudSyncCredentials.BuildValue` reads it back at runtime.

**An environment variable cannot do this job for a shipped build** — it is a runtime lookup, and
an end user has no such variable set. Anything that must reach real users has to be compiled in.
The env-var overrides below still exist, but only for pointing a developer machine at a second
registration.

**A fresh clone and CI have no such file, and that is the intended state:** Google Drive reports
itself unconfigured rather than failing at sign-in with something unexplainable. Keep a copy of
the file somewhere safe, because it cannot be recovered from the repository.

```
LITTLELAUNCHER_ONEDRIVE_CLIENT_ID
LITTLELAUNCHER_GOOGLE_CLIENT_ID
LITTLELAUNCHER_GOOGLE_CLIENT_SECRET
```

## Adding a transport

1. Add a constant to `SyncProviders` (never renumber existing ones — they are serialized) and a
   case to `DisplayName`. Add it to `IsFolderBased` or `IsCloudAccount` if it belongs to one.
2. **If it is another cloud account, implement `ICloudFileStore` only** — a store, plus a line in
   `CloudSyncService.StoreFor`. Everything else (payload, guard, merge, sign-in, refresh, token
   storage) is already shared. Reuse `OAuthPkceClient`; do not add a vendor SDK for one small file.
3. Otherwise write the service, using `LauncherPayload` for serialize / deserialize /
   `ShouldSkipDownload` / `ApplyAsync` / `WriteAtomic` — do not hand-roll them.
4. Add an `ISyncDestination` adapter in `SyncDestinations` and list the constant in
   `SyncProviders.Selectable`. **`LauncherSyncService` needs no change** — it only ever talks to
   the adapters. If you find yourself editing it, the abstraction is in the wrong place.
   The adapter must supply a `GetRemoteModifiedAsync` that returns null rather than throwing, or
   an unreachable destination will break every download instead of just its own.
5. Add settings to `UserSettings` under **Sync destination**, minding the `WhenWritingDefault`
   trap. Tokens are the exception: they go in `CloudTokenStore`, never in settings.json.
6. Add the panel and the `ComboBoxItem` to `SyncPage` — the combo binds `SelectedIndex` directly
   to the provider int, so **item order is the constant order**.
7. `AutoSyncService` should need no change. If it does, the abstraction is in the wrong place.
