[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Verifier = Join-Path $PSScriptRoot 'verify-msi-app-startup.ps1'
$script:ExecutablePath = 'C:\Program Files\WindowThumbWall\WindowThumbWall.exe'
$global:WtwMsiStartupFixtureState = [pscustomobject]@{
    Fixture = $null
    StartCalls = 0
    SleepCalls = 0
    StartArguments = $null
}

function New-FakeProcess {
    param(
        [Parameter(Mandatory)] [hashtable] $Configuration
    )

    $process = [pscustomobject]@{
        Handle = [IntPtr]::new(17)
        Id = 4217
        MainWindowHandle = [IntPtr]$Configuration.MainWindowHandle
        HasExited = [bool]$Configuration.StartExited
        ExitCode = [int]$Configuration.ExitCode
        InputIdleResult = [bool]$Configuration.InputIdleResult
        CloseResult = [bool]$Configuration.CloseResult
        CleanupExitResult = [bool]($Configuration.ContainsKey('CleanupExitResult') -eq $false -or $Configuration['CleanupExitResult'])
        InputIdleCalls = [System.Collections.Generic.List[int]]::new()
        WaitForExitCalls = [System.Collections.Generic.List[int]]::new()
        CloseCalls = 0
        RefreshCalls = 0
        KillCalls = 0
        DisposeCalls = 0
    }

    $process | Add-Member -MemberType ScriptMethod -Name WaitForInputIdle -Value {
        param([int] $Milliseconds)
        $this.InputIdleCalls.Add($Milliseconds)
        return $this.InputIdleResult
    }
    $process | Add-Member -MemberType ScriptMethod -Name Refresh -Value {
        $this.RefreshCalls++
    }
    $process | Add-Member -MemberType ScriptMethod -Name CloseMainWindow -Value {
        $this.CloseCalls++
        if ($this.CloseResult) {
            $this.HasExited = $true
            $this.ExitCode = 0
        }
        return $this.CloseResult
    }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int] $Milliseconds)
        $this.WaitForExitCalls.Add($Milliseconds)
        return [bool]($this.HasExited -or ($this.KillCalls -gt 0 -and $this.CleanupExitResult))
    }
    $process | Add-Member -MemberType ScriptMethod -Name Kill -Value {
        $this.KillCalls++
        if ($this.CleanupExitResult) { $this.HasExited = $true }
    }
    $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value {
        $this.DisposeCalls++
    }
    return $process
}

# These ordinary scoped functions are visible to the verifier while it runs.
# They prevent the fixture from creating a process or sleeping on the host.
function Start-Process {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [switch] $PassThru,
        [Parameter(Mandatory)] [hashtable] $Environment
    )

    $global:WtwMsiStartupFixtureState.StartCalls++
    $global:WtwMsiStartupFixtureState.StartArguments = [pscustomobject]@{
        FilePath = $FilePath
        PassThru = $PassThru.IsPresent
        Environment = $Environment
    }
    return $global:WtwMsiStartupFixtureState.Fixture.Process
}

function Start-Sleep {
    param([Parameter(Mandatory)] [int] $Seconds)

    $global:WtwMsiStartupFixtureState.SleepCalls++
    if ($global:WtwMsiStartupFixtureState.Fixture -and $global:WtwMsiStartupFixtureState.Fixture.ExitDuringSmoke) {
        $global:WtwMsiStartupFixtureState.Fixture.Process.HasExited = $true
    }
}

