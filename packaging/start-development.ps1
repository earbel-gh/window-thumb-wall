[CmdletBinding()]
param(
    [string]$ExecutablePath = '',
    [string]$StateSeedPath = '',
    [string]$DisplayDevice = '\\.\DISPLAY3',
    [switch]$Preflight
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$nativeSource = Join-Path $PSScriptRoot 'DevelopmentLauncher.Native.cs'
if (!(Test-Path -LiteralPath $nativeSource -PathType Leaf)) {
    throw "The native launcher helper is missing: $nativeSource"
}
$nativeTypeName = 'WindowThumbWall.DevelopmentLauncher.NativeLauncher'
if ($null -eq ($nativeTypeName -as [type])) {
    Add-Type -Path $nativeSource
}

$display = [WindowThumbWall.DevelopmentLauncher.NativeLauncher]::ResolveDisplay($DisplayDevice)
[WindowThumbWall.DevelopmentLauncher.NativeLauncher]::RequireTargetAvailable($display)

if ($Preflight) {
    [pscustomobject]@{
        Status = 'PRECHECK_OK'
        DeviceName = $display.DeviceName
        MonitorHandle = ('0x{0:X}' -f $display.MonitorHandle)
        WorkArea = [pscustomobject]@{ Left = $display.WorkLeft; Top = $display.WorkTop; Right = $display.WorkRight; Bottom = $display.WorkBottom }
        SystemDpi = $display.SystemDpi
        TargetDpi = if ($display.TargetDpi -gt 0) { $display.TargetDpi } else { $null }
        ForegroundTargetInUse = $false
    }
    return
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $projectRoot = Split-Path $PSScriptRoot -Parent
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'WindowThumbWall.csproj')
    $framework = [string]$project.Project.PropertyGroup.TargetFramework
    if ([string]::IsNullOrWhiteSpace($framework)) { throw 'Could not resolve the application target framework.' }
    $ExecutablePath = Join-Path $projectRoot "bin\Release\$framework\WindowThumbWall.exe"
}
$executable = Get-Item -LiteralPath $ExecutablePath
if ($executable.PSIsContainer -or $executable.Name -ne 'WindowThumbWall.exe') {
    throw 'The development launch requires an exact built WindowThumbWall.exe path.'
}
$executablePath = [IO.Path]::GetFullPath($executable.FullName)

$resolvedSeedPath = $null
if (![string]::IsNullOrWhiteSpace($StateSeedPath)) {
    $seed = Get-Item -LiteralPath $StateSeedPath
    if ($seed.PSIsContainer) {
        throw 'StateSeedPath must identify a state JSON file.'
    }
    $resolvedSeedPath = [IO.Path]::GetFullPath($seed.FullName)
}

$profilePath = Join-Path ([IO.Path]::GetTempPath()) ('WindowThumbWall-development\' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($profilePath) | Out-Null
$statePath = Join-Path $profilePath 'state.json'
if ($null -ne $resolvedSeedPath) {
    $state = Get-Content -LiteralPath $resolvedSeedPath -Raw | ConvertFrom-Json -AsHashtable
    if ($state -isnot [System.Collections.IDictionary]) {
        throw 'The state seed must contain a JSON object.'
    }
}
else {
    $state = [ordered]@{}
}

$scale = 96.0 / [double]$display.SystemDpi
$geometry = [ordered]@{
    Left = ([double]($display.WorkLeft + 48)) * $scale
    Top = ([double]($display.WorkTop + 64)) * $scale
    Width = ([double]($display.WorkWidth - 112) * 0.68) * $scale
    Height = ([double]($display.WorkHeight - 160) * 0.72) * $scale
    IsMaximized = $false
}
if ($geometry.Width -le 0 -or $geometry.Height -le 0) {
    throw "The target work area is too small for the seeded startup geometry."
}
$state['Geometry'] = $geometry
$state['IsFullScreen'] = $false
$stateJson = ConvertTo-Json -InputObject $state -Depth 100
[IO.File]::WriteAllText($statePath, $stateJson, [Text.UTF8Encoding]::new($false))

# Re-read the exact GDI target and foreground ownership immediately before launch.
[WindowThumbWall.DevelopmentLauncher.NativeLauncher]::RequireTargetAvailable($display)
$childEnvironment = @{
    WINDOWTHUMBWALL_STATE_PATH = $statePath
    WINDOWTHUMBWALL_DEVELOPMENT_NO_ACTIVATE = '1'
}
$previousEnvironment = @{}
foreach ($name in $childEnvironment.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    foreach ($name in $childEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $childEnvironment[$name], 'Process')
    }
    $process = [WindowThumbWall.DevelopmentLauncher.NativeLauncher]::Launch($executablePath)
}
finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}

[pscustomobject]@{
    Status = 'STARTED'
    ProcessId = $process.Id
    Process = $process
    ExecutablePath = $executablePath
    ProfilePath = $profilePath
    StatePath = $statePath
    StateSeedPath = $resolvedSeedPath
    DisplayDevice = $display.DeviceName
    WorkArea = [pscustomobject]@{ Left = $display.WorkLeft; Top = $display.WorkTop; Right = $display.WorkRight; Bottom = $display.WorkBottom }
    SystemDpi = $display.SystemDpi
    TargetDpi = if ($display.TargetDpi -gt 0) { $display.TargetDpi } else { $null }
    SeededGeometry = $geometry
}
