[CmdletBinding()]
param(
    [string]$ApiBase,
    [string]$Repository,
    [string]$Tag,
    [string]$TagObject,
    [string]$Commit,
    [string]$Tree,
    [string]$Directory = 'dist',
    [string]$Token,
    [switch]$DefinitionOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ExpectedReleaseAssetNames {
    param([Parameter(Mandatory)][string]$ReleaseTag)

    $names = foreach ($architecture in 'x64', 'arm64') {
        foreach ($extension in 'zip', 'msi', 'msix') {
            "WindowThumbWall-$ReleaseTag-win-$architecture.$extension"
        }
    }
    $names += 'release-provenance.json'
    return @($names)
}

function Get-ReleaseIdentityMarker {
    param(
        [Parameter(Mandatory)][string]$ReleaseTag,
        [Parameter(Mandatory)][string]$FrozenTagObject,
        [Parameter(Mandatory)][string]$FrozenCommit,
        [Parameter(Mandatory)][string]$FrozenTree,
        [Parameter(Mandatory)][string]$ProvenanceSha256
    )

    return "<!-- window-thumb-wall-release:v1 tag=$ReleaseTag tag_object=$FrozenTagObject commit=$FrozenCommit tree=$FrozenTree provenance_sha256=$ProvenanceSha256 -->"
}

function Test-ReleaseIdentity {
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string]$ReleaseTag,
        [Parameter(Mandatory)][string]$Marker,
        [Parameter(Mandatory)][long]$ExpectedId
    )

    if ([long]$Release.id -ne $ExpectedId) { throw 'The GitHub release ID changed during reconciliation.' }
    if ($Release.tag_name -cne $ReleaseTag) { throw 'The existing GitHub release tag does not match the requested tag.' }
    if ($Release.draft -ne $true) { throw 'Refusing to modify a published GitHub release.' }
    # GitHub may report the default branch here when the tag already existed.
    # The exact target commit is therefore bound by the required marker; the
    # workflow independently re-verifies the signed tag and frozen commit.
    $matches = [regex]::Matches([string]$Release.body, '(?m)^' + [regex]::Escape($Marker) + '\r?$')
    if ($matches.Count -ne 1) { throw 'The draft identity marker is missing, duplicated, or mismatched.' }
}

