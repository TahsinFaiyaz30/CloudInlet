param(
    [Parameter(Mandatory)][string]$Version,
    [string]$AssetDirectory,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [Parameter(Mandatory)][string]$ExpectedRevision,
    [string]$OutputPath,
    [string]$NotesPath,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$repository = 'TahsinFaiyaz30/CloudInlet'
$repositoryUrl = "https://github.com/$repository"
$versionPattern = '(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)'
if ($Version -cnotmatch "^$versionPattern`$") { throw 'Invalid release notes version.' }
$parsedVersion = [Version]$Version
if ($parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255 -or $parsedVersion.Build -gt 65535) { throw 'Release notes version exceeds Windows Installer limits.' }
if ($ExpectedRevision -cnotmatch '^[0-9a-f]{40}$') { throw 'Release notes require the full expected source revision.' }
$root = [IO.Path]::GetFullPath($RepositoryRoot)
if (!$NotesPath) { $NotesPath = Join-Path $root "docs/releases/$Version.json" }
if (!(Test-Path -LiteralPath $NotesPath -PathType Leaf)) { throw "Curated release notes are missing: docs/releases/$Version.json. Review the release changes before publishing." }
$notes = Get-Content -LiteralPath $NotesPath -Raw | ConvertFrom-Json

function Assert-Text($Value, [string]$Field) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or $Value -match '[\r\n]' -or
        $Value -match '(?i)\b(TODO|TBD)\b' -or $Value -match '(?i)^\s*placeholder\s*$') { throw "Release notes $Field must contain finished, single-line text." }
}
function Get-Git([string[]]$Arguments) {
    $result = @(& git -C $root @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "Could not validate release notes Git source: $($Arguments[0])." }
    return $result
}

if ($notes.schemaVersion -ne 1 -or $notes.version -cne $Version) { throw 'Curated release notes schema or version does not match this release.' }
Assert-Text $notes.summary 'summary'
if ($notes.previousTag -isnot [string] -or $notes.previousTag -cnotmatch "^v$versionPattern`$" -or
    [Version]$notes.previousTag.Substring(1) -ge $parsedVersion) { throw 'Release notes previousTag must identify an earlier canonical version.' }
