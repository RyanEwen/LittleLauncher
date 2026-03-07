---
description: "Use when bumping version numbers, creating releases, or modifying version-related files. Documents every file that contains a version string and the release workflow."
applyTo: "**/Directory.Build.props,**/MainWindow.xaml.cs,**/Package.appxmanifest,**/Package.wxs,**/AppxManifest.xml,**/build-msix.ps1,**/.github/workflows/build-msix.yml"
---

# Versioning & Releases

Little Launcher uses **semantic versioning** (`vMAJOR.MINOR.PATCH`).

## Single source of truth

The version is defined **once** in `Directory.Build.props`:

```xml
<Version>1.1.0</Version>
```

All other consumers derive from this automatically:

| Consumer | How it gets the version |
|---|---|
| **App (in-code display)** | `MainWindow.xaml.cs` reads `Assembly.GetName().Version` at startup — set by MSBuild from `<Version>` |
| **WiX MSI installer** | `LittleLauncherSetup.wixproj` passes `ProductVersion=$(Version).0` via `DefineConstants`; CI also passes `-p:Version=...` explicitly |
| **MSIX manifest** | CI workflow stamps `LittleLauncherMSIX/Package.appxmanifest` Identity Version from `Directory.Build.props` before build |
| **Git tag** | Created manually to match: `git tag -a v1.1.0 ...` |

**To bump the version, edit only `Directory.Build.props`.** Everything else is automatic.

## Release workflow

Pushing a tag matching `v*` triggers `.github/workflows/build-msix.yml` which:

1. Reads the version from `Directory.Build.props`
2. Stamps the MSIX manifest with the four-part version
3. Builds for **x64** and **ARM64** (`dotnet build -c Release`)
4. Builds MSI installers via WiX (version injected from props)
5. Creates a **GitHub Release** with auto-generated release notes
6. Attaches four artifacts: `LittleLauncher-{x64,ARM64}-Setup.msi` and `LittleLauncher-{x64,ARM64}-portable.zip`

## How to release

1. Edit `Directory.Build.props` — change `<Version>X.Y.Z</Version>`
2. Commit: `git commit -am "Bump version to vX.Y.Z"`
3. Tag: `git tag -a vX.Y.Z -m "vX.Y.Z: <brief summary>"`
4. Push both: `git push origin main vX.Y.Z`
5. The GitHub Action handles the rest

## Version bump guidance

- **Patch** (`v1.0.1`): Bug fixes, minor tweaks, no new features
- **Minor** (`v1.1.0`): New features, non-breaking changes
- **Major** (`v2.0.0`): Breaking changes to settings format, major redesigns
