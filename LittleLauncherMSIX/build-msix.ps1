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
    .\build-msix.ps1 -NoSign              # Store upload (unsigned)
#>
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    # Optional: path to a CA-trusted PFX certificate for Authenticode signing.
    # When provided, the embedded exe files and the final MSIX are signed with this
    # cert instead of the dev self-signed cert. Required to satisfy Smart App Control.
    [string]$TrustedPfxPath = "",

    [string]$TrustedPfxPassword = "",

    # Skip signing entirely. Use for Microsoft Store uploads — the Store re-signs
    # the package with its own certificate during ingestion.
    [switch]$NoSign
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ── Paths ──────────────────────────────────────────────────────────────────────
$msixDir       = $PSScriptRoot
$repoRoot      = Split-Path $msixDir -Parent
$mainProj      = Join-Path $repoRoot "LittleLauncher\LittleLauncher.csproj"
$flyoutProj    = Join-Path $repoRoot "LauncherShortcut\LauncherShortcut.csproj"
$manifestSrc   = Join-Path $msixDir  "Package.appxmanifest"
$imagesDir     = Join-Path $msixDir  "Images"
$pfxFile       = Join-Path $msixDir  "LittleLauncher.pfx"

$rid           = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$publishDir    = Join-Path $repoRoot "LittleLauncher\bin\$Platform\$Configuration\net10.0-windows10.0.22000.0\$rid\publish"
$flyoutPublish = Join-Path $repoRoot "LauncherShortcut\bin\$Platform\$Configuration\net10.0-windows10.0.22000.0\$rid\publish"
$layoutDir     = Join-Path $msixDir  "bin\msix-layout\$Platform"
$outputDir     = Join-Path $msixDir  "bin\msix-output"

# The Native AOT companion build shells out to vswhere.exe (via the ILCompiler
# targets) to locate the VC toolchain. If the VS Installer dir isn't on PATH the
# native link step fails with exit code 123, so add it when vswhere isn't resolvable.
$vsInstaller = "C:\Program Files (x86)\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstaller) -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

# Windows SDK packaging tools — pick the newest installed 10.x SDK that has them,
# using native host-architecture binaries (so this works across SDK versions / CI runners).
$sdkHostArch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
$sdkRoot     = "C:\Program Files (x86)\Windows Kits\10\bin"
$sdkBin      = if (Test-Path $sdkRoot) {
    Get-ChildItem $sdkRoot -Directory |
        Where-Object { $_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName "$sdkHostArch\makeappx.exe")) } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1 -ExpandProperty FullName |
        ForEach-Object { Join-Path $_ $sdkHostArch }
}
if (-not $sdkBin) {
    Write-Error "Windows SDK packaging tools (makeappx/makepri/signtool) not found under '$sdkRoot'.`nInstall a recent Windows SDK, e.g. winget install Microsoft.WindowsSDK.10.0.26100"
    exit 1
}
$makeappx = Join-Path $sdkBin "makeappx.exe"
$signtool = Join-Path $sdkBin "signtool.exe"
$makepri  = Join-Path $sdkBin "makepri.exe"
foreach ($tool in @($makeappx, $signtool, $makepri)) {
    if (-not (Test-Path $tool)) { Write-Error "Missing SDK tool: $tool"; exit 1 }
}
Write-Host "Toolchain: $sdkBin" -ForegroundColor DarkCyan

# ── Version (from Directory.Build.props) ───────────────────────────────────────
$propsXml    = [xml](Get-Content (Join-Path $repoRoot "Directory.Build.props"))
$version     = $propsXml.SelectSingleNode("//Version").InnerText
if (-not $version) { Write-Error "Cannot read <Version> from Directory.Build.props"; exit 1 }
# MSIX requires four-part version (X.Y.Z.0)
$msixVersion = if ($version -match '^\d+\.\d+\.\d+$') { "$version.0" } else { $version }
Write-Host "Version: $msixVersion (from Directory.Build.props)" -ForegroundColor Cyan

# Named with the version, and set here rather than in the paths block above because the version is
# not known until it has been read. Every build used to write the same two filenames, so the folder
# held whichever build ran last with nothing on disk to say which - and the file you upload to the
# Store is chosen by eye. Partner Center reads the version from the manifest either way; this is so
# the human picking the file cannot get it wrong.
$msixFile    = Join-Path $outputDir "LittleLauncher-$version-$Platform.msix"

