#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][Guid]$FixtureId,
    [Parameter(Mandatory)][Guid]$VmId,
    [Parameter(Mandatory)][Guid]$ExpectedFirmwareUuid,
    [Parameter(Mandatory)][string]$ExpectedAccountName,
    [Parameter(Mandatory)][switch]$ConfirmDisposableGuest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Enrollment is deliberately performed after signing into the disposable guest.
# No host credentials, profile, DPAPI receipt, or production settings are imported.
if (-not $IsWindows -or -not $ConfirmDisposableGuest -or
    $FixtureId -eq [Guid]::Empty -or $VmId -eq [Guid]::Empty -or $ExpectedFirmwareUuid -eq [Guid]::Empty) {
    throw 'Explicit disposable Windows guest identities and confirmation are required.'
}
$system = Get-CimInstance Win32_ComputerSystem
$product = Get-CimInstance Win32_ComputerSystemProduct
if ($system.Manufacturer -ne 'Microsoft Corporation' -or $system.Model -ne 'Virtual Machine' -or
    [Guid]$product.UUID -ne $ExpectedFirmwareUuid) {
    throw 'This is not the explicitly provisioned Hyper-V guest. Enrollment made no changes.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$expectedIdentity = $env:COMPUTERNAME + '\' + $ExpectedAccountName
if ($identity.Name -ne $expectedIdentity -or $ExpectedAccountName -notmatch '^CloudInletLab[A-Za-z0-9_-]*$' -or
    $identity.User.Value -notmatch '^S-1-5-21-.+-\d+$') {
    throw 'Sign into the dedicated local CloudInletLab test account before enrolling it.'
}
$profilePath = [IO.Path]::GetFullPath([Environment]::GetFolderPath('UserProfile'))
if (-not $profilePath.StartsWith('C:\Users\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The test account must use an ordinary local C:\Users profile.'
}
$labRoot = 'C:\CloudInletMigrationLab'
foreach ($path in @('C:\', 'C:\Users', $profilePath, $labRoot)) {
    if (Test-Path -LiteralPath $path) {
        if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Linked enrollment paths are not permitted: $path"
        }
    }
}
if (Test-Path -LiteralPath $labRoot) {
    throw 'The lab root already exists. Enrollment never replaces an existing fixture or receipt.'
}
$manifest = [ordered]@{
    schemaVersion = 1
    fixtureId = $FixtureId.ToString('D')
    vmId = $VmId.ToString('D')
    firmwareUuid = $ExpectedFirmwareUuid.ToString('D')
    computerName = $env:COMPUTERNAME
    expectedAccountSid = $identity.User.Value
    accountName = $identity.Name
    profilePath = $profilePath
    labRoot = $labRoot
    dedicatedGuest = $true
    enrolledUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 4))
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$protected = [Security.Cryptography.ProtectedData]::Protect($bytes,
    [Text.Encoding]::UTF8.GetBytes('CloudInlet.GuestAcceptance.Identity.v1'),
    [Security.Cryptography.DataProtectionScope]::CurrentUser)
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class CloudInletGuestEnrollmentDirectory {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr attributes);
    public static void CreateExclusive(string path) {
        if (!CreateDirectoryW(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
'@
[CloudInletGuestEnrollmentDirectory]::CreateExclusive($labRoot)
# Limit the enrollment and all subsequently inherited evidence to this account,
# SYSTEM and administrators; do not weaken or edit any pre-existing directory ACL.
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($identity.User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl',
        'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $labRoot -AclObject $acl
function Write-NewEnrollmentFile([string]$Name, [byte[]]$Contents) {
    $file = [IO.File]::Open((Join-Path $labRoot $Name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($Contents, 0, $Contents.Length); $file.Flush($true) } finally { $file.Dispose() }
}
Write-NewEnrollmentFile 'guest-identity.json' $bytes
Write-NewEnrollmentFile 'guest-identity.dpapi' $protected
[ordered]@{
    enrolled = $true
    manifestPath = Join-Path $labRoot 'guest-identity.json'
    manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $labRoot 'guest-identity.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    fixtureId = $FixtureId
    firmwareUuid = $ExpectedFirmwareUuid
    vmId = $VmId
    nativeAcceptanceRun = $false
    rebootPerformed = $false
} | ConvertTo-Json
