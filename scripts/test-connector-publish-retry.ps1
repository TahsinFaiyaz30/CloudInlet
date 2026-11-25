#Requires -Version 7.0
# Called by test-connector-release.ps1 with disposable signed fixtures. All network I/O is simulated.
param([string]$Root, [string]$FixtureDir, [string]$HistoricalCatalog, [string]$ConcurrentCatalog, [string]$Revision)
$ErrorActionPreference = 'Stop'
$centralRepo = 'TahsinFaiyaz30/CloudInlet-Connectors'
$targetRepo = 'TahsinFaiyaz30/CloudInlet-Connector-B2'
$mockRoot = Join-Path $FixtureDir 'publisher'
$toolDir = 'tools/CloudInlet.ConnectorTool/bin/Release/net8.0-windows'
New-Item -ItemType Directory -Path "$mockRoot/connectors","$mockRoot/$toolDir","$mockRoot/assets" | Out-Null
Copy-Item "$Root/$toolDir/*" "$mockRoot/$toolDir"
Copy-Item -LiteralPath "$FixtureDir/source.json" -Destination "$mockRoot/connectors/official-source.json"
function Base64File($path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) }
function Json($value) { ConvertTo-Json -InputObject $value -Depth 12 -Compress }
$service = @{ head='a'*40; catalog=(Base64File $HistoricalCatalog); conflict=$true; commits=0
    readme=''; providerReadme=''; draft=$false; created=0; title=''; assets=@{}; failDirectory=$true }
