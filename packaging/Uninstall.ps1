$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\CloudBay'))
if (![IO.Path]::GetFullPath($PSScriptRoot).Equals($installRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run the installed uninstaller from the Windows Installed apps list.'
}
for ($ancestor = [IO.DirectoryInfo]$installRoot; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The installation path contains a directory link. Uninstallation stopped before deleting files.'
    }
}
# Never leave online-only data behind without its provider. Disconnect in the app first.
$providerPath = 'Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$registrationPrefix = "CloudBay!$sid!"
$registrations = @(
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Registry64)
        try {
            $providerKey = $registry.OpenSubKey($providerPath)
            if ($null -ne $providerKey) {
                try { $providerKey.GetSubKeyNames() | Where-Object { $_.StartsWith($registrationPrefix, [StringComparison]::Ordinal) } }
                finally { $providerKey.Dispose() }
            }
        } finally { $registry.Dispose() }
    }
)
if ($registrations.Count -gt 0) {
    throw 'Open CloudBay Settings and disconnect the account first. CloudBay downloads all files and restores Windows folder locations before uninstallation can proceed.'
}
$processes = @(Get-Process -Name CloudBay -ErrorAction SilentlyContinue)
if ($processes.Count -gt 0) { throw 'Quit CloudBay from the tray menu, then run uninstallation again.' }
$shortcutDirectory = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('Programs')) 'CloudBay'))
$programsDirectory = [IO.Path]::GetFullPath([Environment]::GetFolderPath('Programs'))
if (!$shortcutDirectory.StartsWith($programsDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid shortcut directory.' }
if ((Test-Path -LiteralPath $shortcutDirectory) -and ((Get-Item -LiteralPath $shortcutDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The Start menu folder contains a link. Uninstallation stopped.'
}
if (Test-Path -LiteralPath $shortcutDirectory) { Remove-Item -LiteralPath $shortcutDirectory -Recurse -Force }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name CloudBay -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudBay' -Recurse -Force -ErrorAction SilentlyContinue
# The validated target contains binaries only; per-user state, local folders, and B2 data are retained.
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
Write-Output 'CloudBay uninstalled. Local files, B2 versions, and recovery data were retained.'
