#Requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateSet('b2','onedrive')][string]$Connector,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$ToolRoot
)
$ErrorActionPreference = 'Stop'
$repo = 'TahsinFaiyaz30/CloudInlet-Connectors'
$api = "repos/$repo"
$tool = (Resolve-Path (Join-Path $ToolRoot 'tools/CloudInlet.ConnectorTool/bin/Release/net8.0-windows/CloudInlet.ConnectorTool.dll')).Path
$pin = (Resolve-Path (Join-Path $ToolRoot 'connectors/official-source.json')).Path
$inputPath = (Resolve-Path $InputDirectory).Path
$manifest = Get-Content -Raw -LiteralPath (Join-Path $inputPath 'connector.json') | ConvertFrom-Json
if ($manifest.id -cne $Connector -or $manifest.version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Invalid release identity.' }
$tag = "$Connector-v$($manifest.version)"
$filename = "$Connector-$($manifest.version).cipack"
$work = Join-Path ([IO.Path]::GetTempPath()) ("cloudinlet-publish-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$pack = Join-Path $work $filename
function Invoke-GitHub { & gh @args; if ($LASTEXITCODE) { throw "GitHub $($args[0]) failed." } }
function Invoke-Tool { & dotnet $tool @args | Out-Host; if ($LASTEXITCODE) { throw "Connector tool $($args[0]) failed." } }
function Save-Public([uri]$Uri, [string]$Path) {
    Invoke-WebRequest -Uri $Uri -OutFile $Path -MaximumRetryCount 5 -RetryIntervalSec 3
}
function Write-ReleaseText([string]$File, [string[]]$Lines) {
    [IO.File]::WriteAllText((Join-Path $work $File), ($Lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
}
function Hash([string]$File) { (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-Catalog {
    $tree = Invoke-GitHub api "$api/git/trees/main" | ConvertFrom-Json
    $entry = @($tree.tree | Where-Object path -ceq 'catalog.json')
    if ($entry.Count -eq 0) { return @{ file='-'; sha=$null } }
    if ($entry.Count -ne 1 -or $entry[0].type -cne 'blob' -or $entry[0].size -gt 4194304) { throw 'Invalid catalog object.' }
    $blob = Invoke-GitHub api "$api/git/blobs/$($entry[0].sha)" | ConvertFrom-Json
    $file = Join-Path $work ('prior-' + $entry[0].sha + '.json')
    [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($blob.content))
    return @{ file=$file; sha=$entry[0].sha }
}
# Repository admins enable immutability at provisioning. The contents-only token verifies each published release below.
# Listing every page distinguishes an absent tag from a failed request, and includes drafts.
$tags = @(Invoke-GitHub api --paginate "$api/releases?per_page=100" --jq '.[].tag_name')
$release = if ($tag -cin $tags) { Invoke-GitHub release view $tag --repo $repo --json isDraft,assets | ConvertFrom-Json }
$assetNames = @($release.assets | Where-Object { $_ } | ForEach-Object name)
$expected = @($filename, 'release-notes.md', 'SHA256SUMS.txt')
if (@($assetNames | Where-Object { $_ -cnotin $expected }).Count) { throw 'Existing release contains unexpected assets.' }
if ($filename -cin $assetNames) {
    Invoke-GitHub release download $tag --repo $repo --pattern $filename --dir $work
    Invoke-Tool release-verify $Connector $SourceCommit $inputPath $pin $pack
} else {
    if ($release -and !$release.isDraft) { throw 'Published release has no matching package.' }
    Invoke-Tool release-pack $Connector $SourceCommit $inputPath $pin $pack
}
$prior = Get-Catalog
$prospective = Join-Path $work 'prospective.json'
Invoke-Tool release-catalog $pin $prior.file $pack $prospective
Write-ReleaseText 'release-notes.md' @(
    "$($manifest.name) $($manifest.version)", '', $manifest.releaseNotes, '',
    "Install from **Connectors** in a compatible CloudInlet build. SDK range [$($manifest.sdk.min), $($manifest.sdk.max)).",
    '', "[Exact connector source](https://github.com/TahsinFaiyaz30/CloudInlet/tree/$SourceCommit)"
)
Write-ReleaseText 'SHA256SUMS.txt' @("$(Hash $pack)  $filename", "$(Hash (Join-Path $work 'release-notes.md'))  release-notes.md")
if (!$release) {
    $target = Invoke-GitHub api "$api/commits/main" --jq .sha
    Invoke-GitHub release create $tag --repo $repo --target $target --draft --title "$($manifest.name) $($manifest.version)" --notes-file (Join-Path $work 'release-notes.md')
}
foreach ($name in $expected) {
    $file = Join-Path $work $name
    if ($name -cin $assetNames) {
        $asset = $release.assets | Where-Object name -ceq $name
        if ($asset.digest -ine ("sha256:" + (Hash $file)) -or $asset.size -ne (Get-Item -LiteralPath $file).Length) { throw "Existing asset differs: $name" }
    } else {
        if ($release -and !$release.isDraft) { throw "Immutable release is incomplete: $name" }
        Invoke-GitHub release upload $tag $file --repo $repo
    }
}
if (!$release -or $release.isDraft) { Invoke-GitHub release edit $tag --repo $repo --draft=false --latest=false }
$published = Invoke-GitHub api "$api/releases/tags/$tag" | ConvertFrom-Json
if (!$published.immutable -or $published.draft -or $published.assets.Count -ne $expected.Count) { throw 'Release immutability or inventory could not be verified.' }
foreach ($name in $expected) {
    $asset = $published.assets | Where-Object name -ceq $name
    $url = "https://github.com/$repo/releases/download/$tag/$name"
    if ($asset.browser_download_url -cne $url) { throw 'Unexpected release asset URL.' }
    $publicFile = Join-Path $work ("download-" + $name)
    Save-Public $url $publicFile
    if ((Hash $publicFile) -ne (Hash (Join-Path $work $name)) -or $asset.digest -ine ("sha256:" + (Hash $publicFile))) { throw "Public release bytes differ: $name" }
    if ($name -eq $filename) { Invoke-Tool release-verify $Connector $SourceCommit $inputPath $pin $publicFile }
}
# Optimistic concurrency also protects against a separate maintainer changing the catalog.
for ($attempt = 1; $attempt -le 4; $attempt++) {
    $prior = Get-Catalog
    $next = Join-Path $work "catalog-$attempt.json"
    Invoke-Tool release-catalog $pin $prior.file $pack $next
    if ($prior.sha -and (Hash $prior.file) -eq (Hash $next)) { break }
    $body = @{ message="List $($manifest.name) $($manifest.version)"; branch='main'; content=[Convert]::ToBase64String([IO.File]::ReadAllBytes($next)) }
    if ($prior.sha) { $body.sha = $prior.sha }
    $body | ConvertTo-Json -Compress | gh api --method PUT "$api/contents/catalog.json" --input - --silent
    if (!$LASTEXITCODE) { break }
    if ($attempt -eq 4) { throw 'Catalog publication failed. Retry the same immutable release to finish.' }
}
$catalogUrl = (Get-Content -Raw -LiteralPath $pin | ConvertFrom-Json).location
$head = Invoke-GitHub api "$api/commits/main" --jq .sha
$publicCatalog = Join-Path $work 'public-catalog.json'
Save-Public ("${catalogUrl}?revision=$head") $publicCatalog
# An idempotent merge authenticates the public envelope and confirms the published package hash.
$checked = Join-Path $work 'verified-catalog.json'
Invoke-Tool release-catalog $pin $publicCatalog $pack $checked
if ((Hash $publicCatalog) -ne (Hash $checked)) { throw 'The public catalog does not contain the immutable package.' }
Write-Output "Verified $($published.html_url) and $catalogUrl"
