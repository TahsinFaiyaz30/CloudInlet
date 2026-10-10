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
& (Join-Path $PSScriptRoot 'generate-update-manifest.ps1') -Version $source.version -AssetDirectory $assetsRoot -Repository $Repository | Out-Null
$files = @(Get-ChildItem -LiteralPath $assetsRoot -File | Where-Object { $_.Name -like "CloudInlet-$($source.version)-*" -or $_.Name -in @('updates-v2.json', 'SHA256SUMS.txt', 'release-validation.json') } | Sort-Object Name)
if ($files.Count -ne 9) { throw 'The release must contain exactly six canonical packages, schema 2, checksums and validation.' }
foreach ($file in $files) { if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked release payloads are not allowed.' } }

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
    $notes = @"
CloudInlet $($source.version) preserves existing accounts, backup settings, Files On-Demand roots, transfer recovery, and installation choices. Direct OneDrive and Backblaze B2 transfers remain available.

Choose the CloudInlet asset with the same build flavor (Release or Debug) and installer type (EXE or MSI) already installed. Portable ZIP builds are also included. CloudBay users must first take the existing 1.1.2 bridge update; later releases use only CloudInlet packages. The application validates installer length and SHA-256 before applying a matching update.

Signing status is recorded in release-validation.json. Unsigned installers can show a Windows security prompt. Microsoft Store packages, when configured, are submitted separately through Partner Center.
"@
    $request = Join-Path $assetsRoot ('.release-create-' + [Guid]::NewGuid().ToString('N') + '.json')
    @{ tag_name = $tag; target_commitish = $ExpectedRevision; name = "CloudInlet $($source.version)"; body = $notes; draft = $true; prerelease = $false; generate_release_notes = $true } | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8NoBOM
    try {
        # Creation returns the authoritative release ID. A newly created draft
        # may not appear immediately in tag/list lookups on the hosted runner.
        $release = Invoke-Gh -Arguments @('api', "repos/$Repository/releases", '--method', 'POST', '--input', $request) | ConvertFrom-Json
    } finally { if (Test-Path -LiteralPath $request) { Remove-Item -LiteralPath $request } }
    if (!$release.draft) { throw 'Release creation did not return an unpublished draft.' }
    }
}
if ($release.tag_name -cne $tag -or [long]$release.id -lt 1) { throw 'The release identity does not match its immutable tag.' }
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
