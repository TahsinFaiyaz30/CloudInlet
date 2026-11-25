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
        Copy-Item -LiteralPath (Join-Path $path 'connector.json') -Destination $inputPath
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
