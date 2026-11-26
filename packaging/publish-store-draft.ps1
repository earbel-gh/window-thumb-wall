[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9]{12}$')][string]$ProductId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Store output can contain a credential generated during this command, including
# on errors. Discard every stream before the Actions runner can log any of it.
try {
    & msstore publish $PackagePath -id $ProductId --noCommit *> $null
    $publishExitCode = $LASTEXITCODE
} catch {
    throw 'Microsoft Store draft upload could not run. Verify the Store CLI setup and check the current submission before retrying.'
}
if ($publishExitCode -ne 0) {
    throw "Microsoft Store draft upload failed (CLI exit $publishExitCode). Check the current submission before retrying."
}

Write-Output 'Microsoft Store draft upload completed.'
