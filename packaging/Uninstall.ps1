$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$allowedRoots = @('CloudInlet', 'CloudBay') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA ('Programs\' + $_))) }
if (!($allowedRoots | Where-Object { $_.Equals($installRoot, [StringComparison]::OrdinalIgnoreCase) })) {
    throw 'Run the installed uninstaller from the Windows Installed apps list.'
}
# The canonical registration owns this directory even when 1.1.2 retained its
# original CloudBay folder name. Do not adopt an unrelated folder by its name.
$registered = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudInlet' -ErrorAction SilentlyContinue
if (!$registered -or !$registered.InstallLocation -or
    ![IO.Path]::GetFullPath([string]$registered.InstallLocation).TrimEnd('\').Equals($installRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The CloudInlet registration does not own this installation directory. Repair the installation before uninstalling.'
}
for ($ancestor = [IO.DirectoryInfo]$installRoot; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The installation path contains a directory link. Uninstallation stopped before deleting files.'
    }
}
if (Get-ChildItem -LiteralPath $installRoot -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'The installation contains a linked item. Uninstallation stopped before deleting files.' }
# Never leave online-only data behind without its provider. Disconnect in the app first.
$providerPath = 'Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$registrationPrefixes = @("CloudInlet!$sid!", "CloudBay!$sid!")
$registrations = @(
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Registry64)
        try {
            $providerKey = $registry.OpenSubKey($providerPath)
            if ($null -ne $providerKey) {
                try {
                    $providerKey.GetSubKeyNames() | Where-Object {
                        $registration = $_
                        !!($registrationPrefixes | Where-Object { $registration.StartsWith($_, [StringComparison]::Ordinal) })
                    }
                }
                finally { $providerKey.Dispose() }
            }
        } finally { $registry.Dispose() }
    }
)
if ($registrations.Count -gt 0) {
    throw 'Open CloudInlet Settings and disconnect the account first so Windows folder locations and online-only files are handled before uninstallation.'
}
$processes = @(Get-Process -Name CloudInlet -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($processes.Count -gt 0) { throw 'Quit CloudInlet from the tray menu, then run uninstallation again.' }
$programsDirectory = [IO.Path]::GetFullPath([Environment]::GetFolderPath('Programs'))
$shell = New-Object -ComObject WScript.Shell
foreach ($name in @('CloudInlet')) {
    $shortcutDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory $name))
    if (!$shortcutDirectory.StartsWith($programsDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid shortcut directory.' }
    if ((Test-Path -LiteralPath $shortcutDirectory) -and ((Get-Item -LiteralPath $shortcutDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The Start menu folder contains a link. Uninstallation stopped.'
    }
    $shortcutPath = Join-Path $shortcutDirectory ($name + '.lnk')
    if (Test-Path -LiteralPath $shortcutPath) {
        if ((Get-Item -LiteralPath $shortcutPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The Start menu shortcut contains a link.' }
        $link = $shell.CreateShortcut($shortcutPath)
        if ($link.TargetPath -and [IO.Path]::GetFullPath($link.TargetPath).StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $shortcutPath -Force
            if (!(Get-ChildItem -LiteralPath $shortcutDirectory -Force)) { Remove-Item -LiteralPath $shortcutDirectory -Force }
        }
    }
    $run = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $name -ErrorAction SilentlyContinue
    if ($run -and $run.$name -match '^"(?<image>[^\"]+)"(?:\s|$)' -and
        [IO.Path]::GetFullPath($Matches.image).StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $name
    }
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $name
    $registration = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
    if ($registration -and $registration.InstallLocation -and
        [IO.Path]::GetFullPath([string]$registration.InstallLocation).Equals($installRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $uninstallKey -Recurse -Force
    }
}
# The validated target contains binaries only; per-user state, local folders, and B2 data are retained.
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
Write-Output 'CloudInlet uninstalled. Local files, B2 versions, and recovery data were retained.'
