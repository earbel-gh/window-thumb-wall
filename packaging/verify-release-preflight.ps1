[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')] [string] $ReleaseTag,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[1-9][0-9]*$')] [string] $RunId,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[1-9][0-9]*$')] [string] $PullRequest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-PreflightGit {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)
    $output = @(& git -C $script:RepositoryRoot @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "Local Git read failed for '$($Arguments[0])'." }
    return (($output | ForEach-Object { [string] $_ }) -join "`n").Trim()
}

function Invoke-GhObject {
    param([Parameter(Mandatory = $true)][string] $Endpoint)
    $output = @(& gh api $Endpoint 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "GitHub read failed for '$Endpoint'." }
    try { return (($output -join "`n") | ConvertFrom-Json -Depth 50 -ErrorAction Stop) }
    catch { throw "GitHub returned invalid JSON for '$Endpoint'." }
}

function Invoke-GhList {
    param(
        [Parameter(Mandatory = $true)][string] $Endpoint,
        [string] $JqFilter = '.[] | @json'
    )
    # gh emits one compact JSON object per line with --jq; no response body is logged.
    $output = @(& gh api --paginate $Endpoint --jq $JqFilter 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "GitHub list read failed for '$Endpoint'." }
    $items = @()
    foreach ($line in $output) {
        if ([string]::IsNullOrWhiteSpace([string]$line)) { continue }
        try { $items += ([string]$line | ConvertFrom-Json -Depth 50 -ErrorAction Stop) }
        catch { throw "GitHub returned invalid list data for '$Endpoint'." }
    }
    return ,$items
}

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

$root = @(& git rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or $root.Count -eq 0) { throw 'Could not resolve the current repository root.' }
$script:RepositoryRoot = ([string]$root[-1]).Trim()
$status = Invoke-PreflightGit -Arguments @('status', '--porcelain')
Assert-Condition ([string]::IsNullOrWhiteSpace($status)) 'The local checkout must be clean.'
$head = (Invoke-PreflightGit -Arguments @('rev-parse', '--verify', 'HEAD')).ToLowerInvariant()
Assert-Condition ($head -match '^[0-9a-f]{40}([0-9a-f]{24})?$') 'Local HEAD did not resolve to a full commit ID.'
$tree = Invoke-PreflightGit -Arguments @('rev-parse', '--verify', 'HEAD^{tree}')

$projectPath = Join-Path $script:RepositoryRoot 'WindowThumbWall.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw 'WindowThumbWall.csproj is missing from the repository root.' }
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]$project.Project.PropertyGroup.Version
Assert-Condition ($ReleaseTag -ceq "v$projectVersion") 'The release tag must match the project Version exactly.'
$packageVersion = "$projectVersion.0"
Assert-Condition (
    [string]$project.Project.PropertyGroup.AssemblyVersion -ceq $packageVersion -and
    [string]$project.Project.PropertyGroup.FileVersion -ceq $packageVersion
) 'AssemblyVersion and FileVersion must match the four-part release version.'
foreach ($manifest in @('packaging/AppxManifest.xml', 'packaging/Package.appxmanifest')) {
    [xml]$package = Get-Content -LiteralPath (Join-Path $script:RepositoryRoot $manifest) -Raw
    $identity = $package.DocumentElement.ChildNodes | Where-Object LocalName -CEQ 'Identity'
    Assert-Condition ($null -ne $identity -and $identity.GetAttribute('Version') -ceq $packageVersion) "Release version mismatch in '$manifest'."
}

$repository = (Invoke-PreflightGit -Arguments @('config', '--get', 'remote.origin.url'))
if ($repository -match '^https://github\.com/([^/]+/[^/.]+?)(?:\.git)?$') { $repository = $Matches[1] }
elseif ($repository -match '^git@github\.com:([^/]+/[^/.]+?)(?:\.git)?$') { $repository = $Matches[1] }
else { throw 'Could not derive the GitHub repository from origin.' }

$main = Invoke-GhObject -Endpoint "repos/$repository/branches/main"
$mainSha = ([string]$main.commit.sha).ToLowerInvariant()
Assert-Condition ($main.protected -eq $true -and $head -ceq $mainSha) 'Local HEAD must equal the current protected GitHub main SHA.'
Assert-Condition ($tree -ceq [string]$main.commit.commit.tree.sha) 'Local source tree must equal the protected GitHub main tree.'

$run = Invoke-GhObject -Endpoint "repos/$repository/actions/runs/$RunId"
Assert-Condition ([string]$run.path -ceq '.github/workflows/build-release-artifacts.yml') 'The selected run is not build-release-artifacts.yml.'
Assert-Condition ([string]$run.event -ceq 'workflow_dispatch') 'The selected run must be a workflow_dispatch run.'
Assert-Condition ([string]$run.head_branch -ceq 'main') 'The selected run must target main.'
Assert-Condition ([string]$run.status -ceq 'completed' -and [string]$run.conclusion -ceq 'success') 'The selected release build run must have completed successfully.'
Assert-Condition (([string]$run.head_sha).ToLowerInvariant() -ceq $head) 'The workflow run head SHA must equal local HEAD.'