$resolvedRevision = @(Get-Git @('rev-parse', '--verify', "$ExpectedRevision^{commit}"))
if ($resolvedRevision.Count -ne 1 -or $resolvedRevision[0] -cne $ExpectedRevision) { throw 'Release notes source revision could not be resolved exactly.' }
$previous = @(Get-Git @('rev-parse', '--verify', "refs/tags/$($notes.previousTag)^{commit}"))
& git -C $root merge-base --is-ancestor $previous[0] $ExpectedRevision 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Release notes previousTag must be an ancestor of the expected source revision.' }
$range = @(Get-Git @('rev-list', "$($previous[0])..$ExpectedRevision"))
if (!$range.Count) { throw 'Release notes must describe changes after the previous release.' }
$categoryTitles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (@($notes.categories).Count -lt 1) { throw 'Curated release notes need at least one category.' }
foreach ($category in $notes.categories) {
    Assert-Text $category.title 'category title'
    if (!$categoryTitles.Add($category.title)) { throw 'Release notes category titles must be unique.' }
    if ($null -ne $category.collapsed -and $category.collapsed -isnot [bool]) { throw 'Release notes collapsed must be a boolean.' }
    if (@($category.items).Count -lt 1) { throw 'Release notes categories cannot be empty.' }
    foreach ($item in $category.items) {
        Assert-Text $item.text 'change description'
        $references = @($item.commits)
        if (!$references.Count) { throw 'Every release note change needs a commit link.' }
        if (@($references | Select-Object -Unique).Count -ne $references.Count) { throw 'A release note change cannot repeat a commit link.' }
        foreach ($commit in $references) {
            if ($commit -isnot [string] -or $commit -cnotmatch '^[0-9a-f]{40}$' -or $commit -cnotin $range) {
                throw 'Release note commit links must be full revisions within previousTag..ExpectedRevision.'
            }
        }
    }
}
if (@($notes.installationNotes).Count -lt 1 -or @($notes.validation.items).Count -lt 1) { throw 'Curated release notes require installation notes and validation evidence.' }
foreach ($item in @($notes.installationNotes) + @($notes.validation.items)) { Assert-Text $item 'installation or validation detail' }
foreach ($link in @($notes.validation.links)) {
    if ($null -eq $link) { continue }
    Assert-Text $link.label 'evidence link label'
    $uri = $null
    if (![Uri]::TryCreate($link.url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cne 'https' -or $uri.UserInfo) { throw 'Release note evidence links must be absolute HTTPS URLs without credentials.' }
}
if ($null -ne $notes.storeStatus) { Assert-Text $notes.storeStatus 'Store status' }
if ($ValidateOnly) {
    Write-Output "Validated curated release notes for $Version."
    return
}
if (!$AssetDirectory -or !$OutputPath) { throw 'Rendering release notes requires AssetDirectory and OutputPath.' }
$revisionVersion = (Get-Git @('show', "${ExpectedRevision}:version.json")) -join "`n" | ConvertFrom-Json
if ($revisionVersion.version -cne $Version) { throw 'The expected source revision has a different release version.' }
$assetsRoot = [IO.Path]::GetFullPath($AssetDirectory)
if (!(Test-Path -LiteralPath $assetsRoot -PathType Container)) { throw 'Release note asset directory does not exist.' }
for ($ancestor = [IO.DirectoryInfo]$assetsRoot; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release notes cannot read assets through a linked directory.' }
}
$hashes = @{}
function Read-Payload([string]$Name) {
    $path = Join-Path $assetsRoot $Name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release note payload is missing: $Name" }
    $file = Get-Item -LiteralPath $path
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -le 0) { throw "Release note payload must be a regular, nonempty file: $Name" }
    $hashes[$Name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $file
}
$manifestFile = Read-Payload 'updates-v2.json'
$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or $manifest.version -cne $Version -or $manifest.tag -cne "v$Version" -or
    $manifest.repository -cne $repository -or @($manifest.assets).Count -ne 6) { throw 'Release notes require the canonical six-variant schema-2 update manifest.' }
