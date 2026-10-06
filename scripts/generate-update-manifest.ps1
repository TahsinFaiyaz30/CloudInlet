param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [string]$Repository = 'TahsinFaiyaz30/CloudInlet'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Invalid release version.' }
$parsed = [Version]$Version
if ($parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) { throw 'Release version exceeds Windows Installer limits.' }
if ($Repository -cne 'TahsinFaiyaz30/CloudInlet') { throw 'The update feed repository must match the application trust boundary.' }
$root = [IO.Path]::GetFullPath($AssetDirectory)
if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Release asset directory does not exist.' }
for ($ancestor = [IO.DirectoryInfo]$root; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release assets cannot be read through a linked directory.' }
}
function Read-Asset([string]$Name, [string]$Flavor, [string]$Kind) {
    $path = Join-Path $root $Name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release asset is missing: $Name" }
    $item = Get-Item -LiteralPath $path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -le 0 -or $item.Length -gt 1GB) { throw "Invalid release asset: $Name. Updater assets must be regular nonempty files no larger than 1 GiB." }
    [ordered]@{ buildFlavor = $Flavor; installerKind = $Kind; architecture = 'x64'; fileName = $Name; size = $item.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$assets = [Collections.Generic.List[object]]::new()
$legacyAssets = [Collections.Generic.List[object]]::new()
foreach ($flavor in @('Release', 'Debug')) {
    foreach ($kind in @('Exe', 'Msi', 'Portable')) {
        $ending = switch ($kind) { 'Exe' { 'setup.exe' } 'Msi' { 'setup.msi' } 'Portable' { 'portable.zip' } }
        $name = "CloudInlet-$Version-win-x64-$($flavor.ToLowerInvariant())-$ending"
        $asset = Read-Asset $name $flavor $kind
        $assets.Add($asset)
        # Released CloudBay clients require these exact names. They are aliases
        # of the same installer bytes, never a separately built older product.
        $legacyName = "CloudBay-$Version-win-x64-$($flavor.ToLowerInvariant())-$ending"
        $legacyPath = Join-Path $root $legacyName
        if (!(Test-Path -LiteralPath $legacyPath)) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $legacyPath }
        $legacy = Read-Asset $legacyName $flavor $kind
        if ($legacy.size -ne $asset.size -or $legacy.sha256 -cne $asset.sha256) { throw "Legacy update alias differs from its canonical package: $legacyName" }
        $legacyAssets.Add($legacy)
    }
}
foreach ($feed in @(
    @{ Name = 'updates-v2.json'; Schema = 2; Repository = $Repository; Assets = @($assets) },
    @{ Name = 'updates-v1.json'; Schema = 1; Repository = 'TahsinFaiyaz30/CloudBay'; Assets = @($legacyAssets) }
)) {
    $manifest = [ordered]@{ schemaVersion = $feed.Schema; repository = $feed.Repository; version = $Version; tag = "v$Version"; assets = $feed.Assets }
    $temporary = Join-Path $root ('.updates-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
        Move-Item -LiteralPath $temporary -Destination (Join-Path $root $feed.Name) -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
$releaseFiles = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -in @('updates-v1.json', 'updates-v2.json') -or $_.Name -like "CloudInlet-$Version-*" -or $_.Name -like "CloudBay-$Version-*" })
$sums = foreach ($file in $releaseFiles | Sort-Object Name) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release payload cannot contain linked files.' }
    "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($file.Name)"
}
$sums | Set-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Encoding ascii
Write-Output (Join-Path $root 'updates-v2.json')
