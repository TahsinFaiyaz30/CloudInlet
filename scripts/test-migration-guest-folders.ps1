#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prepare', 'VerifyBeforeRestart', 'VerifyAfterRestart', 'Revert', 'RestoreMappings', 'RevertPriorRuntime', 'RestorePriorRuntime')]
    [string]$Stage,
    [Parameter(Mandatory)][string]$TestAssembly,
    [Guid]$ScenarioId = [Guid]::Empty,
    [string]$PriorRuntimeInventory,
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$PriorRuntimeInventorySha256,
    [Parameter(Mandatory)][switch]$ConfirmDisposableGuest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or -not $ConfirmDisposableGuest) { throw 'Explicit disposable Windows guest confirmation is required.' }
$identityPath = 'C:\CloudInletMigrationLab\guest-identity.json'
$envelopePath = 'C:\CloudInletMigrationLab\guest-identity.dpapi'
if (-not (Test-Path -LiteralPath $identityPath) -or -not (Test-Path -LiteralPath $envelopePath)) {
    throw 'The explicitly provisioned guest must be enrolled under its disposable test account first.'
}
$assembly = (Resolve-Path -LiteralPath $TestAssembly).Path
if ([IO.Path]::GetFileName($assembly) -ne 'CloudInlet.Tests.dll') { throw 'Select the verified built CloudInlet.Tests.dll.' }
$testRoot = [IO.Path]::GetDirectoryName($assembly)
if ((Get-Item -LiteralPath $assembly -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked test inputs are not permitted.' }
if (-not (Test-Path -LiteralPath (Join-Path $testRoot 'CloudInlet.Tests.runtimeconfig.json'))) { throw 'Copy the complete verified test output, including dependencies.' }
# The test independently authenticates the enrollment, firmware UUID, SID, profile,
# boot identity and protected phase receipt before making any fixture/Shell change.
$oldOptIn = $env:CLOUDINLET_RUN_GUEST_FOLDER_ACCEPTANCE
$oldStage = $env:CLOUDINLET_GUEST_FOLDER_ACCEPTANCE_STAGE
$oldScenario = $env:CLOUDINLET_GUEST_FOLDER_SCENARIO_ID
$oldInventory = $env:CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY
$oldInventoryHash = $env:CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY_SHA256
if ($Stage -in @('RevertPriorRuntime','RestorePriorRuntime')) {
    if (!$PriorRuntimeInventory -or !$PriorRuntimeInventorySha256 -or $ScenarioId -eq [Guid]::Empty) {
        throw 'Recovery across test runtimes requires an explicit existing scenario and pinned previous inventory.'
    }
} elseif ($PriorRuntimeInventory -or $PriorRuntimeInventorySha256) { throw 'Previous-runtime inputs are limited to the explicit recovery stages.' }
try {
    $env:CLOUDINLET_RUN_GUEST_FOLDER_ACCEPTANCE = '1'
    $env:CLOUDINLET_GUEST_FOLDER_ACCEPTANCE_STAGE = $Stage
    $env:CLOUDINLET_GUEST_FOLDER_SCENARIO_ID = if ($ScenarioId -eq [Guid]::Empty) { $null } else { $ScenarioId.ToString('D') }
    $env:CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY = $PriorRuntimeInventory
    $env:CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY_SHA256 = $PriorRuntimeInventorySha256
    $resultRoot = Join-Path $env:TEMP ('CloudInlet-GuestAcceptance-Results-' + [Guid]::NewGuid().ToString('N'))
    & dotnet vstest $assembly '/TestCaseFilter:FullyQualifiedName~GuestKnownFolderAcceptanceTests' "/ResultsDirectory:$resultRoot" '/Logger:trx;LogFileName=guest-folders.trx'
    if ($LASTEXITCODE -ne 0) { throw "Guest acceptance failed; retain every fixture and receipt. Results: $resultRoot" }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $resultRoot 'guest-folders.trx') -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ([int]$counts.passed -ne 1 -or [int]$counts.total -ne 1) {
        throw "The selected guest acceptance stage did not pass exactly once. Results: $resultRoot"
    }
    Write-Output "Guest stage '$Stage' passed. Results: $resultRoot"
    if ($Stage -eq 'VerifyBeforeRestart') {
        Write-Output 'No reboot was performed. Gracefully restart only this dedicated guest, sign into the same test account, then run VerifyAfterRestart.'
    }
} finally {
    $env:CLOUDINLET_RUN_GUEST_FOLDER_ACCEPTANCE = $oldOptIn
    $env:CLOUDINLET_GUEST_FOLDER_ACCEPTANCE_STAGE = $oldStage
    $env:CLOUDINLET_GUEST_FOLDER_SCENARIO_ID = $oldScenario
    $env:CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY = $oldInventory
    $env:CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY_SHA256 = $oldInventoryHash
}
