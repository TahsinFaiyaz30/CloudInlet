#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
Import-Module (Join-Path $PSScriptRoot 'MigrationLabPlan.psm1') -Force
$testId = [Guid]::NewGuid().ToString('N')
$evidence = Join-Path $repository "artifacts/migration-lab-script-tests/$testId"
Assert-LabOrdinaryAncestors $evidence
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$workspace = Join-Path $evidence 'workspace'
[IO.Directory]::CreateDirectory((Join-Path $workspace 'scripts')) | Out-Null
[IO.File]::WriteAllText((Join-Path $workspace 'CloudInlet.sln'), 'fixture only; no VM is created')
foreach ($name in @('setup-migration-lab.ps1', 'MigrationLabPlan.psm1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $workspace "scripts/$name")
}
$isoPath = Join-Path $workspace 'nonbootable-fixture.iso'
[IO.File]::WriteAllText($isoPath, 'not an OS image; pure validation fixture only')
$results = [Collections.Generic.List[object]]::new()
function Assert-Check([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([ordered]@{ name = $Name; passed = $true }) }
    catch { $results.Add([ordered]@{ name = $Name; passed = $false; error = $_.Exception.Message }) }
}
function Assert-Rejected([scriptblock]$Body) {
    $rejected = $false
    try { & $Body | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw 'Expected validation to reject this input.' }
}
function Copy-Plan($Plan) { return $Plan | ConvertTo-Json -Depth 8 | ConvertFrom-Json }
$plan = New-MigrationLabPlan -WorkspaceRoot $workspace -IsoPath $isoPath
Assert-Check 'fresh plan is offline, unique and bounded' {
    Assert-MigrationLabPlan (Copy-Plan $plan) $workspace
    if ($plan.network -ne 'Disconnected' -or $plan.startAfterCreate -or $plan.automaticStartAction -ne 'Nothing' -or
        $plan.memoryBytes -ne 4GB -or $plan.minimumFreeMemoryBeforeStartBytes -ne 6GB -or
        !$plan.memoryReadinessMustBeRecheckedBeforeStart -or $plan.vmName -notmatch $plan.runId -or
        (Test-Path -LiteralPath $plan.labRoot)) { throw 'Planning mutated the lab or failed its invariant.' }
}
foreach ($available in @([long]0, [long]2GB, [long](6GB - 1), [long]6GB, [long]8GB)) {
    Assert-Check "startup memory readiness at $available bytes remains separate from creation" {
        $readiness = Get-MigrationLabMemoryReadiness -FreePhysicalMemoryBytes $available
        $observed = [DateTime]::MinValue
        if ($readiness.freePhysicalMemoryBytes -ne $available -or $readiness.requiredFreePhysicalMemoryBytes -ne 6GB -or
            $readiness.memoryReadyForStart -ne ($available -ge 6GB) -or !$readiness.recheckRequiredBeforeStart -or
            ![DateTime]::TryParse($readiness.checkedUtc, [ref]$observed)) {
            throw 'Memory readiness must preserve the 6 GiB threshold and require a fresh pre-start check.'
        }
        Assert-MigrationLabPlan (Copy-Plan $plan) $workspace
        if (Test-Path -LiteralPath $plan.labRoot) { throw 'Readiness evaluation wrote a VM fixture.' }
    }
}
Assert-Check 'negative free-memory observations are rejected' {
    Assert-Rejected { Get-MigrationLabMemoryReadiness -FreePhysicalMemoryBytes -1 }
}
Assert-Check 'setup cannot start or resume any VM' {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'setup-migration-lab.ps1'), [ref]$tokens, [ref]$errors)
    $starts = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -in @('Start-VM', 'Resume-VM', 'Restart-VM')
    }, $true))
    if ($errors.Count -ne 0 -or $starts.Count -ne 0) { throw 'Creation must leave the VM powered off.' }
}
Assert-Check 'missing ISO is a reviewable plan, never creation authority' {
    $missing = New-MigrationLabPlan -WorkspaceRoot $workspace
    if ($missing.readyForCreate) { throw 'Missing image was marked ready.' }
    Assert-Rejected { Assert-MigrationLabPlan (Copy-Plan $missing) $workspace }
}
foreach ($invalid in @('relative/path', '\\server\share\lab', 'C:\', 'C:\lab\*.iso', 'C:\lab\test.iso:alternate')) {
    Assert-Check "nonliteral or nonlocal path rejected: $invalid" { Assert-Rejected { Get-LabLocalPath $invalid } }
}
foreach ($change in @(
    @{ name = 'vmName'; value = 'ExistingPersonalVm' },
    @{ name = 'labRoot'; value = (Join-Path $workspace 'personal') },
    @{ name = 'diskPath'; value = (Join-Path $workspace 'personal.vhdx') },
    @{ name = 'evidencePath'; value = (Join-Path $workspace 'personal.jsonl') },
    @{ name = 'network'; value = 'Default Switch' },
    @{ name = 'startAfterCreate'; value = $true },
    @{ name = 'automaticStartAction'; value = 'Start' },
    @{ name = 'generation'; value = 1 },
    @{ name = 'memoryBytes'; value = [long]16GB },
    @{ name = 'minimumFreeMemoryBeforeStartBytes'; value = [long]4GB },
    @{ name = 'memoryReadinessMustBeRecheckedBeforeStart'; value = $false },
    @{ name = 'diskBytes'; value = [long]500GB },
    @{ name = 'actualRebootExercised'; value = $true }
)) {
    Assert-Check "changed $($change.name) rejected" {
        $changed = Copy-Plan $plan
        $changed.($change.name) = $change.value
        Assert-Rejected { Assert-MigrationLabPlan $changed $workspace }
    }
}
Assert-Check 'occupied lab root rejected without removing anything' {
    [IO.Directory]::CreateDirectory($plan.labRoot) | Out-Null
    $sentinel = Join-Path $plan.labRoot 'preserve.txt'
    [IO.File]::WriteAllText($sentinel, 'preserve')
    Assert-Rejected { Assert-MigrationLabPlan (Copy-Plan $plan) $workspace }
    if ([IO.File]::ReadAllText($sentinel) -ne 'preserve') { throw 'Occupied content changed.' }
}
$plan = New-MigrationLabPlan -WorkspaceRoot $workspace -IsoPath $isoPath
$planDirectory = Join-Path $workspace 'artifacts/migration-vm-plans'
[IO.Directory]::CreateDirectory($planDirectory) | Out-Null
$planPath = Join-Path $planDirectory "$($plan.runId).json"
[IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json -Depth 8))
$planHash = (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash
Assert-Check 'same reviewed bytes can be loaded' {
    $loaded = Read-MigrationLabPlan $planPath $planHash $workspace
    if ($loaded.runId -ne $plan.runId) { throw 'Loaded identity differs.' }
}
Assert-Check 'wrong plan digest rejected' { Assert-Rejected { Read-MigrationLabPlan $planPath ('0' * 64) $workspace } }
Assert-Check 'plan outside generated directory rejected' {
    $other = Join-Path $workspace "$($plan.runId).json"
    Copy-Item -LiteralPath $planPath -Destination $other
    Assert-Rejected { Read-MigrationLabPlan $other $planHash $workspace }
}
Assert-Check 'renamed plan identity rejected even with valid digest' {
    $other = Join-Path $planDirectory "$([Guid]::NewGuid().ToString('N')).json"
    Copy-Item -LiteralPath $planPath -Destination $other
    Assert-Rejected { Read-MigrationLabPlan $other $planHash $workspace }
}
Assert-Check 'changed setup scripts invalidate previously reviewed plan' {
    [IO.File]::AppendAllText((Join-Path $workspace 'scripts/setup-migration-lab.ps1'), "`n# changed fixture")
    Assert-Rejected { Read-MigrationLabPlan $planPath $planHash $workspace }
}
Assert-Check 'existing junction ancestor rejected' {
    $target = Join-Path $workspace 'junction-target'
    [IO.Directory]::CreateDirectory($target) | Out-Null
    $junction = Join-Path $workspace 'junction'
    New-Item -ItemType Junction -Path $junction -Value $target -ErrorAction Stop | Out-Null
    Assert-Rejected { Assert-LabOrdinaryAncestors (Join-Path $junction 'future-lab') }
}
Assert-Check 'default entry point produces no lab and needs no Hyper-V privileges' {
    $default = & (Join-Path $PSScriptRoot 'setup-migration-lab.ps1') | ConvertFrom-Json
    if ($default.readyForCreate -or (Test-Path -LiteralPath $default.labRoot)) { throw 'Default entry point wrote or marked a lab ready.' }
}
foreach ($file in @('MigrationLabPlan.psm1', 'setup-migration-lab.ps1', 'test-migration-lab-setup.ps1')) {
    Assert-Check "PowerShell syntax: $file" {
        $tokens = $null; $errors = $null
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count -ne 0) { throw ($errors.Message -join '; ') }
    }
}
$summary = [ordered]@{ checkedUtc = [DateTime]::UtcNow.ToString('o'); passed = @($results | Where-Object passed).Count; failed = @($results | Where-Object { !$_.passed }).Count; hyperVCalled = $false; vmCreated = $false; results = @($results.ToArray()) }
[IO.File]::WriteAllText((Join-Path $evidence 'results.json'), ($summary | ConvertTo-Json -Depth 8))
[pscustomobject]@{ passed = $summary.passed; failed = $summary.failed; evidence = (Join-Path $evidence 'results.json') }
if ($summary.failed -ne 0) { throw "$($summary.failed) migration lab setup checks failed; inspect results.json." }
