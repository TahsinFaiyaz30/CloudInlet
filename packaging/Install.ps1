param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'App'
$sourceExe = Join-Path $source 'CloudInlet.exe'
if (!(Test-Path -LiteralPath $sourceExe)) { throw 'Run this script from the complete CloudInlet release folder.' }
if ((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The release App folder cannot be a linked directory.' }
if (Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'The release contains a linked item. Use the complete original release archive.'
}
if (![Environment]::Is64BitOperatingSystem) { throw 'CloudInlet requires 64-bit Windows.' }
$version = ([Diagnostics.FileVersionInfo]::GetVersionInfo($sourceExe)).ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+([-.][a-zA-Z0-9.]+)?$') { throw 'Unexpected package version.' }
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\CloudInlet'))
# Published CloudInlet 1.1.2 installations can occupy the original directory.
$allowedInstallRoots = @('CloudInlet', 'CloudBay') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA ('Programs\' + $_))) }
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudInlet'
$legacyUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudBay'
$registration = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $legacyUninstallKey) { throw 'Upgrade CloudBay through the published 1.1.2 bridge before installing a later CloudInlet release.' }
# Portable installation must not take over a managed EXE/MSI installation or an
# unrelated directory merely because it happens to have the product's name.
foreach ($kind in @('Exe', 'Msi')) {
    if (Test-Path -LiteralPath ('HKCU:\Software\CloudBay\Distribution\Release\' + $kind)) {
        throw "CloudInlet is already managed by a $kind installer. Update it using the same installer format."
    }
}
function Assert-NormalPortablePath([string]$Path) {
    if ($Path -notmatch '^[a-zA-Z]:[\\/]') { throw 'Portable installation paths must be local absolute paths.' }
    $absolute = [IO.Path]::GetFullPath($Path)
    if ((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "The portable installation contains a linked path: $absolute" }
    for ($ancestor = [IO.DirectoryInfo]([IO.Path]::GetDirectoryName($absolute)); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The portable installation path contains a directory link.' }
    }
}
function Get-PortableRegistrationRoot($Value) {
    if ($null -eq $Value) { return $null }
    if (!$Value.InstallLocation -or !$Value.UninstallString) { throw 'The existing portable installation registration is incomplete.' }
    Assert-NormalPortablePath ([string]$Value.InstallLocation)
    $root = [IO.Path]::GetFullPath([string]$Value.InstallLocation).TrimEnd('\')
    if (!($allowedInstallRoots | Where-Object { $_.Equals($root, [StringComparison]::OrdinalIgnoreCase) })) {
        throw 'The existing installation uses a different location. Its files and registration were left unchanged.'
    }
    if ([string]$Value.UninstallString -notmatch '(?i)(?:^|\s)-File\s+"(?<script>[^"]+)"(?:\s|$)' -or
        ![IO.Path]::GetFullPath($Matches.script).Equals((Join-Path $root 'Uninstall.ps1'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The existing installation is not owned by the portable installer.'
    }
    Assert-NormalPortablePath $root
    $registeredUninstaller = Join-Path $root 'Uninstall.ps1'
    Assert-NormalPortablePath $registeredUninstaller
    if (!(Test-Path -LiteralPath $root -PathType Container) -or !(Test-Path -LiteralPath $registeredUninstaller -PathType Leaf)) { throw 'The registered portable installation or its uninstaller is missing. Restore those files before upgrading.' }
    return $root
}
$previousRoot = Get-PortableRegistrationRoot $registration
if ($previousRoot) { $installRoot = $previousRoot }
if ((Test-Path -LiteralPath $installRoot) -and !$previousRoot) {
    throw 'The installation folder belongs to an unregistered installation. Nothing was overwritten.'
}
# Keep registered binary locations stable. Accounts and backup roots are stored
# separately and are never moved by the portable installer.
$destination = [IO.Path]::GetFullPath((Join-Path $installRoot $version))
if (!$destination.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid install destination.' }
for ($ancestor = [IO.DirectoryInfo]$destination; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The installation path contains a directory link. Choose a normal per-user installation location.'
    }
}
$sourceUninstaller = Join-Path $PSScriptRoot 'Uninstall.ps1'
Assert-NormalPortablePath $sourceUninstaller
$destinationUninstaller = Join-Path $installRoot 'Uninstall.ps1'
Assert-NormalPortablePath $destinationUninstaller
if ((Test-Path -LiteralPath $destinationUninstaller) -and !(Test-Path -LiteralPath $destinationUninstaller -PathType Leaf)) { throw 'The destination Uninstall.ps1 is not a file. Resolve that conflict before installing.' }
if (!(Test-Path -LiteralPath $sourceUninstaller -PathType Leaf)) { throw 'The portable release is missing Uninstall.ps1. Extract the complete release archive before installing.' }
# Use Windows' argument parser, with the same exact flags as App.OnLaunched.
# Text matching can mistake a flag-shaped path for an isolated instance or miss
# a quoted flag, leaving the real client running during an upgrade.
if ($null -eq ('CloudInlet.Packaging.CommandLine' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloudInlet.Packaging
{
    public static class CommandLine
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static string[] Parse(string commandLine)
        {
            if (String.IsNullOrWhiteSpace(commandLine)) return new string[0];
            int count;
            IntPtr arguments = CommandLineToArgvW(commandLine, out count);
            if (arguments == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                string[] result = new string[count];
                for (int index = 0; index < count; index++)
                    result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size));
                return result;
            }
            finally { LocalFree(arguments); }
        }

        public static bool IsIsolated(string commandLine)
        {
            foreach (string argument in Parse(commandLine))
            {
                if (String.Equals(argument, "--ui-live", StringComparison.Ordinal) ||
                    String.Equals(argument, "--ui-smoke", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
'@
}
# Check all ownership and shortcut conflicts before changing files or stopping
# the running app, so a rejected handoff leaves an installation that can retry.
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Test-OwnedStartupCommand([string]$Command) {
    if (!$Command -or !$previousRoot) { return $false }
    $arguments = [CloudInlet.Packaging.CommandLine]::Parse($Command)
    if (!$arguments.Length -or ![IO.Path]::IsPathRooted($arguments[0])) { return $false }
    $image = [IO.Path]::GetFullPath($arguments[0])
    $owned = $image.StartsWith($previousRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($image) -eq 'CloudInlet.exe'
    if ($owned) { Assert-NormalPortablePath $image }
    return $owned
}
$currentStartup = [string](Get-ItemProperty -LiteralPath $startupKey -Name CloudInlet -ErrorAction SilentlyContinue).CloudInlet
$ownsCurrentStartup = Test-OwnedStartupCommand $currentStartup
if ($currentStartup -and !$ownsCurrentStartup) { throw 'The CloudInlet startup entry belongs to a different installation. Resolve that startup entry before installing.' }
$shell = New-Object -ComObject WScript.Shell
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) 'CloudInlet'
$shortcutPath = Join-Path $shortcutDirectory 'CloudInlet.lnk'
Assert-NormalPortablePath $shortcutDirectory
Assert-NormalPortablePath $shortcutPath
if ((Test-Path -LiteralPath $shortcutDirectory) -and !(Test-Path -LiteralPath $shortcutDirectory -PathType Container)) { throw 'The CloudInlet Start menu folder is occupied by a file.' }
if ((Test-Path -LiteralPath $shortcutPath) -and !(Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw 'The CloudInlet Start menu shortcut is occupied by a directory.' }
if (Test-Path -LiteralPath $shortcutPath) {
    $existingShortcut = $shell.CreateShortcut($shortcutPath)
    if (!$previousRoot -or !$existingShortcut.TargetPath -or
        ![IO.Path]::GetFullPath($existingShortcut.TargetPath).StartsWith($previousRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The CloudInlet Start menu shortcut belongs to a different installation. Resolve that shortcut before installing.'
    }
}
$running = @(Get-Process -Name CloudInlet -ErrorAction SilentlyContinue | Where-Object {
    $candidate = $_
    $native = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $candidate.Id)
    # Preview and smoke instances use separate named pipes and state. The main
    # client's shutdown request cannot stop them, so they must not block upgrade.
    $native -and ![CloudInlet.Packaging.CommandLine]::IsIsolated([string]$native.CommandLine)
})
if ($running.Count -gt 0) {
    $shutdownProcess = Start-Process -FilePath $sourceExe -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
    if (!$shutdownProcess.WaitForExit(10000)) { throw 'CloudInlet did not answer the shutdown request. Quit it from its tray menu and run installation again.' }
    $stillRunning = @($running | Where-Object { $_.Refresh(); !$_.HasExited })
    if ($stillRunning.Count -gt 0 -and $shutdownProcess.ExitCode -eq 5) { throw 'Windows denied access to the running CloudInlet app. Quit it from its tray menu and run installation again.' }
    if ($stillRunning.Count -gt 0 -and $shutdownProcess.ExitCode -ne 0) { throw 'CloudInlet could not receive the shutdown request. Quit it from its tray menu and run installation again.' }
    foreach ($process in $running) {
        if (!$process.WaitForExit(60000)) { throw 'CloudInlet is still finishing a transfer. Quit it from its tray menu and run installation again.' }
    }
}
$staging = [IO.Path]::GetFullPath((Join-Path $installRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))))
if (!$staging.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging destination.' }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $staging -Recurse -Force
$previous = $null
if (Test-Path -LiteralPath $destination) {
    $previous = [IO.Path]::GetFullPath($destination + '.previous-' + [Guid]::NewGuid().ToString('N'))
    if (!$previous.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid previous installation destination.' }
    Move-Item -LiteralPath $destination -Destination $previous
}
try { Move-Item -LiteralPath $staging -Destination $destination }
catch {
    if ($null -ne $previous -and !(Test-Path -LiteralPath $destination)) { Move-Item -LiteralPath $previous -Destination $destination }
    throw
}
Copy-Item -LiteralPath $sourceUninstaller -Destination $installRoot -Force
$exe = Join-Path $destination 'CloudInlet.exe'
if ((Test-Path -LiteralPath $shortcutDirectory) -and ((Get-Item -LiteralPath $shortcutDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The CloudInlet Start menu folder contains a link. Installation stopped.'
}
New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe; $shortcut.WorkingDirectory = $destination; $shortcut.IconLocation = "$exe,0"; $shortcut.Save()
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'CloudInlet' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $version -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'CloudInlet' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value "$exe,0" -PropertyType String -Force | Out-Null
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall.ps1')`""
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
if ($ownsCurrentStartup) {
    New-ItemProperty -LiteralPath $startupKey -Name CloudInlet -Value "`"$exe`" --background" -PropertyType String -Force | Out-Null
}
Write-Output "CloudInlet $version installed at $destination"
if (!$NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $destination }
