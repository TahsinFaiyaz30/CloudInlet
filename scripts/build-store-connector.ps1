#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName='Store')]
param(
    [Parameter(Mandatory)][string]$Pack,
    [Parameter(Mandatory)][string]$Catalog,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory,ParameterSetName='Local')][switch]$LocalValidationIdentity,
    [Parameter(Mandatory,ParameterSetName='Store')][ValidatePattern('^[A-Za-z0-9.-]{3,50}$')][string]$IdentityName,
    [Parameter(Mandatory,ParameterSetName='Store')][ValidatePattern('^[A-Za-z0-9.-]{3,50}$')][string]$MainIdentityName,
    [Parameter(Mandatory,ParameterSetName='Store')][ValidatePattern('^CN=')][string]$Publisher,
    [Parameter(Mandatory,ParameterSetName='Store')][string]$PublisherDisplayName
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packFile = (Resolve-Path -LiteralPath $Pack).Path
$catalogFile = (Resolve-Path -LiteralPath $Catalog).Path
$hash = (Get-FileHash -LiteralPath $packFile -Algorithm SHA256).Hash
$envelope = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
$entries = ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload)) | ConvertFrom-Json).packages
$matched = @($entries | Where-Object hash -CEQ $hash)
if ($matched.Count -ne 1) { throw 'Select an exact published connector pack from the official catalog.' }
$manifest = $matched[0].manifest
$tool = Join-Path $repo 'tools/CloudInlet.ConnectorTool/bin/Release/net8.0-windows/CloudInlet.ConnectorTool.dll'
dotnet $tool release-check-entry (Join-Path $repo 'connectors/official-source.json') $catalogFile $manifest.id $manifest.version $manifest.sourceRevision $packFile
if ($LASTEXITCODE) { throw 'Connector or catalog authentication failed. Build CloudInlet.ConnectorTool in Release and verify the inputs.' }
if ($LocalValidationIdentity) {
    $MainIdentityName = 'CloudInlet.LocalValidation'
    $IdentityName = "$MainIdentityName.$($manifest.id)"
    $Publisher = 'CN=CloudInlet Local Validation'
    $PublisherDisplayName = 'CloudInlet Local Validation'
}
if ($IdentityName -eq $MainIdentityName -or $IdentityName.Length -gt 50) { throw 'The optional package requires its own valid package identity.' }
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$makeAppx = Get-ChildItem -LiteralPath $sdk -Directory | Where-Object Name -Match '^10\.0\.\d+\.0$' |
    Sort-Object { [Version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (!$makeAppx) { throw 'Install the Windows SDK with MakeAppx.' }
$out = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$file = Join-Path $out "$IdentityName-$($manifest.version)-x64.msix"
if (Test-Path -LiteralPath $file) { throw "An optional package already exists at $file." }
$work = [IO.Directory]::CreateDirectory((Join-Path $out ([guid]::NewGuid().ToString('N')))).FullName
$xml = Join-Path $work 'AppxManifest.xml'
@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3" IgnorableNamespaces="uap3">
  <Identity Name="$IdentityName" Publisher="$([Security.SecurityElement]::Escape($Publisher))" Version="$($manifest.version).0" ProcessorArchitecture="x64" />
  <Properties><DisplayName>$([Security.SecurityElement]::Escape($manifest.name)) Connector</DisplayName><PublisherDisplayName>$([Security.SecurityElement]::Escape($PublisherDisplayName))</PublisherDisplayName><Logo>StoreLogo.png</Logo></Properties>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /><uap3:MainPackageDependency Name="$MainIdentityName" /></Dependencies>
  <Resources><Resource Language="en-US" /></Resources>
</Package>
"@ | Set-Content -LiteralPath $xml -Encoding utf8NoBOM
$mapping = Join-Path $work 'files.map'
$logo = Join-Path $repo 'CloudInlet/Assets/CloudInlet-48.png'
@('[Files]', ('"{0}" "AppxManifest.xml"' -f $xml), ('"{0}" "StoreLogo.png"' -f $logo),
    ('"{0}" "Connectors\{1}.cipack"' -f $packFile, $hash.ToLowerInvariant())) | Set-Content -LiteralPath $mapping -Encoding utf8
& $makeAppx pack /f $mapping /p $file /no
if ($LASTEXITCODE) { throw 'MakeAppx failed to build the optional package.' }
$archive = [IO.Compression.ZipFile]::OpenRead($file)
try {
    $payload = $archive.GetEntry("Connectors/$($hash.ToLowerInvariant()).cipack").Open()
    try {
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload)) -cne $hash) { throw 'Packaged connector bytes changed.' }
    } finally { $payload.Dispose() }
} finally { $archive.Dispose() }
Write-Output $file
