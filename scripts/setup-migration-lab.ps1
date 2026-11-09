#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Create')][string]$Mode = 'Plan',
    [string]$IsoPath,
    [switch]$WritePlan,
    [string]$PlanPath,
    [string]$PlanSha256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'This setup requires Windows and PowerShell 7.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
Import-Module (Join-Path $PSScriptRoot 'MigrationLabPlan.psm1') -Force

if ($Mode -eq 'Plan') {
    if ($PlanPath -or $PlanSha256) { throw 'PlanPath and PlanSha256 apply only to Create.' }
    $plan = New-MigrationLabPlan -WorkspaceRoot $workspace -IsoPath $IsoPath
    $json = $plan | ConvertTo-Json -Depth 8
    if ($WritePlan) {
        $directory = Join-Path $workspace 'artifacts/migration-vm-plans'
        Assert-LabOrdinaryAncestors $directory
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $path = Join-Path $directory "$($plan.runId).json"
        $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally { $stream.Dispose() }
        [pscustomobject]@{ planPath = $path; planSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; readyForCreate = $plan.readyForCreate }
    } else { $json }
    return
}

if ($IsoPath -or $WritePlan -or !$PlanPath -or !$PlanSha256) {
    throw 'Create requires only the reviewed PlanPath and PlanSha256.'
}
$plan = Read-MigrationLabPlan -PlanPath $PlanPath -PlanSha256 $PlanSha256 -WorkspaceRoot $workspace
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Create must run in an elevated PowerShell 7 terminal. It never elevates itself or changes group membership.'
}
Import-Module Hyper-V -ErrorAction Stop
# Enumeration must succeed before any write, including directories or the VHD.
$vmsBefore = @(Get-VM -ErrorAction Stop)
if (@($vmsBefore | Where-Object Name -eq $plan.vmName).Count -ne 0) { throw 'The planned VM name already exists.' }
$volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($plan.labRoot))
if (!$volume.IsReady -or $volume.DriveType -ne 'Fixed' -or $volume.DriveFormat -ne 'NTFS') {
    throw 'The dedicated lab must be on a local fixed NTFS drive.'
}
if ($volume.AvailableFreeSpace -lt ([long]$plan.diskBytes + 8GB)) {
    throw 'The lab drive needs at least 88 GiB free before creating the 80 GiB dynamic disk.'
}
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
$memoryAtCreation = Get-MigrationLabMemoryReadiness -FreePhysicalMemoryBytes ([long]$os.FreePhysicalMemory * 1KB)
if (!$memoryAtCreation.memoryReadyForStart) {
    Write-Warning 'The VM can be created powered off, but memory is not ready for startup. Keep it off until a fresh check finds at least 6 GiB free physical memory. The guest remains configured for 4 GiB.'
}
# Keep the exact ISO stable until its DVD attachment has been verified.
$isoStream = [IO.File]::Open($plan.iso.path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$audit = $null
$vm = $null
try {
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($isoStream)) -cne $plan.iso.sha256) {
        throw 'The ISO changed after planning. Create and review a new plan.'
    }
    Assert-MigrationLabPlan $plan $workspace
    # CreateDirectoryW rejects an occupied directory atomically, unlike CreateDirectory.
    if ($null -eq ('CloudInletMigrationLabDirectory' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class CloudInletMigrationLabDirectory {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr attributes);
    public static void CreateExclusive(string path) {
        if (!CreateDirectoryW(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
'@
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $plan.labRoot)) | Out-Null
    Assert-LabOrdinaryAncestors $plan.labRoot
    [CloudInletMigrationLabDirectory]::CreateExclusive($plan.labRoot)
    $audit = [IO.File]::Open($plan.evidencePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    function Write-LabEvent([string]$Event, $Details) {
        $record = [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); event = $Event; runId = $plan.runId; details = $Details }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($record | ConvertTo-Json -Depth 8 -Compress) + "`n")
        $audit.Write($bytes, 0, $bytes.Length)
        $audit.Flush($true)
    }
    Write-LabEvent 'creation-intent' @{ planSha256 = $PlanSha256.ToUpperInvariant(); vmName = $plan.vmName; labRoot = $plan.labRoot; isoSha256 = $plan.iso.sha256; memoryAtCreation = $memoryAtCreation }
    $vm = New-VM -Name $plan.vmName -Generation 2 -MemoryStartupBytes $plan.memoryBytes `
        -Path $plan.configurationPath -NewVHDPath $plan.diskPath -NewVHDSizeBytes $plan.diskBytes -ErrorAction Stop
    Write-LabEvent 'vm-created' @{ vmId = $vm.Id.ToString(); state = $vm.State.ToString() }
    if ($vm.Generation -ne 2 -or $vm.State -ne 'Off' -or @($vmsBefore | Where-Object Id -eq $vm.Id).Count -ne 0) {
        throw 'New-VM did not return a fresh powered-off Generation 2 VM.'
    }
    # Every subsequent mutation targets the returned identity, never an existing VM by name.
    Set-VM -VM $vm -AutomaticStartAction Nothing -AutomaticStopAction ShutDown -CheckpointType Disabled -ErrorAction Stop
    Set-VMProcessor -VM $vm -Count $plan.processorCount -ErrorAction Stop
    Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes $plan.memoryBytes -ErrorAction Stop
    Get-VMNetworkAdapter -VM $vm -ErrorAction Stop | Disconnect-VMNetworkAdapter -ErrorAction Stop
    Write-LabEvent 'offline-configuration-applied' @{ vmId = $vm.Id.ToString(); automaticStartAction = 'Nothing'; memoryBytes = $plan.memoryBytes; processorCount = $plan.processorCount }
    Set-VMKeyProtector -VM $vm -NewLocalKeyProtector -ErrorAction Stop
    Enable-VMTPM -VM $vm -ErrorAction Stop
    Add-VMDvdDrive -VM $vm -Path $plan.iso.path -ErrorAction Stop
    $dvd = @(Get-VMDvdDrive -VM $vm -ErrorAction Stop)
    if ($dvd.Count -ne 1 -or $dvd[0].Path -cne $plan.iso.path) { throw 'The guest DVD identity differs from the planned ISO.' }
    Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows -FirstBootDevice $dvd[0] -ErrorAction Stop
    Write-LabEvent 'firmware-configuration-applied' @{ vmId = $vm.Id.ToString(); secureBoot = $true; localTpm = $true; isoSha256 = $plan.iso.sha256 }
    $created = Get-VM -Id $vm.Id -ErrorAction Stop
    $adapters = @(Get-VMNetworkAdapter -VM $created -ErrorAction Stop)
    $disks = @(Get-VMHardDiskDrive -VM $created -ErrorAction Stop)
    $security = Get-VMSecurity -VM $created -ErrorAction Stop
    $firmware = Get-VMFirmware -VM $created -ErrorAction Stop
    $firmwareUuid = $null
    if ($null -ne $created.PSObject.Properties['BIOSGUID'] -and $null -ne $created.BIOSGUID) {
        $firmwareId = [Guid]::Parse($created.BIOSGUID.ToString())
        if ($firmwareId -ne [Guid]::Empty) { $firmwareUuid = $firmwareId.ToString('D') }
    }
    if ($created.State -ne 'Off' -or $created.AutomaticStartAction -ne 'Nothing' -or
        @($adapters | Where-Object { ![string]::IsNullOrEmpty($_.SwitchName) }).Count -ne 0 -or
        $disks.Count -ne 1 -or $disks[0].Path -cne $plan.diskPath -or !$security.TpmEnabled -or
        $firmware.SecureBoot -ne 'On') { throw 'The resulting VM failed offline configuration verification.' }
    Write-LabEvent 'setup-complete' @{
        vmId = $created.Id.ToString(); firmwareUuid = $firmwareUuid; state = 'Off'; network = 'Disconnected'; diskPath = $plan.diskPath
        isoSha256 = $plan.iso.sha256; tpmEnabled = $true; secureBoot = $true
        memoryAtCreation = $memoryAtCreation
        osInstalled = $false; actualRebootExercised = $false; actualKnownFolderMigrationExercised = $false
    }
    [pscustomobject]@{ status = 'CreatedOff'; vmId = $created.Id; firmwareUuid = $firmwareUuid; fixtureId = $plan.runId; vmName = $created.Name; evidencePath = $plan.evidencePath; memoryAtCreation = $memoryAtCreation; nextStep = 'Recheck at least 6 GiB free physical memory before starting this 4 GiB guest. Then install Windows interactively and create a disposable local account. See docs/MIGRATION_VM_SETUP.md.' }
} catch {
    if ($null -ne $audit) {
        Write-LabEvent 'setup-failed-retained' @{ errorType = $_.Exception.GetType().FullName; message = $_.Exception.Message; vmId = $(if ($null -ne $vm) { $vm.Id.ToString() } else { $null }); automaticCleanup = $false }
    }
    throw
} finally {
    if ($null -ne $audit) { $audit.Dispose() }
    $isoStream.Dispose()
}
