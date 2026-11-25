# Release Process

WindowThumbWall uses a tag-driven release flow. Pushing a version tag builds all three official package formats and generates release metadata for GitHub Releases and Microsoft Store copy.

## Release Gate

A new signed annotated tag triggers the public release workflow. Treat the tag
push as a release action. Keep existing tags and their source history intact,
including tags whose release runs stopped.

### Evidence required before the tag

1. Align the project version, both package manifests, and the localized release
   notes. Run the repository's Windows tests and release fixtures against the
   candidate. Follow the developer guide for changes that affect installed
   runtime behavior.
2. Review the complete candidate. Wait for any requested automated review on
   the final pull request head. Address the comments, rerun affected checks,
   and obtain another review if the head changes. Merge through the repository
   protections and record the final reviewed head, resulting main commit,
   last review request, submitted review, and disposition of the findings.
3. Run **Build Release Artifacts** on that exact main commit with both checks
   enabled. Review the run's job and step outcomes.

```powershell
gh workflow run build-release-artifacts.yml --ref main -f verify_previous_msi_upgrade=true -f verify_store_package=true
```

The distribution job must pass the Windows tests, release fixtures, x64 and
ARM64 package builds, and the real x64 MSI upgrade from v0.6.10. The separate
`store-package-check` job runs the Store packaging steps on the same runner
label as tagged releases. It verifies the x64 and ARM64 Store upload bundle
version and SHA-256. A package-only run receives read-only GitHub permissions
and no Store credentials. Its submission steps, the regular `store` job, and
the GitHub draft job must be skipped.

4. Run the read-only verifier from the clean checkout of the approved main
   commit. It checks the exact merged PR, automated review timing, the
   workflow dispatch, and the required jobs and steps.

```powershell
.\packaging\verify-release-preflight.ps1 -ReleaseTag vX.Y.Z -RunId <run-id> -PullRequest <pr-number>
```

Record the verifier result, main commit, source tree, pull request, run and
attempt. Read the Store package job summary and record the bundle SHA-256.
Treat skipped required steps, mismatched source, and uncertain results as
incomplete. Request approval for that exact public release after the evidence
is complete, then use the signed tag procedure.

### Evidence after the tag

GitHub Actions verifies the new annotated tag against the trusted SSH signer
list from protected main and freezes the source identity. The tag run verifies
the release packages and their provenance. The separate Store job checks and
builds the exact Store package. It observes the pending submission state
before making a write. Preserve any existing draft and reconcile an uncertain
prior effect in Partner Center before a retry.

Record the Store upload package SHA-256, manifest version, architecture set,
and actual provider submission ID and state. Track certification and public
Store availability separately. The GitHub draft job requires the same
successful tag run and verifies all six distribution assets against the
artifact manifest. Verify the draft and its six assets plus provenance before
publishing it, then read back the public release and update its release
tracking.

### Recovering a Partial GitHub Draft

The GitHub job reconciles the draft for the exact signed tag after the Store
job succeeds. The initial draft body carries an identity marker containing the
tag, immutable annotated tag object, target commit, source tree, and SHA-256 of
`release-provenance.json`. A retry requires that marker and the same tag and
target commit. A published release, a mismatched marker or target, an unknown
asset, or an asset with conflicting bytes stops recovery for owner review.

The draft asset set is exactly the six ZIP, MSI, and MSIX packages for x64 and
ARM64 plus `release-provenance.json`. The job verifies the local provenance
manifest before contacting GitHub. It checks existing assets by GitHub SHA-256
digest and size, or downloads and hashes the bytes when GitHub omits the digest.
It uploads only missing assets, then reads the same release ID and asset set
back before succeeding. Do not delete a conflicting asset or draft to force a
retry.

If the Store job already succeeded and the GitHub job failed, rerun only the
failed GitHub job in the same workflow run. That reuses the successful Store
result and reconciles the existing draft; it does not submit to the Store
again. If an upload result was uncertain, the next run first reads the draft
and its assets and proceeds only from that observed state. Manual workflow
dispatch remains a build-and-verify path and never creates or repairs a draft.

