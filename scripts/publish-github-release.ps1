param(
    [Parameter(Mandatory)][string]$AssetDirectory,
    [Parameter(Mandatory)][string]$ExpectedRevision,
    [string]$Repository = 'TahsinFaiyaz30/CloudInlet',
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..')
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$assetsRoot = [IO.Path]::GetFullPath($AssetDirectory)
if ($Repository -cne 'TahsinFaiyaz30/CloudInlet') { throw 'Unexpected release repository.' }
if (!$env:GH_TOKEN -and !$env:GITHUB_TOKEN) { throw 'Set GH_TOKEN with contents:write before publishing a release.' }
$source = & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $root
if ($ExpectedRevision -cne $source.sourceRevision) { throw 'The release commit does not match the checked-out source.' }
$status = @(& git -C $root status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $status.Count) { throw 'Publish a release from a clean tracked source tree.' }
& git -C $root fetch origin main --tags
if ($LASTEXITCODE -ne 0) { throw 'Could not verify the current release source.' }
$source = & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $root -CheckRelease
$latestMain = (& git -C $root rev-parse origin/main).Trim()
if ($LASTEXITCODE -ne 0 -or $latestMain -cne $ExpectedRevision) { throw 'Main changed while this release was building. Publish the newest successful CI source instead.' }
$evidencePath = Join-Path $assetsRoot 'release-validation.json'
$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
if ($evidence.sourceRevision -cne $ExpectedRevision -or $evidence.version -cne $source.version) { throw 'Release validation belongs to a different source version or commit.' }
foreach ($flavor in @('Debug', 'Release')) {
    $gate = @($evidence.configurations | Where-Object { $_.configuration -ceq $flavor })
    if ($gate.Count -ne 1 -or $gate[0].buildSucceeded -ne $true -or $gate[0].testsPassed -lt 1 -or $gate[0].testsFailed -ne 0 -or $gate[0].testsSkipped -ne 0) { throw "The $flavor build and test gates have not passed." }
}
$curatedNotesPath = "docs/releases/$($source.version).json"
& git -C $root ls-files --error-unmatch -- $curatedNotesPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Curated release notes must be committed with the release source before publication.' }
& (Join-Path $PSScriptRoot 'generate-update-manifest.ps1') -Version $source.version -AssetDirectory $assetsRoot -Repository $Repository | Out-Null
$inputFiles = @(Get-ChildItem -LiteralPath $assetsRoot -File | Sort-Object Name)
$manifest = Get-Content -LiteralPath (Join-Path $assetsRoot 'updates-v2.json') -Raw | ConvertFrom-Json
$publicNames = @($manifest.assets.fileName) + @('updates-v2.json', 'SHA256SUMS.txt')
$expectedNames = $publicNames + @('release-validation.json')
$notesPath = Join-Path $assetsRoot 'release-notes.md'
if (Test-Path -LiteralPath $notesPath -PathType Leaf) { $expectedNames += 'release-notes.md' }
$flavorNames = @('Debug-build-validation.json', 'Release-build-validation.json')
$flavorFiles = @($inputFiles | Where-Object { $_.Name -cin $flavorNames })
if ($flavorFiles.Count) {
    if ($flavorFiles.Count -ne 2) { throw 'Build validation records must include both Debug and Release.' }
    foreach ($flavorFile in $flavorFiles) {
        $flavorEvidence = Get-Content -LiteralPath $flavorFile.FullName -Raw | ConvertFrom-Json
        $flavor = $flavorFile.Name.Replace('-build-validation.json', '')
        if ($flavorEvidence.version -cne $source.version -or $flavorEvidence.sourceRevision -cne $ExpectedRevision -or
            $flavorEvidence.configuration -cne $flavor -or $flavorEvidence.buildSucceeded -ne $true -or
            $flavorEvidence.testsPassed -lt 1 -or $flavorEvidence.testsFailed -ne 0 -or $flavorEvidence.testsSkipped -ne 0) {
            throw 'Per-flavor release validation must describe passing gates for this exact source.'
        }
    }
    $expectedNames += $flavorNames
}
$installerAssets = @($manifest.assets | Where-Object { $_.installerKind -cin @('Exe', 'Msi') })
$sidecarNames = @($installerAssets | ForEach-Object { $_.fileName + '.sha256' })
$sidecarFiles = @($inputFiles | Where-Object { $_.Name -cin $sidecarNames })
if ($sidecarFiles.Count) {
    if ($sidecarFiles.Count -ne $sidecarNames.Count) { throw 'Installer checksum sidecars must include the complete four-file group.' }
    $expectedNames += $sidecarNames
}
$storeBase = "CloudInlet-$($source.version)-win-x64-release-store"
$storeNames = @("$storeBase.msix", "$storeBase.msixupload", "$storeBase-package-info.json")
$storeFiles = @($inputFiles | Where-Object { $_.Name -cin $storeNames })
if ($storeFiles.Count -gt 0) {
    if ($storeFiles.Count -ne $storeNames.Count) { throw 'Store release assets must include the complete MSIX, MSIXUPLOAD and package-info group.' }
    $expectedNames += $storeNames
    $publicNames += "$storeBase.msix"
}
if ($inputFiles.Count -ne $expectedNames.Count -or @($inputFiles | Where-Object { $_.Name -cnotin $expectedNames }).Count) {
    $unexpected = @($inputFiles | Where-Object { $_.Name -cnotin $expectedNames } | ForEach-Object Name) -join ', '
    throw "The release contains an unexpected asset inventory ($($inputFiles.Count) files, expected $($expectedNames.Count)). Unexpected files: $unexpected"
}
foreach ($file in $inputFiles) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -le 0) { throw "Invalid release payload: $($file.Name). Require regular nonempty files." }
}

