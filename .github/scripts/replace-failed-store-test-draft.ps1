<#
.SYNOPSIS
Replaces one identified failed Store test draft and stages the current packages.
.DESCRIPTION
The prior submission must be the current pending draft, have failed ingestion, and
match the earlier Tier2 pricing test. The new submission remains uncommitted.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+$')][string]$FailedSubmissionId,
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

# Refuse to remove any submission other than the failed Tier2 test draft.
$app = Invoke-RestMethod -Uri $api -Headers $headers
if ($app.pendingApplicationSubmission.id -ne $FailedSubmissionId) {
    throw 'The selected failed draft is no longer the current pending submission.'
}
$failedUri = "$api/submissions/$FailedSubmissionId"
$failed = Invoke-RestMethod -Uri $failedUri -Headers $headers
if ($failed.status -ne 'CommitFailed' -or $failed.pricing.priceId -ne 'Tier2' -or $failed.targetPublishMode -ne 'Manual') {
    throw 'The selected draft is not the held, failed Tier2 test submission.'
}
$null = Invoke-RestMethod -Method Delete -Uri $failedUri -Headers $headers
Write-Output "Removed failed test submission $FailedSubmissionId."

# A new API draft clones the last published submission, including its 21 features.
$new = Invoke-RestMethod -Method Post -Uri "$api/submissions" -Headers $headers -ContentType 'application/json'
if ($new.status -ne 'PendingCommit' -or [string]::IsNullOrWhiteSpace($new.id)) {
    throw 'Store did not return an editable replacement draft.'
}
Write-Output "Created replacement draft $($new.id)."
& "$PSScriptRoot/update-store-test-draft.ps1" -SubmissionId $new.id -PriceId $PriceId -UploadPath $UploadPath
if (!$?) { throw 'Replacement draft staging failed. Inspect the new draft before retrying.' }