## Privacy Pages

[Deploy Privacy Page](../.github/workflows/deploy-pages.yml) is an operational
publication workflow for the English and Japanese privacy notices. Its automatic
trigger is a main push affecting those notices or their Pages workflow. Its
manual trigger also publishes to the `github-pages` environment. Treat this as a
dedicated publication operation when changing or running it.

## MSI upgrade check

The signed-tag build checks both a fresh x64 install and an upgrade from the
published v0.6.10 MSI on its isolated Windows runner. The upgrade keeps the
shared cleanup component identity and schedules removal of the old product
after the new install executes. Its check confirms the old ProductCode is
gone, the new executable has the release version, and a legacy settings
fixture remains byte-for-byte intact. A failed check holds both publication jobs.

For pre-integration evidence, dispatch **Build Release Artifacts** on the exact
candidate branch with `verify_previous_msi_upgrade` enabled. Its isolated
x64 fixture uses the target 0.6.11 version and candidate source. The normal
development outputs keep their existing version, and the dispatch cannot reach
either publication job. The signed-tag build repeats the gate with the actual
release MSI.

The standalone CI MSIX is packed directly by MakeAppx. Its manifest declares the English and Japanese resource languages explicitly. The Visual Studio package project resolves its own resource placeholder during MSBuild.

## Distribution Invariant
The official distribution formats are fixed.

- ZIP: `WindowThumbWall-vX.Y.Z-win-x64.zip`
- MSI: `WindowThumbWall-vX.Y.Z-win-x64.msi`
- MSIX: `WindowThumbWall-vX.Y.Z-win-x64.msix`

Do not change this set without explicitly revisiting [invariants.md](invariants.md).

## Prepare Release-Specific Highlights
If you want version-specific notes, create one of the following before tagging.

Example:
```text
packaging/release-notes-0.2.3.md
packaging/release-notes-ja-0.2.3.md
packaging/release-notes-en-0.2.3.md
```

How they are used:
- `release-notes-<version>.md`: shared fallback for both languages
- `release-notes-ja-<version>.md`: overrides Japanese Store metadata and Japanese "What's new"
- `release-notes-en-<version>.md`: overrides GitHub Release text, English Store metadata, and English "What's new"

Each file can contain short markdown bullets or short prose describing what changed in that release. The metadata generator embeds that content into the generated release assets.

## Tag and Push
After approval for the exact version, rerun the preflight immediately before
creating the signed tag. Set the approved version, hosted run ID, and merged
pull request number. Use the repository's trusted SSH signer and verify the
tag against its reviewed commit before pushing. Run the block as a unit.

```powershell
& {
    $ErrorActionPreference = 'Stop'
    $releaseTag = 'vX.Y.Z'
    $runId = '<run-id>'
    $pullRequest = '<pr-number>'
    $evidence = .\packaging\verify-release-preflight.ps1 -ReleaseTag $releaseTag -RunId $runId -PullRequest $pullRequest
    if ($evidence.Preflight -cne 'passed') { throw 'Release preflight did not pass.' }
    $audit = "Preflight run $($evidence.RunId) attempt $($evidence.RunAttempt), PR #$($evidence.PullRequest), Copilot review $($evidence.CopilotReviewId)"
    git tag -s $evidence.ReleaseTag $evidence.HeadSha -m "Release $($evidence.ReleaseTag)" -m $audit
    if ($LASTEXITCODE -ne 0) { throw 'Signed tag creation failed.' }
    git -c gpg.ssh.allowedSignersFile=.github/release-allowed-signers verify-tag $evidence.ReleaseTag
    if ($LASTEXITCODE -ne 0) { throw 'Release tag signature verification failed.' }
    $tagTarget = git rev-parse --verify "$($evidence.ReleaseTag)^{commit}"
    if ($LASTEXITCODE -ne 0 -or $tagTarget -cne $evidence.HeadSha) { throw 'The tag target does not match the preflight commit.' }
    $tagRef = "refs/tags/$($evidence.ReleaseTag)"
    git push origin "${tagRef}:$tagRef"
    if ($LASTEXITCODE -ne 0) { throw 'Tag push failed. Read the remote tag and workflow state before retrying.' }
}
```

