param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$path = [IO.Path]::GetFullPath($PackagePath)
$archive = [IO.Compression.ZipFile]::OpenRead($path)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if (!$entry) { throw 'Package manifest missing.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $archive.Dispose() }
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$ns.AddNamespace('p','http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('v','http://schemas.microsoft.com/appx/manifest/virtualization/windows10')
$ns.AddNamespace('d6','http://schemas.microsoft.com/appx/manifest/desktop/windows10/6')
function Assert-Set($Actual,$Expected,[string]$Label) {
    if ((@($Actual | Sort-Object) -join "`n") -cne (@($Expected | Sort-Object) -join "`n")) { throw "$Label does not match the permitted scope." }
}
$directories = @($manifest.SelectNodes('/p:Package/p:Properties/v:FileSystemWriteVirtualization/v:ExcludedDirectories/v:ExcludedDirectory',$ns) | ForEach-Object { $_.InnerText })
$keys = @($manifest.SelectNodes('/p:Package/p:Properties/v:RegistryWriteVirtualization/v:ExcludedKeys/v:ExcludedKey',$ns) | ForEach-Object { $_.InnerText })
Assert-Set $directories @('$(KnownFolder:LocalAppData)\CloudBay\Client') 'External application data'
Assert-Set $keys @('HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders','HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders') 'External registry keys'
$capabilities = @($manifest.SelectNodes('/p:Package/p:Capabilities/*',$ns) | ForEach-Object { $_.GetAttribute('Name') })
Assert-Set $capabilities @('internetClient','runFullTrust','unvirtualizedResources') 'Capabilities'
foreach ($name in @('RegistryWriteVirtualization','FileSystemWriteVirtualization')) {
    if ($manifest.SelectSingleNode('/p:Package/p:Properties/d6:' + $name,$ns).InnerText -cne 'disabled') { throw 'Supported Windows 10 behavior changed.' }
}
if ($manifest.SelectSingleNode('/p:Package/p:Dependencies/p:TargetDeviceFamily',$ns).GetAttribute('MinVersion') -cne '10.0.19041.0') { throw 'Minimum supported Windows version changed.' }
$application = $manifest.SelectSingleNode('/p:Package/p:Applications/p:Application',$ns)
if ($application.GetAttribute('Id') -cne 'CloudBay' -or $application.GetAttribute('Executable') -cne 'CloudInlet.exe') { throw 'Durable application identity changed.' }
if ($manifest.SelectSingleNode("//*[local-name()='StartupTask']").GetAttribute('TaskId') -cne 'CloudBayStartup') { throw 'Durable startup identity changed.' }
$cloudFiles = @($manifest.SelectNodes("//*[local-name()='Extension' and @Category='windows.cloudFiles']/*[local-name()='CloudFiles']"))
if ($cloudFiles.Count -ne 1 -or $cloudFiles[0].GetAttribute('IconResource') -cne 'Assets\CloudInlet-32.png') { throw 'Packaged Cloud Files registration is missing or incorrect.' }
$metadataPath = Join-Path ([IO.Path]::GetDirectoryName($path)) ([IO.Path]::GetFileNameWithoutExtension($path) + '-package-info.json')
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
Assert-Set $metadata.virtualizationPolicy.windows11.excludedDirectories $directories 'Policy metadata directories'
Assert-Set $metadata.virtualizationPolicy.windows11.excludedRegistryKeys $keys 'Policy metadata registry keys'
if ($manifest.SelectSingleNode('/p:Package/p:Identity',$ns).GetAttribute('Version') -cne ($metadata.version + '.0')) { throw 'Metadata version differs from the manifest.' }
[ordered]@{ passed=$true; package=$path; version=$metadata.version; capabilities=$capabilities; externalDirectories=$directories; externalRegistryKeys=$keys; windows10Fallback='disabled'; stableIdentitiesPreserved=$true } | ConvertTo-Json -Depth 4
