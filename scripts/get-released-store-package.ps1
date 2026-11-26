#Requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Tag,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = 'TahsinFaiyaz30/CloudInlet'
$metadata = gh release view $Tag --repo $repo --json tagName,isDraft,isPrerelease,assets
if ($LASTEXITCODE -ne 0) { throw "Could not read release $Tag." }
$release = $metadata | ConvertFrom-Json
if ($release.tagName -cne $Tag -or $release.isDraft -or $release.isPrerelease) { throw 'A published stable CloudInlet release is required.' }
$packageName = "CloudInlet-$($Tag.Substring(1))-win-x64-release-store.msix"
$indexName = 'SHA256SUMS.txt'
$directory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
gh release download $Tag --repo $repo --pattern $packageName --pattern $indexName --dir $directory
if ($LASTEXITCODE -ne 0) { throw "Could not download the published MSIX and checksums for $Tag to a fresh directory." }
foreach ($name in @($packageName, $indexName)) {
    $asset = @($release.assets | Where-Object name -CEQ $name)
    $file = Get-Item -LiteralPath (Join-Path $directory $name)
    $digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Length -or $asset[0].digest -cne "sha256:$digest") { throw "Release size or SHA-256 mismatch for $name." }
    if ($name -ceq $packageName) { $packageDigest = $digest }
}
$entries = @(Get-Content -LiteralPath (Join-Path $directory $indexName) | Where-Object { $_ -cmatch ('^[a-f0-9]{64}  ' + [regex]::Escape($packageName) + '$') })
if ($entries.Count -ne 1 -or $entries[0] -cne "$packageDigest  $packageName") { throw 'The published checksum list does not identify these exact Store bytes.' }
$package = Join-Path $directory $packageName
& (Join-Path $PSScriptRoot 'test-store-manifest.ps1') -PackagePath $package -PublishedVersion $Tag.Substring(1)
Write-Output "Verified $packageName ($packageDigest)."
