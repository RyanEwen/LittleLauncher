<#
.SYNOPSIS
Updates an explicitly selected existing Store draft without creating, deleting, or committing submissions.
.DESCRIPTION
Combines the approved pair of listing features, sets a trial price tier, and uploads the
current release packages. Requires the standard Entra publishing environment variables.
#>
param(
    [Parameter(Mandatory)][string]$SubmissionId,
    [Parameter(Mandatory)][ValidatePattern('^Tier[0-9]+$')][string]$PriceId,
    [Parameter(Mandatory)][string]$UploadPath
)
$ErrorActionPreference = 'Stop'
$api = 'https://manage.devcenter.microsoft.com/v1.0/my/applications/9P3ZZBDQ6PJF'
$token = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$env:AZURE_AD_TENANT_ID/oauth2/token" -Body @{
    grant_type = 'client_credentials'
    client_id = $env:AZURE_AD_APPLICATION_CLIENT_ID
    client_secret = $env:AZURE_AD_APPLICATION_SECRET
    resource = 'https://manage.devcenter.microsoft.com'
}
$headers = @{ Authorization = "Bearer $($token.access_token)" }
$app = Invoke-RestMethod -Uri $api -Headers $headers
if ($app.pendingApplicationSubmission.id -ne $SubmissionId) {
    throw 'The requested draft is not the current pending submission.'
}
$uri = "$api/submissions/$SubmissionId"
# Preserve the complete submission and change only the approved listing, price, and packages.
$draft = Invoke-RestMethod -Uri $uri -Headers $headers
if ($draft.status -ne 'PendingCommit') { throw "Draft is not editable: $($draft.status)" }
$listing = $draft.listings.'en-us'.baseListing
$features = @($listing.features)
$tabs = 'Tabs and bookmark bars in Web Launchers.'
$extensions = 'Chrome extension support in Web Launchers.'
$combined = 'Tabs, bookmark bars, and Chrome extension support in Web Launchers'
if ($features -contains $tabs -and $features -contains $extensions) {
    $listing.features = @($features | ForEach-Object {
        if ($_ -eq $tabs) { $combined }
        elseif ($_ -ne $extensions) { $_ }
    })
} elseif ($features -notcontains $combined) {
    throw 'The approved feature pair was not found. No changes sent.'
}
foreach ($language in $draft.listings.PSObject.Properties) {
    if (@($language.Value.baseListing.features).Count -gt 20) {
        throw "Listing still exceeds 20 features: $($language.Name)"
    }
}
$draft.pricing.priceId = $PriceId

# The upload contains one real MSIX bundle with both architectures.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($UploadPath)
try { $names = @($zip.Entries | ForEach-Object FullName) }
finally { $zip.Dispose() }
if ($names.Count -ne 1 -or $names[0] -notmatch '^LittleLauncher-[0-9.]+\.msixbundle$') {
    throw 'Expected exactly one versioned MSIX bundle in the upload.'
}
$oldPackages = @($draft.applicationPackages | Where-Object { $_.fileStatus -ne 'PendingUpload' })
foreach ($package in $oldPackages) { $package.fileStatus = 'PendingDelete' }
$draft.applicationPackages = @($oldPackages) + @(@{
    fileName = [IO.Path]::GetFileName($UploadPath)
    fileStatus = 'PendingUpload'
})
$updated = Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -ContentType 'application/json' -Body ($draft | ConvertTo-Json -Depth 100)
# Upload to the Store-provided SAS URL without printing credentials or the URL.
try {
    Invoke-WebRequest -Method Put -Uri $updated.fileUploadUrl -InFile $UploadPath -ContentType 'application/zip' -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } | Out-Null
} catch { throw 'Store package upload failed. Inspect the pending draft before retrying.' }
$verified = Invoke-RestMethod -Uri $uri -Headers $headers
Write-Output "Draft $SubmissionId updated, NOT committed. PriceId: $($verified.pricing.priceId); English features: $(@($verified.listings.'en-us'.baseListing.features).Count)."
Write-Output "Bundle uploaded: $($names[0])"
if ($verified.pricing.priceId -ne $PriceId) { throw 'The Store did not retain the requested tier in its response.' }
