# Pure plan validation shared by the Hyper-V setup entry point and isolated tests.
Set-StrictMode -Version Latest

function Get-LabLocalPath {
    param([Parameter(Mandatory)][string]$Path)
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Substring(2).Contains(':') -or
        $Path.IndexOfAny([char[]]'*?') -ge 0) { throw 'A literal absolute local-drive path is required.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full.Length -lt 4) { throw 'A drive root is not an eligible lab path.' }
    return $full
}

function Assert-LabOrdinaryAncestors {
    param([Parameter(Mandatory)][string]$Path)
    $current = Get-LabLocalPath $Path
    while (![string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Lab paths and ISO paths must not traverse reparse points.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-LabToolHashes {
    param([Parameter(Mandatory)][string]$WorkspaceRoot)
    return [ordered]@{
        entryPoint = (Get-FileHash -LiteralPath (Join-Path $WorkspaceRoot 'scripts/setup-migration-lab.ps1') -Algorithm SHA256).Hash
        planModule = (Get-FileHash -LiteralPath (Join-Path $WorkspaceRoot 'scripts/MigrationLabPlan.psm1') -Algorithm SHA256).Hash
    }
}

function Get-MigrationLabMemoryReadiness {
    param([Parameter(Mandatory)][ValidateRange(0, [long]::MaxValue)][long]$FreePhysicalMemoryBytes)
    # Creation leaves the VM off. This observation never authorizes a later start;
    # the unchanged 4 GiB guest plus 2 GiB host reserve must be available then.
    return [ordered]@{
        checkedUtc = [DateTime]::UtcNow.ToString('o')
        freePhysicalMemoryBytes = $FreePhysicalMemoryBytes
        requiredFreePhysicalMemoryBytes = [long]6GB
        memoryReadyForStart = $FreePhysicalMemoryBytes -ge 6GB
        recheckRequiredBeforeStart = $true
    }
}

function New-MigrationLabPlan {
    param(
        [Parameter(Mandatory)][string]$WorkspaceRoot,
        [string]$IsoPath,
        [Guid]$RunId = [Guid]::NewGuid()
    )
    $workspace = Get-LabLocalPath $WorkspaceRoot
    Assert-LabOrdinaryAncestors $workspace
    if (!(Test-Path -LiteralPath (Join-Path $workspace 'CloudInlet.sln') -PathType Leaf)) {
        throw 'The workspace must be the CloudInlet repository containing this script.'
    }
    if ($RunId -eq [Guid]::Empty) { throw 'An empty lab identity is invalid.' }
    $id = $RunId.ToString('N')
    $root = Join-Path $workspace "artifacts/migration-vm/$id"
    $iso = $null
    if (![string]::IsNullOrWhiteSpace($IsoPath)) {
        $isoFull = Get-LabLocalPath $IsoPath
        Assert-LabOrdinaryAncestors $isoFull
        if ([IO.Path]::GetExtension($isoFull) -ine '.iso' -or !(Test-Path -LiteralPath $isoFull -PathType Leaf)) {
            throw 'Supply an existing Windows x64 ISO obtained from a trusted source.'
        }
        $iso = [ordered]@{ path = $isoFull; sha256 = (Get-FileHash -LiteralPath $isoFull -Algorithm SHA256).Hash }
    }
    return [ordered]@{
        schemaVersion = 1
        runId = $id
        createdUtc = [DateTime]::UtcNow.ToString('o')
        workspaceRoot = $workspace
        vmName = "CloudInlet-MigrationLab-$id"
        labRoot = $root
        configurationPath = Join-Path $root 'configuration'
        diskPath = Join-Path $root 'system.vhdx'
        evidencePath = Join-Path $root 'setup-evidence.jsonl'
        generation = 2
        memoryBytes = [long]4GB
        minimumFreeMemoryBeforeStartBytes = [long]6GB
        memoryReadinessMustBeRecheckedBeforeStart = $true
        diskBytes = [long]80GB
        processorCount = 2
        network = 'Disconnected'
        startAfterCreate = $false
        automaticStartAction = 'Nothing'
        secureBootTemplate = 'MicrosoftWindows'
        localTpm = $true
        iso = $iso
        readyForCreate = $null -ne $iso
        tools = Get-LabToolHashes $workspace
        actualRebootExercised = $false
        actualKnownFolderMigrationExercised = $false
    }
}

function Assert-MigrationLabPlan {
    param([Parameter(Mandatory)]$Plan, [Parameter(Mandatory)][string]$WorkspaceRoot)
    $workspace = Get-LabLocalPath $WorkspaceRoot
    if ($Plan.schemaVersion -ne 1 -or $Plan.runId -cnotmatch '^[0-9a-f]{32}$') { throw 'Invalid lab plan identity or schema.' }
    $id = [Guid]::ParseExact($Plan.runId, 'N')
    if ($id -eq [Guid]::Empty) { throw 'An empty lab identity is invalid.' }
    $root = Join-Path $workspace "artifacts/migration-vm/$($Plan.runId)"
    $expected = [ordered]@{
        workspaceRoot = $workspace
        vmName = "CloudInlet-MigrationLab-$($Plan.runId)"
        labRoot = $root
        configurationPath = Join-Path $root 'configuration'
        diskPath = Join-Path $root 'system.vhdx'
        evidencePath = Join-Path $root 'setup-evidence.jsonl'
        generation = 2
        memoryBytes = [long]4GB
        minimumFreeMemoryBeforeStartBytes = [long]6GB
        memoryReadinessMustBeRecheckedBeforeStart = $true
        diskBytes = [long]80GB
        processorCount = 2
        network = 'Disconnected'
        startAfterCreate = $false
        automaticStartAction = 'Nothing'
        secureBootTemplate = 'MicrosoftWindows'
        localTpm = $true
        actualRebootExercised = $false
        actualKnownFolderMigrationExercised = $false
    }
    foreach ($name in $expected.Keys) {
        if ([string]$Plan.$name -cne [string]$expected[$name]) { throw "Unexpected plan value: $name." }
    }
    foreach ($path in @($workspace, $root, $Plan.configurationPath, $Plan.diskPath, $Plan.evidencePath)) {
        Assert-LabOrdinaryAncestors $path
    }
    if (Test-Path -LiteralPath $root) { throw 'The lab path already exists; partial or previous labs are never reused.' }
    if (!$Plan.readyForCreate -or $null -eq $Plan.iso) { throw 'Create needs a reviewed plan with an existing Windows ISO.' }
    $iso = Get-LabLocalPath $Plan.iso.path
    Assert-LabOrdinaryAncestors $iso
    if ($iso -cne $Plan.iso.path -or [IO.Path]::GetExtension($iso) -ine '.iso' -or
        !(Test-Path -LiteralPath $iso -PathType Leaf) -or $Plan.iso.sha256 -cnotmatch '^[0-9A-F]{64}$') {
        throw 'The planned ISO is invalid or missing.'
    }
    $hashes = Get-LabToolHashes $workspace
    if ($Plan.tools.entryPoint -cne $hashes.entryPoint -or $Plan.tools.planModule -cne $hashes.planModule) {
        throw 'The setup scripts changed after planning; create and review a new plan.'
    }
}

function Read-MigrationLabPlan {
    param(
        [Parameter(Mandatory)][string]$PlanPath,
        [Parameter(Mandatory)][string]$PlanSha256,
        [Parameter(Mandatory)][string]$WorkspaceRoot
    )
    if ($PlanSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'The reviewed plan SHA-256 is required.' }
    $path = Get-LabLocalPath $PlanPath
    Assert-LabOrdinaryAncestors $path
    $directory = Join-Path (Get-LabLocalPath $WorkspaceRoot) 'artifacts/migration-vm-plans'
    if ([IO.Path]::GetDirectoryName($path) -cne $directory -or
        [IO.Path]::GetFileName($path) -cnotmatch '^[0-9a-f]{32}\.json$') { throw 'The plan must be an exact generated plan in this workspace.' }
    # Hold the file against writes/replacement while hashing and parsing the same bytes.
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -gt 64KB) { throw 'The lab plan is unexpectedly large.' }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
        if ($hash -ine $PlanSha256) { throw 'The lab plan does not match the reviewed SHA-256.' }
        $stream.Position = 0
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false), $true, 1024, $true)
        try { $plan = $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    if ([IO.Path]::GetFileNameWithoutExtension($path) -cne $plan.runId) { throw 'The plan filename and identity differ.' }
    Assert-MigrationLabPlan $plan $WorkspaceRoot
    return $plan
}

Export-ModuleMember -Function Get-LabLocalPath, Assert-LabOrdinaryAncestors, Get-MigrationLabMemoryReadiness, New-MigrationLabPlan, Assert-MigrationLabPlan, Read-MigrationLabPlan
