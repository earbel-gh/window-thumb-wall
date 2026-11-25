[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-store-submission-slot.ps1'
$productId = '9N0000000000'
$fixture = [pscustomobject]@{ ExitCode = 0; Response = '' }

function msstore {
    if (($args -join ' ') -cne "apps get $productId") {
        throw 'The fixture detected a command other than the expected Store read.'
    }
    Set-Variable -Name LASTEXITCODE -Value $fixture.ExitCode -Scope 1
    Write-Output $fixture.Response
}

function Assert-Rejected {
    param(
        [string]$Name,
        [string]$Response,
        [string]$ExpectedMessage,
        [int]$ExitCode = 0
    )
    $fixture.Response = $Response
    $fixture.ExitCode = $ExitCode
    try {
        $null = & $verifier -ProductId $productId
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw
        }
        Write-Output "PASS reject $Name"
        return
    }
    throw "The Store verifier unexpectedly accepted $Name."
}

$fixture.ExitCode = 0
$fixture.Response = '{"Id":"9N0000000000","PendingApplicationSubmission":null}'
$null = & $verifier -ProductId $productId
Write-Output 'PASS exact product with no pending submission'

Assert-Rejected -Name 'existing draft' -Response '{"Id":"9N0000000000","PendingApplicationSubmission":{"Id":"existing-draft"}}' -ExpectedMessage 'existing pending submission'
Assert-Rejected -Name 'incomplete pending state' -Response '{"Id":"9N0000000000","PendingApplicationSubmission":{}}' -ExpectedMessage 'existing pending submission'
Assert-Rejected -Name 'wrong product' -Response '{"Id":"9N0000000001","PendingApplicationSubmission":null}' -ExpectedMessage 'configured product'
Assert-Rejected -Name 'missing pending state' -Response '{"Id":"9N0000000000"}' -ExpectedMessage 'configured product'
Assert-Rejected -Name 'invalid JSON' -Response 'Store unexpected response' -ExpectedMessage 'invalid application metadata'
Assert-Rejected -Name 'failed read' -Response '{"Id":"9N0000000000","PendingApplicationSubmission":null}' -ExpectedMessage 'CLI exit' -ExitCode 42
