#Requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
if ($env:CLOUDINLET_CONNECTOR_SIGNING_KEY) { throw 'Release contract tests run in an unsigned build job.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path $repo ('artifacts/connector-release-checks/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$tool = Join-Path $repo 'tools/CloudInlet.ConnectorTool/bin/Release/net8.0-windows/CloudInlet.ConnectorTool.dll'
dotnet build (Join-Path $repo 'tools/CloudInlet.ConnectorTool') -c Release -v:q
if ($LASTEXITCODE) { throw 'Tool build failed.' }
$pin = Join-Path $work 'source.json'
$revision = git -C $repo rev-parse HEAD
$fixture = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
function Check-Tool { & dotnet $tool @args; if ($LASTEXITCODE) { throw "Expected success from $($args[0])." } }
function Reject-Tool { & dotnet $tool @args 2>&1 | Out-Null; if (!$LASTEXITCODE) { throw "Expected rejection from $($args[0])." } }
try {
    $key = $fixture.ExportPkcs8PrivateKey()
    $env:CLOUDINLET_CONNECTOR_SIGNING_KEY = [Convert]::ToBase64String($key)
    $official = Get-Content -Raw -LiteralPath (Join-Path $repo 'connectors/official-source.json') | ConvertFrom-Json
    $official.publicKey = [Convert]::ToBase64String($fixture.ExportSubjectPublicKeyInfo())
    $official | ConvertTo-Json | Set-Content -LiteralPath $pin -Encoding utf8NoBOM
    $prior = '-'
    foreach ($project in 'BackblazeB2', 'OneDrive') {
        $path = Join-Path $repo "connectors/$project"
        dotnet build (Join-Path $path "CloudInlet.Connectors.$project.csproj") -c Release -v:q
        if ($LASTEXITCODE) { throw 'Provider build failed.' }
        $spec = Get-Content -Raw -LiteralPath (Join-Path $path 'connector.json') | ConvertFrom-Json
        $id = $spec.id
        $inputPath = Join-Path $work $id
        New-Item -ItemType Directory -Path $inputPath | Out-Null
        $spec.version = '1.0.0'
        $spec | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $inputPath 'connector.json') -Encoding utf8NoBOM
        foreach ($name in $spec.files.PSObject.Properties.Name) { Copy-Item -LiteralPath (Join-Path $path "bin/Release/net8.0/$name") -Destination $inputPath }
        $pack = Join-Path $work "$id.cipack"
        Check-Tool release-pack $id $revision $inputPath $pin $pack
        Check-Tool release-verify $id $revision $inputPath $pin $pack
        Reject-Tool release-verify $id ('0' * 40) $inputPath $pin $pack
        Reject-Tool release-pack $id $revision $inputPath (Join-Path $repo 'connectors/official-source.json') (Join-Path $work 'wrong-signer.cipack')
        $next = Join-Path $work "$id.json"
        Check-Tool release-catalog $pin $prior $pack $next
        $copy = Join-Path $work "$id-retry.json"
        Check-Tool release-catalog $pin $next $pack $copy
        if ((Get-FileHash $copy).Hash -cne (Get-FileHash $next).Hash) { throw 'Idempotent catalog replay changed bytes.' }
        $prior = $next
    }
    $envelope = Get-Content -Raw -LiteralPath $prior | ConvertFrom-Json
    $catalog = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload)) | ConvertFrom-Json
    if (($catalog.packages.manifest.id -join ',') -cne 'b2,onedrive') { throw 'Latest catalog did not preserve both independent releases.' }
    $other = Join-Path $work 'duplicate.cipack'
    Check-Tool release-pack onedrive $revision $inputPath $pin $other
    Reject-Tool release-catalog $pin $prior $other (Join-Path $work 'conflict.json')
    $envelope.signature = [Convert]::ToBase64String([byte[]]::new(64))
    $bad = Join-Path $work 'tampered.json'
    $envelope | ConvertTo-Json | Set-Content -LiteralPath $bad -Encoding utf8NoBOM
    Reject-Tool release-catalog $pin $bad $pack (Join-Path $work 'forged.json')
    Reject-Tool release-readme $pin $bad central $revision (Join-Path $work 'forged.md')
    Reject-Tool release-route personal 1.0.1
    Reject-Tool release-route b2 'v1.0.1'
    $central = 'TahsinFaiyaz30/CloudInlet-Connectors'
    $historicalCatalog = $prior
    # Exercise the exact original feed shape and migrate one signed location at a time.
    $legacy = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload)) | ConvertFrom-Json
    foreach ($entry in $legacy.packages) { $entry.locations = @($entry.locations[1]) }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($legacy | ConvertTo-Json -Depth 12 -Compress))
    $envelope.payload = [Convert]::ToBase64String($bytes)
    $envelope.signature = [Convert]::ToBase64String($fixture.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256))
    $legacyFile = Join-Path $work 'legacy-locations.json'
    $envelope | ConvertTo-Json | Set-Content -LiteralPath $legacyFile -Encoding utf8NoBOM
    $moved = Join-Path $work 'moved-b2.json'
    Check-Tool release-catalog $pin $legacyFile (Join-Path $work 'b2.cipack') $moved
    Check-Tool release-check-entry $pin $moved b2 1.0.0 $revision (Join-Path $work 'b2.cipack')
    $updated = Get-Content -Raw $moved | ConvertFrom-Json
    $entries = ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($updated.payload)) | ConvertFrom-Json).packages
    if (($entries[0] | ConvertTo-Json -Depth 12 -Compress) -cne ($catalog.packages[0] | ConvertTo-Json -Depth 12 -Compress) -or
        ($entries[1] | ConvertTo-Json -Depth 12 -Compress) -cne ($legacy.packages[1] | ConvertTo-Json -Depth 12 -Compress)) {
        throw 'Publishing the original B2 pack changed signed metadata or the other connector.'
    }
    foreach ($case in @(
        @{id='b2'; name='Backblaze B2'; repository='CloudInlet-Connector-B2'; version='1.0.1'},
        @{id='onedrive'; name='OneDrive'; repository='CloudInlet-Connector-OneDrive'; version='1.1.0'}
    )) {
        $id = $case.id
        $original = Check-Tool release-route $id 1.0.0 | ConvertFrom-Json
        $route = Check-Tool release-route $id $case.version | ConvertFrom-Json
        if (!$original.historical -or $original.repository -cne $route.repository -or $original.tag -cne 'v1.0.0' -or
            $original.originalDownload -cne "https://github.com/$central/releases/download/$id-v1.0.0/$id-1.0.0.cipack" -or
            $route.historical -or $route.repository -cne "TahsinFaiyaz30/$($case.repository)" -or $route.tag -cne "v$($case.version)" -or
            $route.title -cne "$($case.name) Connector $($case.version)") { throw 'Incorrect release routing.' }
        $inputPath = Join-Path $work $id
        $file = Join-Path $inputPath 'connector.json'
        $spec = Get-Content -Raw -LiteralPath $file | ConvertFrom-Json
        $spec.version = $case.version
        $spec | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $file -Encoding utf8NoBOM
        $pack = Join-Path $work $route.filename
        Check-Tool release-pack $id $revision $inputPath $pin $pack
        $next = Join-Path $work "$id-future.json"
        Check-Tool release-catalog $pin $prior $pack $next
        Check-Tool release-check-entry $pin $next $id $case.version $revision $pack
        Reject-Tool release-check-entry $pin $next $id $case.version ('0' * 40) $pack
        $prior = $next
    }
    $envDoc = Get-Content -Raw -LiteralPath $prior | ConvertFrom-Json
    $doc = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envDoc.payload)) | ConvertFrom-Json
    if ($doc.packages.Count -ne 4) { throw 'Lost connector history.' }
    foreach ($old in $catalog.packages) {
        $saved = $doc.packages | Where-Object { $_.manifest.id -ceq $old.manifest.id -and $_.manifest.version -ceq $old.manifest.version }
        if (($saved | ConvertTo-Json -Depth 12 -Compress) -cne ($old | ConvertTo-Json -Depth 12 -Compress)) { throw 'Historical entry changed.' }
    }
    foreach ($section in 'central','b2','onedrive') {
        $md = Join-Path $work "$section.md"
        Check-Tool release-readme $pin $prior $section $revision $md
        $content = Get-Content -Raw -LiteralPath $md
        foreach ($item in $doc.packages) {
            $expected = if ($section -ceq 'central') { $item.manifest.version -ne '1.0.0' } else { $item.manifest.id -ceq $section }
            if ($content.Contains($item.locations[0]) -ne $expected) { throw "Incorrect directory selection for $section." }
        }
    }
    # Even a signed feed cannot reassign historical packages to a new repository or another provider.
    $rebound = Join-Path $work 'rebound.json'
    foreach ($index in 0,2) {
        $item = $doc.packages[$index]
        $originalUrl = $item.locations[0]
        $item.locations[0] = $doc.packages[3].locations[0]
        $bytes = [Text.Encoding]::UTF8.GetBytes(($doc | ConvertTo-Json -Depth 12 -Compress))
        $envDoc.payload = [Convert]::ToBase64String($bytes)
        $envDoc.signature = [Convert]::ToBase64String($fixture.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256))
        $envDoc | ConvertTo-Json | Set-Content -LiteralPath $rebound -Encoding utf8NoBOM
        Reject-Tool release-readme $pin $rebound central $revision (Join-Path $work 'rebound.md')
        $item.locations[0] = $originalUrl
    }
    $concurrent = Join-Path $work 'concurrent-onedrive.json'
    Check-Tool release-catalog $pin $historicalCatalog $pack $concurrent
    & "$PSScriptRoot/test-connector-publish-retry.ps1" -Root $repo -FixtureDir $work -HistoricalCatalog $historicalCatalog -ConcurrentCatalog $concurrent -Revision $revision
    $base = Join-Path $work 'core'
    New-Item -ItemType Directory -Path $base | Out-Null
    Copy-Item -LiteralPath $pin -Destination (Join-Path $base 'connector-source.json')
    Copy-Item -LiteralPath (Join-Path $repo 'CloudInlet.ConnectorSdk/bin/Release/net8.0/CloudInlet.ConnectorSdk.dll') -Destination $base
    Check-Tool assert-core $base
    $renamed = Join-Path $base 'other.bin'
    [IO.File]::Copy($pack, $renamed)
    Reject-Tool assert-core $base
    [IO.File]::Copy((Join-Path $inputPath $spec.assembly), $renamed, $true)
    Reject-Tool assert-core $base
    "Connector publication and base-payload checks passed. Evidence $work"
} finally {
    Remove-Item Env:CLOUDINLET_CONNECTOR_SIGNING_KEY -ErrorAction SilentlyContinue
    if ($key) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($key) }
    $fixture.Dispose()
}
