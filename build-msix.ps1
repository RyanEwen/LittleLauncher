<#
.SYNOPSIS
    Builds the SelfHostedHelper MSIX package.

.DESCRIPTION
    1. Publishes the app with dotnet publish (self-contained)
    2. Assembles the MSIX layout directory (app files + manifest + assets)
    3. Packages with makeappx.exe
    4. Signs with signtool.exe using the self-signed certificate

.PARAMETER Platform
    Target platform: x64 (default) or ARM64

.PARAMETER Configuration
    Build configuration: Release (default) or Debug

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -Platform ARM64
#>
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ── Paths ──────────────────────────────────────────────────────────
$repoRoot      = $PSScriptRoot
$mainProj      = Join-Path $repoRoot "SelfHostedHelper\SelfHostedHelper.csproj"
$msixDir       = Join-Path $repoRoot "SelfHostedHelperMSIX"
$manifestFile  = Join-Path $msixDir  "Package.appxmanifest"
$imagesDir     = Join-Path $msixDir  "Images"
$pfxFile       = Join-Path $msixDir  "SelfHostedHelper.pfx"

$rid           = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$publishDir    = Join-Path $repoRoot "SelfHostedHelper\bin\$Platform\$Configuration\net10.0-windows10.0.22000.0\$rid\publish"
$flyoutProj    = Join-Path $repoRoot "LauncherShortcut\LauncherShortcut.csproj"
$flyoutPublish = Join-Path $repoRoot "LauncherShortcut\bin\$Platform\$Configuration\net10.0-windows10.0.22000.0\$rid\publish"
$layoutDir     = Join-Path $repoRoot "build\msix-layout\$Platform"
$outputDir     = Join-Path $repoRoot "build\msix-output"
$msixFile      = Join-Path $outputDir "SelfHostedHelper-$Platform.msix"

# Windows SDK tools — use native host architecture binaries
$sdkHostArch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
$sdkBin = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\$sdkHostArch"
$makeappx = Join-Path $sdkBin "makeappx.exe"
$signtool = Join-Path $sdkBin "signtool.exe"
$makepri  = Join-Path $sdkBin "makepri.exe"

foreach ($tool in @($makeappx, $signtool, $makepri)) {
    if (-not (Test-Path $tool)) {
        Write-Error "Missing SDK tool: $tool`nInstall Windows SDK 10.0.26100.0"
        exit 1
    }
}

if (-not (Test-Path $pfxFile)) {
    Write-Error "Missing signing certificate: $pfxFile`nSee README for certificate setup instructions."
    exit 1
}

# ── Step 1: Publish ────────────────────────────────────────────────
Write-Host "`n=== Publishing $Platform $Configuration ===" -ForegroundColor Cyan
dotnet publish $mainProj `
    -c $Configuration `
    -r $rid `
    -p:Platform=$Platform `
    --self-contained `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=false

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed"
    exit 1
}

# ── Step 2: Publish LauncherShortcut (Native AOT) ─────────────────
Write-Host \"`n=== Publishing LauncherShortcut (Native AOT) ===\" -ForegroundColor Cyan
dotnet publish $flyoutProj `
    -c $Configuration `
    -r $rid `
    -p:Platform=$Platform

if ($LASTEXITCODE -ne 0) {
    Write-Error \"LauncherShortcut AOT publish failed\"
    exit 1
}

# ── Step 3: Assemble MSIX layout ──────────────────────────────────
Write-Host "`n=== Assembling MSIX layout ===" -ForegroundColor Cyan

# Clean and create layout directory
if (Test-Path $layoutDir) { Remove-Item $layoutDir -Recurse -Force }
New-Item $layoutDir -ItemType Directory -Force | Out-Null

# Copy published app files
Write-Host "  Copying published files from $publishDir"
Copy-Item "$publishDir\*" $layoutDir -Recurse -Force
# Replace managed LauncherShortcut files with Native AOT binary
$flyoutExe = Join-Path $flyoutPublish "TaskbarLauncherFlyout.exe"
if (Test-Path $flyoutExe) {
    Write-Host "  Replacing LauncherShortcut with Native AOT binary"
    # Remove managed files that won't exist in AOT output
    foreach ($f in @("TaskbarLauncherFlyout.dll", "TaskbarLauncherFlyout.deps.json", "TaskbarLauncherFlyout.runtimeconfig.json")) {
        $managed = Join-Path $layoutDir $f
        if (Test-Path $managed) { Remove-Item $managed -Force }
    }
    Copy-Item $flyoutExe $layoutDir -Force
} else {
    Write-Host "  WARNING: Native AOT binary not found, using managed build" -ForegroundColor Yellow
}
# Copy manifest (rename to AppxManifest.xml as required by makeappx)
Copy-Item $manifestFile (Join-Path $layoutDir "AppxManifest.xml") -Force

# Copy image assets
$layoutImages = Join-Path $layoutDir "Images"
New-Item $layoutImages -ItemType Directory -Force | Out-Null
Copy-Item "$imagesDir\*" $layoutImages -Recurse -Force

Write-Host "  Layout ready: $layoutDir"

# ── Step 4: Generate resources.pri ────────────────────────────────
Write-Host "`n=== Generating resources.pri ===" -ForegroundColor Cyan

# Create a priconfig.xml for makepri
$priconfigFile = Join-Path $layoutDir "priconfig.xml"
& $makepri createconfig /cf $priconfigFile /dq en-US /o
if ($LASTEXITCODE -ne 0) {
    Write-Error "makepri createconfig failed"
    exit 1
}

# Generate resources.pri from the layout directory
& $makepri new /pr $layoutDir /cf $priconfigFile /mn (Join-Path $layoutDir "AppxManifest.xml") /of (Join-Path $layoutDir "resources.pri") /o
if ($LASTEXITCODE -ne 0) {
    Write-Error "makepri new failed"
    exit 1
}

# Remove the priconfig.xml from the layout (not needed in the package)
Remove-Item $priconfigFile -Force -ErrorAction SilentlyContinue

# ── Step 5: Package ───────────────────────────────────────────────
Write-Host "`n=== Packaging MSIX ===" -ForegroundColor Cyan

if (-not (Test-Path $outputDir)) { New-Item $outputDir -ItemType Directory -Force | Out-Null }
if (Test-Path $msixFile) { Remove-Item $msixFile -Force }

& $makeappx pack /d $layoutDir /p $msixFile /o
if ($LASTEXITCODE -ne 0) {
    Write-Error "makeappx pack failed"
    exit 1
}

# ── Step 6: Sign ──────────────────────────────────────────────────
Write-Host "`n=== Signing MSIX ===" -ForegroundColor Cyan

& $signtool sign /fd SHA256 /a /f $pfxFile /p "SelfHostedHelper" $msixFile
if ($LASTEXITCODE -ne 0) {
    Write-Error "signtool sign failed"
    exit 1
}

# ── Done ──────────────────────────────────────────────────────────
$size = [math]::Round((Get-Item $msixFile).Length / 1MB, 1)
Write-Host "`n=== SUCCESS ===" -ForegroundColor Green
Write-Host "  MSIX: $msixFile ($size MB)"
Write-Host "  To install: double-click the .msix file"
Write-Host "  NOTE: The signing certificate must be trusted on the target machine."
Write-Host "        Import SelfHostedHelperMSIX\SelfHostedHelper.cer into"
Write-Host "        'Trusted Root Certification Authorities' (Local Machine) first."
