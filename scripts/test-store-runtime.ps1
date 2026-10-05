param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts/store-runtime-smoke')
)
$ErrorActionPreference = 'Stop'
$packagePath = [IO.Path]::GetFullPath($PackagePath)
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
foreach ($candidate in @($packagePath, $outputRoot)) {
    for ($ancestor = $candidate; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Store runtime validation paths cannot contain links.' }
    }
}
if (!(Test-Path -LiteralPath $packagePath -PathType Leaf) -or [IO.Path]::GetExtension($packagePath) -cne '.msix') { throw 'Provide the local validation MSIX package.' }
$developerMode = Get-ItemProperty -LiteralPath 'HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/AppModelUnlock' -ErrorAction SilentlyContinue
if ($developerMode.AllowDevelopmentWithoutDevLicense -ne 1) { throw 'Existing Windows Developer Mode is required for unsigned development registration. This test does not change Developer Mode or certificate trust.' }
if (Get-AppxPackage -Name 'CloudBay.LocalValidation') { throw 'A local CloudBay validation package is already registered. This test will not replace an existing package.' }
$workspace = Join-Path $outputRoot ([Guid]::NewGuid().ToString('N'))
$payload = Join-Path $workspace 'Payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $payload)
if (Get-ChildItem -LiteralPath $payload -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'The extracted test payload contains a linked item.' }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $payload 'AppxManifest.xml') -Raw
$identity = $manifest.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
$applications = @($manifest.DocumentElement.SelectNodes("*[local-name()='Applications']/*[local-name()='Application']"))
if (!$identity -or $identity.GetAttribute('Name') -cne 'CloudBay.LocalValidation' -or $identity.GetAttribute('Publisher') -cne 'CN=CloudBay Local Validation' -or $applications.Count -ne 1 -or $applications[0].GetAttribute('Id') -cne 'CloudBay' -or $applications[0].GetAttribute('Executable') -cne 'CloudBay.exe') { throw 'Only the separate CloudBay.LocalValidation identity may be registered by this test.' }
$expectedVersion = ([Version]$identity.GetAttribute('Version')).ToString(3)
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'CloudBay.exe')).ProductVersion.Split('+')[0] -cne $expectedVersion) { throw 'The Store test payload version does not match its manifest.' }
if (!('CloudBay.StoreRuntimeTests.Activation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace CloudBay.StoreRuntimeTests {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.Interface)] object items, out uint processId);
    }
    public static class Activation {
        public static uint Launch(string id, string arguments) {
            var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
            try {
                uint processId;
                int result = ((IApplicationActivationManager)instance).ActivateApplication(id, arguments, 2, out processId);
                Marshal.ThrowExceptionForHR(result);
                return processId;
            } finally { Marshal.FinalReleaseComObject(instance); }
        }
    }
}
'@
}
$registered = $null
function Get-ClientStateSnapshot {
    $state = [ordered]@{}
    foreach ($relative in @('Client', 'Debug/Client')) {
        foreach ($name in @('settings.json', 'credentials.dpapi')) {
            $path = Join-Path $env:LOCALAPPDATA ('CloudBay/' + $relative + '/' + $name)
            $state[$relative + '/' + $name] = if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { 'absent' }
        }
    }
    $mappings = [ordered]@{}
    $folderKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders')
    try {
        if ($folderKey) { foreach ($name in $folderKey.GetValueNames() | Sort-Object) { $mappings[$name] = [string]$folderKey.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } }
    } finally { if ($folderKey) { $folderKey.Dispose() } }
    $state['knownFolderMappings'] = $mappings
    $state | ConvertTo-Json -Depth 4 -Compress
}
$before = Get-ClientStateSnapshot
try {
    Add-AppxPackage -Register (Join-Path $payload 'AppxManifest.xml') -ErrorAction Stop
    $registered = Get-AppxPackage -Name 'CloudBay.LocalValidation'
    if (!$registered -or $registered.Publisher -cne 'CN=CloudBay Local Validation' -or !$registered.InstallLocation.Equals($payload, [StringComparison]::OrdinalIgnoreCase)) { throw 'The newly registered test package does not match its isolated payload.' }
    $aumid = $registered.PackageFamilyName + '!CloudBay'
    $resultDirectory = Join-Path $workspace 'Result'
    New-Item -ItemType Directory -Path $resultDirectory | Out-Null
    $arguments = '--ui-smoke --store-runtime-smoke "--validation-output=' + $resultDirectory + '"'
    $processId = [CloudBay.StoreRuntimeTests.Activation]::Launch($aumid, $arguments)
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        if ((Test-Path -LiteralPath (Join-Path $resultDirectory 'complete.json')) -or (Test-Path -LiteralPath (Join-Path $resultDirectory 'failure.txt'))) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    $failure = Join-Path $resultDirectory 'failure.txt'
    if (Test-Path -LiteralPath $failure) { throw "The packaged app reported a runtime failure. Inspect $failure" }
    $completion = Join-Path $resultDirectory 'complete.json'
    if (!(Test-Path -LiteralPath $completion)) { throw "The packaged app did not complete the isolated runtime probe. Activation process: $processId; diagnostics: $resultDirectory" }
    $result = Get-Content -LiteralPath $completion -Raw | ConvertFrom-Json
    if ($result.version -cne $expectedVersion -or $result.packageName -cne 'CloudBay.LocalValidation' -or $result.packageFamilyName -cne $registered.PackageFamilyName -or $result.installerKind -cne 'Store' -or $result.startupTaskId -cne 'CloudBayStartup') { throw 'The packaged runtime identity or startup-task probe returned unexpected results.' }
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process -and !$process.WaitForExit(10000)) { throw 'The isolated Store runtime process did not finish gracefully.' }
    Write-Output "Store runtime validation passed: $completion"
} finally {
    # Remove only this newly registered development identity at its exact expected payload path.
    $current = Get-AppxPackage -Name 'CloudBay.LocalValidation'
    if ($current -and $current.Publisher -ceq 'CN=CloudBay Local Validation' -and $current.InstallLocation.Equals($payload, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-AppxPackage -Package $current.PackageFullName -ErrorAction Stop
    }
    if (Get-AppxPackage -Name 'CloudBay.LocalValidation') { throw 'The isolated Store test package is still registered. Inspect its registration before retrying.' }
    $after = Get-ClientStateSnapshot
    if ($before -cne $after) { throw 'Account settings, credential bytes, or Windows folder mappings changed during Store validation. Investigate before accepting the test.' }
    [ordered]@{ packageName='CloudBay.LocalValidation'; registrationRemoved=$true; certificateTrustChanged=$false; developerModeChanged=$false; clientStatePreserved=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'cleanup-validation.json') -Encoding utf8NoBOM
}
