#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Publisher, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
if ($Id -cnotmatch '^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$' -or $Id -match '^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)' -or $Id -in 'local','b2','onedrive' -or $Id.Length -gt 96) { throw 'Choose a unique lowercase connector ID.' }
if (Test-Path -LiteralPath $Destination) { throw 'Choose a new project directory.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk = Join-Path $repo 'CloudInlet.ConnectorSdk/CloudInlet.ConnectorSdk.csproj'
dotnet build $sdk -c Release -v:q
if ($LASTEXITCODE) { throw 'SDK build failed.' }
$dest = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path (Join-Path $dest 'sdk') -Force | Out-Null
@('bin/', 'obj/', '*.dpapi', '*.cipack') | Set-Content -LiteralPath (Join-Path $dest '.gitignore') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $repo 'CloudInlet.ConnectorSdk/bin/Release/net8.0/CloudInlet.ConnectorSdk.dll') -Destination (Join-Path $dest 'sdk')
$example = Join-Path $repo 'connectors/Example'
(Get-Content -Raw -LiteralPath (Join-Path $example 'ExampleConnector.csproj')).Replace('<None Include="../../LICENSE"', '<None Update="LICENSE"').Replace('<ProjectReference Include="../../CloudInlet.ConnectorSdk/CloudInlet.ConnectorSdk.csproj" Private="false" />','<Reference Include="CloudInlet.ConnectorSdk"><HintPath>sdk/CloudInlet.ConnectorSdk.dll</HintPath><Private>false</Private></Reference>') | Set-Content -LiteralPath (Join-Path $dest "$Id.csproj") -Encoding utf8NoBOM
(Get-Content -Raw -LiteralPath (Join-Path $example 'ExampleConnector.cs')).Replace('example.readonly',$Id) | Set-Content -LiteralPath (Join-Path $dest 'Connector.cs') -Encoding utf8NoBOM
$manifest = Get-Content -Raw -LiteralPath (Join-Path $example 'connector.json') | ConvertFrom-Json
$manifest.id = $Id
$manifest.name = $Id
$manifest.publisher = $Publisher
$manifest.assembly = "$Id.dll"
$manifest.files = @{ "$Id.dll" = ''; 'LICENSE.txt' = '' }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dest 'connector.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $dest
Write-Output $dest
