param(
    [Parameter(Mandatory)][string]$AppFolder,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('Debug', 'Release')][string]$Configuration,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SourceRevision,
    [string]$InnoCompiler,
    [string]$WixToolPath,
    [switch]$HelperPrepared
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appRoot = [IO.Path]::GetFullPath($AppFolder)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Installer version must be a stable major.minor.patch version.' }
$parts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($parts[0] -gt 255 -or $parts[1] -gt 255 -or $parts[2] -gt 65535) { throw 'Version exceeds Windows Installer major.minor.patch limits.' }
if (!$SourceRevision) { $SourceRevision = (git -C $repository rev-parse HEAD).Trim() }
if ($SourceRevision -notmatch '^[a-f0-9]{40}$') { throw 'SourceRevision must be a full Git commit SHA.' }
function Assert-NormalTree([string]$Path) {
    for ($ancestor = [IO.DirectoryInfo]$Path; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installer input/output contains a linked directory.' }
    }
    if ((Test-Path -LiteralPath $Path) -and (Get-ChildItem -LiteralPath $Path -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) { throw 'Installer input/output contains a linked file.' }
}
Assert-NormalTree $appRoot
Assert-NormalTree $outputRoot
$appExe = Join-Path $appRoot 'CloudBay.exe'
if (!(Test-Path -LiteralPath $appExe)) { throw 'AppFolder must contain the full published CloudBay application.' }
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExe).ProductVersion.Split('+')[0]
if ($productVersion -ne $Version) { throw "The application version ($productVersion) does not match installer version ($Version)." }
if (Get-ChildItem -LiteralPath $appRoot -Recurse -File | Where-Object { $_.Name -match '^(credentials\.dpapi|settings\.json|.*\.(sqlite|sqlite-wal|sqlite-shm))$' }) { throw 'Published application contains private client state.' }
if (!(Test-Path -LiteralPath (Join-Path $appRoot 'THIRD-PARTY-NOTICES.md'))) { throw 'Copy third-party notices into the application payload before building installers.' }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if (!$HelperPrepared) { & (Join-Path $PSScriptRoot 'build-installer-helper.ps1') -AppFolder $appRoot }
$helperFile = Join-Path $appRoot 'CloudBay.SetupHelper.exe'
if (!(Test-Path -LiteralPath $helperFile)) { throw 'The installer/update helper is missing from the application payload.' }
$tools = & (Join-Path $PSScriptRoot 'get-installer-tools.ps1') -InnoCompiler $InnoCompiler -WixToolPath $WixToolPath
$flavor = $Configuration.ToLowerInvariant()
$productName = if ($Configuration -eq 'Debug') { 'CloudBay Debug' } else { 'CloudBay' }
$startupName = if ($Configuration -eq 'Debug') { 'CloudBayDebug' } else { 'CloudBay' }
$upgradeCode = if ($Configuration -eq 'Debug') { '14BA2C55-3BED-4F8C-927B-2157E7852A96' } else { '7C2B596C-321A-4F07-9A96-C2E83136223D' }
$assetName = "CloudBay-$Version-win-x64-$flavor-setup"
foreach ($extension in @('exe', 'msi')) {
    if (Test-Path -LiteralPath (Join-Path $outputRoot "$assetName.$extension")) { throw 'Installer output already exists. Use a fresh output directory so published version bytes stay immutable.' }
}
$stagingRoot = Join-Path $outputRoot ('.installer-' + $flavor + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
$distribution = [ordered]@{ schemaVersion = 1; version = $Version; buildFlavor = $Configuration; installerKind = 'Exe'; architecture = 'x64'; installScope = 'perUser'; installDirectory = ''; startWithWindows = $true; desktopShortcut = $false; sourceRevision = $SourceRevision }
$exeMetadata = Join-Path $stagingRoot 'distribution-exe.json'
$distribution | ConvertTo-Json | Set-Content -LiteralPath $exeMetadata -Encoding utf8NoBOM
$distribution.installerKind = 'Msi'
$msiMetadata = Join-Path $stagingRoot 'distribution-msi.json'
$distribution | ConvertTo-Json | Set-Content -LiteralPath $msiMetadata -Encoding utf8NoBOM

& $tools.InnoCompiler '/Qp' "/DAppFolder=$appRoot" "/DAppVersion=$Version" "/DBuildFlavor=$Configuration" "/DSourceRevision=$SourceRevision" "/DOutputDirectory=$stagingRoot" "/DOutputName=$assetName" "/DRepository=$repository" "/DMetadataFile=$exeMetadata" "/DHelperFile=$helperFile" (Join-Path $repository 'packaging/CloudBay.iss')
if ($LASTEXITCODE -ne 0) { throw 'EXE installer compilation failed.' }

# Managed DTF code is packaged inside a native x64 MSI custom-action DLL.
# It never depends on the application's own .NET runtime or user data.
dotnet build (Join-Path $repository 'packaging/InstallerActions/CloudBay.InstallerActions.csproj') -c Release -p:Platform=x64 -v:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'MSI installer actions build failed.' }
$actionsDll = Join-Path $repository 'packaging/InstallerActions/bin/x64/Release/net472/CloudBay.InstallerActions.CA.dll'
if (!(Test-Path -LiteralPath $actionsDll)) { throw 'The native MSI custom-action wrapper was not generated.' }

function Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
function Stable-Id([string]$Value) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { 'I' + ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Replace('-', '').Substring(0, 30)) }
    finally { $algorithm.Dispose() }
}
function Stable-Guid([string]$Value) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)); $guidBytes = [byte[]]::new(16)
        [Array]::Copy($bytes, $guidBytes, 16)
        $guidBytes[7] = ($guidBytes[7] -band 15) -bor 80; $guidBytes[8] = ($guidBytes[8] -band 63) -bor 128
        ([Guid]::new($guidBytes)).ToString('D')
    } finally { $algorithm.Dispose() }
}
$registry = "Software\CloudBay\Distribution\$Configuration\Msi"
$components = [Collections.Generic.List[string]]::new()
$directories = [Text.StringBuilder]::new()
function Append-Directory([string]$Folder, [string]$Relative, [string]$DirectoryId) {
    foreach ($file in Get-ChildItem -LiteralPath $Folder -File | Sort-Object Name) {
        if ($file.Name -eq 'distribution.json') { continue }
        $relativeFile = if ($Relative) { $Relative + '\' + $file.Name } else { $file.Name }
        $componentId = Stable-Id ($Configuration + '|component|' + $relativeFile.ToLowerInvariant())
        $fileId = if ($relativeFile -eq 'CloudBay.exe') { 'CloudBayExe' } else { Stable-Id ('file|' + $relativeFile.ToLowerInvariant()) }
        $guid = Stable-Guid ($Configuration + '|Msi|' + $relativeFile.ToLowerInvariant())
        $components.Add($componentId)
        [void]$directories.AppendLine("<Component Id=`"$componentId`" Guid=`"$guid`" Bitness=`"always64`"><File Id=`"$fileId`" Source=`"$(Xml $file.FullName)`" /><RegistryValue Root=`"HKCU`" Key=`"$(Xml $registry)\Files`" Name=`"$componentId`" Value=`"1`" Type=`"integer`" KeyPath=`"yes`" /></Component>")
    }
    foreach ($folderItem in Get-ChildItem -LiteralPath $Folder -Directory | Sort-Object Name) {
        $relativeDirectory = if ($Relative) { $Relative + '\' + $folderItem.Name } else { $folderItem.Name }
        $childId = Stable-Id ('directory|' + $relativeDirectory.ToLowerInvariant())
        [void]$directories.AppendLine("<Directory Id=`"$childId`" Name=`"$(Xml $folderItem.Name)`">")
        Append-Directory $folderItem.FullName $relativeDirectory $childId
        [void]$directories.AppendLine('</Directory>')
    }
}
Append-Directory $appRoot '' 'INSTALLDIR'
$componentRefs = ($components | ForEach-Object { "<ComponentRef Id=`"$_`" />" }) -join "`n"
$wxs = Join-Path $stagingRoot 'CloudBay.wxs'
@"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">
  <Package Name="$(Xml $productName)" Manufacturer="CloudBay" Version="$Version" Language="1033" UpgradeCode="$upgradeCode" Scope="perUser" InstallerVersion="500">
    <MajorUpgrade Schedule="afterInstallInitialize" MigrateFeatures="no" DowngradeErrorMessage="A newer build of CloudBay is already installed." />
    <MediaTemplate EmbedCab="yes" CompressionLevel="medium" />
    <Property Id="CB_FLAVOR" Value="$Configuration" />
    <Property Id="CB_REVISION" Value="$SourceRevision" />
    <Property Id="UPDATE" Secure="yes" />
    <Property Id="INSTALLDIR" Secure="yes" />
    <Property Id="ARPNOMODIFY" Value="1" />
    <Property Id="ARPURLINFOABOUT" Value="https://github.com/TahsinFaiyaz30/CloudBay" />
    <Property Id="ARPPRODUCTICON" Value="CloudBayIcon" />
    <Property Id="MSIRESTARTMANAGERCONTROL" Value="Disable" />
    <Launch Condition="Installed OR (VersionNT64 AND CB_WINDOWS_BUILD &gt;= 22000)" Message="CloudBay requires 64-bit Windows 11 or later." />
    <Icon Id="CloudBayIcon" SourceFile="$(Xml (Join-Path $appRoot 'Assets/CloudBay.ico'))" />
    <Binary Id="InstallerActions" SourceFile="$(Xml $actionsDll)" />
    <CustomAction Id="CheckWindowsVersion" BinaryRef="InstallerActions" DllEntry="CheckWindowsVersion" Execute="immediate" Return="check" />
    <CustomAction Id="PrepareInstall" BinaryRef="InstallerActions" DllEntry="PrepareInstall" Execute="immediate" Return="check" />
    <CustomAction Id="ValidateAndStop" BinaryRef="InstallerActions" DllEntry="ValidateAndStop" Execute="immediate" Return="check" />
    <CustomAction Id="StopBeforeUninstall" BinaryRef="InstallerActions" DllEntry="StopBeforeUninstall" Execute="immediate" Return="check" />
    <CustomAction Id="WriteDistribution" BinaryRef="InstallerActions" DllEntry="WriteDistribution" Execute="deferred" Return="check" Impersonate="yes" />
    <InstallUISequence>
      <Custom Action="CheckWindowsVersion" Before="LaunchConditions" />
      <Custom Action="PrepareInstall" Before="CostInitialize" Condition="NOT UPGRADINGPRODUCTCODE" />
    </InstallUISequence>
    <InstallExecuteSequence>
      <Custom Action="CheckWindowsVersion" Before="LaunchConditions" />
      <Custom Action="PrepareInstall" Before="CostInitialize" Condition="NOT UPGRADINGPRODUCTCODE" />
      <Custom Action="ValidateAndStop" Before="InstallValidate" Condition="NOT (REMOVE~=&quot;ALL&quot;)" />
      <Custom Action="StopBeforeUninstall" Before="InstallValidate" Condition="REMOVE~=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE" />
      <Custom Action="WriteDistribution" After="InstallFiles" Condition="NOT (REMOVE~=&quot;ALL&quot;)" />
    </InstallExecuteSequence>
    <StandardDirectory Id="LocalAppDataFolder"><Directory Id="ProgramsFolder" Name="Programs"><Directory Id="INSTALLDIR" Name="$(Xml $productName)">
      $directories
      <Component Id="DistributionMetadata" Guid="$(Stable-Guid "$Configuration|Msi|distribution")" Bitness="always64">
        <File Id="DistributionFile" Source="$(Xml $msiMetadata)" Name="distribution.json" />
        <RegistryValue Root="HKCU" Key="$(Xml $registry)" Name="InstallDirectory" Value="[INSTALLDIR]" Type="string" KeyPath="yes" />
        <RegistryValue Root="HKCU" Key="$(Xml $registry)" Name="Version" Value="$Version" Type="string" />
        <RegistryValue Root="HKCU" Key="$(Xml $registry)" Name="BuildFlavor" Value="$Configuration" Type="string" />
      </Component>
      <Component Id="StartupComponent" Guid="$(Stable-Guid "$Configuration|Msi|startup")" Bitness="always64">
        <RegistryValue Root="HKCU" Key="Software\Microsoft\Windows\CurrentVersion\Run" Name="$startupName" Value="&quot;[INSTALLDIR]CloudBay.exe&quot; --background" Type="string" KeyPath="yes" />
      </Component>
    </Directory></Directory></StandardDirectory>
    <StandardDirectory Id="ProgramMenuFolder"><Component Id="StartMenuShortcut" Guid="$(Stable-Guid "$Configuration|Msi|startmenu")" Bitness="always64">
      <Shortcut Id="CloudBayStartMenu" Name="$(Xml $productName)" Target="[INSTALLDIR]CloudBay.exe" WorkingDirectory="INSTALLDIR" Icon="CloudBayIcon" />
      <RegistryValue Root="HKCU" Key="$(Xml $registry)" Name="StartMenuShortcut" Value="1" Type="integer" KeyPath="yes" />
    </Component></StandardDirectory>
    <StandardDirectory Id="DesktopFolder"><Component Id="DesktopShortcutComponent" Guid="$(Stable-Guid "$Configuration|Msi|desktop")" Bitness="always64">
      <Shortcut Id="CloudBayDesktop" Name="$(Xml $productName)" Target="[INSTALLDIR]CloudBay.exe" WorkingDirectory="INSTALLDIR" Icon="CloudBayIcon" />
      <RegistryValue Root="HKCU" Key="$(Xml $registry)" Name="DesktopShortcut" Value="1" Type="integer" KeyPath="yes" />
    </Component></StandardDirectory>
    <Feature Id="Main" Title="$(Xml $productName)" Level="1" AllowAbsent="no" Display="expand" ConfigurableDirectory="INSTALLDIR">
      $componentRefs
      <ComponentRef Id="DistributionMetadata" /><ComponentRef Id="StartMenuShortcut" />
      <Feature Id="Startup" Title="Start with Windows" Description="Start CloudBay in the background when you sign in." Level="1"><ComponentRef Id="StartupComponent" /></Feature>
      <Feature Id="DesktopShortcut" Title="Desktop shortcut" Description="Create a CloudBay shortcut on the desktop." Level="2"><ComponentRef Id="DesktopShortcutComponent" /></Feature>
    </Feature>
    <ui:WixUI Id="WixUI_FeatureTree" />
    <WixVariable Id="WixUILicenseRtf" Value="$(Xml (Join-Path $repository 'packaging/License.rtf'))" />
  </Package>
</Wix>
"@ | Set-Content -LiteralPath $wxs -Encoding utf8NoBOM
& $tools.WixToolPath build $wxs -arch x64 -ext WixToolset.UI.wixext/5.0.2 -o (Join-Path $stagingRoot "$assetName.msi")
if ($LASTEXITCODE -ne 0) { throw 'MSI installer compilation or validation failed.' }

foreach ($extension in @('exe', 'msi')) {
    $built = Join-Path $stagingRoot "$assetName.$extension"
    $final = Join-Path $outputRoot "$assetName.$extension"
    if (Test-Path -LiteralPath $final) {
        throw 'Installer output already exists. Use a fresh output directory so published version bytes stay immutable.'
    }
    Move-Item -LiteralPath $built -Destination $final
    $hash = (Get-FileHash -LiteralPath $final -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $assetName.$extension" | Set-Content -LiteralPath ($final + '.sha256') -Encoding ascii
    Write-Output "Installer: $final"
}
