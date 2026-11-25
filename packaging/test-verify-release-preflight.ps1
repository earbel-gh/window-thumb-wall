[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:Fixture = $null
$global:MainSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$global:HeadSha = $global:MainSha
$global:FinalHead = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
$global:TreeSha = 'dddddddddddddddddddddddddddddddddddddddd'
$global:RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
$script:Verifier = Join-Path $PSScriptRoot 'verify-release-preflight.ps1'
[xml]$project = Get-Content -LiteralPath (Join-Path $global:RepositoryRoot 'WindowThumbWall.csproj') -Raw
$script:ReleaseTag = "v$($project.Project.PropertyGroup.Version)"

function global:git {
    $argsText = @($args | ForEach-Object { [string]$_ })
    $global:LASTEXITCODE = 0
    if ($argsText -contains '--show-toplevel') { return $global:RepositoryRoot }
    if ($argsText -contains '--porcelain') { return '' }
    if ($argsText -contains '--verify' -and $argsText -contains 'HEAD') { return $global:HeadSha }
    if ($argsText -contains '--verify' -and $argsText -contains 'HEAD^{tree}') { return $global:TreeSha }
    if ($argsText -contains '--get' -and $argsText -contains 'remote.origin.url') { return 'https://github.com/example/window-thumb-wall.git' }
    $global:LASTEXITCODE = 1
    return ''
}

function global:gh {
    $argsText = @($args | ForEach-Object { [string]$_ })
    $endpoint = $argsText | Where-Object { $_ -like 'repos/*' } | Select-Object -First 1
    $global:LASTEXITCODE = 0
    $data = $global:Fixture
    if ($endpoint -like '*/branches/main') { return ($data.Main | ConvertTo-Json -Depth 20 -Compress) }
    if ($endpoint -like '*/actions/runs/*/jobs*') {
        if ($endpoint -notlike "*/attempts/$($data.Run.run_attempt)/jobs?*" -or
            $argsText -notcontains '.jobs[] | @json') {
            throw 'Expected the jobs collection for the selected run attempt.'
        }
        foreach ($item in $data.Jobs) { Write-Output ($item | ConvertTo-Json -Depth 20 -Compress) }
        return
    }
    if ($endpoint -like '*/actions/runs/*') { return ($data.Run | ConvertTo-Json -Depth 20 -Compress) }
    if ($endpoint -like '*/pulls/*/reviews*') { foreach ($item in $data.Reviews) { Write-Output ($item | ConvertTo-Json -Depth 20 -Compress) }; return }
    if ($endpoint -like '*/issues/*/timeline*') { foreach ($item in $data.Timeline) { Write-Output ($item | ConvertTo-Json -Depth 20 -Compress) }; return }
    if ($endpoint -like '*/pulls/*') { return ($data.PullRequest | ConvertTo-Json -Depth 20 -Compress) }
    if ($endpoint -like "*/git/commits/$global:FinalHead") { return ($data.ReviewedCommit | ConvertTo-Json -Depth 20 -Compress) }
    $global:LASTEXITCODE = 1
    return ''
}

function New-Step([string]$Name, [string]$Conclusion = 'success') {
    [pscustomobject]@{ name = $Name; status = 'completed'; conclusion = $Conclusion }
}
function New-Fixture {
    $storeSteps = @(
        (New-Step 'Verify frozen checkout'), (New-Step 'Derive Store package version'),
        (New-Step 'Locate MSBuild'), (New-Step 'Restore'), (New-Step 'Patch Package.appxmanifest version'),
        (New-Step 'Build Store Package'),
        (New-Step 'Verify Store upload package and record its SHA256'),
        (New-Step 'Check Store product configuration' 'skipped'), (New-Step 'Setup Store CLI' 'skipped'),
        (New-Step 'Configure Store CLI and check for an existing submission' 'skipped'),
        (New-Step 'MSStore Create Draft Submission' 'skipped'), (New-Step 'MSStore Commit Submission' 'skipped')
    )
    $buildSteps = @(
        (New-Step 'Build application and test project'), (New-Step 'Test Store draft protection'),
        (New-Step 'Test tag verification, draft recovery and release preflight'), (New-Step 'Build architectures'),
        (New-Step 'Verify x64 MSI lifecycle'), (New-Step 'Verify v0.6.10 MSI major upgrade preserves user state'),
        (New-Step 'Upload unsigned development artifacts')
    )
    [pscustomobject]@{
        Main = @{ protected = $true; commit = @{ sha = $global:MainSha; commit = @{ tree = @{ sha = $global:TreeSha } } } }
        ReviewedCommit = @{ tree = @{ sha = $global:TreeSha } }
        Run = [pscustomobject]@{
            path = '.github/workflows/build-release-artifacts.yml'; event = 'workflow_dispatch'
            head_branch = 'main'; status = 'completed'; conclusion = 'success'; head_sha = $global:HeadSha; run_attempt = 2
        }
        Jobs = @(
            [pscustomobject]@{ name = 'build'; status = 'completed'; conclusion = 'success'; steps = $buildSteps },
            [pscustomobject]@{ id = 101; name = 'store-package-check / publish'; status = 'completed'; conclusion = 'success'; steps = $storeSteps },
            [pscustomobject]@{ name = 'store'; status = 'completed'; conclusion = 'skipped'; steps = @() },
            [pscustomobject]@{ name = 'release'; status = 'completed'; conclusion = 'skipped'; steps = @() }
        )
        PullRequest = [pscustomobject]@{
            state = 'closed'; merged = $true; base = [pscustomobject]@{ ref = 'main' }
            merge_commit_sha = $global:MainSha; head = [pscustomobject]@{ sha = $global:FinalHead }
            merged_at = '2026-11-20T10:00:00Z'
        }
        Timeline = @([pscustomobject]@{
            event = 'review_requested'; requested_reviewer = [pscustomobject]@{ login = 'Copilot' }
            created_at = '2026-11-20T08:00:00Z'
        })
        Reviews = @([pscustomobject]@{
            user = [pscustomobject]@{ login = 'copilot-pull-request-reviewer[bot]' }
            id = 12; state = 'COMMENTED'; submitted_at = '2026-11-20T09:00:00Z'; commit_id = $global:FinalHead
        })
    }
}

function Invoke-Fixture([string]$Name, [string]$ExpectedFailure = '') {
    try {
        $null = & $script:Verifier -ReleaseTag $script:ReleaseTag -RunId '12345' -PullRequest '77'
    }
    catch {
        if ([string]::IsNullOrWhiteSpace($ExpectedFailure)) { throw "Fixture '$Name' unexpectedly failed: $($_.Exception.Message)" }
        if ($_.Exception.Message -notlike "*$ExpectedFailure*") { throw "Fixture '$Name' failed at an unexpected gate: $($_.Exception.Message)" }
        Write-Output "PASS reject $Name"
        return
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedFailure)) { throw "Fixture '$Name' unexpectedly passed." }
    Write-Output "PASS accept $Name"
}