$attempt = [string]$run.run_attempt
Assert-Condition ($attempt -match '^[1-9][0-9]*$') 'The selected run must have a valid attempt number.'
$jobs = Invoke-GhList -Endpoint "repos/$repository/actions/runs/$RunId/attempts/$attempt/jobs?per_page=100" -JqFilter '.jobs[] | @json'
function Find-Job([string]$Name) { @($jobs | Where-Object { [string]$_.name -ceq $Name }) | Select-Object -First 1 }
function Assert-Job([string]$Name, [string]$Conclusion) {
    $job = Find-Job $Name
    Assert-Condition ($null -ne $job) "Required run job '$Name' is missing."
    Assert-Condition ([string]$job.status -ceq 'completed' -and [string]$job.conclusion -ceq $Conclusion) "Run job '$Name' must conclude '$Conclusion'."
    return $job
}
$buildJob = Assert-Job -Name 'build' -Conclusion 'success'
foreach ($stepName in @(
    'Build application and test project',
    'Test Store draft protection',
    'Test tag verification, draft recovery and release preflight',
    'Build architectures',
    'Verify x64 MSI lifecycle',
    'Verify v0.6.10 MSI major upgrade preserves user state',
    'Upload unsigned development artifacts'
)) {
    $step = @($buildJob.steps | Where-Object { [string]$_.name -ceq $stepName }) | Select-Object -First 1
    Assert-Condition ($null -ne $step -and [string]$step.conclusion -ceq 'success') "Build job step '$stepName' must have succeeded."
}

$storeJob = Assert-Job -Name 'store-package-check / publish' -Conclusion 'success'
foreach ($stepName in @(
    'Verify frozen checkout',
    'Derive Store package version',
    'Locate MSBuild',
    'Restore',
    'Patch Package.appxmanifest version',
    'Build Store Package',
    'Verify Store upload package and record its SHA256'
)) {
    $step = @($storeJob.steps | Where-Object { [string]$_.name -ceq $stepName }) | Select-Object -First 1
    Assert-Condition ($null -ne $step -and [string]$step.conclusion -ceq 'success') "Store package job step '$stepName' must have succeeded."
}
foreach ($jobName in @('store', 'release')) { $null = Assert-Job -Name $jobName -Conclusion 'skipped' }
foreach ($stepName in @(
    'Check Store product configuration',
    'Setup Store CLI',
    'Configure Store CLI and check for an existing submission',
    'MSStore Create Draft Submission',
    'MSStore Commit Submission'
)) {
    $step = @($storeJob.steps | Where-Object { [string]$_.name -ceq $stepName }) | Select-Object -First 1
    Assert-Condition ($null -ne $step -and [string]$step.conclusion -ceq 'skipped') "Store package dry-run step '$stepName' must be present and skipped."
}

$pr = Invoke-GhObject -Endpoint "repos/$repository/pulls/$PullRequest"
Assert-Condition ([string]$pr.state -ceq 'closed' -and $pr.merged -eq $true) 'The selected pull request must be merged.'
Assert-Condition ([string]$pr.base.ref -ceq 'main') 'The selected pull request must target main.'
Assert-Condition (([string]$pr.merge_commit_sha).ToLowerInvariant() -ceq $mainSha) 'The pull request merge commit must equal current main.'
$finalHead = ([string]$pr.head.sha).ToLowerInvariant()
Assert-Condition ($finalHead -match '^[0-9a-f]{40}([0-9a-f]{24})?$') 'The pull request final head SHA is invalid.'
$reviewedCommit = Invoke-GhObject -Endpoint "repos/$repository/git/commits/$finalHead"
Assert-Condition ([string]$reviewedCommit.tree.sha -ceq $tree) 'The integrated source tree must match the reviewed final pull request head tree.'

$timeline = Invoke-GhList -Endpoint "repos/$repository/issues/$PullRequest/timeline?per_page=100"
$reviews = Invoke-GhList -Endpoint "repos/$repository/pulls/$PullRequest/reviews?per_page=100"
$mergedAt = [DateTimeOffset]$pr.merged_at
$lastRequest = $timeline | Where-Object {
    [string]$_.event -ceq 'review_requested' -and
    $null -ne $_.PSObject.Properties['requested_reviewer'] -and
    $null -ne $_.requested_reviewer -and
    [string]$_.requested_reviewer.login -in @('Copilot', 'copilot-pull-request-reviewer[bot]') -and
    [DateTimeOffset]$_.created_at -le $mergedAt
} | Sort-Object { [DateTimeOffset]$_.created_at } -Descending | Select-Object -First 1
Assert-Condition ($null -ne $lastRequest) 'The final Copilot review request must be recorded before merge.'
$lastReview = $reviews | Where-Object {
    [string]$_.user.login -ceq 'copilot-pull-request-reviewer[bot]' -and
    -not [string]::IsNullOrWhiteSpace([string]$_.submitted_at) -and
    ([string]$_.commit_id).ToLowerInvariant() -ceq $finalHead -and
    [DateTimeOffset]$_.submitted_at -gt [DateTimeOffset]$lastRequest.created_at -and
    [DateTimeOffset]$_.submitted_at -lt $mergedAt
} | Sort-Object { [DateTimeOffset]$_.submitted_at }, id -Descending | Select-Object -First 1
Assert-Condition ($null -ne $lastReview -and [string]$lastReview.state -in @('APPROVED', 'COMMENTED')) 'A completed Copilot result for the last request and final PR head must arrive before merge.'

[pscustomobject]@{
    ReleaseTag = $ReleaseTag
    Version = $projectVersion
    HeadSha = $head
    MainSha = $mainSha
    SourceTree = $tree
    RunId = $RunId
    RunAttempt = $attempt
    PullRequest = [int]$PullRequest
    PullRequestHead = $finalHead
    CopilotReviewId = $lastReview.id
    StorePackageJobId = $storeJob.id
    Preflight = 'passed'
}