## Version Alignment Map
Treat the Git tag `vX.Y.Z` as the release source of truth. Use the table below to decide which places must be updated manually and which places are derived automatically.

| Target | Format | How it is kept in sync | Notes |
| --- | --- | --- | --- |
| Git tag | `vX.Y.Z` | Manual | Release trigger. Both release workflows start from this tag format. |
| [WindowThumbWall.csproj](../WindowThumbWall.csproj) | `X.Y.Z` and `X.Y.Z.0` | Manual | Update `Version`, `AssemblyVersion`, and `FileVersion` together. |
| [packaging/AppxManifest.xml](../packaging/AppxManifest.xml) | `X.Y.Z.0` | Manual in source, automatic in release output | The build workflow patches the copied manifest inside the release package from the tag, but the source manifest should still be kept current for local packaging clarity. |
| [packaging/Package.appxmanifest](../packaging/Package.appxmanifest) | `X.Y.Z.0` | Manual in source, automatic in Store workflow | The Store workflow rewrites the manifest before building the Store package, but the checked-in file should still track the current release version. |
| [docs/notification-attention-design.md](notification-attention-design.md) | current behavior | Manual | Update whenever notification matching rules or attention visuals change. |
| Release notes input files | `packaging/release-notes-<version>.md` and localized variants | Manual | Create the files for the exact plain version such as `0.6.2`, without the `v` prefix. |
| Release highlights input files | `packaging/release-highlights-<version>.md` and localized variants | Manual | Optional, but if used they must match the same plain version as the tag. |
| Generated artifact filenames | `WindowThumbWall-vX.Y.Z-win-*.zip/msi/msix` | Automatic from tag | Produced by the build workflow. |
| Generated GitHub Release body | `dist/release-notes.md` | Automatic from tag and metadata inputs | Produced by [packaging/generate-release-metadata.ps1](../packaging/generate-release-metadata.ps1). |
| Generated Store metadata | `dist/store-listing-*.md`, `dist/store-whats-new-*.md` | Automatic from tag and metadata inputs | Produced by the metadata generator. |
| Store package version | `X.Y.Z.0` | Automatic from tag | Both release workflows normalize `vX.Y.Z` to a four-part Store/MSIX version. |
| GitHub Release title | `X.Y.Z` | Manual today | The draft release is currently created with `WindowThumbWall vX.Y.Z`, so rename it to the plain version before publishing if you want the numeric-only convention. |

### Minimum Manual Update Set
Before creating `vX.Y.Z`, make sure these version-bearing sources have been reviewed together:

1. [WindowThumbWall.csproj](../WindowThumbWall.csproj)
2. [packaging/AppxManifest.xml](../packaging/AppxManifest.xml)
3. [packaging/Package.appxmanifest](../packaging/Package.appxmanifest)
4. [docs/notification-attention-design.md](notification-attention-design.md) when notification matching or attention cues changed
5. `packaging/release-notes-<version>.md` or localized equivalents when the release has user-visible changes
6. `packaging/release-highlights-<version>.md` or localized equivalents if you use highlights files

After the draft GitHub Release is created, also verify that the release title follows the chosen convention.

## What The Build Workflow Generates
The [build-release-artifacts workflow](../.github/workflows/build-release-artifacts.yml) runs [packaging/generate-release-metadata.ps1](../packaging/generate-release-metadata.ps1) on each tagged release.

