#Requires -Version 7.0
param([Parameter(Mandatory)][string]$AppFolder, [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$canonical = Join-Path $PSScriptRoot '../connectors/official-source.json'
$target = Join-Path $AppFolder 'connector-source.json'
if ($VerifyOnly) {
    if (!(Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $canonical).Hash) {
        throw 'Release packaging requires the source-controlled official catalog and publisher pin.'
    }
} else { Copy-Item -LiteralPath $canonical -Destination $target -Force }