function Test-GitHubAssetMatches {
    param(
        [Parameter(Mandatory)][scriptblock]$Api,
        [Parameter(Mandatory)]$Asset,
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)][string]$BaseUri,
        [Parameter(Mandatory)][string]$Repo
    )

    if ([long]$Asset.size -ne $Expected.Size) { throw "Existing asset size conflicts with verified local asset: $($Expected.Name)" }
    $digestProperty = $Asset.PSObject.Properties['digest']
    $digest = if ($null -ne $digestProperty) { [string]$digestProperty.Value } else { '' }
    if ($digest) {
        if ($digest -cne "sha256:$($Expected.Sha256)") { throw "Existing asset digest conflicts with verified local asset: $($Expected.Name)" }
        return
    }

    $temporaryPath = Join-Path ([IO.Path]::GetTempPath()) ('window-thumb-wall-release-' + [guid]::NewGuid().ToString('N') + '.asset')
    try {
        $downloadUri = "$($BaseUri.TrimEnd('/'))/repos/$Repo/releases/assets/$([long]$Asset.id)"
        $download = & $Api 'GET' $downloadUri $null $null $temporaryPath 'application/octet-stream'
        if ([int]$download.StatusCode -ne 200 -or -not (Test-Path -LiteralPath $temporaryPath)) { throw "Could not download existing asset for hash verification: $($Expected.Name)" }
        $downloadHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($downloadHash -cne $Expected.Sha256) { throw "Existing asset bytes conflict with verified local asset: $($Expected.Name)" }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Get-VerifiedLocalReleaseBundle {
    param(
        [Parameter(Mandatory)][string]$BundleDirectory,
        [Parameter(Mandatory)][string]$ReleaseTag,
        [Parameter(Mandatory)][string]$FrozenTagObject,
        [Parameter(Mandatory)][string]$FrozenCommit,
        [Parameter(Mandatory)][string]$FrozenTree
    )

    if ($ReleaseTag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' -or
        $FrozenTagObject -notmatch '^[0-9a-f]{40}$' -or
        $FrozenCommit -notmatch '^[0-9a-f]{40}$' -or
        $FrozenTree -notmatch '^[0-9a-f]{40}$') {
        throw 'The release identity is malformed.'
    }

    $resolvedDirectory = (Resolve-Path -LiteralPath $BundleDirectory).Path
    $manifestVerifier = Join-Path $PSScriptRoot 'release-artifact-manifest.ps1'
    & $manifestVerifier -Mode Verify -Directory $resolvedDirectory -Tag $ReleaseTag `
        -TagObject $FrozenTagObject -Commit $FrozenCommit -Tree $FrozenTree
    if (-not $?) { throw 'Local release provenance verification failed.' }

    $provenancePath = Join-Path $resolvedDirectory 'release-provenance.json'
    $record = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if ($record.tag -cne $ReleaseTag -or $record.tagObject -cne $FrozenTagObject -or
        $record.commit -cne $FrozenCommit -or $record.tree -cne $FrozenTree) {
        throw 'Local release provenance does not match the verified release identity.'
    }

    $filesByName = @{}
    foreach ($entry in @($record.files)) {
        if ($filesByName.ContainsKey([string]$entry.name)) { throw 'Local release provenance contains a duplicate file entry.' }
        $filesByName[[string]$entry.name] = $entry
    }

    $assets = foreach ($name in Get-ExpectedReleaseAssetNames -ReleaseTag $ReleaseTag) {
        $filePath = Join-Path $resolvedDirectory $name
        $file = Get-Item -LiteralPath $filePath -ErrorAction Stop
        if ($file.PSIsContainer -or $file.Length -le 0) { throw "Required release asset is empty or invalid: $name" }
        if ($name -ne 'release-provenance.json') {
            $entry = $filesByName[$name]
            if (-not $entry -or [long]$entry.bytes -ne [long]$file.Length) { throw "Release asset is absent from or differs from provenance: $name" }
            $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($entry.sha256 -cne $hash) { throw "Release asset hash differs from provenance: $name" }
        } else {
            $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        [pscustomobject]@{ Name = $name; Path = $filePath; Size = [long]$file.Length; Sha256 = $hash }
    }

    $notesPath = Join-Path $resolvedDirectory 'release-notes.md'
    $notesEntry = $filesByName['release-notes.md']
    $notesFile = Get-Item -LiteralPath $notesPath -ErrorAction Stop
    if (-not $notesEntry -or [long]$notesEntry.bytes -ne [long]$notesFile.Length -or
        $notesEntry.sha256 -cne (Get-FileHash -LiteralPath $notesPath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Release notes do not match local release provenance.'
    }
    $notes = Get-Content -LiteralPath $notesPath -Raw
    if ($notes -match '(?m)^<!-- window-thumb-wall-release:v1\b') { throw 'Release notes already contain a release identity marker.' }

    return [pscustomobject]@{
        Assets = @($assets)
        Notes = $notes
        ProvenanceSha256 = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Invoke-GitHubDraftReleaseReconcile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock]$Api,
        [Parameter(Mandatory)][string]$BaseUri,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$ReleaseTag,
        [Parameter(Mandatory)][string]$FrozenTagObject,
        [Parameter(Mandatory)][string]$FrozenCommit,
        [Parameter(Mandatory)][string]$FrozenTree,
        [Parameter(Mandatory)][string]$BundleDirectory
    )

    $bundle = Get-VerifiedLocalReleaseBundle -BundleDirectory $BundleDirectory -ReleaseTag $ReleaseTag `
        -FrozenTagObject $FrozenTagObject -FrozenCommit $FrozenCommit -FrozenTree $FrozenTree
    $marker = Get-ReleaseIdentityMarker -ReleaseTag $ReleaseTag -FrozenTagObject $FrozenTagObject `
        -FrozenCommit $FrozenCommit -FrozenTree $FrozenTree -ProvenanceSha256 $bundle.ProvenanceSha256
    $releasesUri = "$($BaseUri.TrimEnd('/'))/repos/$Repo/releases"
    # The tag endpoint supplies published releases. Authenticated list results
    # include drafts, which are required to recover a partial upload.
    $found = @(
        for ($page = 1; ; $page++) {
            $lookup = & $Api 'GET' "$releasesUri`?per_page=100&page=$page" $null $null $null 'application/json'
            if ([int]$lookup.StatusCode -ne 200 -or $null -eq $lookup.Data) {
                throw "GitHub release listing failed with HTTP $($lookup.StatusCode)."
            }
            $pageRows = @($lookup.Data)
            $pageRows | Where-Object { $_.tag_name -ceq $ReleaseTag }
            if ($pageRows.Count -lt 100) { break }
        }
    )
    if ($found.Count -gt 1) { throw 'More than one release refers to the requested tag.' }
    if ($found.Count -eq 0) {
        $body = "$marker`n`n$($bundle.Notes)"
        $create = & $Api 'POST' $releasesUri `
            @{ tag_name = $ReleaseTag; target_commitish = $FrozenCommit; name = "WindowThumbWall $ReleaseTag"; body = $body; draft = $true; prerelease = $false } `
            $null $null 'application/json'
        if ([int]$create.StatusCode -notin 200, 201 -or -not $create.Data.id) { throw 'GitHub did not return a release identity after draft creation.' }
        $releaseId = [long]$create.Data.id
    } else {
        $releaseId = [long]$found[0].id
    }
    if ($releaseId -le 0) { throw 'GitHub did not supply a valid release ID.' }

    $releaseUri = "$($BaseUri.TrimEnd('/'))/repos/$Repo/releases/$releaseId"
    $assetsUri = "$releaseUri/assets?per_page=100"
    $releaseReply = & $Api 'GET' $releaseUri $null $null $null 'application/json'
    if ([int]$releaseReply.StatusCode -ne 200 -or -not $releaseReply.Data.id) { throw 'Could not read back the selected GitHub release ID.' }
    $release = $releaseReply.Data
    Test-ReleaseIdentity -Release $release -ReleaseTag $ReleaseTag -Marker $marker -ExpectedId $releaseId
    if (-not $release.upload_url) { throw 'The selected GitHub draft has no upload URL.' }
    $uploadBase = [uri]([string]$release.upload_url -replace '\{\?name,label\}$', '')
    if (!$uploadBase.IsAbsoluteUri -or $uploadBase.AbsolutePath -cne "/repos/$Repo/releases/$releaseId/assets" -or $uploadBase.Query -or $uploadBase.Fragment) {
        throw 'The GitHub asset upload URL does not identify the selected draft.'
    }

    $assetReply = & $Api 'GET' $assetsUri $null $null $null 'application/json'
    if ([int]$assetReply.StatusCode -ne 200 -or $null -eq $assetReply.Data) { throw 'Could not read GitHub release assets.' }
    $remoteAssets = @($assetReply.Data)
    if ($remoteAssets.Count -ge 100) { throw 'The release has too many assets to reconcile safely.' }

    $expectedByName = @{}
    foreach ($asset in $bundle.Assets) { $expectedByName[$asset.Name] = $asset }
    $remoteByName = @{}
    foreach ($asset in $remoteAssets) {
        $name = [string]$asset.name
        if (-not $expectedByName.ContainsKey($name)) { throw "Unexpected asset in GitHub draft: $name" }
        if ($remoteByName.ContainsKey($name)) { throw "Duplicate asset in GitHub draft: $name" }
        $expected = $expectedByName[$name]
        Test-GitHubAssetMatches -Api $Api -Asset $asset -Expected $expected -BaseUri $BaseUri -Repo $Repo
        $remoteByName[$name] = $asset
    }

    foreach ($expected in $bundle.Assets) {
        if ($remoteByName.ContainsKey($expected.Name)) { continue }
        $uploadUri = $uploadBase.AbsoluteUri + '?name=' + [uri]::EscapeDataString($expected.Name)
        $upload = & $Api 'POST' $uploadUri $null $expected.Path $null 'application/octet-stream'
        if ([int]$upload.StatusCode -notin 200, 201) { throw "GitHub asset upload failed for $($expected.Name); re-read the draft before any retry." }

        $readback = & $Api 'GET' $releaseUri $null $null $null 'application/json'
        if ([int]$readback.StatusCode -ne 200 -or [long]$readback.Data.id -ne $releaseId) { throw 'Release identity changed after an asset upload.' }
        Test-ReleaseIdentity -Release $readback.Data -ReleaseTag $ReleaseTag -Marker $marker -ExpectedId $releaseId
        $assetReadback = & $Api 'GET' $assetsUri $null $null $null 'application/json'
        if ([int]$assetReadback.StatusCode -ne 200) { throw 'Could not read back uploaded GitHub assets.' }
        $uploaded = @($assetReadback.Data | Where-Object { $_.name -ceq $expected.Name })
        if ($uploaded.Count -ne 1) { throw "Uploaded asset readback did not include exactly one asset: $($expected.Name)" }
        Test-GitHubAssetMatches -Api $Api -Asset $uploaded[0] -Expected $expected -BaseUri $BaseUri -Repo $Repo
        $remoteByName[$expected.Name] = $uploaded[0]
    }

    $finalRelease = & $Api 'GET' $releaseUri $null $null $null 'application/json'
    if ([int]$finalRelease.StatusCode -ne 200 -or [long]$finalRelease.Data.id -ne $releaseId) { throw 'Final GitHub release readback failed.' }
    Test-ReleaseIdentity -Release $finalRelease.Data -ReleaseTag $ReleaseTag -Marker $marker -ExpectedId $releaseId
    $finalAssets = & $Api 'GET' $assetsUri $null $null $null 'application/json'
    if ([int]$finalAssets.StatusCode -ne 200) { throw 'Final GitHub asset readback failed.' }
    $finalByName = @{}
    foreach ($asset in @($finalAssets.Data)) {
        $name = [string]$asset.name
        if (-not $expectedByName.ContainsKey($name)) { throw "Unexpected asset appeared during reconciliation: $name" }
        if ($finalByName.ContainsKey($name)) { throw "Duplicate asset appeared during reconciliation: $name" }
        Test-GitHubAssetMatches -Api $Api -Asset $asset -Expected $expectedByName[$name] -BaseUri $BaseUri -Repo $Repo
        $finalByName[$name] = $asset
    }
    if ($finalByName.Count -ne $bundle.Assets.Count -or
        @($bundle.Assets | Where-Object { -not $finalByName.ContainsKey($_.Name) }).Count -ne 0) {
        throw 'Final GitHub draft asset set does not exactly match the verified release bundle.'
    }
    Write-Host "Verified draft release $releaseId for $ReleaseTag with $($bundle.Assets.Count) assets."
}