Generated files:
- `dist/release-notes.md`: GitHub Release body
- `dist/store-listing-ja.md`: Japanese Store listing copy
- `dist/store-listing-en.md`: English Store listing copy
- `dist/store-whats-new-ja.md`: Japanese "What's new" copy
- `dist/store-whats-new-en.md`: English "What's new" copy

## What Gets Applied Automatically
- GitHub Release draft creation uses `dist/release-notes.md` automatically as the release body.
- ZIP, MSI, and MSIX artifacts are attached to the draft release automatically.

## What Still Requires Manual Store Entry
The current Microsoft Store workflow submits binaries, but it does not update Store listing text fields such as description, keywords, screenshots, or the "What's new" field.

Use the generated files below when updating Partner Center manually:
- `dist/store-listing-ja.md`
- `dist/store-listing-en.md`
- `dist/store-whats-new-ja.md`
- `dist/store-whats-new-en.md`

## Microsoft Store Submission Automation
For an existing live free Microsoft Store product, the signed-tag build calls
[release-to-store.yml](../.github/workflows/release-to-store.yml) only after the
required tests and distribution builds succeed.

Tagged releases queue Store jobs for this product. After authenticating, the Store workflow checks for existing pending
Store state and leaves any pending draft untouched. The commit receipt ID records
the submission commit. Track Microsoft certification separately.

Behavior:
1. The workflow checks out the frozen commit and verifies the source tree.
2. The three-part tag version is normalized to a four-part Store manifest version.
3. The Windows Application Packaging Project is built as a Store upload package.
4. The workflow verifies the bundle version and architectures, then records the
   package SHA-256. It checks the frozen tag again before uploading the package
   as a draft submission through the Microsoft Store CLI.
5. The workflow rechecks the tag before committing the draft submission.
   The GitHub draft job depends on this successful Store submission.

Example:
- `v0.4.2` becomes `0.4.2.0` inside the Store packaging workflow.

Four-component tags are not accepted for Store submissions.

### Store Workflow Success Criteria
The Store workflow is intentionally limited to operations that directly control submission:

- create the Store upload package
- create or update the draft submission
- commit the draft submission

Submission status polling is not part of the workflow success criteria.

Reasoning:
- The Microsoft Store ingestion APIs can return transient errors while status polling even after submission commit has already started.
- A release workflow should fail when submission creation or submission commit fails.
- A release workflow should not fail solely because post-submit status observation is temporarily unavailable.

If you need to confirm the later Partner Center state, verify it separately in Partner Center or with a follow-up manual check.

If an earlier run started creating or committing a Store submission, reconcile
that submission in Partner Center before retrying. A draft GitHub Release
failure after a successful Store job should be recovered from the failed
GitHub job, with the same tag and package hashes.

## Store Workflow Prerequisites
GitHub secrets:
- `AZURE_AD_TENANT_ID`
- `AZURE_AD_APPLICATION_CLIENT_ID`
- `AZURE_AD_APPLICATION_SECRET`
- `SELLER_ID`

GitHub variable:
- `MSSTORE_PRODUCT_ID`

The Azure AD app registration must already be authorized in Partner Center for the seller account.

## Local release fixtures

Run the following in the product checkout before sharing the candidate.

```powershell
.\packaging\test-verify-release-tag.ps1
.\packaging\test-reconcile-github-release.ps1
.\packaging\test-verify-store-submission-slot.ps1
.\packaging\test-verify-release-preflight.ps1
```

Generate and review the localized metadata with the local test command below.
Use the developer guide's installed MSIX verification when packaging or
notification behavior changes. For optional release highlights, use the
matching versioned files under `packaging/`. If the Store listing is updated
manually, use the generated Store metadata.

## Local Test Command
You can generate the metadata locally before tagging.

```powershell
.\packaging\generate-release-metadata.ps1 -Version v0.2.3 -OutputDir dist-test
```

If you use a versioned scratch directory such as `dist-release-0.6.5` for local review, treat it as disposable local output. Keep the checked-in metadata inputs under `packaging/`, and remove the generated directory after review if you do not need the snapshot anymore.
