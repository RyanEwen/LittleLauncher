<#
.SYNOPSIS
Commits an already uploaded Store draft only after verifying manual publication.
.DESCRIPTION
Preserves pricing, listings, and packages. Never invokes a publish endpoint. Re-running
after commit only reads status, so an interrupted run cannot submit a second time.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+$')][string]$SubmissionId,
    [switch]$InspectOnly
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
    throw 'The requested submission is not the current pending submission.'
}
$uri = "$api/submissions/$SubmissionId"
$draft = Invoke-RestMethod -Uri $uri -Headers $headers
if ($InspectOnly) {
    # This read-only path answers which documented tier range the account supports.
    [pscustomobject]@{
        status = $draft.status
        priceId = $draft.pricing.priceId
        isAdvancedPricingModel = $draft.pricing.isAdvancedPricingModel
        manualPublication = $draft.targetPublishMode -eq 'Manual'
    } | ConvertTo-Json | Write-Output
    return
}
if ($draft.status -eq 'PendingCommit') {
    # Change only the release hold; the existing uploaded bundle remains in place.
    $draft.targetPublishMode = 'Manual'
    $null = Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -ContentType 'application/json' -Body ($draft | ConvertTo-Json -Depth 100)
    $verified = Invoke-RestMethod -Uri $uri -Headers $headers
    if ($verified.targetPublishMode -ne 'Manual') { throw 'Manual publication hold was not retained. Not committing.' }
    if ($verified.pricing.priceId -ne $draft.pricing.priceId) { throw 'Price changed unexpectedly. Not committing.' }
    Write-Output "Verified publication hold: Manual; price: $($verified.pricing.priceId)"
    $result = Invoke-RestMethod -Method Post -Uri "$uri/commit" -Headers $headers -ContentType 'application/json'
    Write-Output "Commit response: $($result.status)"
} elseif ($draft.targetPublishMode -ne 'Manual') {
    throw 'Existing submission is not held for manual publication.'
}

# Wait only for ingestion, not the potentially multi-day certification process.
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $status = Invoke-RestMethod -Uri "$uri/status" -Headers $headers
    Write-Output "Submission status: $($status.status)"
    if ($status.status -match 'Failed$') {
        $status.statusDetails | ConvertTo-Json -Depth 20 | Write-Output
        throw 'Microsoft could not process the submission.'
    }
    if ($status.status -notin @('PendingCommit', 'CommitStarted')) { break }
    Start-Sleep -Seconds 20
}
$final = Invoke-RestMethod -Uri $uri -Headers $headers
[pscustomobject]@{
    status = $final.status
    targetPublishMode = $final.targetPublishMode
    priceId = $final.pricing.priceId
    packages = @($final.applicationPackages | Select-Object fileName, fileStatus, version, architecture)
} | ConvertTo-Json -Depth 10 | Write-Output
if ($final.targetPublishMode -ne 'Manual') { throw 'Manual publication hold could not be confirmed after commit.' }
