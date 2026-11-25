[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:Fixture = $null
$global:MainSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$global:HeadSha = $global:MainSha
$global:FinalHead = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
$global:CandidateSha = $global:FinalHead
$global:TreeSha = 'dddddddddddddddddddddddddddddddddddddddd'
$global:MainRunId = '12345'
$global:CandidateRunId = '67890'
$global:BranchRunId = '54321'
$global:CandidateBranch = 'codex/example'
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
    if ($argsText -contains 'merge-base' -and $argsText -contains '--is-ancestor') { return '' }
    if ($argsText -contains '--get' -and $argsText -contains 'remote.origin.url') { return 'https://github.com/example/window-thumb-wall.git' }
    $global:LASTEXITCODE = 1
    return ''
}

function global:gh {
    $argsText = @($args | ForEach-Object { [string]$_ })
    $endpoint = $argsText | Where-Object { $_ -like 'repos/*' } | Select-Object -First 1
    $global:LASTEXITCODE = 0
    $data = $global:Fixture
    if ($endpoint -like '*/branches/main') { return [string](ConvertTo-Json -InputObject $data.Main -Depth 20 -Compress) }
    if ($endpoint -like '*/git/ref/heads/*') {
        if ($endpoint -ne "repos/example/window-thumb-wall/git/ref/heads/$($data.CandidateBranch)") {
            throw 'Unexpected candidate branch ref endpoint.'
        }
        return ([pscustomobject]@{ object = [pscustomobject]@{ sha = $data.BranchSha } } | ConvertTo-Json -Depth 20 -Compress)
    }
    if ($endpoint -like '*/actions/runs/*/jobs*') {
        $runId = [regex]::Match($endpoint, 'repos/[^/]+/[^/]+/actions/runs/(\d+)/').Groups[1].Value
        $run = if ($runId -eq $data.MainRunId) { $data.MainRun } elseif ($runId -eq $data.CandidateRunId) { $data.CandidateRun } elseif ($runId -eq $data.BranchRunId) { $data.BranchRun } else { $null }
        $jobs = if ($runId -eq $data.MainRunId) { $data.Jobs } elseif ($runId -eq $data.CandidateRunId) { $data.CandidateJobs } elseif ($runId -eq $data.BranchRunId) { $data.BranchJobs } else { $null }
        if ($null -eq $run -or $endpoint -notlike "*/attempts/$($run.run_attempt)/jobs?*" -or
            $argsText -notcontains '.jobs[] | @json') {
            throw 'Expected the jobs collection for the selected run attempt.'
        }
        foreach ($item in $jobs) { Write-Output ($item | ConvertTo-Json -Depth 20 -Compress) }
        return
    }
    if ($endpoint -like '*/actions/runs/*') {
        $runId = [regex]::Match($endpoint, 'repos/[^/]+/[^/]+/actions/runs/(\d+)').Groups[1].Value
        if ($runId -eq $data.MainRunId) { return ($data.MainRun | ConvertTo-Json -Depth 20 -Compress) }
        if ($runId -eq $data.CandidateRunId) { return ($data.CandidateRun | ConvertTo-Json -Depth 20 -Compress) }
        if ($runId -eq $data.BranchRunId) { return ($data.BranchRun | ConvertTo-Json -Depth 20 -Compress) }
        throw "Unexpected workflow run ID '$runId'."
    }
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
        (New-Step 'Generate release metadata'), (New-Step 'Verify release-shaped candidate artifacts'),
        (New-Step 'Upload unsigned development artifacts')
    )
    $candidateSteps = @((New-Step 'Verify release-shaped candidate artifacts'))
    $buildCompleted = '2026-11-20T09:20:00Z'
    $storeCompleted = '2026-11-20T09:30:00Z'
    $fixture = [pscustomobject]@{
        MainRunId = $global:MainRunId; CandidateRunId = $global:CandidateRunId; BranchRunId = $global:BranchRunId
        CandidateBranch = $global:CandidateBranch; BranchSha = $global:CandidateSha
        Main = @{ protected = $true; commit = @{ sha = $global:MainSha; commit = @{ tree = @{ sha = $global:TreeSha } } } }
        ReviewedCommit = @{ tree = @{ sha = $global:TreeSha } }
        MainRun = [pscustomobject]@{
            path = '.github/workflows/build-release-artifacts.yml'; event = 'workflow_dispatch'
            head_branch = 'main'; status = 'completed'; conclusion = 'success'; head_sha = $global:HeadSha; run_attempt = 2
        }
        CandidateRun = [pscustomobject]@{
            path = '.github/workflows/build-release-artifacts.yml'; event = 'workflow_dispatch'
            head_branch = $global:CandidateBranch; status = 'completed'; conclusion = 'success'; head_sha = $global:CandidateSha; run_attempt = 1
        }
        BranchRun = [pscustomobject]@{
            path = '.github/workflows/build-release-artifacts.yml'; event = 'workflow_dispatch'
            head_branch = $global:CandidateBranch; status = 'completed'; conclusion = 'success'; head_sha = $global:CandidateSha; run_attempt = 1
        }
        Jobs = @(
            [pscustomobject]@{ name = 'build'; status = 'completed'; conclusion = 'success'; completed_at = $buildCompleted; steps = $buildSteps },
            [pscustomobject]@{ id = 101; name = 'store-package-check / publish'; status = 'completed'; conclusion = 'success'; completed_at = $storeCompleted; steps = $storeSteps },
            [pscustomobject]@{ name = 'store'; status = 'completed'; conclusion = 'skipped'; steps = @() },
            [pscustomobject]@{ name = 'release'; status = 'completed'; conclusion = 'skipped'; steps = @() }
        )
        PullRequest = [pscustomobject]@{
            state = 'closed'; merged = $true; draft = $false
            merge_commit_sha = $global:MainSha; head = [pscustomobject]@{ sha = $global:FinalHead; ref = $global:CandidateBranch; repo = [pscustomobject]@{ full_name = 'example/window-thumb-wall' } }; base = [pscustomobject]@{ ref = 'main'; repo = [pscustomobject]@{ full_name = 'example/window-thumb-wall' } }
            merged_at = '2026-11-20T10:00:00Z'
        }
        CandidateJobs = $null
        BranchJobs = $null
        Timeline = @([pscustomobject]@{
            event = 'review_requested'; requested_reviewer = [pscustomobject]@{ login = 'Copilot' }
            created_at = '2026-11-20T08:00:00Z'
        })
        Reviews = @([pscustomobject]@{
            user = [pscustomobject]@{ login = 'copilot-pull-request-reviewer[bot]' }
            id = 12; state = 'COMMENTED'; submitted_at = '2026-11-20T09:00:00Z'; commit_id = $global:FinalHead
        })
    }
    $fixture = [pscustomobject]$fixture
    $jobsJson = ConvertTo-Json -InputObject $fixture.Jobs -Depth 50 -Compress
    $fixture.CandidateJobs = @(ConvertFrom-Json -InputObject $jobsJson -Depth 50)
    $fixture.BranchJobs = @(ConvertFrom-Json -InputObject $jobsJson -Depth 50)
    return $fixture
}

