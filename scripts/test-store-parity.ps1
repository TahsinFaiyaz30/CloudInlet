param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$AppFolder,
    [ValidateSet('Candidate','Default','Broad','Unpackaged')][string[]]$Policies = @('Unpackaged','Default','Broad','Candidate'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts/store-certification/parity')
)
$ErrorActionPreference = 'Stop'
$packagePath = [IO.Path]::GetFullPath($PackagePath)
$appFolder = [IO.Path]::GetFullPath($AppFolder)
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
function Assert-NoLinks([string]$Path) {
    for ($ancestor = $Path; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked validation path: $Path" }
    }
}
foreach ($path in @($packagePath,$appFolder,$outputRoot)) { Assert-NoLinks $path }
if (!(Test-Path -LiteralPath (Join-Path $appFolder 'CloudInlet.exe'))) { throw 'Published test application missing.' }
if (!(Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw 'Local validation package missing.' }
if (Get-AppxPackage -Name 'CloudInlet.LocalValidation') { throw 'An existing validation package must not be replaced.' }
$developerMode = Get-ItemProperty -LiteralPath 'HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/AppModelUnlock' -ErrorAction SilentlyContinue
if ($developerMode.AllowDevelopmentWithoutDevLicense -ne 1) { throw 'Existing Developer Mode is required. This script does not change security settings.' }
if (!('CloudInlet.StoreParity.Activation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace CloudInlet.StoreParity {
 [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 interface IActivation {
  [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
  [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint pid);
  [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr items, out uint pid);
 }
 public static class Activation {
  public static uint Launch(string id, string args) {
   var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
   try { uint pid; Marshal.ThrowExceptionForHR(((IActivation)instance).ActivateApplication(id,args,2,out pid)); return pid; }
   finally { Marshal.FinalReleaseComObject(instance); }
  }
 }
}
'@
}
function Get-UserSnapshot {
    $snapshot = [ordered]@{}
    foreach ($relative in @('Client','Debug/Client')) {
        foreach ($name in @('settings.json','settings.json.bak','credentials.dpapi','credentials.dpapi.bak','onedrive.dpapi','onedrive.dpapi.bak')) {
            $path = Join-Path $env:LOCALAPPDATA ('CloudBay/' + $relative + '/' + $name)
            $snapshot[$relative + '/' + $name] = if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { 'absent' }
        }
    }
    foreach ($name in @('User Shell Folders','Shell Folders')) {
        $values = [ordered]@{}
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\' + $name)
        try { if ($key) { foreach ($value in $key.GetValueNames() | Sort-Object) { $values[$value] = [string]$key.GetValue($value,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } } }
        finally { if ($key) { $key.Dispose() } }
        $snapshot[$name] = $values
    }
    $snapshot | ConvertTo-Json -Depth 5 -Compress
}
function Get-ExternalEvidence([string]$Token) {
    $path = Join-Path $env:LOCALAPPDATA ('CloudBay/Client/StoreValidation/' + $Token)
    $markers = [ordered]@{}
    foreach ($keyName in @('Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders','Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders','Software\CloudInlet.StoreParityControl')) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyName + '\CloudInletStoreParity-' + $Token)
        try { $markers[$keyName] = if ($key) { $key.GetValue('Token') -ceq $Token } else { $false } }
        finally { if ($key) { $key.Dispose() } }
    }
    [ordered]@{ settingsVisible = (Test-Path -LiteralPath (Join-Path $path 'settings.json')); credentialsVisible = (Test-Path -LiteralPath (Join-Path $path 'credentials.dpapi')); journalVisible = (Test-Path -LiteralPath (Join-Path $path 'transfers.db')); registryMarkers = $markers }
}
function Register-Fixture([string]$Payload) {
    if (Get-AppxPackage -Name 'CloudInlet.LocalValidation') { throw 'Unexpected existing validation registration.' }
    Add-AppxPackage -Register (Join-Path $Payload 'AppxManifest.xml') -ErrorAction Stop
    $registration = Get-AppxPackage -Name 'CloudInlet.LocalValidation'
    if (!$registration -or $registration.Publisher -cne 'CN=CloudInlet Local Validation' -or !$registration.InstallLocation.Equals($Payload,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected validation identity/location.' }
    return $registration.PackageFamilyName + '!CloudBay'
}
function Remove-Fixture([string]$Payload) {
    $registration = Get-AppxPackage -Name 'CloudInlet.LocalValidation'
    if (!$registration) { return }
    if ($registration.Publisher -cne 'CN=CloudInlet Local Validation' -or !$registration.InstallLocation.Equals($Payload,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing removal of an unexpected package.' }
    Remove-AppxPackage -Package $registration.PackageFullName -ErrorAction Stop
    if (Get-AppxPackage -Name 'CloudInlet.LocalValidation') { throw 'Validation package was not removed.' }
}
function Invoke-Probe([string]$Token,[string]$Phase,[string]$Directory,[string]$Aumid) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $arguments = @('--store-parity-probe',"--parity-token=$Token","--parity-phase=$Phase","--validation-output=$Directory")
    $process = $null
    try {
        if ($Aumid) {
            $command = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
            $probePid = [CloudInlet.StoreParity.Activation]::Launch($Aumid,$command)
            $process = Get-Process -Id $probePid -ErrorAction SilentlyContinue
        } else {
            $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appFolder 'CloudInlet.exe'))
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($start)
        }
        if ($process) {
            # Hold the exact launched process handle; never stop a client by name.
            $null = $process.Handle
            if (!$process.WaitForExit(100000)) {
                $process.Kill()
                $process.WaitForExit()
                throw 'The isolated probe timed out and was stopped before fixture cleanup.'
            }
        }
        $resultFile = Join-Path $Directory ($Phase + '.json')
        if (!(Test-Path -LiteralPath $resultFile)) { throw "Probe produced no evidence: $resultFile" }
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
        if ($result.token -cne $Token -or $result.phase -cne $Phase) { throw 'Probe returned a different fixture identity.' }
        return $result
    } finally { if ($process) { $process.Dispose() } }
}
$before = Get-UserSnapshot
$run = Join-Path $outputRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
try {
    foreach ($policy in $Policies) {
        $token = [Guid]::NewGuid().ToString('N')
        $workspace = Join-Path $run $policy
        $payload = Join-Path $workspace 'Payload'
        $evidence = Join-Path $workspace 'Evidence'
        New-Item -ItemType Directory -Path $payload -Force | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($packagePath,$payload)
        if (Get-ChildItem -LiteralPath $payload -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked package payload.' }
        $manifestPath = Join-Path $payload 'AppxManifest.xml'
        [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
        $identity = $manifest.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
        $app = $manifest.DocumentElement.SelectSingleNode("*[local-name()='Applications']/*[local-name()='Application']")
        if ($identity.GetAttribute('Name') -cne 'CloudInlet.LocalValidation' -or $identity.GetAttribute('Publisher') -cne 'CN=CloudInlet Local Validation' -or $app.GetAttribute('Id') -cne 'CloudBay' -or $app.GetAttribute('Executable') -cne 'CloudInlet.exe') { throw 'Only the dedicated local validation package is allowed.' }
        if ($policy -in @('Broad','Default')) {
            foreach ($node in @($manifest.SelectNodes("//*[namespace-uri()='http://schemas.microsoft.com/appx/manifest/virtualization/windows10']"))) { $null = $node.ParentNode.RemoveChild($node) }
        }
        if ($policy -eq 'Default') {
            foreach ($node in @($manifest.SelectNodes("//*[local-name()='RegistryWriteVirtualization' or local-name()='FileSystemWriteVirtualization' or @Name='unvirtualizedResources']"))) { $null = $node.ParentNode.RemoveChild($node) }
        }
        $manifest.Save($manifestPath)
        $entry = [ordered]@{ policy=$policy; token=$token; passed=$false; limitations=@('No real user Known Folder redirection','No live B2/OneDrive credentials or network transfer'); phases=[ordered]@{} }
        $aumid = ''
        try {
            if ($policy -ne 'Unpackaged') { $aumid = Register-Fixture $payload }
            $seed = Invoke-Probe $token 'seed' $evidence $aumid
            $entry.phases.seed = $seed
            if (!$seed.passed) { throw $seed.error }
            $entry.externalBeforeRemoval = Get-ExternalEvidence $token
            if ($policy -ne 'Unpackaged') { Remove-Fixture $payload }
            $entry.externalAfterRemoval = Get-ExternalEvidence $token
            if ($policy -ne 'Unpackaged') { $aumid = Register-Fixture $payload }
            $resume = Invoke-Probe $token 'resume' $evidence $aumid
            $entry.phases.resume = $resume
            if ($policy -eq 'Default') {
                # An existing real AppData parent can be used through Windows' fallback;
                # do not manufacture an expected data-loss result. Registry isolation is
                # independently observable in a new key under each real Shell key.
                if (@($entry.externalBeforeRemoval.registryMarkers.Values | Where-Object { $_ }).Count) { throw 'Default policy unexpectedly exposed an external registry marker.' }
                if (!$resume.passed -and $entry.externalBeforeRemoval.settingsVisible) { throw $resume.error }
                $entry.expectedIsolationDifference = $true
                $entry.sharedStatePresentOnThisMachine = $entry.externalBeforeRemoval.settingsVisible
                $entry.passed = $true
            } else {
                if (!$resume.passed) { throw $resume.error }
                if (!$entry.externalBeforeRemoval.settingsVisible -or !$entry.externalAfterRemoval.credentialsVisible) { throw 'Shared state was not retained outside package isolation.' }
                $markers = @($entry.externalBeforeRemoval.registryMarkers.Values)
                if (!$markers[0] -or !$markers[1]) { throw 'Windows Shell mapping-key markers were not visible outside the package.' }
                if ($policy -eq 'Candidate' -and $markers[2]) { throw 'The unrelated registry control key escaped candidate virtualization.' }
                $disconnect = Invoke-Probe $token 'disconnect' $evidence $aumid
                $entry.phases.disconnect = $disconnect
                if (!$disconnect.passed -or !$disconnect.registrationRemoved -or !$disconnect.ordinaryFilePreserved) { throw 'Explicit safe disconnect failed.' }
                if ($policy -ne 'Unpackaged') { Remove-Fixture $payload; $aumid = Register-Fixture $payload }
                $verify = Invoke-Probe $token 'verify' $evidence $aumid
                $entry.phases.verify = $verify
                if (!$verify.passed) { throw $verify.error }
                $entry.passed = $true
            }
        } catch { $entry.error = $_.Exception.ToString() }
        finally {
            Remove-Fixture $payload
            $cleanup = Invoke-Probe $token 'cleanup' (Join-Path $evidence 'Cleanup') ''
            $entry.cleanup = $cleanup
            if (!$cleanup.passed) { $entry.passed = $false }
            $state = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA ('CloudBay/Client/StoreValidation/' + $token)))
            $parent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CloudBay/Client/StoreValidation'))
            if ([IO.Path]::GetDirectoryName($state) -cne $parent -or [IO.Path]::GetFileName($state) -cne $token) { throw 'Fixture state escaped its expected directory.' }
            Assert-NoLinks $state
            if (Test-Path -LiteralPath $state) {
                if (Get-ChildItem -LiteralPath $state -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked state fixture; cleanup stopped.' }
                Remove-Item -LiteralPath $state -Recurse -Force
            }
            if ((Get-UserSnapshot) -cne $before) { throw 'Real client state or Windows folder mappings changed. Stop and investigate.' }
            $entry | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workspace 'result.json') -Encoding utf8NoBOM
            $results.Add($entry)
        }
    }
} finally {
    [ordered]@{ testedUtc=[DateTimeOffset]::UtcNow; results=$results.ToArray(); realClientStatePreserved=((Get-UserSnapshot) -ceq $before); developerModeChanged=$false; certificateTrustChanged=$false } | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8NoBOM
    Write-Output (Join-Path $run 'summary.json')
}
if (@($results | Where-Object { !$_.passed }).Count -gt 0) { throw 'At least one policy failed. Inspect the retained evidence.' }
