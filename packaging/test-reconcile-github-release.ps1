$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'reconcile-github-release.ps1') -DefinitionOnly

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('window-thumb-wall-release-fixture-' + [guid]::NewGuid().ToString('N'))
$directory = Join-Path $scratch 'dist'
$tag = 'v1.2.3'
$tagObject = '1' * 40
$commit = '2' * 40
$tree = '3' * 40
$baseUri = 'https://api.github.invalid'
$repository = 'fixture/window-thumb-wall'
$releaseId = 43

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Name)
    try { & $Action } catch { Write-Output "PASS $Name"; return }
    throw "Fixture '$Name' unexpectedly passed."
}

function Copy-FixtureAsset {
    param([Parameter(Mandatory)]$Asset)
    $copy = [ordered]@{ id = $Asset.id; name = $Asset.name; size = $Asset.size }
    $digest = $Asset.PSObject.Properties['digest']
    if ($null -ne $digest) { $copy.digest = $digest.Value }
    return [pscustomobject]$copy
}

function New-MockApi {
    param([Parameter(Mandatory)]$State)
    $baseUri = $script:FixtureBaseUri
    $repository = $script:FixtureRepository
    $tag = $script:FixtureTag
    $releaseId = $script:FixtureReleaseId
    return {
        param($Method, $Uri, $Body, $FilePath, $OutPath, $Accept)
        $script:FixtureRequests += ,([pscustomobject]@{ Method = $Method; Uri = $Uri })
        $releasesUri = "$baseUri/repos/$repository/releases"
        $releaseUri = "$baseUri/repos/$repository/releases/$releaseId"
        $assetsUri = "$releaseUri/assets?per_page=100"

        if ($Method -eq 'GET' -and $Uri -ceq "$releasesUri`?per_page=100&page=1") {
            $rows = if ($State.Release) { ,@($State.Release) } else { ,@() }
            return [pscustomobject]@{ StatusCode = 200; Data = $rows }
        }
        if ($Method -eq 'POST' -and $Uri -ceq "$baseUri/repos/$repository/releases") {
            if ($State.Release) { throw 'Mock rejected a duplicate draft create.' }
            $State.Release = [pscustomobject]@{
                id = $releaseId; tag_name = $Body.tag_name; target_commitish = $Body.target_commitish
                body = $Body.body.Replace("`n", "`r`n"); draft = [bool]$Body.draft; upload_url = "https://uploads.github.invalid/repos/$repository/releases/$releaseId/assets{?name,label}"
            }
            return [pscustomobject]@{ StatusCode = 201; Data = $State.Release }
        }
        if ($Method -eq 'GET' -and $Uri -ceq $releaseUri) {
            if ($State.Release) { return [pscustomobject]@{ StatusCode = 200; Data = $State.Release } }
            return [pscustomobject]@{ StatusCode = 404; Data = $null }
        }
        if ($Method -eq 'GET' -and $Uri -ceq $assetsUri) {
            return [pscustomobject]@{ StatusCode = 200; Data = @($State.Assets.Values) }
        }
        if ($Method -eq 'POST' -and $Uri -like 'https://uploads.github.invalid/*') {
            $encodedName = ([uri]$Uri).Query -replace '^\?name=', ''
            $name = [uri]::UnescapeDataString($encodedName)
            if ($State.Assets.ContainsKey($name)) { throw "Mock rejected duplicate upload: $name" }
            $bytes = [IO.File]::ReadAllBytes($FilePath)
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            $asset = [pscustomobject]@{ id = $State.NextAssetId; name = $name; size = $bytes.Length; digest = "sha256:$hash" }
            $State.NextAssetId++
            $State.Assets[$name] = $asset
            $State.Bytes[$asset.id] = $bytes
            $State.UploadCount++
            return [pscustomobject]@{ StatusCode = 201; Data = $asset }
        }
        if ($Method -eq 'GET' -and $Uri -like "$baseUri/repos/$repository/releases/assets/*") {
            $id = [long]([uri]$Uri).Segments[-1]
            if (-not $State.Bytes.ContainsKey($id)) { return [pscustomobject]@{ StatusCode = 404; Data = $null } }
            [IO.File]::WriteAllBytes($OutPath, [byte[]]$State.Bytes[$id])
            return [pscustomobject]@{ StatusCode = 200; Data = $null }
        }
        throw "Unmocked API request: $Method $Uri"
    }.GetNewClosure()
}

