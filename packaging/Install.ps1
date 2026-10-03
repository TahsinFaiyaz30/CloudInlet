param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'App'
$sourceExe = Join-Path $source 'CloudBay.exe'
if (!(Test-Path -LiteralPath $sourceExe)) { throw 'Run this script from the complete CloudBay release folder.' }
if (![Environment]::Is64BitOperatingSystem) { throw 'CloudBay requires 64-bit Windows.' }
$version = ([Diagnostics.FileVersionInfo]::GetVersionInfo($sourceExe)).ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+([-.][a-zA-Z0-9.]+)?$') { throw 'Unexpected package version.' }
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\CloudBay'))
$destination = [IO.Path]::GetFullPath((Join-Path $installRoot $version))
if (!$destination.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid install destination.' }
for ($ancestor = [IO.DirectoryInfo]$destination; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The installation path contains a directory link. Choose a normal per-user installation location.'
    }
}
$running = @(Get-Process -Name CloudBay -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $shutdownProcess = Start-Process -FilePath $sourceExe -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
    $shutdownProcess.WaitForExit(10000) | Out-Null
    foreach ($process in $running) {
        if (!$process.WaitForExit(60000)) { throw 'CloudBay is still finishing a transfer. Quit it from its tray menu and run installation again.' }
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
$exe = Join-Path $destination 'CloudBay.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) 'CloudBay'
if ((Test-Path -LiteralPath $shortcutDirectory) -and ((Get-Item -LiteralPath $shortcutDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The CloudBay Start menu folder contains a link. Installation stopped.'
}
New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
$shortcut = $shell.CreateShortcut((Join-Path $shortcutDirectory 'CloudBay.lnk'))
$shortcut.TargetPath = $exe; $shortcut.WorkingDirectory = $destination; $shortcut.IconLocation = "$exe,0"; $shortcut.Save()
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudBay'
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'CloudBay' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $version -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'CloudBay' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value "$exe,0" -PropertyType String -Force | Out-Null
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall.ps1')`""
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Get-ItemProperty -LiteralPath $startupKey -Name CloudBay -ErrorAction SilentlyContinue).CloudBay) {
    New-ItemProperty -LiteralPath $startupKey -Name CloudBay -Value "`"$exe`" --background" -PropertyType String -Force | Out-Null
}
Write-Output "CloudBay $version installed at $destination"
if (!$NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $destination }