if ($sidecarFiles.Count) {
    foreach ($installer in $installerAssets) {
        $sidecar = Get-Content -LiteralPath (Join-Path $assetsRoot ($installer.fileName + '.sha256')) -Raw
        if ($sidecar.TrimEnd([char[]]"`r`n") -cne ($installer.sha256 + '  ' + $installer.fileName)) {
            throw "Installer checksum sidecar does not match its package: $($installer.fileName).sha256"
        }
    }
}
if ($storeFiles.Count) {
    $store = Get-Content -LiteralPath (Join-Path $assetsRoot "$storeBase-package-info.json") -Raw | ConvertFrom-Json
    if ($store.version -cne $source.version -or $store.sourceRevision -cne $ExpectedRevision -or
        $store.localValidationIdentity -cne $false -or $store.msix -cne "$storeBase.msix" -or $store.msixupload -cne "$storeBase.msixupload") {
        throw 'Store package metadata must describe the production packages from this exact version and source revision.'
    }
}

# Rendering validates the curated version/range and public checksums before any
# tag or GitHub mutation. Never fall back to generic or commit-by-commit notes.
& (Join-Path $PSScriptRoot 'generate-release-notes.ps1') -Version $source.version -AssetDirectory $assetsRoot -RepositoryRoot $root -ExpectedRevision $ExpectedRevision -OutputPath $notesPath | Out-Null
$notes = Get-Content -LiteralPath $notesPath -Raw
$files = @($publicNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $assetsRoot $_) } | Sort-Object Name)

