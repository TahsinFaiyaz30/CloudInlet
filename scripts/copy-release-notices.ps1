param(
    [Parameter(Mandatory = $true)][string]$AppFolder,
    [Parameter(Mandatory = $true)][string]$AssetsPath
)
$ErrorActionPreference = 'Stop'
$appRoot = [IO.Path]::GetFullPath($AppFolder)
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
$dependencies = Get-Content -LiteralPath (Join-Path $appRoot 'CloudBay.deps.json') -Raw | ConvertFrom-Json
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
$licenseRoot = Join-Path $appRoot 'Licenses'
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
$records = [Collections.Generic.List[object]]::new()
$pinnedNotices = @{
    'Apache-2.0.txt' = 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30'
    'CsWinRT-2.2.0-LICENSE.txt' = '9906940F61B1F0B533FA7D99BAF55178B2808FBE113EA51DFBFAD8572CCD5F2B'
}
foreach ($notice in $pinnedNotices.GetEnumerator()) {
    $source = Join-Path $PSScriptRoot ("../packaging/licenses/" + $notice.Key)
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $notice.Value) {
        throw 'A pinned upstream license changed; review its provenance before publishing.'
    }
}

function Get-PackageRoot([string]$Identity) {
    if ($Identity -notmatch '^[A-Za-z0-9._-]+/[A-Za-z0-9._+-]+$') { throw 'Invalid package identity in publish manifest.' }
    foreach ($folder in $packageFolders) {
        $candidate = Join-Path $folder $Identity.ToLowerInvariant()
        if (Test-Path -LiteralPath $candidate -PathType Container) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw "License source package was not restored: $Identity"
}

function Copy-Notice([string]$Source, [string]$RelativeDestination, [string]$Attribution) {
    $destination = [IO.Path]::GetFullPath((Join-Path $licenseRoot $RelativeDestination))
    if (!$destination.StartsWith($licenseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A dependency notice escaped the license directory.'
    }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    if ($sourceHash -ne (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) { throw 'Dependency notice copy mismatch.' }
    return [pscustomobject]@{ file = $RelativeDestination.Replace('\', '/'); sha256 = $sourceHash; source = $Attribution }
}

$runtime = $dependencies.runtimeTarget.name
$dotnetPack = @($dependencies.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' })
if ($dotnetPack.Count -ne 1) { throw 'The self-contained .NET runtime license source is ambiguous.' }
$dotnetIdentity = $dotnetPack[0] -replace '^runtimepack\.', ''
$dotnetRoot = Get-PackageRoot $dotnetIdentity
$dotnetLicense = Join-Path $dotnetRoot 'LICENSE.TXT'

$noticeIdentities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($library in $dependencies.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package' -and $library.Name -notlike 'runtimepack.*') { continue }
    $null = $noticeIdentities.Add(($library.Name -replace '^runtimepack\.', ''))
}
# Native Windows App SDK payload is copied by package build targets, rather than loaded
# through the managed dependency graph. SDKs may omit these packages from .deps.json.
# Retain notices from the resolved restore graph as well as published runtime packs;
# neither the NuGet cache inventory nor a hard-coded package version defines the release.
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -eq 'package') { $null = $noticeIdentities.Add($library.Name) }
}

foreach ($identity in @($noticeIdentities | Sort-Object)) {
    $sourceRoot = Get-PackageRoot $identity
    $nuspec = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.nuspec' -File)
    if ($nuspec.Count -ne 1) { throw "Dependency license metadata is ambiguous: $identity" }
    [xml]$specification = Get-Content -LiteralPath $nuspec[0].FullName -Raw
    $metadata = $specification.package.metadata
    $files = [Collections.Generic.List[object]]::new()
    $originals = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object { $_.Name -match '(?i)(license|notice)' })
    foreach ($original in $originals) {
        $relative = $original.FullName.Substring($sourceRoot.Length + 1)
        $files.Add((Copy-Notice $original.FullName (Join-Path $identity $relative) "$identity/$relative"))
    }
    $licenseType = [string]$metadata.license.type
    $licenseDeclaration = [string]$metadata.license.InnerText
    if ($licenseType -eq 'file') {
        $declaredPath = [IO.Path]::GetFullPath((Join-Path $sourceRoot $licenseDeclaration))
        if (!$declaredPath.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            !(Test-Path -LiteralPath $declaredPath -PathType Leaf)) { throw "Missing declared license: $identity" }
        if ($declaredPath -notin $originals.FullName) {
            $files.Add((Copy-Notice $declaredPath (Join-Path $identity $licenseDeclaration) "$identity/$licenseDeclaration"))
        }
    } elseif ($licenseType -eq 'expression') {
        if ($licenseDeclaration -notin @('MIT', 'Apache-2.0')) {
            throw "Review the new dependency license before publishing: $identity ($licenseDeclaration)"
        }
        if ($files.Count -eq 0) {
            $fallback = if ($licenseDeclaration -eq 'MIT') { $dotnetLicense }
                else { Join-Path $PSScriptRoot '../packaging/licenses/Apache-2.0.txt' }
            $attribution = if ($licenseDeclaration -eq 'MIT') { "$dotnetIdentity/LICENSE.TXT" }
                else { 'https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.6/LICENSE.TXT' }
            $files.Add((Copy-Notice $fallback (Join-Path $identity 'LICENSE.txt') $attribution))
        }
    }
    if ($identity -like 'Microsoft.Windows.SDK.NET.Ref/*') {
        $projectionVersion = (Get-Item -LiteralPath (Join-Path $appRoot 'WinRT.Runtime.dll')).VersionInfo.ProductVersion
        if ($projectionVersion -notmatch '^2\.2\.0\.\d+\+8649ee3eeb2445ca2a36d80d878ef60b96a6c65d$') {
            throw 'Review CsWinRT license provenance before publishing a changed projection runtime.'
        }
        $projectionSource = Join-Path $PSScriptRoot '../packaging/licenses/CsWinRT-2.2.0-LICENSE.txt'
        $projectionAttribution = 'https://github.com/microsoft/CsWinRT/blob/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE'
        $files.Add((Copy-Notice $projectionSource (Join-Path $identity 'CsWinRT-LICENSE.txt') $projectionAttribution))
    }
    $records.Add([pscustomobject]@{
        package = [string]$metadata.id; version = [string]$metadata.version
        licenseType = $licenseType; license = $licenseDeclaration; licenseUrl = [string]$metadata.licenseUrl
        copyright = [string]$metadata.copyright; repository = [string]$metadata.repository.url
        files = @($files.ToArray())
    })
}
foreach ($required in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsAppSDK.Runtime', 'CommunityToolkit.Mvvm', 'SQLitePCLRaw.core')) {
    $record = @($records | Where-Object package -eq $required)
    if ($record.Count -ne 1 -or $record[0].files.Count -eq 0) { throw "Required runtime notices are missing: $required" }
}
Copy-Notice (Join-Path $PSScriptRoot '../packaging/licenses/README.md') 'README.md' 'CloudBay/packaging/licenses/README.md' | Out-Null
[pscustomobject]@{ runtimeTarget = $runtime; packages = @($records.ToArray()) } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $licenseRoot 'packages.json') -Encoding utf8
Write-Output "Retained license metadata for $($records.Count) resolved packages and runtime packs."
