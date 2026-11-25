[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PreviousMsi,
    [Parameter(Mandatory = $true)]
    [string]$CurrentMsi,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,
    [Parameter(Mandatory = $true)]
    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'
$script:Stage = 'preflight'
$script:Failure = $null
$script:CleanupFailures = [System.Collections.Generic.List[string]]::new()
$script:PreviousProductCode = $null
$script:CurrentProductCode = $null
$script:StatePath = Join-Path (Join-Path $env:LOCALAPPDATA 'WindowThumbWall') 'state.json'
$script:StateDirectory = Split-Path -Parent $script:StatePath
$script:FixtureBytes = [Text.UTF8Encoding]::new($false).GetBytes(
    '{"AutoAddApps":["notepad"],"Slots":[{"ProcessName":"notepad","Title":"Upgrade probe","IsPinned":false}]}' + "`n")
$script:FixtureHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($script:FixtureBytes))
$script:FixtureCreated = $false
$script:LogDirectoryReady = $false

Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace WindowThumbWall.MsiUpgradeGate
{
    public static class InstallerApi
    {
        [DllImport("msi.dll", EntryPoint = "MsiQueryProductStateW", CharSet = CharSet.Unicode)]
        public static extern int MsiQueryProductState(string productCode);
    }
}
'@

function Get-MsiProductCode {
    param([Parameter(Mandatory = $true)][string]$Path)

    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Path, 0)
        $view = $database.OpenView('SELECT `Property`, `Value` FROM `Property`')
        $null = $view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            try {
                if ([string]$record.StringData(1) -eq 'ProductCode') {
                    return [string]$record.StringData(2)
                }
            }
            finally {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
                $record = $null
            }
        }
        throw "MSI Property table has no ProductCode: $Path"
    }
    finally {
        foreach ($comObject in @($record, $view, $database, $installer)) {
            if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject)
            }
        }
    }
}

function Get-ProductState {
    param([Parameter(Mandatory = $true)][string]$ProductCode)
    return [WindowThumbWall.MsiUpgradeGate.InstallerApi]::MsiQueryProductState($ProductCode)
}

function Assert-ProductAbsent {
    param(
        [Parameter(Mandatory = $true)][string]$ProductCode,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $state = Get-ProductState -ProductCode $ProductCode
    if ($state -ne -1) {
        throw "$Label MSI product is not absent (MsiQueryProductState=$state); refusing to touch it."
    }
}

function Invoke-Msi {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string[]]$MsiArguments,
        [Parameter(Mandatory = $true)][string]$LogName
    )

    $script:Stage = $Stage
    $logPath = Join-Path $script:LogDirectory $LogName
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $MsiArguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    [void]$startInfo.ArgumentList.Add('/qn')
    [void]$startInfo.ArgumentList.Add('/norestart')
    [void]$startInfo.ArgumentList.Add('/l*v')
    [void]$startInfo.ArgumentList.Add($logPath)

    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $process.WaitForExit(900000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(30000)
            throw "$Stage timed out; MSI log: $logPath"
        }
        if ($process.ExitCode -ne 0) {
            throw "$Stage failed with exit code $($process.ExitCode); MSI log: $logPath"
        }
    } finally {
        $process.Dispose()
    }
    Write-Host "$Stage succeeded; log: $logPath"
}

