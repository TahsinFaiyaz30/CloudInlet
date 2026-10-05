param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [string]$Repository = 'TahsinFaiyaz30/CloudBay'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Invalid release version.' }
$parsed = [Version]$Version
if ($parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) { throw 'Release version exceeds Windows Installer limits.' }
if ($Repository -cne 'TahsinFaiyaz30/CloudBay') { throw 'The update feed repository must match the application trust boundary.' }
$root = [IO.Path]::GetFullPath($AssetDirectory)
if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Release asset directory does not exist.' }
for ($ancestor = [IO.DirectoryInfo]$root; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release assets cannot be read through a linked directory.' }
}
$assets = [Collections.Generic.List[object]]::new()
foreach ($flavor in @('Release', 'Debug')) {
    foreach ($kind in @('Exe', 'Msi', 'Portable')) {
        $ending = switch ($kind) { 'Exe' { 'setup.exe' } 'Msi' { 'setup.msi' } 'Portable' { 'portable.zip' } }
        $name = "CloudBay-$Version-win-x64-$($flavor.ToLowerInvariant())-$ending"
        $path = Join-Path $root $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release asset is missing: $name" }
        $item = Get-Item -LiteralPath $path
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -le 0 -or $item.Length -gt 1GB) { throw "Invalid release asset: $name. Updater assets must be regular nonempty files no larger than 1 GiB." }
        $assets.Add([ordered]@{ buildFlavor = $flavor; installerKind = $kind; architecture = 'x64'; fileName = $name; size = $item.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
}
$manifest = [ordered]@{ schemaVersion = 1; repository = $Repository; version = $Version; tag = "v$Version"; assets = @($assets) }
$manifestPath = Join-Path $root 'updates-v1.json'
$temporary = Join-Path $root ('.updates-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $manifestPath -Force
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
$releaseFiles = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -eq 'updates-v1.json' -or $_.Name -like "CloudBay-$Version-*" })
$sums = foreach ($file in $releaseFiles | Sort-Object Name) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release payload cannot contain linked files.' }
    "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($file.Name)"
}
$sums | Set-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Encoding ascii
Write-Output $manifestPath
