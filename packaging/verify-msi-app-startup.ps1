[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ExecutablePath,
    [Parameter(Mandatory)] [string] $StatePath,
    [switch] $AllowNoMainWindow
)

$ErrorActionPreference = 'Stop'
if ($AllowNoMainWindow -and
    ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted')) {
    throw 'A missing main window is permitted only on a GitHub-hosted Actions runner.'
}
if (-not [IO.Path]::IsPathFullyQualified($StatePath) -or (Test-Path -LiteralPath $StatePath)) {
    throw 'The smoke test needs an absolute path to fresh isolated application state.'
}

$app = Start-Process -FilePath $ExecutablePath -PassThru -Environment @{
    WINDOWTHUMBWALL_STATE_PATH = $StatePath
}
$verificationError = $null
$requiresHeadlessCleanup = $false
try {
    # Retain the launched process handle so cleanup cannot address a recycled PID.
    $null = $app.Handle
    if (-not $app.WaitForInputIdle(15000)) {
        throw 'The installed application message loop did not reach input idle.'
    }
    Start-Sleep -Seconds 3
    $app.Refresh()
    $window = $app.MainWindowHandle
    if ($app.HasExited) {
        throw "Installed application exited during smoke test with code $($app.ExitCode)."
    }

    Write-Host "MSI startup: PID $($app.Id) reached input idle. Main window handle: $window."
    if ($window -eq [IntPtr]::Zero) {
        if (-not $AllowNoMainWindow) {
            throw 'The installed application main window is unavailable for clean-close verification.'
        }
        $requiresHeadlessCleanup = $true
        Write-Host 'No main window is observable on this GitHub-hosted runner. Process cleanup follows.'
    } else {
        if (-not $app.CloseMainWindow()) {
            throw 'The observable installed application window did not accept a clean close request.'
        }
        if (-not $app.WaitForExit(10000)) {
            throw 'Installed application did not exit after a clean close request.'
        }
        if ($app.ExitCode -ne 0) {
            throw "Installed application clean close returned exit code $($app.ExitCode)."
        }
        Write-Host 'The installed application main window closed and the process exited successfully.'
    }
} catch {
    $verificationError = $_
    throw
} finally {
    try {
        $alreadyExited = $app.HasExited
        if ($requiresHeadlessCleanup -and $alreadyExited) {
            throw "Installed application exited before smoke-test cleanup with code $($app.ExitCode)."
        }
        if (-not $alreadyExited) {
            $app.Kill()
            if (-not $app.WaitForExit(10000)) {
                throw 'The installed application process survived smoke-test cleanup.'
            }
            Write-Host 'Smoke-test process cleanup verified.'
        }
    } catch {
        if ($null -eq $verificationError) { throw }
        Write-Warning "Smoke-test process cleanup also failed: $($_.Exception.Message)"
    } finally {
        $app.Dispose()
    }
}
