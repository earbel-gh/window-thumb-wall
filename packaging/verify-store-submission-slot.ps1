[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Z0-9]{12}$')]
    [string]$ProductId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The pinned MSIX CLI writes application JSON to stdout and diagnostics to stderr.
$metadata = @(& msstore apps get $ProductId)
if ($LASTEXITCODE -ne 0) {
    throw "Could not verify the Store draft slot (CLI exit $LASTEXITCODE)."
}
try {
    $storeApp = ($metadata -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable -NoEnumerate
}
catch {
    throw 'Store CLI returned invalid application metadata.'
}

if ($storeApp -isnot [System.Collections.IDictionary] -or
    $storeApp['Id'] -isnot [string] -or
    $storeApp['Id'] -ine $ProductId -or
    -not $storeApp.Contains('PendingApplicationSubmission')) {
    throw 'Store CLI did not return draft state for the configured product.'
}
if ($null -ne $storeApp['PendingApplicationSubmission']) {
    throw 'The Store product has an existing pending submission. Its draft was left untouched.'
}

Write-Output 'Verified that the Store product has no pending submission.'
