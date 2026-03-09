<#
.SYNOPSIS
    Builds the Little Launcher MSIX package for sideloading.

.DESCRIPTION
    1. Reads the version from Directory.Build.props (single source of truth)
    2. Publishes the app self-contained (bundles the .NET runtime)
    3. Publishes the companion LauncherShortcut as a Native AOT binary
    4. Assembles the MSIX layout (app files + stamped manifest + image assets)
    5. Generates resources.pri via makepri (merges app resources with image assets)
    6. Packages with makeappx.exe
    7. Signs with signtool.exe using a self-signed certificate

    The MSIX publishes with WindowsPackageType=MSIX to suppress the unpackaged-only
    auto-bootstrapper, and declares a PackageDependency on Microsoft.WindowsAppRuntime.1.8
    so the framework package provides WinRT activation factories.
    The WinAppRuntime framework package must be installed on the target machine.

.PARAMETER Platform
    Target platform: x64 or ARM64 (default: auto-detect from PROCESSOR_ARCHITECTURE)

.PARAMETER Configuration
    Build configuration: Release (default) or Debug

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -Platform ARM64
    .\build-msix.ps1 -Platform x64 -Configuration Debug
#>
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ── Paths ──────────────────────────────────────────────────────────────────────
$repoRoot      = $PSScriptRoot
$mainProj      = Join-Path $repoRoot "LittleLauncher\LittleLauncher.csproj"
$flyoutProj    = Join-Path $repoRoot "LauncherShortcut\LauncherShortcut.csproj"
$msixDir       = Join-Path $repoRoot "LittleLauncherMSIX"
$manifestSrc   = Join-Path $msixDir  "Package.appxmanifest"
$imagesDir     = Join-Path $msixDir  "Images"
$pfxFile       = Join-Path $msixDir  "LittleLauncher.pfx"

$rid           = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$publishDir    = Join-Path $repoRoot "LittleLauncher\bin\$Platform\$Configuration\net10.0-windows10.0.22000.0\$rid\publish"
$flyoutPublish = Join-Path $repoRoot "LauncherShortcut\bin\$Platform\$Configuration\net10.0-windows10.0.22000.0\$rid\publish"
$layoutDir     = Join-Path $repoRoot "build\msix-layout\$Platform"
$outputDir     = Join-Path $repoRoot "build\msix-output"
$msixFile      = Join-Path $outputDir "LittleLauncher-$Platform.msix"

# Windows SDK tools — use native host architecture binaries
$sdkHostArch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
$sdkBin      = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\$sdkHostArch"
$makeappx    = Join-Path $sdkBin "makeappx.exe"
$signtool    = Join-Path $sdkBin "signtool.exe"
$makepri     = Join-Path $sdkBin "makepri.exe"

foreach ($tool in @($makeappx, $signtool, $makepri)) {
    if (-not (Test-Path $tool)) {
        Write-Error "Missing SDK tool: $tool`nInstall Windows SDK 10.0.26100.0"
        exit 1
    }
}

# ── Version (from Directory.Build.props) ───────────────────────────────────────
$propsXml    = [xml](Get-Content (Join-Path $repoRoot "Directory.Build.props"))
$version     = $propsXml.SelectSingleNode("//Version").InnerText
if (-not $version) { Write-Error "Cannot read <Version> from Directory.Build.props"; exit 1 }
# MSIX requires four-part version (X.Y.Z.0)
$msixVersion = if ($version -match '^\d+\.\d+\.\d+$') { "$version.0" } else { $version }
Write-Host "Version: $msixVersion (from Directory.Build.props)" -ForegroundColor Cyan

# ── Signing certificate (auto-generate if missing) ─────────────────────────────
if (-not (Test-Path $pfxFile)) {
    Write-Host "Signing certificate not found — generating self-signed cert..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=RyanEwen" `
        -KeyUsage DigitalSignature -FriendlyName "Little Launcher Dev" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    $pwd = ConvertTo-SecureString -String "LittleLauncher" -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfxFile -Password $pwd | Out-Null
    $cerFile = Join-Path $msixDir "LittleLauncher.cer"
    Export-Certificate -Cert $cert -FilePath $cerFile | Out-Null
    Write-Host "  Created $pfxFile"
    Write-Host "  To trust the MSIX, import $cerFile into Trusted People:" -ForegroundColor Cyan
    Write-Host "    Import-Certificate -FilePath `"$cerFile`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople" -ForegroundColor DarkCyan
}

# ── Step 1: Publish main app (self-contained) ─────────────────────────────────
Write-Host "`n=== Publishing LittleLauncher ($Platform $Configuration) ===" -ForegroundColor Cyan
dotnet publish $mainProj `
    -c $Configuration `
    -r $rid `
    -p:Platform=$Platform `
    --self-contained `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:WindowsPackageType=MSIX

if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed"; exit 1 }

# ── Step 2: Publish LauncherShortcut (Native AOT) ─────────────────────────────
Write-Host "`n=== Publishing LauncherShortcut (Native AOT) ===" -ForegroundColor Cyan
dotnet publish $flyoutProj `
    -c $Configuration `
    -r $rid `
    -p:Platform=$Platform

if ($LASTEXITCODE -ne 0) { Write-Error "LauncherShortcut AOT publish failed"; exit 1 }

# ── Step 3: Assemble MSIX layout ──────────────────────────────────────────────
Write-Host "`n=== Assembling MSIX layout ===" -ForegroundColor Cyan

