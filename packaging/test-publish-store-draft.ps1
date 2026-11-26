[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publisher = Join-Path $PSScriptRoot 'publish-store-draft.ps1'
$pwsh = (Get-Process -Id $PID).Path
$productId = '9N0000000000'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('window-thumb-wall-store-output-' + [guid]::NewGuid())
$fixturePath = Join-Path $scratch 'fake-store.ps1'
$packagePath = Join-Path $scratch 'fake package.msixupload'
$fixture = @{ ExitCode = 0; Calls = 0; LaunchFails = $false }

function msstore {
    $expected = @('publish', $packagePath, '-id', $productId, '--noCommit')
    if ($args.Count -ne $expected.Count -or ($args -join '|') -cne ($expected -join '|')) {
        throw 'The fixture received an unexpected Store command.'
    }
    $fixture.Calls++
    if ($fixture.LaunchFails) {
        throw 'Synthetic launch failure includes synthetic-credential-fragment-four'
    }
    & $pwsh -NoLogo -NoProfile -File $fixturePath $fixture.ExitCode
}

$null = New-Item -ItemType Directory -Path $scratch
try {
    Set-Content -LiteralPath $fixturePath -Encoding utf8 @'
param([int]$ExitCode)
[Console]::Out.WriteLine('FileUploadUrl: https://example.invalid/container/blob?synthetic-credential-fragment-one')
[Console]::Out.WriteLine('  synthetic-credential-fragment-two')
[Console]::Error.WriteLine('Provider error includes synthetic-credential-fragment-three')
exit $ExitCode
'@
    Set-Content -LiteralPath $packagePath -Encoding utf8 'Synthetic package fixture'

    foreach ($exitCode in 0, 42) {
        $fixture.ExitCode = $exitCode
        $output = [System.Collections.Generic.List[string]]::new()
        try {
            & $publisher -PackagePath $packagePath -ProductId $productId *>&1 | ForEach-Object { $output.Add([string]$_) }
        } catch {
            $output.Add($_.Exception.Message)
        }
        $expected = if ($exitCode -eq 0) {
            'Microsoft Store draft upload completed.'
        } else {
            'Microsoft Store draft upload failed (CLI exit 42). Check the current submission before retrying.'
        }
        if ($output.Count -ne 1 -or $output[0] -cne $expected) {
            throw "Unexpected visible output for synthetic Store CLI exit $exitCode."
        }
        Write-Output "PASS Store CLI exit $exitCode with safe diagnostics and no raw streams"
    }
    if ($fixture.Calls -ne 2) { throw 'Both cases must run the fake Store process exactly once.' }

    $fixture.LaunchFails = $true
    $launchError = $null
    try {
        $null = & $publisher -PackagePath $packagePath -ProductId $productId
    } catch {
        $launchError = $_.Exception.Message
    }
    if ($launchError -cne 'Microsoft Store draft upload could not run. Verify the Store CLI setup and check the current submission before retrying.') {
        throw 'The launch failure did not produce the safe error.'
    }
    Write-Output 'PASS Store CLI launch failure produces only safe diagnostics'
} finally {
    foreach ($path in $fixturePath, $packagePath) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    Remove-Item -LiteralPath $scratch
}
