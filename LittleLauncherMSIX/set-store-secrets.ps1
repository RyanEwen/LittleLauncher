<#
.SYNOPSIS
    Loads the four Microsoft Store publishing credentials into GitHub Actions secrets.

.DESCRIPTION
    `store-publish.yml` submits to the Store with the Microsoft Store Developer CLI, which
    needs four values from Partner Center / Microsoft Entra:

      AZURE_AD_TENANT_ID              Entra tenant ID
      AZURE_AD_APPLICATION_CLIENT_ID  App registration (client) ID
      AZURE_AD_APPLICATION_SECRET     Client secret for that app registration
      SELLER_ID                       Partner Center seller / publisher ID

    This script prompts for each one and pipes it straight to `gh secret set`. Values are
    read as secure strings, never echoed, never written to disk, and never placed in a
    command line (so they stay out of shell history and the process list).

    Run it again whenever the client secret is rotated — Entra secrets expire (24 months
    max), and an expired secret fails the publish step with an auth error.

.PREREQUISITES
    - GitHub CLI authenticated with `repo` scope:  gh auth status
    - The Entra app registration must have the **Manager** role in Partner Center
      (Account settings > User management > Microsoft Entra applications).

.EXAMPLE
    .\set-store-secrets.ps1
    .\set-store-secrets.ps1 -Repo RyanEwen/LittleLauncher
    .\set-store-secrets.ps1 -Only AZURE_AD_APPLICATION_SECRET   # rotate just the secret
#>
param(
    [string]$Repo = "RyanEwen/LittleLauncher",

    # Set only these secrets instead of all four (handy for rotating the client secret).
    [ValidateSet("AZURE_AD_TENANT_ID", "AZURE_AD_APPLICATION_CLIENT_ID",
                 "AZURE_AD_APPLICATION_SECRET", "SELLER_ID")]
    [string[]]$Only
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) not found. Install it from https://cli.github.com/ and run 'gh auth login'."
    exit 1
}

# Fail early with a clear message rather than midway through prompting.
gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "gh is not authenticated. Run 'gh auth login' first."
    exit 1
}

$secrets = [ordered]@{
    "AZURE_AD_TENANT_ID"             = "Entra tenant ID          (entra.microsoft.com > Identity > Overview)"
    "AZURE_AD_APPLICATION_CLIENT_ID" = "Application (client) ID  (Entra > App registrations > your app)"
    "AZURE_AD_APPLICATION_SECRET"    = "Client secret VALUE      (Entra > your app > Certificates & secrets)"
    "SELLER_ID"                      = "Seller / Publisher ID    (Partner Center > Account settings > Identifiers)"
}

if ($Only) {
    $filtered = [ordered]@{}
    foreach ($k in $secrets.Keys) { if ($Only -contains $k) { $filtered[$k] = $secrets[$k] } }
    $secrets = $filtered
}

Write-Host "`nSetting Store publishing secrets on $Repo" -ForegroundColor Cyan
Write-Host "Input is hidden. Press Enter on a blank prompt to skip a secret.`n" -ForegroundColor DarkGray

$set = 0
$skipped = @()

foreach ($name in $secrets.Keys) {
    Write-Host $name -ForegroundColor Yellow
    Write-Host "  $($secrets[$name])" -ForegroundColor DarkGray
    $secure = Read-Host "  value" -AsSecureString

    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))

    if ([string]::IsNullOrWhiteSpace($plain)) {
        Write-Host "  ~ skipped`n" -ForegroundColor DarkGray
        $skipped += $name
        continue
    }

    try {
        # Piped over stdin so the value never appears in a command line or history.
        $plain | gh secret set $name --repo $Repo --body -
        if ($LASTEXITCODE -ne 0) { throw "gh secret set exited $LASTEXITCODE" }
        Write-Host "  + set`n" -ForegroundColor Green
        $set++
    }
    finally {
        # Don't leave the plaintext sitting in a variable for the rest of the session.
        $plain = $null
        [System.GC]::Collect()
    }
}

Write-Host "Done — $set secret(s) set." -ForegroundColor Green
if ($skipped) { Write-Host "Skipped: $($skipped -join ', ')" -ForegroundColor DarkGray }

Write-Host "`nCurrently configured secrets:" -ForegroundColor Cyan
gh secret list --repo $Repo

Write-Host @"

Next: verify before enabling the tag trigger.
  gh workflow run 'Publish to Microsoft Store' --repo $Repo
  gh run watch (gh run list --workflow=store-publish.yml --limit 1 --json databaseId --jq '.[0].databaseId')
"@ -ForegroundColor DarkCyan
