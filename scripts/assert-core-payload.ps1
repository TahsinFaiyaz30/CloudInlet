#Requires -Version 7.0
param([Parameter(Mandatory)][string]$AppFolder)
$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot '../tools/CloudInlet.ConnectorTool'
dotnet build (Join-Path $tool 'CloudInlet.ConnectorTool.csproj') -c Release -v:q | Out-Host
if ($LASTEXITCODE) { throw 'Connector payload verifier failed to build.' }
dotnet (Join-Path $tool 'bin/Release/net8.0-windows/CloudInlet.ConnectorTool.dll') assert-core $AppFolder
if ($LASTEXITCODE) { throw 'Provider-free base package check failed.' }