function Invoke-Gh([string[]]$Arguments) {
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0]) $($Arguments[1])" }
    return $output
}
$tag = $source.tag
$tagExists = @(& git -C $root tag --list $tag).Count -eq 1
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the release tag.' }
if ($tagExists) {
    $tagRevision = (& git -C $root rev-list -n 1 $tag).Trim()
    if ($LASTEXITCODE -ne 0 -or $tagRevision -cne $ExpectedRevision) { throw 'An immutable release tag already points to a different commit.' }
} else {
    & git -C $root -c user.name='github-actions[bot]' -c user.email='41898282+github-actions[bot]@users.noreply.github.com' tag -a $tag $ExpectedRevision -m "CloudInlet $($source.version)" -m 'Verified Debug and Release Windows installers and update manifest.'
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the annotated release tag.' }
    & git -C $root push origin "refs/tags/$tag"
    if ($LASTEXITCODE -ne 0) { throw 'Could not push the annotated release tag.' }
}
$lookup = & gh api "repos/$Repository/releases/tags/$tag" 2>$null
$lookupExit = $LASTEXITCODE
if ($lookupExit -eq 0) { $release = $lookup | ConvertFrom-Json }
else {
    # Distinguish a missing release from authentication, throttling, or service failures.
    $all = Invoke-Gh -Arguments @('api', "repos/$Repository/releases?per_page=100", '--paginate', '--slurp')
    $matching = @($all | ConvertFrom-Json | ForEach-Object { $_ } | ForEach-Object { $_ } | Where-Object { $_.tag_name -ceq $tag })
    if ($matching.Count -gt 1) { throw 'Duplicate release tags are not allowed.' }
    if ($matching.Count -eq 1) { $release = $matching[0] }
    else {
    $request = Join-Path $assetsRoot ('.release-create-' + [Guid]::NewGuid().ToString('N') + '.json')
    @{ tag_name = $tag; target_commitish = $ExpectedRevision; name = "CloudInlet $($source.version)"; body = $notes; draft = $true; prerelease = $false; generate_release_notes = $false } | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8NoBOM
    try {
        # Creation returns the authoritative release ID. A newly created draft
        # may not appear immediately in tag/list lookups on the hosted runner.
        $release = Invoke-Gh -Arguments @('api', "repos/$Repository/releases", '--method', 'POST', '--input', $request) | ConvertFrom-Json
    } finally { if (Test-Path -LiteralPath $request) { Remove-Item -LiteralPath $request } }
    if (!$release.draft) { throw 'Release creation did not return an unpublished draft.' }
    }
}
if ($release.tag_name -cne $tag -or [long]$release.id -lt 1) { throw 'The release identity does not match its immutable tag.' }
if ($release.body -cne $notes) { throw 'The existing release notes differ from the validated source. Investigate the draft or publish a new version; do not silently replace release content.' }
$remoteNames = @($release.assets | ForEach-Object { $_.name })
if (@($remoteNames | Where-Object { $_ -cnotin $files.Name }).Count -or (!$release.draft -and $remoteNames.Count -ne $files.Count)) { throw 'The existing release has a different immutable asset inventory.' }
foreach ($file in $files) {
    $existing = @($release.assets | Where-Object { $_.name -ceq $file.Name })
    if ($existing.Count -gt 1) { throw 'Duplicate release asset names are not allowed.' }
    # GitHub documents that a failed upload can retain a zero-byte starter.
    # Only that incomplete record on this unpublished draft can be removed.
    if ($existing.Count -eq 1 -and $release.draft -and $existing[0].state -ceq 'starter' -and $existing[0].size -eq 0 -and [long]$existing[0].id -gt 0) {
        Invoke-Gh -Arguments @('api', "repos/$Repository/releases/assets/$($existing[0].id)", '--method', 'DELETE') | Out-Null
        $existing = @()
    }
    if ($existing.Count) {
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing[0].size -ne $file.Length -or $existing[0].digest -cne $digest) { throw "An existing release asset differs: $($file.Name). Bump the version; immutable assets are never replaced." }
    } elseif (!$release.draft) { throw 'A published release cannot receive new or replacement assets.' }
    else {
        $name = [Uri]::EscapeDataString($file.Name)
        Invoke-Gh -Arguments @('api', "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$name", '--method', 'POST', '--header', 'Content-Type: application/octet-stream', '--input', $file.FullName) | Out-Null
    }
}
$ready = Invoke-Gh -Arguments @('api', "repos/$Repository/releases/$($release.id)") | ConvertFrom-Json
if ($ready.tag_name -cne $tag -or $ready.id -ne $release.id) { throw 'The verified release identity changed.' }
if ($ready.body -cne $notes) { throw 'The release notes changed before publication.' }
if (@($ready.assets).Count -ne $files.Count -or @($ready.assets | Where-Object { $_.name -cnotin $files.Name }).Count) { throw 'Draft release inventory is incomplete or contains unexpected assets.' }
foreach ($file in $files) {
    $remote = @($ready.assets | Where-Object { $_.name -ceq $file.Name })
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($remote.Count -ne 1 -or $remote[0].size -ne $file.Length -or $remote[0].digest -cne $digest -or $remote[0].state -cne 'uploaded') { throw 'GitHub upload length or SHA-256 validation failed.' }
}
if ($ready.draft) {
    & git -C $root fetch origin main
    if ($LASTEXITCODE -ne 0 -or (& git -C $root rev-parse origin/main).Trim() -cne $ExpectedRevision) { throw 'Main changed before publication. The verified draft remains unpublished.' }
    $published = Invoke-Gh -Arguments @('api', "repos/$Repository/releases/$($release.id)", '--method', 'PATCH', '-F', 'draft=false', '-f', 'make_latest=true') | ConvertFrom-Json
    if ($published.id -ne $release.id -or $published.tag_name -cne $tag -or $published.draft -or !$published.published_at) { throw 'The verified release was not published.' }
}
Write-Output "https://github.com/$Repository/releases/tag/$tag"