function Invoke-Fixture {
    param([Parameter(Mandatory)]$State)
    $script:FixtureRequests = @()
    $api = New-MockApi -State $State
    Invoke-GitHubDraftReleaseReconcile -Api $api -BaseUri $script:FixtureBaseUri -Repo $script:FixtureRepository `
        -ReleaseTag $script:FixtureTag -FrozenTagObject $script:FixtureTagObject -FrozenCommit $script:FixtureCommit `
        -FrozenTree $script:FixtureTree -BundleDirectory $script:FixtureDirectory
}

try {
    $script:FixtureBaseUri = $baseUri
    $script:FixtureRepository = $repository
    $script:FixtureTag = $tag
    $script:FixtureTagObject = $tagObject
    $script:FixtureCommit = $commit
    $script:FixtureTree = $tree
    $script:FixtureDirectory = $directory
    $script:FixtureReleaseId = $releaseId
    $script:FixtureRequests = @()
    $null = New-Item -ItemType Directory -Path $directory -Force

    $names = @(Get-ExpectedReleaseAssetNames -ReleaseTag $tag) + @(
        'release-notes.md', 'store-listing-ja.md', 'store-listing-en.md',
        'store-whats-new-ja.md', 'store-whats-new-en.md'
    )
    foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $directory $name), "fixture:$name") }
    & (Join-Path $PSScriptRoot 'release-artifact-manifest.ps1') -Mode Write -Directory $directory `
        -Tag $tag -TagObject $tagObject -Commit $commit -Tree $tree -Toolchain 'local fixture'

    $state = @{ Release = $null; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 1000 }
    Invoke-Fixture -State $state
    if (-not $state.Release -or $state.Assets.Count -ne 7 -or $state.UploadCount -ne 7) { throw 'Initial draft creation did not upload the exact seven assets.' }
    Write-Output 'PASS initial draft and seven verified assets'

    $partial = @{ Release = $state.Release; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 2000 }
    foreach ($name in @($state.Assets.Keys | Select-Object -First 3)) {
        $partial.Assets[$name] = $state.Assets[$name]
        $assetId = [long]$state.Assets[$name].id
        $source = Join-Path $directory $name
        $partial.Bytes[$assetId] = [IO.File]::ReadAllBytes($source)
    }
    Invoke-Fixture -State $partial
    if ($partial.Assets.Count -ne 7 -or $partial.UploadCount -ne 4) { throw 'Partial recovery did not upload only four missing assets.' }
    Write-Output 'PASS partial draft uploads only missing assets'

    $noDigest = @{ Release = $partial.Release; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 3000 }
    foreach ($name in $partial.Assets.Keys) {
        $asset = $partial.Assets[$name]
        $noDigest.Assets[$name] = [pscustomobject]@{ id = $asset.id; name = $asset.name; size = $asset.size; digest = $asset.digest }
        $noDigest.Bytes[[long]$asset.id] = [IO.File]::ReadAllBytes((Join-Path $directory $name))
    }
    $oneAsset = $noDigest.Assets.Values | Select-Object -First 1
    $oneAsset.PSObject.Properties.Remove('digest')
    Invoke-Fixture -State $noDigest
    Write-Output 'PASS missing provider digest verified by downloaded bytes'

    $conflict = @{ Release = $noDigest.Release; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 4000 }
    foreach ($name in $noDigest.Assets.Keys) { $conflict.Assets[$name] = Copy-FixtureAsset $noDigest.Assets[$name]; $conflict.Bytes[[long]$noDigest.Assets[$name].id] = $noDigest.Bytes[[long]$noDigest.Assets[$name].id] }
    $conflictingAsset = $conflict.Assets.Values | Where-Object { $_.PSObject.Properties['digest'] } | Select-Object -First 1
    $conflict.Assets[$conflictingAsset.name].digest = 'sha256:' + ('f' * 64)
    Assert-Throws -Name 'reject conflicting digest without upload' -Action { Invoke-Fixture -State $conflict }
    if ($conflict.UploadCount -ne 0) { throw 'A conflicting draft caused an upload.' }

    $badBytes = @{ Release = $noDigest.Release; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 4500 }
    foreach ($name in $noDigest.Assets.Keys) {
        $asset = $noDigest.Assets[$name]
        $badBytes.Assets[$name] = Copy-FixtureAsset $asset
        $badBytes.Bytes[[long]$asset.id] = [byte[]]$noDigest.Bytes[[long]$asset.id]
    }
    $badDigestFallback = $badBytes.Assets.Values | Where-Object { -not $_.PSObject.Properties['digest'] } | Select-Object -First 1
    if (-not $badDigestFallback) {
        $badDigestFallback = [pscustomobject]@{ id = $badBytes.Assets.Values[0].id; name = $badBytes.Assets.Values[0].name; size = $badBytes.Assets.Values[0].size }
        $badBytes.Assets[$badDigestFallback.name] = $badDigestFallback
    }
    $badBytes.Bytes[[long]$badDigestFallback.id] = [byte[]]@(0, 1, 2)
    Assert-Throws -Name 'reject conflicting downloaded asset bytes' -Action { Invoke-Fixture -State $badBytes }
    if ($badBytes.UploadCount -ne 0) { throw 'Conflicting downloaded bytes caused an upload.' }

    $wrongMarker = @{ Release = [pscustomobject]@{ id = $releaseId; tag_name = $tag; target_commitish = $commit; body = 'wrong marker'; draft = $true; upload_url = $partial.Release.upload_url }; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 5000 }
    Assert-Throws -Name 'reject mismatched marker' -Action { Invoke-Fixture -State $wrongMarker }
    if ($wrongMarker.UploadCount -ne 0) { throw 'A mismatched draft caused an upload.' }

    $wrongTargetBody = $partial.Release.body.Replace("commit=$commit", 'commit=' + ('4' * 40))
    $wrongTarget = @{ Release = [pscustomobject]@{ id = $releaseId; tag_name = $tag; target_commitish = $commit; body = $wrongTargetBody; draft = $true; upload_url = $partial.Release.upload_url }; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 5500 }
    Assert-Throws -Name 'reject wrong target SHA marker' -Action { Invoke-Fixture -State $wrongTarget }
    if ($wrongTarget.UploadCount -ne 0) { throw 'A wrong target SHA marker caused an upload.' }

    $published = @{ Release = [pscustomobject]@{ id = $releaseId; tag_name = $tag; target_commitish = $commit; body = $partial.Release.body; draft = $false; upload_url = $partial.Release.upload_url }; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 6000 }
    Assert-Throws -Name 'reject published release' -Action { Invoke-Fixture -State $published }
    if ($published.UploadCount -ne 0) { throw 'A published release caused an upload.' }

    $unknown = @{ Release = $partial.Release; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 7000 }
    foreach ($name in $partial.Assets.Keys) { $unknown.Assets[$name] = Copy-FixtureAsset $partial.Assets[$name]; $unknown.Bytes[[long]$partial.Assets[$name].id] = $partial.Bytes[[long]$partial.Assets[$name].id] }
    $unknownAsset = [pscustomobject]@{ id = 9999; name = 'unexpected.bin'; size = 1; digest = 'sha256:' + ('0' * 64) }
    $unknown.Assets[$unknownAsset.name] = $unknownAsset
    $unknown.Bytes[9999L] = [byte[]]@(0)
    Assert-Throws -Name 'reject unknown release asset' -Action { Invoke-Fixture -State $unknown }
    if ($unknown.UploadCount -ne 0) { throw 'An unknown asset caused an upload.' }

    $wrongUpload = @{ Release = [pscustomobject]@{ id = $releaseId; tag_name = $tag; body = $partial.Release.body; draft = $true; upload_url = 'https://uploads.github.invalid/repos/other/draft/releases/999/assets{?name,label}' }; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 7500 }
    Assert-Throws -Name 'reject upload URL for the wrong draft' -Action { Invoke-Fixture -State $wrongUpload }
    if ($wrongUpload.UploadCount -ne 0) { throw 'An upload was sent to an unrelated draft.' }

    $provenancePath = Join-Path $directory 'release-provenance.json'
    $savedProvenance = [IO.File]::ReadAllText($provenancePath)
    $noRemoteBeforeManifestFailure = @{ Release = $null; Assets = @{}; Bytes = @{}; UploadCount = 0; NextAssetId = 8000 }
    try {
        [IO.File]::WriteAllText($provenancePath, '{}')
        Assert-Throws -Name 'reject bad local manifest before remote mutation' -Action { Invoke-Fixture -State $noRemoteBeforeManifestFailure }
        if ($noRemoteBeforeManifestFailure.Release -or $noRemoteBeforeManifestFailure.UploadCount -ne 0) { throw 'Bad local provenance reached a remote write.' }
    } finally {
        [IO.File]::WriteAllText($provenancePath, $savedProvenance)
    }

    function Invoke-WebRequest {
        param([string]$Uri, [string]$Method, [hashtable]$Headers, [switch]$SkipHttpErrorCheck,
            [string]$OutFile, [switch]$PassThru, [int]$MaximumRedirection)
        if ($Method -eq 'POST' -and (!$PSBoundParameters.ContainsKey('MaximumRedirection') -or $MaximumRedirection -ne 0)) {
            throw 'A write request followed HTTP redirects.'
        }
        if ($OutFile) {
            if (!$PassThru) { return }
            [IO.File]::WriteAllBytes($OutFile, [byte[]]@(1, 2, 3))
            return [pscustomobject]@{ StatusCode = 200; Content = $null }
        }
        return [pscustomobject]@{ StatusCode = 200; Content = '[]' }
    }

    $script:ReleaseApiToken = 'test-token'
    $reply = Invoke-GitHubReleaseApi -Method GET -Uri 'https://api.github.com/repos/fixture/window-thumb-wall/releases/43/assets'
    if ($null -eq $reply.Data -or @($reply.Data).Count -ne 0) { throw 'An empty asset array did not survive the REST transport.' }
    $binaryFile = Join-Path $scratch 'binary'
    $reply = Invoke-GitHubReleaseApi -Method GET -Uri 'https://api.github.com/repos/fixture/window-thumb-wall/releases/assets/1' -OutPath $binaryFile
    if ($reply.StatusCode -ne 200 -or (Get-Item -LiteralPath $binaryFile).Length -ne 3) { throw 'The binary download lost its HTTP result or content.' }
    $null = Invoke-GitHubReleaseApi -Method POST -Uri 'https://api.github.com/repos/fixture/window-thumb-wall/releases'
    Assert-Throws -Name 'reject credential forwarding to another origin' -Action {
        Invoke-GitHubReleaseApi -Method POST -Uri 'https://unrelated.invalid/upload'
    }
    Write-Output 'PASS production REST adapter preserves empty JSON arrays and binary HTTP status'
    Write-Output 'PASS all GitHub draft reconciliation fixtures'
} finally {
    if (Test-Path -LiteralPath $scratch -PathType Container) {
        $resolvedScratch = (Resolve-Path -LiteralPath $scratch).Path
        $actualParent = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($resolvedScratch)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $expectedParent = $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        if (-not [string]::Equals($actualParent, $expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedScratch) -notmatch '^window-thumb-wall-release-fixture-[a-f0-9]{32}$') {
            throw 'Refused cleanup outside the exact release fixture directory.'
        }
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
