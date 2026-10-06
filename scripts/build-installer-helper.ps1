param([Parameter(Mandatory)][string]$AppFolder)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appRoot = [IO.Path]::GetFullPath($AppFolder)
for ($ancestor = [IO.DirectoryInfo]$appRoot; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installer helper destination contains a linked directory.' }
}
if (!(Test-Path -LiteralPath (Join-Path $appRoot 'CloudInlet.exe'))) { throw 'Build the complete application payload before adding the installer helper.' }
dotnet build (Join-Path $repository 'packaging/InstallerHelper/CloudInlet.InstallerHelper.csproj') -c Release -v:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The installer/update helper build failed.' }
$helperRoot = Join-Path $repository 'packaging/InstallerHelper/bin/Release/net472'
foreach ($name in @('CloudInlet.SetupHelper.exe', 'CloudInlet.SetupHelper.exe.config')) {
    $source = Join-Path $helperRoot $name
    $target = Join-Path $appRoot $name
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The helper destination is a linked file.' }
    }
    if (!(Test-Path -LiteralPath $source)) { throw "The helper output is missing: $name" }
    Copy-Item -LiteralPath $source -Destination $target -Force
}
$notices = Join-Path $appRoot 'Licenses/Installers'
for ($ancestor = [IO.DirectoryInfo]$notices; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installer notices destination contains a linked directory.' }
}
New-Item -ItemType Directory -Path $notices -Force | Out-Null
foreach ($name in @('WiX-5.0.2-LICENSE.txt', 'Inno-6.7.3-LICENSE.txt')) {
    $target = Join-Path $notices $name
    if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installer notice is a linked file.' }
    Copy-Item -LiteralPath (Join-Path $repository ('packaging/licenses/' + $name)) -Destination $target -Force
}
$sourceRoot = Join-Path $notices 'CloudInlet-sources'
if ((Test-Path -LiteralPath $sourceRoot) -and ((Get-Item -LiteralPath $sourceRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installer source destination is a linked directory.' }
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
foreach ($folder in @('InstallerActions', 'InstallerHelper', 'InstallerShared')) {
    $destination = Join-Path $sourceRoot $folder
    if ((Test-Path -LiteralPath $destination) -and ((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installer source destination is a linked directory.' }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository ('packaging/' + $folder)) -File) {
        $target = Join-Path $destination $file.Name
        if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installer source destination is a linked file.' }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}
$installerNotice = Join-Path $notices 'README.md'
if ((Test-Path -LiteralPath $installerNotice) -and ((Get-Item -LiteralPath $installerNotice -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installer notice is a linked file.' }
@'
# Installer notices and source

The EXE installer is built with unmodified Inno Setup 6.7.3, by Jordan Russell and
Martijn Laan. Its license is retained in `Inno-6.7.3-LICENSE.txt`.
Project: https://jrsoftware.org/ . Pinned source: https://github.com/jrsoftware/issrc/tree/is-6_7_3 .

The MSI contains WiX Toolset 5.0.2 DTF Windows Installer libraries and the native
SfxCA wrapper. Copyright (c) .NET Foundation and contributors. Those components
are governed by the Microsoft Reciprocal License in `WiX-5.0.2-LICENSE.txt`.
Their complete corresponding upstream source and build files are available at:
https://github.com/wixtoolset/wix/tree/aa65968c419420d32e3e1b647aea0082f5ca5b78 .
Source archive: https://github.com/wixtoolset/wix/archive/aa65968c419420d32e3e1b647aea0082f5ca5b78.zip .

The CloudInlet custom-action and setup-helper source used by this payload is retained
in `CloudInlet-sources`, alongside its project/build configuration. CloudInlet's own
source is covered by the repository MIT license. Upstream WiX components retain
their MS-RL terms; inclusion of those components does not change their license.
'@ | Set-Content -LiteralPath $installerNotice -Encoding utf8NoBOM
$sourceLicense = Join-Path $sourceRoot 'LICENSE'
if ((Test-Path -LiteralPath $sourceLicense) -and ((Get-Item -LiteralPath $sourceLicense -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installer source license is a linked file.' }
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $sourceLicense -Force