$global:Fixture = New-Fixture
Invoke-Fixture -Name 'complete preflight'

$global:Fixture = New-Fixture
$global:Fixture.Run.head_sha = 'cccccccccccccccccccccccccccccccccccccccc'
Invoke-Fixture -Name 'wrong workflow run head' -ExpectedFailure 'run head SHA'

$global:Fixture = New-Fixture
$global:HeadSha = 'cccccccccccccccccccccccccccccccccccccccc'
Invoke-Fixture -Name 'checkout differs from protected main' -ExpectedFailure 'Local HEAD must equal'
$global:HeadSha = $global:MainSha

$global:Fixture = New-Fixture
($global:Fixture.Jobs | Where-Object name -eq 'store-package-check / publish').conclusion = 'skipped'
Invoke-Fixture -Name 'Store package job skipped' -ExpectedFailure "must conclude 'success'"

$global:Fixture = New-Fixture
$build = $global:Fixture.Jobs | Where-Object name -eq 'build'
($build.steps | Where-Object name -eq 'Verify v0.6.10 MSI major upgrade preserves user state').conclusion = 'skipped'
Invoke-Fixture -Name 'MSI major upgrade skipped' -ExpectedFailure 'major upgrade preserves user state'

$global:Fixture = New-Fixture
($global:Fixture.Jobs | Where-Object name -eq 'release').conclusion = 'success'
Invoke-Fixture -Name 'release write job executed' -ExpectedFailure "must conclude 'skipped'"

$global:Fixture = New-Fixture
$global:Fixture.Reviews[0].submitted_at = '2026-11-20T10:01:00Z'
Invoke-Fixture -Name 'review requested but result submitted after merge' -ExpectedFailure 'must arrive before merge'

$global:Fixture = New-Fixture
$global:Fixture.Timeline += [pscustomobject]@{
    event = 'review_requested'; requested_reviewer = [pscustomobject]@{ login = 'Copilot' }
    created_at = '2026-11-20T09:30:00Z'
}
Invoke-Fixture -Name 'last review request was still pending at merge' -ExpectedFailure 'must arrive before merge'
$global:Fixture.Reviews[0].submitted_at = $global:Fixture.Timeline[-1].created_at
Invoke-Fixture -Name 'request and review timestamps are equal' -ExpectedFailure 'must arrive before merge'
$global:Fixture.Reviews[0].submitted_at = '2026-11-20T09:40:00Z'
Invoke-Fixture -Name 'last review request was answered before merge'

$global:Fixture = New-Fixture
$global:Fixture.Reviews[0].state = 'CHANGES_REQUESTED'
Invoke-Fixture -Name 'latest review requested changes' -ExpectedFailure 'must arrive before merge'

$global:Fixture = New-Fixture
$global:Fixture.ReviewedCommit.tree.sha = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
Invoke-Fixture -Name 'integrated source differs from reviewed tree' -ExpectedFailure 'must match the reviewed final pull request head tree'

$global:Fixture = New-Fixture
$store = $global:Fixture.Jobs | Where-Object name -eq 'store-package-check / publish'
($store.steps | Where-Object name -eq 'Setup Store CLI').conclusion = 'success'
Invoke-Fixture -Name 'Store CLI ran in the package-only job' -ExpectedFailure "must be present and skipped"

Write-Output 'PASS all release preflight fixtures'
