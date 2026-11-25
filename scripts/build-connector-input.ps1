#Requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9.-]{0,95}$')][string]$Connector,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$head = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -or $head -cne $SourceCommit) { throw 'Check out the requested exact source commit first.' }
$dirty = git -C $repo status --porcelain --untracked-files=normal
if ($LASTEXITCODE -or $dirty) { throw 'Official build input must come from a clean checkout.' }
$registry = $env:CLOUDINLET_CONNECTOR_PROVIDERS ?? (Join-Path $repo 'packaging/connector-distribution/providers.json')
$folder = (Get-Content -Raw -LiteralPath $registry | ConvertFrom-Json -AsHashtable)[$Connector].folder
if ($folder -cnotmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$') { throw 'Missing or invalid provider folder in the distribution registry.' }
$provider = Join-Path $repo "connectors/$folder"
$manifest = Get-Content -Raw -LiteralPath (Join-Path $provider 'connector.json') | ConvertFrom-Json
if ($manifest.id -cne $Connector -or $manifest.version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Unexpected provider metadata.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a new output directory.' }
# Portable PDB checksums can incorporate compiler inputs specific to the build host.
# Distribute deterministic IL and keep the exact source revision in the package.
dotnet build (Join-Path $provider "CloudInlet.Connectors.$folder.csproj") -c Release --no-incremental -v:minimal "-p:Version=$($manifest.version)" -p:ContinuousIntegrationBuild=true -p:Deterministic=true -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$repo=/_/source" "-p:SourceRevisionId=$SourceCommit"
if ($LASTEXITCODE) { throw 'Connector compilation failed.' }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $provider 'connector.json') -Destination $output
foreach ($name in $manifest.files.PSObject.Properties.Name) {
    if ($name -cnotmatch '^(?:[a-zA-Z0-9_.-]+\.dll|LICENSE\.txt|README\.md)$' -or $name.StartsWith('.')) { throw 'Unexpected payload filename.' }
    $built = Join-Path $provider "bin/Release/net8.0/$name"
    if ($name.EndsWith('.dll')) {
        Copy-Item -LiteralPath $built -Destination $output
    } else {
        [IO.File]::WriteAllText((Join-Path $output $name), [IO.File]::ReadAllText($built).ReplaceLineEndings("`n"), [Text.UTF8Encoding]::new($false))
    }
}
