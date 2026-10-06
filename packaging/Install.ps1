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
$legacyInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\CloudBay'))
$legacyUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudBay'
$legacyRegistration = Get-ItemProperty -LiteralPath $legacyUninstallKey -ErrorAction SilentlyContinue
# An existing portable-script installation keeps its remembered binary directory;
# client settings and credentials remain in their separate durable state tree.
if ($legacyRegistration -and $legacyRegistration.InstallLocation -and
    [IO.Path]::GetFullPath([string]$legacyRegistration.InstallLocation).Equals($legacyInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $installRoot = $legacyInstallRoot
}
$destination = [IO.Path]::GetFullPath((Join-Path $installRoot $version))
if (!$destination.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid install destination.' }
for ($ancestor = [IO.DirectoryInfo]$destination; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The installation path contains a directory link. Choose a normal per-user installation location.'
    }
}
# Use Windows' argument parser, with the same exact flags as App.OnLaunched.
# Text matching can mistake a flag-shaped path for an isolated instance or miss
# a quoted flag, leaving the real client running during an upgrade.
if ($null -eq ('CloudBay.Packaging.CommandLine' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloudBay.Packaging
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
$running = @(Get-Process -Name CloudInlet, CloudBay -ErrorAction SilentlyContinue | Where-Object {
    $candidate = $_
    $native = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $candidate.Id)
    # Preview and smoke instances use separate named pipes and state. The main
    # client's shutdown request cannot stop them, so they must not block upgrade.
    $native -and ![CloudBay.Packaging.CommandLine]::IsIsolated([string]$native.CommandLine)
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
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $installRoot -Force
$exe = Join-Path $destination 'CloudInlet.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) 'CloudInlet'
if ((Test-Path -LiteralPath $shortcutDirectory) -and ((Get-Item -LiteralPath $shortcutDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The CloudInlet Start menu folder contains a link. Installation stopped.'
}
New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
$shortcut = $shell.CreateShortcut((Join-Path $shortcutDirectory 'CloudInlet.lnk'))
$shortcut.TargetPath = $exe; $shortcut.WorkingDirectory = $destination; $shortcut.IconLocation = "$exe,0"; $shortcut.Save()
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudInlet'
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
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
if (!(Get-ItemProperty -LiteralPath $startupKey -Name CloudInlet -ErrorAction SilentlyContinue) -and
    (Get-ItemProperty -LiteralPath $startupKey -Name CloudBay -ErrorAction SilentlyContinue)) {
    $oldApproval = (Get-ItemProperty -LiteralPath $approvedKey -Name CloudBay -ErrorAction SilentlyContinue).CloudBay
    if ($oldApproval -is [byte[]] -and !(Get-ItemProperty -LiteralPath $approvedKey -Name CloudInlet -ErrorAction SilentlyContinue)) {
        New-ItemProperty -LiteralPath $approvedKey -Name CloudInlet -Value $oldApproval -PropertyType Binary -Force | Out-Null
    }
}
if ((Get-ItemProperty -LiteralPath $startupKey -Name CloudInlet -ErrorAction SilentlyContinue).CloudInlet -or
    (Get-ItemProperty -LiteralPath $startupKey -Name CloudBay -ErrorAction SilentlyContinue).CloudBay) {
    New-ItemProperty -LiteralPath $startupKey -Name CloudInlet -Value "`"$exe`" --background" -PropertyType String -Force | Out-Null
}
Remove-ItemProperty -LiteralPath $startupKey -Name CloudBay -ErrorAction SilentlyContinue
if ($installRoot.Equals($legacyInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $legacyUninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    $legacyShortcutDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) 'CloudBay'
    $legacyShortcut = Join-Path $legacyShortcutDirectory 'CloudBay.lnk'
    if (Test-Path -LiteralPath $legacyShortcut) {
        if ((Get-Item -LiteralPath $legacyShortcutDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint -or
            (Get-Item -LiteralPath $legacyShortcut -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'The old Start menu shortcut contains a link. Remove it manually.'
        }
        $oldLink = $shell.CreateShortcut($legacyShortcut)
        if ($oldLink.TargetPath -and [IO.Path]::GetFullPath($oldLink.TargetPath).StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $legacyShortcut -Force
            if (!(Get-ChildItem -LiteralPath $legacyShortcutDirectory -Force)) { Remove-Item -LiteralPath $legacyShortcutDirectory -Force }
        }
    }
}
Write-Output "CloudInlet $version installed at $destination"
if (!$NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $destination }
