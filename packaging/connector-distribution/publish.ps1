#Requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9.-]{0,95}$')][string]$Connector,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$Version,
    [string]$InputDirectory,
    [Parameter(Mandatory)][string]$ToolRoot,
    [switch]$VerifyHistorical
)
$ErrorActionPreference = 'Stop'
$central = 'TahsinFaiyaz30/CloudInlet-Connectors'
$tool = (Resolve-Path (Join-Path $ToolRoot 'tools/CloudInlet.ConnectorTool/bin/Release/net8.0-windows/CloudInlet.ConnectorTool.dll')).Path
$pin = (Resolve-Path (Join-Path $ToolRoot 'connectors/official-source.json')).Path
$route = & dotnet $tool release-route $Connector $Version | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Invalid official release route.' }
$repo = $route.repository
$tag = $route.tag
$filename = $route.filename
$api = "repos/$repo"
$work = Join-Path ([IO.Path]::GetTempPath()) ("cloudinlet-publish-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$pack = Join-Path $work $filename
function Invoke-GitHub { & gh @args; if ($LASTEXITCODE) { throw "GitHub $($args[0]) failed." } }
function Invoke-Provider {
    $catalogToken = $env:GH_TOKEN
    try {
        $env:GH_TOKEN = $env:CONNECTOR_RELEASE_TOKEN
        Invoke-GitHub @args
    } finally { $env:GH_TOKEN = $catalogToken }
}
function Invoke-Tool { & dotnet $tool @args | Out-Host; if ($LASTEXITCODE) { throw "Connector tool $($args[0]) failed." } }
function Save-Public([uri]$Uri, [string]$Path) {
    Invoke-WebRequest -Uri $Uri -OutFile $Path -MaximumRetryCount 5 -RetryIntervalSec 3
}
function Write-ReleaseText([string]$File, [string[]]$Lines) {
    [IO.File]::WriteAllText((Join-Path $work $File), ($Lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
}
function Hash([string]$File) { (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-Catalog {
    $head = Invoke-GitHub api "repos/$central/git/ref/heads/main" --jq .object.sha
    $tree = Invoke-GitHub api "repos/$central/git/trees/$head" | ConvertFrom-Json
    $entry = $tree.tree | Where-Object path -ceq 'catalog.json'
    if ($entry.type -cne 'blob' -or $entry.size -gt 4194304) { throw 'The existing central signed catalog is missing or invalid.' }
    $blob = Invoke-GitHub api "repos/$central/git/blobs/$($entry.sha)" | ConvertFrom-Json
    $file = Join-Path $work ('prior-' + $entry.sha + '.json')
    [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($blob.content))
    return @{ file=$file; head=$head; readme=($tree.tree | Where-Object path -ceq 'README.md').sha }
}
$catalogUrl = (Get-Content -Raw -LiteralPath $pin | ConvertFrom-Json).location
$expected = @($filename, 'release-notes.md', 'SHA256SUMS.txt')
if ($route.historical) {
    $publicCatalog = Join-Path $work 'historical-catalog.json'
    Save-Public $catalogUrl $publicCatalog
    Save-Public $route.originalDownload $pack
    Invoke-Tool release-check-entry $pin $publicCatalog $Connector $Version $SourceCommit $pack
    if ($VerifyHistorical) { return }
}
if ($VerifyHistorical -or $repo -ceq $central -or !$env:CONNECTOR_RELEASE_TOKEN -or !$env:GH_TOKEN) { throw 'Missing scoped publisher access or invalid release destination.' }
if ($route.historical) {
    $original = Invoke-GitHub api "repos/$central/releases/tags/$Connector-v$Version" | ConvertFrom-Json
    if (!$original.immutable -or $original.draft -or $original.prerelease -or $original.assets.Count -ne $expected.Count) { throw 'Invalid original immutable release.' }
    foreach ($name in $expected) {
        $asset = $original.assets | Where-Object name -ceq $name
        $url = "https://github.com/$central/releases/download/$Connector-v$Version/$name"
        if ($asset.browser_download_url -cne $url) { throw "Missing original release asset: $name" }
        $file = Join-Path $work $name
        if ($name -cne $filename) { Save-Public $url $file }
        if ($asset.digest -ine ("sha256:" + (Hash $file)) -or $asset.size -ne (Get-Item -LiteralPath $file).Length) { throw "Original release bytes differ: $name" }
    }
} else {
    $inputPath = (Resolve-Path $InputDirectory).Path
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $inputPath 'connector.json') | ConvertFrom-Json
    if ($manifest.id -cne $Connector -or $manifest.version -cne $Version) { throw 'The release input differs from the selected source manifest.' }
}
$guideRevision = (git -C $ToolRoot rev-parse HEAD)
if ($LASTEXITCODE -or $guideRevision -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Missing reviewed publisher revision.' }
# Repository admins enable immutability at provisioning. The contents-only token verifies each published release below.
# Listing every page distinguishes an absent tag from a failed request, and includes drafts.
$tags = @(Invoke-Provider api --paginate "$api/releases?per_page=100" --jq '.[].tag_name')
$release = if ($tag -cin $tags) { Invoke-Provider release view $tag --repo $repo --json 'isDraft,assets,name' | ConvertFrom-Json }
if ($release -and $release.name -cne $route.title) { throw 'Existing release title differs from the reviewed connector identity.' }
$assetNames = @($release.assets | Where-Object { $_ } | ForEach-Object name)
if (@($assetNames | Where-Object { $_ -cnotin $expected }).Count) { throw 'Existing release contains unexpected assets.' }
if (!$route.historical) {
    if ($filename -cin $assetNames) {
        Invoke-Provider release download $tag --repo $repo --pattern $filename --dir $work
        Invoke-Tool release-verify $Connector $SourceCommit $inputPath $pin $pack
    } else {
        if ($release -and !$release.isDraft) { throw 'Published release has no matching package.' }
        Invoke-Tool release-pack $Connector $SourceCommit $inputPath $pin $pack
    }
    Write-ReleaseText 'release-notes.md' @(
        $manifest.releaseNotes, '',
        "Install from **Connectors** in a compatible CloudInlet build. SDK range [$($manifest.sdk.min), $($manifest.sdk.max)).",
        '', "[Exact connector source](https://github.com/TahsinFaiyaz30/CloudInlet/tree/$SourceCommit/connectors/$($route.sourceFolder))"
    )
    Write-ReleaseText 'SHA256SUMS.txt' @("$(Hash $pack)  $filename", "$(Hash (Join-Path $work 'release-notes.md'))  release-notes.md")
}
$prior = Get-Catalog
$prospective = Join-Path $work 'prospective.json'
Invoke-Tool release-catalog $pin $prior.file $pack $prospective
Invoke-Tool release-readme $pin $prospective central $guideRevision (Join-Path $work 'prospective.md')
if (!$release) {
    $refs = @(Invoke-Provider api "$api/git/matching-refs/tags/$tag" --jq '.[].ref')
    if ("refs/tags/$tag" -cin $refs) { throw 'The release tag already exists without a corresponding release.' }
    $target = Invoke-Provider api "$api/commits/main" --jq .sha
    Invoke-Provider release create $tag --repo $repo --target $target --draft --title $route.title --notes-file (Join-Path $work 'release-notes.md')
}
foreach ($name in $expected) {
    $file = Join-Path $work $name
    if ($name -cin $assetNames) {
        $asset = $release.assets | Where-Object name -ceq $name
        if ($asset.digest -ine ("sha256:" + (Hash $file)) -or $asset.size -ne (Get-Item -LiteralPath $file).Length) { throw "Existing asset differs: $name" }
    } else {
        if ($release -and !$release.isDraft) { throw "Immutable release is incomplete: $name" }
        Invoke-Provider release upload $tag $file --repo $repo
    }
}
if (!$release -or $release.isDraft) {
    $publicTags = @(Invoke-Provider api --paginate "$api/releases?per_page=100" --jq '.[] | select(.draft == false and .prerelease == false) | .tag_name')
    $newer = @($publicTags | Where-Object { $_ -cmatch '^v\d+\.\d+\.\d+$' -and [version]$_.Substring(1) -gt [version]$Version })
    $latest = ($newer.Count -eq 0).ToString().ToLowerInvariant()
    Invoke-Provider release edit $tag --repo $repo --draft=false "--latest=$latest"
}
$published = Invoke-Provider api "$api/releases/tags/$tag" | ConvertFrom-Json
$notes = [IO.File]::ReadAllText((Join-Path $work 'release-notes.md')).TrimEnd()
if (!$published.immutable -or $published.draft -or $published.prerelease -or $published.assets.Count -ne $expected.Count -or
    $published.name -cne $route.title -or ([string]$published.body).ReplaceLineEndings("`n").TrimEnd() -cne $notes) {
    throw 'Published release identity, notes, immutability or inventory could not be verified.'
}
foreach ($name in $expected) {
    $asset = $published.assets | Where-Object name -ceq $name
    $url = "https://github.com/$repo/releases/download/$tag/$name"
    if ($asset.browser_download_url -cne $url) { throw 'Unexpected release asset URL.' }
    $publicFile = Join-Path $work ("download-" + $name)
    Save-Public $url $publicFile
    if ((Hash $publicFile) -ne (Hash (Join-Path $work $name)) -or $asset.digest -ine ("sha256:" + (Hash $publicFile))) { throw "Public release bytes differ: $name" }
    if ($name -eq $filename) { Invoke-Tool release-check-entry $pin $prospective $Connector $Version $SourceCommit $publicFile }
}
# Commit the signed feed and its directory together against the exact branch tip we read.
$commitMutation = 'mutation($input:CreateCommitOnBranchInput!){createCommitOnBranch(input:$input){commit{oid}}}'
$readme = Join-Path $work 'README.md'
for ($attempt = 1; $attempt -le 4; $attempt++) {
    $prior = Get-Catalog
    $next = Join-Path $work "catalog-$attempt.json"
    Invoke-Tool release-catalog $pin $prior.file $pack $next
    Invoke-Tool release-readme $pin $next central $guideRevision $readme
    $readmeBlob = git hash-object --no-filters -- $readme
    if ($LASTEXITCODE) { throw 'Cannot calculate directory blob identity.' }
    if ((Hash $prior.file) -ceq (Hash $next) -and $readmeBlob -ceq $prior.readme) { break }
    $changes = @(
        @{ path='catalog.json'; contents=[Convert]::ToBase64String([IO.File]::ReadAllBytes($next)) }
        @{ path='README.md'; contents=[Convert]::ToBase64String([IO.File]::ReadAllBytes($readme)) }
    )
    $commit = @{ branch=@{repositoryNameWithOwner=$central; branchName='main'}; expectedHeadOid=$prior.head
        message=@{headline="List $($route.title)"}; fileChanges=@{additions=$changes} }
    @{query=$commitMutation; variables=@{input=$commit}} | ConvertTo-Json -Depth 10 -Compress | gh api graphql --input - --silent
    if (!$LASTEXITCODE) { break }
    if ($attempt -eq 4) { throw 'Catalog publication failed. Retry the same immutable release to finish.' }
}
$head = Invoke-GitHub api "repos/$central/commits/main" --jq .sha
$publicCatalog = Join-Path $work 'public-catalog.json'
Save-Public ("${catalogUrl}?revision=$head") $publicCatalog
Invoke-Tool release-check-entry $pin $publicCatalog $Connector $Version $SourceCommit $pack
# A rerun after the central commit can repair the provider directory without re-signing.
for ($attempt = 1; $attempt -le 4; $attempt++) {
    $current = Invoke-Provider api "$api/contents/README.md?ref=main" | ConvertFrom-Json
    $latestCatalog = Get-Catalog
    Invoke-Tool release-readme $pin $latestCatalog.file $Connector $guideRevision $readme
    $contents = [Convert]::ToBase64String([IO.File]::ReadAllBytes($readme))
    if (($current.content -replace '\s','') -ceq $contents) { break }
    $body = @{message="Refresh $($route.title) release history"; branch='main'; sha=$current.sha; content=$contents}
    $request = Join-Path $work 'directory-request.json'
    $body | ConvertTo-Json -Compress | Set-Content -LiteralPath $request -Encoding utf8NoBOM
    try { Invoke-Provider api --method PUT "$api/contents/README.md" --input $request --silent; break }
    catch { if ($attempt -eq 4) { throw } }
}
Write-Output "Verified $($published.html_url) and $catalogUrl"