function Invoke-GitHubReleaseApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [hashtable]$Body,
        [string]$FilePath,
        [string]$OutPath,
        [string]$Accept = 'application/json'
    )

    $endpoint = [uri]$Uri
    if (!$endpoint.IsAbsoluteUri -or $endpoint.Scheme -cne 'https' -or !$endpoint.IsDefaultPort -or
        $endpoint.UserInfo -or $endpoint.Host -notin 'api.github.com', 'uploads.github.com') {
        throw 'The release request must use an approved GitHub HTTPS endpoint.'
    }
    $headers = @{ Authorization = "Bearer $script:ReleaseApiToken"; Accept = $Accept }
    $parameters = @{ Uri = $Uri; Method = $Method; Headers = $headers; SkipHttpErrorCheck = $true }
    if ($Body) { $parameters.ContentType = 'application/json'; $parameters.Body = ConvertTo-Json -InputObject $Body -Depth 5 -Compress }
    if ($FilePath) { $parameters.InFile = $FilePath; $parameters.ContentType = 'application/octet-stream' }
    if ($OutPath) { $parameters.OutFile = $OutPath; $parameters.PassThru = $true }
    $response = Invoke-WebRequest @parameters
    $data = $null
    if (-not $OutPath -and $response.Content) {
        try { $data = $response.Content | ConvertFrom-Json -Depth 20 -NoEnumerate } catch { $data = $response.Content }
    }
    return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Data = $data }
}

if (-not $DefinitionOnly) {
    foreach ($required in @($ApiBase, $Repository, $Tag, $TagObject, $Commit, $Tree, $Token)) {
        if ([string]::IsNullOrWhiteSpace($required)) { throw 'A required GitHub draft reconciliation input is missing.' }
    }
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'The GitHub repository name is malformed.' }
    $script:ReleaseApiToken = $Token
    $api = { param($Method, $Uri, $Body, $FilePath, $OutPath, $Accept) Invoke-GitHubReleaseApi -Method $Method -Uri $Uri -Body $Body -FilePath $FilePath -OutPath $OutPath -Accept $Accept }
    Invoke-GitHubDraftReleaseReconcile -Api $api -BaseUri $ApiBase -Repo $Repository -ReleaseTag $Tag `
        -FrozenTagObject $TagObject -FrozenCommit $Commit -FrozenTree $Tree -BundleDirectory $Directory
}