function Assert-True {
    param([Parameter(Mandatory)] [bool] $Condition, [Parameter(Mandatory)] [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Get-ConfigFlag {
    param([Parameter(Mandatory)] [hashtable] $Configuration, [Parameter(Mandatory)] [string] $Name)
    return $Configuration.ContainsKey($Name) -and [bool]$Configuration[$Name]
}

function Invoke-StartupCase {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [hashtable] $Configuration,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $RunnerEnvironment,
        [switch] $AllowNoMainWindow,
        [string] $ExpectedError = ''
    )

    $oldActions = $env:GITHUB_ACTIONS
    $oldRunner = $env:RUNNER_ENVIRONMENT
    $global:WtwMsiStartupFixtureState.StartCalls = 0
    $global:WtwMsiStartupFixtureState.SleepCalls = 0
    $global:WtwMsiStartupFixtureState.StartArguments = $null
    $global:WtwMsiStartupFixtureState.Fixture = [pscustomobject]@{
        Process = New-FakeProcess -Configuration $Configuration
        ExitDuringSmoke = [bool]($Configuration.ContainsKey('ExitDuringSmoke') -and $Configuration['ExitDuringSmoke'])
    }

    if ($Configuration.GitHubActions) { $env:GITHUB_ACTIONS = 'true' } else { Remove-Item Env:GITHUB_ACTIONS -ErrorAction SilentlyContinue }
    if ($RunnerEnvironment) { $env:RUNNER_ENVIRONMENT = $RunnerEnvironment } else { Remove-Item Env:RUNNER_ENVIRONMENT -ErrorAction SilentlyContinue }

    $statePath = Join-Path ([IO.Path]::GetTempPath()) "wtw-msi-startup-fixture-$([guid]::NewGuid().ToString('N'))"
    Assert-True ([IO.Path]::IsPathFullyQualified($statePath)) 'The fixture state path must be fully qualified.'
    Assert-True (-not (Test-Path -LiteralPath $statePath)) 'The fixture state path must not already exist.'

    $caught = $null
    try {
        if ($AllowNoMainWindow) {
            $null = & $script:Verifier -ExecutablePath $script:ExecutablePath -StatePath $statePath -AllowNoMainWindow
        } else {
            $null = & $script:Verifier -ExecutablePath $script:ExecutablePath -StatePath $statePath
        }
        if ($ExpectedError) { throw "The verifier unexpectedly accepted $Name." }
    } catch {
        $caught = $_
        if (-not $ExpectedError) { throw }
        if ($_.Exception.Message -notlike "*$ExpectedError*") { throw }
    } finally {
        if ($null -eq $oldActions) { Remove-Item Env:GITHUB_ACTIONS -ErrorAction SilentlyContinue } else { $env:GITHUB_ACTIONS = $oldActions }
        if ($null -eq $oldRunner) { Remove-Item Env:RUNNER_ENVIRONMENT -ErrorAction SilentlyContinue } else { $env:RUNNER_ENVIRONMENT = $oldRunner }
    }

    if ($ExpectedError) {
        Assert-True ($null -ne $caught) "$Name did not produce the expected failure."
    }
    if (Get-ConfigFlag $Configuration 'ExpectSpawn') {
        Assert-True ($global:WtwMsiStartupFixtureState.StartCalls -eq 1) "$Name did not spawn exactly one stub process."
        Assert-True ($global:WtwMsiStartupFixtureState.StartArguments.FilePath -ceq $script:ExecutablePath) "$Name passed the wrong executable path."
        Assert-True ($global:WtwMsiStartupFixtureState.StartArguments.PassThru) "$Name did not request the process object."
        Assert-True ($global:WtwMsiStartupFixtureState.StartArguments.Environment.WINDOWTHUMBWALL_STATE_PATH -ceq $statePath) "$Name did not pass the isolated state path to the child."
        Assert-True ($global:WtwMsiStartupFixtureState.Fixture.Process.DisposeCalls -eq 1) "$Name did not dispose its process object exactly once."
    } else {
        Assert-True ($global:WtwMsiStartupFixtureState.StartCalls -eq 0) "$Name spawned a process before rejecting the invocation."
    }
    if (Get-ConfigFlag $Configuration 'ExpectInputIdle') {
        Assert-True ($global:WtwMsiStartupFixtureState.Fixture.Process.InputIdleCalls.Count -eq 1 -and $global:WtwMsiStartupFixtureState.Fixture.Process.InputIdleCalls[0] -eq 15000) "$Name did not require the expected input-idle wait."
    }
    if (Get-ConfigFlag $Configuration 'ExpectSleep') {
        Assert-True ($global:WtwMsiStartupFixtureState.SleepCalls -eq 1) "$Name did not complete the startup observation interval."
    }
    if (Get-ConfigFlag $Configuration 'ExpectClose') {
        Assert-True ($global:WtwMsiStartupFixtureState.Fixture.Process.CloseCalls -eq 1) "$Name did not request a clean close for its observable window."
    }
    if (Get-ConfigFlag $Configuration 'ExpectKill') {
        Assert-True ($global:WtwMsiStartupFixtureState.Fixture.Process.KillCalls -eq 1) "$Name did not clean up its owned process."
        Assert-True ($global:WtwMsiStartupFixtureState.Fixture.Process.WaitForExitCalls.Contains(10000)) "$Name did not verify process cleanup."
    } else {
        Assert-True ($global:WtwMsiStartupFixtureState.Fixture.Process.KillCalls -eq 0) "$Name force-killed a process that should have exited cleanly or was never spawned."
    }
    if (Get-ConfigFlag $Configuration 'ExpectCleanExit') {
        Assert-True ($global:WtwMsiStartupFixtureState.Fixture.Process.HasExited -and $global:WtwMsiStartupFixtureState.Fixture.Process.ExitCode -eq 0) "$Name did not observe a clean process exit."
        Assert-True ($global:WtwMsiStartupFixtureState.Fixture.Process.WaitForExitCalls.Contains(10000)) "$Name did not wait for clean exit."
    }

    Write-Output "PASS $Name"
}

Invoke-StartupCase -Name 'observable window closes cleanly' -RunnerEnvironment '' -Configuration @{
    GitHubActions = $false; MainWindowHandle = 123; StartExited = $false; ExitCode = 0
    InputIdleResult = $true; CloseResult = $true; ExpectSpawn = $true; ExpectInputIdle = $true
    ExpectSleep = $true; ExpectClose = $true; ExpectKill = $false; ExpectCleanExit = $true
}

Invoke-StartupCase -Name 'hosted opt-in does not mask failed close of an observable window' -RunnerEnvironment 'github-hosted' -AllowNoMainWindow -ExpectedError 'did not accept a clean close request' -Configuration @{
    GitHubActions = $true; MainWindowHandle = 124; StartExited = $false; ExitCode = 0
    InputIdleResult = $true; CloseResult = $false; ExpectSpawn = $true; ExpectInputIdle = $true
    ExpectSleep = $true; ExpectClose = $true; ExpectKill = $true
}

Invoke-StartupCase -Name 'hosted runner may clean up without an observable window' -RunnerEnvironment 'github-hosted' -AllowNoMainWindow -Configuration @{
    GitHubActions = $true; MainWindowHandle = 0; StartExited = $false; ExitCode = 0
    InputIdleResult = $true; CloseResult = $false; ExpectSpawn = $true; ExpectInputIdle = $true
    ExpectSleep = $true; ExpectClose = $false; ExpectKill = $true
}

Invoke-StartupCase -Name 'non-hosted opt-in is rejected before process launch' -RunnerEnvironment 'self-hosted' -AllowNoMainWindow -ExpectedError 'only on a GitHub-hosted Actions runner' -Configuration @{
    GitHubActions = $true; MainWindowHandle = 0; StartExited = $false; ExitCode = 0
    InputIdleResult = $true; CloseResult = $false; ExpectSpawn = $false
}

Invoke-StartupCase -Name 'hosted no-window mode still rejects early exit' -RunnerEnvironment 'github-hosted' -AllowNoMainWindow -ExpectedError 'exited during smoke test with code 23' -Configuration @{
    GitHubActions = $true; MainWindowHandle = 0; StartExited = $false; ExitCode = 23
    ExitDuringSmoke = $true; InputIdleResult = $true; CloseResult = $false; ExpectSpawn = $true
    ExpectInputIdle = $true; ExpectSleep = $true; ExpectKill = $false
}

Invoke-StartupCase -Name 'hosted no-window mode still rejects input-idle timeout' -RunnerEnvironment 'github-hosted' -AllowNoMainWindow -ExpectedError 'did not reach input idle' -Configuration @{
    GitHubActions = $true; MainWindowHandle = 0; StartExited = $false; ExitCode = 0
    InputIdleResult = $false; CloseResult = $false; ExpectSpawn = $true; ExpectInputIdle = $true
    ExpectSleep = $false; ExpectKill = $true
}

Invoke-StartupCase -Name 'no-window mode without hosted opt-in remains a failure' -RunnerEnvironment '' -ExpectedError 'main window is unavailable for clean-close verification' -Configuration @{
    GitHubActions = $false; MainWindowHandle = 0; StartExited = $false; ExitCode = 0
    InputIdleResult = $true; CloseResult = $false; ExpectSpawn = $true; ExpectInputIdle = $true
    ExpectSleep = $true; ExpectClose = $false; ExpectKill = $true
}

Invoke-StartupCase -Name 'hosted no-window cleanup timeout remains a failure' -RunnerEnvironment 'github-hosted' -AllowNoMainWindow -ExpectedError 'survived smoke-test cleanup' -Configuration @{
    GitHubActions = $true; MainWindowHandle = 0; StartExited = $false; ExitCode = 0
    InputIdleResult = $true; CloseResult = $false; CleanupExitResult = $false; ExpectSpawn = $true
    ExpectInputIdle = $true; ExpectSleep = $true; ExpectClose = $false; ExpectKill = $true
}