# ── Signing certificate (auto-generate if missing) ─────────────────────────────
if ($NoSign) {
    Write-Host "Skipping signing (Store upload mode)" -ForegroundColor Yellow
} elseif (-not (Test-Path $pfxFile)) {
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

# For local (non-Store) builds, replace the Store publisher with the dev cert subject
# so signtool can sign the package. The Store re-signs with its own cert during ingestion.
if (-not $NoSign -and [string]::IsNullOrEmpty($TrustedPfxPath)) {
    $devCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxFile, "LittleLauncher")
    $devPublisher = $devCert.Subject
    Write-Host "  Stamping Publisher=$devPublisher (dev cert)"
    # Read the current Identity Publisher from the manifest so we know what to replace
    if ($manifestContent -match '<Identity[^>]+Publisher="([^"]+)"') {
        $storePublisher = $Matches[1]
        $manifestContent = $manifestContent.Replace("Publisher=`"$storePublisher`"", "Publisher=`"$devPublisher`"")
    }
} elseif (-not $NoSign -and -not [string]::IsNullOrEmpty($TrustedPfxPath)) {
    $trustedCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($TrustedPfxPath, $TrustedPfxPassword)
    $trustedPublisher = $trustedCert.Subject
    Write-Host "  Stamping Publisher=$trustedPublisher (trusted cert)"
    if ($manifestContent -match '<Identity[^>]+Publisher="([^"]+)"') {
        $storePublisher = $Matches[1]
        $manifestContent = $manifestContent.Replace("Publisher=`"$storePublisher`"", "Publisher=`"$trustedPublisher`"")
    }
}

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

# ── Step 5b: Sign embedded executables (before packaging) ────────────────────
# Smart App Control requires Authenticode signatures on all PE files from a
# CA-trusted OV or EV code signing certificate.  Self-signed certs do NOT work.
# Pass -TrustedPfxPath and -TrustedPfxPassword to enable this step.
if (-not $NoSign) {
    if (-not [string]::IsNullOrEmpty($TrustedPfxPath)) {
        if (-not (Test-Path $TrustedPfxPath)) {
            Write-Error "TrustedPfxPath not found: $TrustedPfxPath"
            exit 1
        }
        Write-Host "`n=== Signing embedded executables (Authenticode) ===" -ForegroundColor Cyan
        $exesToSign = Get-ChildItem $layoutDir -Filter "*.exe" -Recurse
        foreach ($exe in $exesToSign) {
            Write-Host "  Signing $($exe.Name) ..."
            & $signtool sign /fd SHA256 /f $TrustedPfxPath /p $TrustedPfxPassword `
                /tr http://timestamp.digicert.com /td SHA256 $exe.FullName
            if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed for $($exe.FullName)"; exit 1 }
        }
    } else {
        Write-Warning "TrustedPfxPath not specified — executables will not be Authenticode-signed."
        Write-Warning "Smart App Control may block the app. Use -TrustedPfxPath to sign with a CA-trusted cert."
    }
}

# ── Step 5: Package ───────────────────────────────────────────────────────────
Write-Host "`n=== Packaging MSIX ===" -ForegroundColor Cyan

if (-not (Test-Path $outputDir)) { New-Item $outputDir -ItemType Directory -Force | Out-Null }
if (Test-Path $msixFile) { Remove-Item $msixFile -Force }

& $makeappx pack /d $layoutDir /p $msixFile /o
if ($LASTEXITCODE -ne 0) { Write-Error "makeappx pack failed"; exit 1 }

# ── Step 6: Sign ──────────────────────────────────────────────────────────────
if ($NoSign) {
    Write-Host "`n=== Skipping MSIX signing (Store upload) ===" -ForegroundColor Yellow
} else {
    Write-Host "`n=== Signing MSIX ===" -ForegroundColor Cyan

    if (-not [string]::IsNullOrEmpty($TrustedPfxPath)) {
        # Sign with the CA-trusted cert (satisfies Smart App Control)
        & $signtool sign /fd SHA256 /f $TrustedPfxPath /p $TrustedPfxPassword `
            /tr http://timestamp.digicert.com /td SHA256 $msixFile
    } else {
        # Fall back to the dev self-signed cert (suitable for local testing only)
        & $signtool sign /fd SHA256 /a /f $pfxFile /p "LittleLauncher" $msixFile
    }
    if ($LASTEXITCODE -ne 0) { Write-Error "signtool sign failed"; exit 1 }
}

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