if (Test-Path $layoutDir) { Remove-Item $layoutDir -Recurse -Force }
New-Item $layoutDir -ItemType Directory -Force | Out-Null

# Copy published app files
Write-Host "  Copying published files..."
Copy-Item "$publishDir\*" $layoutDir -Recurse -Force

# dotnet publish omits compiled XAML (.xbf) files from the publish output.
# Copy them from the RID build directory (parent of publish) to the layout.
$ridBuildDir = Split-Path $publishDir -Parent
Get-ChildItem $ridBuildDir -Filter "*.xbf" -Recurse |
    Where-Object { $_.FullName -notlike "*\publish\*" } |
    ForEach-Object {
        $rel = $_.FullName.Substring($ridBuildDir.Length + 1)
        $dest = Join-Path $layoutDir $rel
        $destDir = Split-Path $dest -Parent
        if (-not (Test-Path $destDir)) { New-Item $destDir -ItemType Directory -Force | Out-Null }
        Copy-Item $_.FullName $dest -Force
    }
Write-Host "  Copied compiled XAML (.xbf) files"

# Replace managed LauncherShortcut with Native AOT binary
$flyoutExe = Join-Path $flyoutPublish "LittleLauncherFlyout.exe"
if (Test-Path $flyoutExe) {
    Write-Host "  Replacing LauncherShortcut with Native AOT binary"
    foreach ($f in @("LittleLauncherFlyout.dll", "LittleLauncherFlyout.deps.json", "LittleLauncherFlyout.runtimeconfig.json")) {
        $stale = Join-Path $layoutDir $f
        if (Test-Path $stale) { Remove-Item $stale -Force }
    }
    Copy-Item $flyoutExe $layoutDir -Force
} else {
    Write-Warning "Native AOT binary not found at $flyoutExe — using managed build"
}

# Copy and stamp manifest (replace placeholders with build-time values)
$msixArch = if ($Platform -eq "ARM64") { "arm64" } else { "x64" }
$manifestContent = (Get-Content $manifestSrc -Raw)
$manifestContent = $manifestContent -replace 'ARCH_PLACEHOLDER', $msixArch
$manifestContent = $manifestContent -replace 'VERSION_PLACEHOLDER', $msixVersion
Set-Content -Path (Join-Path $layoutDir "AppxManifest.xml") -Value $manifestContent -NoNewline

# Copy image assets
$layoutImages = Join-Path $layoutDir "Images"
New-Item $layoutImages -ItemType Directory -Force | Out-Null
Copy-Item "$imagesDir\*" $layoutImages -Recurse -Force

Write-Host "  Layout ready: $layoutDir"

# ── Step 4: Generate resources.pri ────────────────────────────────────────────
Write-Host "`n=== Generating resources.pri ===" -ForegroundColor Cyan

# Remove any existing resources.pri from dotnet publish (we regenerate it to
# include the image assets copied into the layout).
$existingPri = Join-Path $layoutDir "resources.pri"
if (Test-Path $existingPri) { Remove-Item $existingPri -Force }

$priconfigFile = Join-Path $layoutDir "priconfig.xml"
& $makepri createconfig /cf $priconfigFile /dq en-US /o
if ($LASTEXITCODE -ne 0) { Write-Error "makepri createconfig failed"; exit 1 }

& $makepri new /pr $layoutDir /cf $priconfigFile /mn (Join-Path $layoutDir "AppxManifest.xml") /of $existingPri /o
if ($LASTEXITCODE -ne 0) { Write-Error "makepri new failed"; exit 1 }

# Clean up priconfig from layout
Remove-Item $priconfigFile -Force -ErrorAction SilentlyContinue

# ── Step 5: Package ───────────────────────────────────────────────────────────
Write-Host "`n=== Packaging MSIX ===" -ForegroundColor Cyan

if (-not (Test-Path $outputDir)) { New-Item $outputDir -ItemType Directory -Force | Out-Null }
if (Test-Path $msixFile) { Remove-Item $msixFile -Force }

& $makeappx pack /d $layoutDir /p $msixFile /o
if ($LASTEXITCODE -ne 0) { Write-Error "makeappx pack failed"; exit 1 }

# ── Step 6: Sign ──────────────────────────────────────────────────────────────
Write-Host "`n=== Signing MSIX ===" -ForegroundColor Cyan

& $signtool sign /fd SHA256 /a /f $pfxFile /p "LittleLauncher" $msixFile
if ($LASTEXITCODE -ne 0) { Write-Error "signtool sign failed"; exit 1 }

# ── Done ──────────────────────────────────────────────────────────────────────
$size = [math]::Round((Get-Item $msixFile).Length / 1MB, 1)
Write-Host "`n=== SUCCESS ===" -ForegroundColor Green
Write-Host "  MSIX:     $msixFile ($size MB)"
Write-Host "  Version:  $msixVersion"
Write-Host "  Platform: $Platform"
Write-Host "  To install:"
Write-Host "    Add-AppxPackage -Path '$msixFile' -ForceUpdateFromAnyVersion"
Write-Host "  NOTE: The signing certificate must be trusted on the target machine."
Write-Host "        Import LittleLauncherMSIX\LittleLauncher.cer into"
Write-Host "        'Trusted Root Certification Authorities' (Local Machine) first."
