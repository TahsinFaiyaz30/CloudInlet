param(
    [Parameter(Mandatory)][string]$AppFolder,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PackageIdentityName = $env:CLOUDBAY_STORE_IDENTITY_NAME,
    [string]$Publisher = $env:CLOUDBAY_STORE_PUBLISHER,
    [string]$PublisherDisplayName = $env:CLOUDBAY_STORE_PUBLISHER_DISPLAY_NAME,
    [string]$SourceRevision,
    [string]$MakeAppxPath,
    [switch]$LocalValidationIdentity
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Store packages require a stable three-component version.' }
$versionValue = [Version]$Version
if ($versionValue.Major -gt 255 -or $versionValue.Minor -gt 255 -or $versionValue.Build -gt 65535) { throw 'Version exceeds the common MSI/MSIX version limits.' }
if ($LocalValidationIdentity) {
    if ($PackageIdentityName -or $Publisher -or $PublisherDisplayName) { throw 'Local validation identity cannot be combined with production Store identity values.' }
    $PackageIdentityName = 'CloudBay.LocalValidation'
    $Publisher = 'CN=CloudBay Local Validation'
    $PublisherDisplayName = 'CloudBay Local Validation'
} elseif (!$PackageIdentityName -or !$Publisher -or !$PublisherDisplayName) {
    throw 'Configure CLOUDBAY_STORE_IDENTITY_NAME, CLOUDBAY_STORE_PUBLISHER, and CLOUDBAY_STORE_PUBLISHER_DISPLAY_NAME from Partner Center. -LocalValidationIdentity produces a separate test package only.'
}
if ($PackageIdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$' -or $Publisher -notmatch '^CN=' -or $Publisher.Contains([char]0) -or $PublisherDisplayName.Length -gt 256) { throw 'Invalid Partner Center package identity.' }
if (!$SourceRevision) { $SourceRevision = (& git -C $repository rev-parse HEAD).Trim() }
if ($SourceRevision -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid source revision.' }
$app = [IO.Path]::GetFullPath($AppFolder)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath (Join-Path $app 'CloudBay.exe') -PathType Leaf)) { throw 'Published CloudBay.exe is missing.' }
$applicationVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $app 'CloudBay.exe')).ProductVersion.Split('+')[0]
if ($applicationVersion -cne $Version) { throw 'The published application version does not match the Store package version.' }
$distributionPath = Join-Path $app 'distribution.json'
if (Test-Path -LiteralPath $distributionPath) {
    $existingDistribution = Get-Content -LiteralPath $distributionPath -Raw | ConvertFrom-Json
    if ($existingDistribution.buildFlavor -cne 'Release' -or $existingDistribution.version -cne $Version) { throw 'Microsoft Store packages require the matching Release application payload.' }
}
if ($output.StartsWith($app.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Store output must not be nested inside the published application.' }
foreach ($path in @($app, $output)) {
    $ancestor = $path
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Store package paths must not contain links.' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}
if (Get-ChildItem -LiteralPath $app -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Published application contains a linked item.' }
if (!$MakeAppxPath) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $MakeAppxPath = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object { $_.Name -match '^10\.0\.\d+\.0$' } | Sort-Object { [Version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/makeappx.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$MakeAppxPath -or !(Test-Path -LiteralPath $MakeAppxPath -PathType Leaf)) { throw 'Install a Windows 10/11 SDK containing x64 MakeAppx.exe.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$staging = Join-Path $output ('.store-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'Payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
try {
    Get-ChildItem -LiteralPath $app -Force | Copy-Item -Destination $payload -Recurse
    $distribution = [ordered]@{ schemaVersion = 1; version = $Version; buildFlavor = 'Release'; installerKind = 'Store'; architecture = 'x64'; installScope = 'perUser'; installDirectory = ''; startWithWindows = $false; desktopShortcut = $false; sourceRevision = $SourceRevision }
    $distribution | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'distribution.json') -Encoding utf8NoBOM
    $assetRoot = Join-Path $payload 'StoreAssets'
    New-Item -ItemType Directory -Path $assetRoot | Out-Null
    Add-Type -AssemblyName System.Drawing.Common
    $sourceIcon = [Drawing.Icon]::new((Join-Path $payload 'Assets/CloudBay.ico'), 256, 256)
    $bitmap = $sourceIcon.ToBitmap()
    try {
        foreach ($size in @(44, 50, 150)) {
            $canvas = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [Drawing.Graphics]::FromImage($canvas)
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($bitmap, 0, 0, $size, $size)
                $canvas.Save((Join-Path $assetRoot "Logo$size.png"), [Drawing.Imaging.ImageFormat]::Png)
            } finally { $graphics.Dispose(); $canvas.Dispose() }
        }
    } finally { $bitmap.Dispose(); $sourceIcon.Dispose() }
    $identityXml = [Security.SecurityElement]::Escape($PackageIdentityName)
    $publisherXml = [Security.SecurityElement]::Escape($Publisher)
    $displayXml = [Security.SecurityElement]::Escape($PublisherDisplayName)
    @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
 xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap desktop desktop6 rescap">
 <Identity Name="$identityXml" Publisher="$publisherXml" Version="$Version.0" ProcessorArchitecture="x64" />
 <Properties>
  <DisplayName>CloudBay</DisplayName><PublisherDisplayName>$displayXml</PublisherDisplayName><Logo>StoreAssets\Logo50.png</Logo>
  <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
  <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
 </Properties>
 <Resources><Resource Language="en-US" /></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
 <Applications><Application Id="CloudBay" Executable="CloudBay.exe" EntryPoint="Windows.FullTrustApplication">
  <uap:VisualElements DisplayName="CloudBay" Description="Native Windows backup and Files On-Demand for Backblaze B2." BackgroundColor="transparent" Square150x150Logo="StoreAssets\Logo150.png" Square44x44Logo="StoreAssets\Logo44.png" />
  <Extensions><desktop:Extension Category="windows.startupTask" Executable="CloudBay.exe" EntryPoint="Windows.FullTrustApplication">
   <desktop:StartupTask TaskId="CloudBayStartup" Enabled="false" DisplayName="CloudBay" />
  </desktop:Extension></Extensions>
 </Application></Applications>
 <Capabilities><Capability Name="internetClient" /><rescap:Capability Name="runFullTrust" /><rescap:Capability Name="unvirtualizedResources" /></Capabilities>
</Package>
"@ | Set-Content -LiteralPath (Join-Path $payload 'AppxManifest.xml') -Encoding utf8NoBOM
    $suffix = if ($LocalValidationIdentity) { 'local-validation' } else { 'store' }
    $baseName = "CloudBay-$Version-win-x64-release-$suffix"
    $msix = Join-Path $output "$baseName.msix"
    if (Test-Path -LiteralPath $msix) { throw 'Store output already exists; use a fresh output directory.' }
    $buildLog = Join-Path $output 'makeappx.log'
    & $MakeAppxPath pack /d $payload /p $msix /o *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $buildLog -Tail 20 | Write-Host
        throw "MakeAppx schema or payload validation failed. Full diagnostics: $buildLog"
    }
    $uploadStaging = Join-Path $staging 'Upload'
    New-Item -ItemType Directory -Path $uploadStaging | Out-Null
    Copy-Item -LiteralPath $msix -Destination $uploadStaging
    $upload = Join-Path $output "$baseName.msixupload"
    [IO.Compression.ZipFile]::CreateFromDirectory($uploadStaging, $upload, [IO.Compression.CompressionLevel]::Optimal, $false)
    [ordered]@{ version = $Version; sourceRevision = $SourceRevision; localValidationIdentity = [bool]$LocalValidationIdentity; packageIdentityName = $PackageIdentityName; publisher = $Publisher; signed = $false; restrictedCapabilities = @('runFullTrust', 'unvirtualizedResources'); msix = [IO.Path]::GetFileName($msix); msixupload = [IO.Path]::GetFileName($upload) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "$baseName-package-info.json") -Encoding utf8NoBOM
    Write-Output $msix
    Write-Output $upload
} finally {
    $checked = [IO.Path]::GetFullPath($staging)
    if (!$checked.StartsWith($output.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Store staging escaped its output directory.' }
    if ((Test-Path -LiteralPath $checked) -and (Get-ChildItem -LiteralPath $checked -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) { throw 'Store staging cleanup found a linked item.' }
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse }
}
