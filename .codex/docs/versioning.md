> **Scope:** Use when bumping version numbers, creating releases, or modifying version-related files. Documents every file that contains a version string and the release workflow.
> **Governs:** `**/Directory.Build.props`, `**/MainWindow.xaml.cs`, `**/Package.appxmanifest`, `**/AppxManifest.xml`, `**/build-msix.ps1`, `**/.github/workflows/build-msix.yml`.

# Versioning & Releases

Little Launcher uses **semantic versioning** (`vMAJOR.MINOR.PATCH`).

## Single source of truth

The version is defined **once** in `Directory.Build.props`:

```xml
<Version>1.2.0</Version>
```

All other consumers derive from this automatically:

| Consumer | How it gets the version |
|---|---|
| **App (in-code display)** | `MainWindow.xaml.cs` reads `Assembly.GetName().Version` at startup — set by MSBuild from `<Version>` |
| **MSIX manifest** | `LittleLauncherMSIX/build-msix.ps1` replaces `VERSION_PLACEHOLDER` in `Package.appxmanifest` with the version from `Directory.Build.props` at build time |
| **Portable zip** | Built from the same `dotnet build` output, so it carries the assembly version |
| **Git tag** | Created manually to match: `git tag -a v1.1.0 ...` |

**`Directory.Build.props` is the only file to edit.** There are no fallback version strings left to keep in sync — the WiX `Package.wxs` define was the last one, and it went with the MSI.

## Release workflow

Pushing a tag matching `v*` triggers `.github/workflows/build-msix.yml` which:

1. Reads the version from `Directory.Build.props`
2. Stamps the MSIX manifest with the four-part version
3. Builds for **x64** and **ARM64** (`dotnet build -c Release`) and Authenticode-signs the exes
4. Creates a **GitHub Release** with auto-generated release notes (commit summary + full changelog link)
5. Attaches two artifacts: `LittleLauncher-{x64,ARM64}-portable.zip`

The Store package is **not** built or submitted by that workflow. `store-publish.yml` only proves the packaging still works; the `.msix` files you upload are built locally and submitted in Partner Center by hand — see [installer.md](installer.md).

## How to release

1. Edit `Directory.Build.props` — change `<Version>X.Y.Z</Version>`
2. Commit: `git commit -am "Bump version to vX.Y.Z"`
3. Tag: `git tag -a vX.Y.Z -m "vX.Y.Z: <brief summary>"`
4. Push both: `git push origin main vX.Y.Z`
5. The GitHub Action publishes the release; build and upload the Store package separately

## Version bump guidance

- **Patch** (`v1.0.1`): Bug fixes, minor tweaks, no new features
- **Minor** (`v1.1.0`): New features, non-breaking changes
- **Major** (`v2.0.0`): Breaking changes to settings format, major redesigns