$links = [ordered]@{}
$publicNames = [Collections.Generic.List[string]]::new()
foreach ($flavor in @('Release', 'Debug')) {
    foreach ($kind in @('Exe', 'Msi', 'Portable')) {
        $suffix = switch ($kind) { 'Exe' { 'setup.exe' }; 'Msi' { 'setup.msi' }; 'Portable' { 'portable.zip' } }
        $name = "CloudInlet-$Version-win-x64-$($flavor.ToLowerInvariant())-$suffix"
        $entries = @($manifest.assets | Where-Object { $_.buildFlavor -ceq $flavor -and $_.installerKind -ceq $kind })
        if ($entries.Count -ne 1 -or $entries[0].architecture -cne 'x64' -or $entries[0].fileName -cne $name) { throw 'Release notes manifest has a missing, duplicated, or unexpected package variant.' }
        $file = Read-Payload $name
        if ($entries[0].size -ne $file.Length -or $entries[0].sha256 -cne $hashes[$name]) { throw "Release notes manifest size or SHA-256 does not match final bytes: $name" }
        $publicNames.Add($name)
        $linkKind = if ($kind -ceq 'Portable') { 'zip' } else { $kind.ToLowerInvariant() }
        $links["$($flavor.ToLowerInvariant())-$linkKind"] = $name
    }
}
$storeName = "CloudInlet-$Version-win-x64-release-store.msix"
$hasStore = Test-Path -LiteralPath (Join-Path $assetsRoot $storeName) -PathType Leaf
if ($hasStore) {
    Read-Payload $storeName | Out-Null
    $publicNames.Add($storeName)
    $links['store-msix'] = $storeName
    $storeInfoFile = Read-Payload "CloudInlet-$Version-win-x64-release-store-package-info.json"
    $storeInfo = Get-Content -LiteralPath $storeInfoFile.FullName -Raw | ConvertFrom-Json
    if ($storeInfo.version -cne $Version -or $storeInfo.sourceRevision -cne $ExpectedRevision -or
        $storeInfo.localValidationIdentity -cne $false -or $storeInfo.msix -cne $storeName) { throw 'Release notes require production Store metadata from this exact version and source.' }
}
$publicNames.Add('updates-v2.json')
$links['update-feed'] = 'updates-v2.json'
$links['checksums'] = 'SHA256SUMS.txt'
$checksumFile = Read-Payload 'SHA256SUMS.txt'
$checksumEntries = @{}
foreach ($line in @(Get-Content -LiteralPath $checksumFile.FullName)) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([^\\/]+)$') { throw 'SHA256SUMS.txt has an invalid checksum record.' }
    $digest = $Matches[1]; $name = $Matches[2]
    if ($checksumEntries.ContainsKey($name) -or $name -cnotin $publicNames -or $digest -cne $hashes[$name]) { throw 'SHA256SUMS.txt must list each public payload and update feed exactly once with its final hash.' }
    $checksumEntries[$name] = $digest
}
if ($checksumEntries.Count -ne $publicNames.Count) { throw 'SHA256SUMS.txt does not cover the exact public release inventory.' }
$evidenceFile = Read-Payload 'release-validation.json'
$evidence = Get-Content -LiteralPath $evidenceFile.FullName -Raw | ConvertFrom-Json
if ($evidence.version -cne $Version -or $evidence.sourceRevision -cne $ExpectedRevision) { throw 'Release note validation evidence belongs to another version or source.' }
$buildSummaries = [Collections.Generic.List[string]]::new()
$signingSummaries = [Collections.Generic.List[string]]::new()
foreach ($flavor in @('Release', 'Debug')) {
    $gates = @($evidence.configurations | Where-Object { $_.configuration -ceq $flavor })
    if ($gates.Count -ne 1 -or $gates[0].buildSucceeded -ne $true -or $gates[0].testsPassed -lt 1 -or
        $gates[0].testsFailed -ne 0 -or $gates[0].testsSkipped -ne 0) { throw "Release notes require passed $flavor build and test evidence." }
    $buildSummaries.Add("$flavor $($gates[0].testsPassed) tests passed")
    $signedText = if ($gates[0].signed -isnot [bool]) { 'signing status not recorded' } elseif ($gates[0].signed) { 'signed' } else { 'unsigned' }
    $signingSummaries.Add("$flavor EXE/MSI: $signedText")
}