# Scope-check every request and refuse any unrecognized command before it can touch GitHub.
function gh {
    $global:LASTEXITCODE = 0
    $command = $args -join ' '
    $isCentral = $command.Contains("repos/$centralRepo/") -or $args[1] -eq 'graphql'
    $expectedToken = if ($isCentral) { 'fixture-catalog' } else { 'fixture-provider' }
    if ($env:GH_TOKEN -cne $expectedToken) { throw "Wrong repository credential for $command" }
    if (!$isCentral -and !$command.Contains($targetRepo)) { throw "Unexpected release destination $command" }
    switch -Wildcard ($command) {
        "api repos/$centralRepo/git/ref/heads/main *" { return $service.head }
        "api repos/$centralRepo/commits/main *" { return $service.head }
        "api repos/$centralRepo/git/trees/*" {
            return Json @{tree=@(@{path='catalog.json';type='blob';sha='catalog';size=4096},@{path='README.md';sha=$service.readme})}
        }
        "api repos/$centralRepo/git/blobs/catalog" { return Json @{content=$service.catalog} }
        'api graphql *' {
            $change = (($input | Out-String) | ConvertFrom-Json).variables.input
            if ($change.branch.repositoryNameWithOwner -cne $centralRepo -or $change.branch.branchName -cne 'main' -or
                $change.expectedHeadOid -cne $service.head -or ($change.fileChanges.additions.path -join ',') -cne 'catalog.json,README.md') { throw 'Invalid atomic catalog commit.' }
            if ($service.conflict) {
                $service.conflict = $false
                $service.head = 'b'*40
                $service.catalog = Base64File $ConcurrentCatalog
                $global:LASTEXITCODE = 1
                return
            }
            $service.head = 'c'*40
            $service.catalog = $change.fileChanges.additions[0].contents
            [IO.File]::WriteAllBytes("$mockRoot/central.md",[Convert]::FromBase64String($change.fileChanges.additions[1].contents))
            $service.readme = git hash-object --no-filters -- "$mockRoot/central.md"
            $service.commits++
            return
        }
        "api --paginate repos/$targetRepo/releases?*" { if ($service.created -and $command -notlike '*select(*') { 'v1.0.1' }; return }
        "api repos/$targetRepo/git/matching-refs/*" { return }
        "api repos/$targetRepo/commits/main *" { return 'd'*40 }
        'release create *' {
            if ($service.created -or $args[2] -cne 'v1.0.1' -or '--draft' -notin $args) { throw 'Duplicate or unexpected draft.' }
            $service.title = $args[[array]::IndexOf($args,'--title')+1]
            $service.created++
            $service.draft = $true
            return
        }
        'release upload *' {
            $path = $args[3]
            $name = Split-Path -Leaf $path
            if (!$service.draft -or $service.assets.ContainsKey($name)) { throw 'Attempt to replace a published asset.' }
            Copy-Item -LiteralPath $path -Destination "$mockRoot/assets/$name"
            $digest = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
            $service.assets[$name] = @{name=$name; digest="sha256:$digest"; size=(Get-Item $path).Length
                browser_download_url="https://github.com/$targetRepo/releases/download/v1.0.1/$name"}
            return
        }
        'release edit *' {
            if (!$service.draft -or '--latest=true' -notin $args -or $service.assets.Count -ne 3) { throw 'Incomplete provider release or missing Latest.' }
            $service.draft = $false
            return
        }
        'release view *' { return Json @{isDraft=$service.draft; name=$service.title; assets=@($service.assets.Values)} }
        'release download *' {
            Copy-Item -LiteralPath "$mockRoot/assets/b2-1.0.1.cipack" -Destination $args[-1]
            return
        }
        "api repos/$targetRepo/releases/tags/v1.0.1" {
            return Json @{immutable=$true; draft=$service.draft; name=$service.title; body=(Get-Content -Raw "$mockRoot/assets/release-notes.md")
                assets=@($service.assets.Values); html_url="https://github.com/$targetRepo/releases/tag/v1.0.1"}
        }
        "api repos/$targetRepo/contents/README.md?ref=main" { return Json @{sha='readme';content=$service.providerReadme} }
        "api --method PUT repos/$targetRepo/contents/README.md *" {
            if ($service.failDirectory) { $global:LASTEXITCODE = 1; return }
            $service.providerReadme = (Get-Content -Raw $args[[array]::IndexOf($args,'--input')+1] | ConvertFrom-Json).content
            return
        }
        default { throw "Unexpected simulated GitHub command $command" }
    }
}
function Invoke-WebRequest([uri]$Uri,[string]$OutFile,[int]$MaximumRetryCount,[int]$RetryIntervalSec) {
    if ($Uri.GetLeftPart('Path') -ceq "https://raw.githubusercontent.com/$centralRepo/main/catalog.json") {
        [IO.File]::WriteAllBytes($OutFile,[Convert]::FromBase64String($service.catalog))
        return
    }
    $name = $Uri.Segments[-1]
    if ($service.draft -or $service.assets[$name].browser_download_url -cne $Uri.AbsoluteUri) { throw 'Unexpected public download.' }
    Copy-Item -LiteralPath "$mockRoot/assets/$name" -Destination $OutFile
}
$oldCentral = $env:GH_TOKEN
$oldProvider = $env:CONNECTOR_RELEASE_TOKEN
try {
    $env:GH_TOKEN = 'fixture-catalog'
    $env:CONNECTOR_RELEASE_TOKEN = 'fixture-provider'
    $params = @{Connector='b2'; SourceCommit=$Revision; Version='1.0.1'; InputDirectory="$FixtureDir/b2"; ToolRoot=$mockRoot}
    $scriptPath = "$Root/packaging/connector-distribution/publish.ps1"
    try { & $scriptPath @params | Out-Null; throw 'Expected a simulated provider README interruption.' }
    catch { if ($_.Exception.Message -cne 'GitHub api failed.') { throw } }
    $service.failDirectory = $false
    & $scriptPath @params | Out-Null
    $md = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($service.providerReadme))
    $centralMd = Get-Content -Raw "$mockRoot/central.md"
    if ($service.created -ne 1 -or $service.commits -ne 1 -or $service.title -cne 'Backblaze B2 Connector 1.0.1' -or
        !$centralMd.Contains('onedrive-1.1.0.cipack') -or !$md.Contains('b2-1.0.0.cipack') -or !$md.Contains('b2-1.0.1.cipack')) {
        throw 'Catalog conflict or interrupted directory refresh did not recover.'
    }
    Write-Output 'Simulated provider publication recovered from a central catalog conflict and an interrupted README update.'
} finally { $env:GH_TOKEN=$oldCentral; $env:CONNECTOR_RELEASE_TOKEN=$oldProvider }