function Invoke-Fixture([string]$Name, [string]$ExpectedFailure = '', [switch]$CandidateMode, [switch]$BeforePullRequest) {
    try {
        if ($BeforePullRequest) {
            $null = & $script:Verifier -ReleaseTag $script:ReleaseTag -RunId $global:BranchRunId -CandidateBranch $global:CandidateBranch
        }
        elseif ($CandidateMode) {
            $null = & $script:Verifier -ReleaseTag $script:ReleaseTag -RunId $global:BranchRunId -CandidateBranch $global:CandidateBranch -PullRequest '77'
        }
        else {
            $null = & $script:Verifier -ReleaseTag $script:ReleaseTag -RunId $global:MainRunId -CandidateRunId $global:CandidateRunId -PullRequest '77'
        }
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

$global:HeadSha = $global:CandidateSha
$global:Fixture = New-Fixture
$global:Fixture.PullRequest = $null
Invoke-Fixture -Name 'candidate proof before opening a pull request' -BeforePullRequest
$global:Fixture.BranchSha = $global:MainSha
Invoke-Fixture -Name 'candidate branch advanced after the run' -ExpectedFailure 'Local HEAD must equal the remote candidate branch' -BeforePullRequest
$global:HeadSha = $global:MainSha

$global:Fixture = New-Fixture
$global:HeadSha = $global:CandidateSha
$global:Fixture.PullRequest.state = 'open'
$global:Fixture.PullRequest.merged = $false
$global:Fixture.PullRequest.merged_at = $null
$global:Fixture.PullRequest.head.sha = $global:CandidateSha
$global:Fixture.Reviews[0].commit_id = $global:CandidateSha
Invoke-Fixture -Name 'open pull request and candidate artifacts' -CandidateMode
$global:HeadSha = $global:MainSha

$global:Fixture = New-Fixture
$global:Fixture.CandidateRun.head_sha = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
Invoke-Fixture -Name 'wrong pre-merge candidate run SHA' -ExpectedFailure 'head SHA must equal the expected release source'

$global:Fixture = New-Fixture
$global:CandidateRunId = $global:MainRunId
Invoke-Fixture -Name 'main and candidate run IDs reused' -ExpectedFailure 'two distinct runs'
$global:CandidateRunId = '67890'

$global:Fixture = New-Fixture
($global:Fixture.CandidateJobs[0].steps | Where-Object name -eq 'Verify release-shaped candidate artifacts').conclusion = 'skipped'
Invoke-Fixture -Name 'candidate release artifacts skipped' -ExpectedFailure "step 'Verify release-shaped candidate artifacts' must conclude 'success'"

$global:Fixture = New-Fixture
($global:Fixture.CandidateJobs[0]).completed_at = '2026-11-20T10:01:00Z'
Invoke-Fixture -Name 'candidate completed after merge' -ExpectedFailure 'completed before merge'

$global:Fixture = New-Fixture
$global:Fixture.MainRun.head_sha = 'cccccccccccccccccccccccccccccccccccccccc'
Invoke-Fixture -Name 'wrong workflow run head' -ExpectedFailure 'head SHA must equal the expected release source'

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
Invoke-Fixture -Name 'Store CLI ran in the package-only job' -ExpectedFailure "step 'Setup Store CLI' must conclude 'skipped'"

Write-Output 'PASS all release preflight fixtures'