$lines = [Collections.Generic.List[string]]::new()
function Add([string]$Line = '') { $lines.Add($Line) }
function Add-TableRow([string]$Link, [string]$Type) {
    $name = $links[$Link]
    Add ('<tr><td nowrap><a href="{0}/releases/download/v{1}/{2}">{2}</a></td><td align="center">{3}</td><td>' -f $repositoryUrl, $Version, $name, $Type)
    # A fenced code block inside an HTML table gets GitHub's native copy control.
    Add
    Add '```'
    Add $hashes[$name]
    Add '```'
    Add
    Add '</td></tr>'
}
function Add-TableHeader { Add '<table>'; Add '<thead><tr><th align="left">File</th><th align="center">Type</th><th align="left">SHA-256</th></tr></thead>'; Add '<tbody>' }
function Add-TableEnd { Add '</tbody>'; Add '</table>' }
# GitHub already renders the release name above the body.
Add $notes.summary
Add
Add '## Downloads'
Add
Add '**Windows x64** · EXE recommended'
Add
$badges = @(foreach ($kind in @('EXE', 'MSI', 'ZIP')) { "[![Download $kind](https://img.shields.io/badge/Download-$kind-0078D4?style=for-the-badge)][release-$($kind.ToLowerInvariant())]" })
if ($hasStore) { $badges += '[![Download MSIX](https://img.shields.io/badge/Download-MSIX-666666?style=for-the-badge)][store-msix]' }
Add ($badges -join ' ')
Add
foreach ($flavor in @('Release', 'Debug')) {
    Add "### $flavor (x64)"
    Add
    if ($flavor -ceq 'Release') { Add 'Recommended for everyday use.' } else { Add 'Diagnostic builds with separate app state.' }
    Add
    Add-TableHeader
    $key = $flavor.ToLowerInvariant()
    Add-TableRow "$key-exe" 'EXE'
    Add-TableRow "$key-msi" 'MSI'
    Add-TableRow "$key-zip" 'ZIP'
    if ($flavor -ceq 'Release' -and $hasStore) {
        Add-TableRow 'store-msix' 'MSIX'
    }
    Add-TableEnd
    if ($flavor -ceq 'Release' -and $hasStore) {
        Add
        $signing = if ($storeInfo.signed -isnot [bool]) { 'signing status not recorded' } elseif ($storeInfo.signed) { 'signed' } else { 'unsigned' }
        $status = if ($notes.storeStatus) { '; ' + $notes.storeStatus.TrimEnd('.') } else { '' }
        Add "*Store package: $signing$status.*"
    }
    Add
}
Add '### Metadata'
Add
Add-TableHeader
Add-TableRow 'update-feed' 'JSON'
Add-TableRow 'checksums' 'TXT'
Add-TableEnd
Add
Add '<details>'
Add '<summary>Verify download (SHA-256)</summary>'
Add
Add '1. Copy the SHA-256 from the table for your file, or download [SHA256SUMS.txt][checksums].'
Add '2. Open PowerShell in the folder containing your download and run:'
Add
Add '   ```powershell'
Add ("   (Get-FileHash -LiteralPath '.\CloudInlet-$Version-win-x64-release-setup.exe' -Algorithm SHA256).Hash")
Add '   ```'
Add
Add '3. Compare the result with the hash for the **same file and Release/Debug build**. All 64 characters must match; uppercase/lowercase does not matter.'
Add
Add 'For another package, replace the filename in the command. If the hashes differ, do not run the file; download it again.'
Add
Add '</details>'
Add
Add '## Changelog'
Add
foreach ($category in $notes.categories) {
    if ($category.collapsed) { Add '<details>'; Add "<summary>$($category.title)</summary>" } else { Add "### $($category.title)" }
    Add
    foreach ($item in $category.items) {
        $commitLinks = @($item.commits | ForEach-Object { "[$($_.Substring(0, 7))]($repositoryUrl/commit/$_)" }) -join ', '
        Add "- $($item.text) ($commitLinks)"
    }
    Add
    if ($category.collapsed) { Add '</details>'; Add }
}
Add '<details>'
Add '<summary>Installation notes</summary>'
Add
foreach ($item in $notes.installationNotes) { Add "- $item" }
Add ('- **Signing:** ' + ($signingSummaries -join '; ') + '.')
Add
Add "[Installation guide]($repositoryUrl/blob/v$Version/docs/INSTALLERS.md) · [Data retention]($repositoryUrl/blob/v$Version/docs/UNINSTALL.md)"
Add
Add '</details>'
Add
Add '<details>'
Add '<summary>Validation</summary>'
Add
Add ('- Build validation: ' + ($buildSummaries -join '; ') + '; zero failures or skips.')
foreach ($item in $notes.validation.items) { Add "- $item" }
$evidenceLinks = @($notes.validation.links | Where-Object { $null -ne $_ })
if ($evidenceLinks.Count) {
    Add
    Add (@($evidenceLinks | ForEach-Object { "[$($_.label)]($($_.url))" }) -join ' · ')
}
Add
Add '</details>'
Add
Add '---'
Add
Add "[Full changelog: $($notes.previousTag) → v$Version]($repositoryUrl/compare/$($notes.previousTag)...v$Version)"
Add
foreach ($key in $links.Keys) { Add "[${key}]: $repositoryUrl/releases/download/v$Version/$($links[$key])" }
# Write only after every source, note, evidence, and public checksum check passes.
$destination = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($destination) -ine '.md') { throw 'Release notes output must be a Markdown file, never a release payload.' }
$outputDirectory = Split-Path -Parent $destination
if (!(Test-Path -LiteralPath $outputDirectory -PathType Container)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
$lines | Set-Content -LiteralPath $destination -Encoding utf8NoBOM
Write-Output $destination