try {
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
        [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'The major-upgrade gate is restricted to GitHub-hosted Windows runners.'
    }

    $previousPath = (Resolve-Path -LiteralPath $PreviousMsi).Path
    $currentPath = (Resolve-Path -LiteralPath $CurrentMsi).Path
    $previousHash = (Get-FileHash -LiteralPath $previousPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedPreviousHash = 'a6f911bbf44ff35a076f2ee5a5930cb684afe816431011491df047a8e9db8253'
    if ($previousHash -cne $expectedPreviousHash) {
        throw "Previous MSI SHA-256 mismatch: $previousHash"
    }

    $script:PreviousProductCode = Get-MsiProductCode -Path $previousPath
    $script:CurrentProductCode = Get-MsiProductCode -Path $currentPath
    if ($script:PreviousProductCode -eq $script:CurrentProductCode) {
        throw 'Previous and current MSI ProductCodes must differ for a major upgrade.'
    }

    Assert-ProductAbsent -ProductCode $script:PreviousProductCode -Label 'Previous'
    Assert-ProductAbsent -ProductCode $script:CurrentProductCode -Label 'Current'
    if (Test-Path -LiteralPath $script:StatePath) {
        throw "Upgrade fixture state already exists: $script:StatePath"
    }
    if (Test-Path -LiteralPath $script:StateDirectory) {
        throw "Upgrade fixture directory already exists: $script:StateDirectory"
    }

    New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
    $script:LogDirectory = (Resolve-Path -LiteralPath $LogDirectory).Path
    $script:LogDirectoryReady = $true

    Invoke-Msi -Stage 'Install v0.6.10 MSI' -MsiArguments @('/i', $previousPath) -LogName 'msi-upgrade-old-install.log'
    if ((Get-ProductState -ProductCode $script:PreviousProductCode) -ne 5) {
        throw 'Previous MSI did not reach INSTALLSTATE_DEFAULT after installation.'
    }

    $script:Stage = 'create isolated state fixture'
    New-Item -ItemType Directory -Force -Path $script:StateDirectory | Out-Null
    [IO.File]::WriteAllBytes($script:StatePath, $script:FixtureBytes)
    $script:FixtureCreated = $true

    Invoke-Msi -Stage 'Install current MSI over v0.6.10' -MsiArguments @('/i', $currentPath) -LogName 'msi-upgrade-new-install.log'
    if ((Get-ProductState -ProductCode $script:PreviousProductCode) -ne -1) {
        throw 'Previous MSI product remains installed after major upgrade.'
    }
    if ((Get-ProductState -ProductCode $script:CurrentProductCode) -ne 5) {
        throw 'Current MSI did not reach INSTALLSTATE_DEFAULT after major upgrade.'
    }

    $installedExe = Join-Path $env:ProgramFiles 'WindowThumbWall\WindowThumbWall.exe'
    if (-not (Test-Path -LiteralPath $installedExe)) {
        throw "Installed executable not found after upgrade: $installedExe"
    }
    $installedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
    if ($installedVersion -cne $ExpectedVersion) {
        throw "Installed executable version was $installedVersion; expected $ExpectedVersion."
    }
    if (-not (Test-Path -LiteralPath $script:StatePath)) {
        throw 'Upgrade removed the isolated state fixture.'
    }
    $stateHash = (Get-FileHash -LiteralPath $script:StatePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($stateHash -cne $script:FixtureHash) {
        throw "Upgrade changed the isolated state fixture (SHA-256 $stateHash)."
    }
    Write-Host "Upgrade preserved isolated state fixture SHA-256 $stateHash."
}
catch {
    $script:Failure = "Stage '$script:Stage' failed: $($_.Exception.Message)"
}
finally {
    if ($script:LogDirectoryReady) {
        foreach ($product in @(
            @{ Code = $script:CurrentProductCode; Label = 'current'; Log = 'msi-upgrade-new-uninstall.log' },
            @{ Code = $script:PreviousProductCode; Label = 'previous'; Log = 'msi-upgrade-old-uninstall.log' }
        )) {
            if ([string]::IsNullOrWhiteSpace([string]$product.Code)) { continue }
            try {
                $state = Get-ProductState -ProductCode $product.Code
                if ($state -ne -1) {
                    Invoke-Msi -Stage "Uninstall owned $($product.Label) MSI" -MsiArguments @('/x', [string]$product.Code) -LogName $product.Log
                    if ((Get-ProductState -ProductCode $product.Code) -ne -1) {
                        throw "Owned $($product.Label) MSI product remains installed."
                    }
                }
            }
            catch {
                $script:CleanupFailures.Add("$($product.Label) MSI cleanup: $($_.Exception.Message)")
            }
        }
    }

    if ($script:FixtureCreated -and (Test-Path -LiteralPath $script:StatePath)) {
        try {
            $remainingHash = (Get-FileHash -LiteralPath $script:StatePath -Algorithm SHA256).Hash.ToUpperInvariant()
            if ($remainingHash -eq $script:FixtureHash) {
                Remove-Item -LiteralPath $script:StatePath -Force
                if ((Test-Path -LiteralPath $script:StateDirectory) -and
                    -not (Get-ChildItem -LiteralPath $script:StateDirectory -Force | Select-Object -First 1)) {
                    Remove-Item -LiteralPath $script:StateDirectory -Force
                }
            } else {
                $script:CleanupFailures.Add('Fixture state changed; left the unexpected bytes in place for diagnosis.')
            }
        }
        catch {
            $script:CleanupFailures.Add("Fixture cleanup: $($_.Exception.Message)")
        }
    }
}

if ($script:Failure -or $script:CleanupFailures.Count -gt 0) {
    $messages = [System.Collections.Generic.List[string]]::new()
    if ($script:Failure) { $messages.Add($script:Failure) }
    foreach ($cleanupFailure in $script:CleanupFailures) { $messages.Add($cleanupFailure) }
    throw ([string]::Join(' ', $messages))
}

Write-Host 'Major-upgrade gate passed; owned MSI products were removed.'
