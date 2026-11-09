param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/validation'),
    [switch]$PhysicalContext,
    [string]$ExpectedUserSid,
    [string]$CompletionFile,
    [switch]$ExeMigration
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!$output.StartsWith($repository.TrimEnd('\') + '\artifacts\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer fixture output must be inside the workspace artifacts directory.' }
function Assert-NormalPath([string]$Path) {
    for ($ancestor = [IO.DirectoryInfo]$Path; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installer tests cannot follow a linked path.' }
    }
}
Assert-NormalPath $output
if (!$PhysicalContext) {
    # Some desktop execution hosts inherit a caller-specific registry overlay.
    # MSI executes through the Windows Installer service and sees the physical
    # hive. Run every fixture operation in the same physical current-user scope.
    $bridge = Join-Path $output ('installer-host-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $bridge -Force | Out-Null
    $resultFile = Join-Path $bridge 'result.json'
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $runtime = Join-Path $PSHOME 'pwsh.exe'
    if (!(Test-Path -LiteralPath $runtime)) { throw 'Installer tests require PowerShell 7.' }
    $command = '"' + $runtime + '" -NoProfile -File "' + $PSCommandPath + '" -OutputDirectory "' + $output + '" -PhysicalContext -ExpectedUserSid "' + $sid + '" -CompletionFile "' + $resultFile + '"'
    if ($ExeMigration) { $command += ' -ExeMigration' }
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine=$command; CurrentDirectory=$repository; ProcessStartupInformation=$startup }
    if ($created.ReturnValue -ne 0) { throw "The physical installer-test process could not start (WMI code $($created.ReturnValue))." }
    Write-Output "Installer acceptance is running in its own current-user Windows process. Evidence: $bridge"
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    while (!(Test-Path -LiteralPath $resultFile)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Installer acceptance did not finish. Do not terminate an active Windows Installer transaction.' }
        if (!(Get-Process -Id $created.ProcessId -ErrorAction SilentlyContinue)) { throw 'Installer acceptance exited without a result; inspect its transcript.' }
        Start-Sleep -Milliseconds 250
    }
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    if (!$result.success) { throw "Installer acceptance failed: $($result.error). Evidence: $bridge" }
    Write-Output "Installer/update fixture tests passed: $($result.fixtureRoot)"
    return
}
if ($ExpectedUserSid -notmatch '^S-1-' -or [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $ExpectedUserSid) { throw 'Physical installer acceptance is not running as the expected Windows user.' }
if (!$CompletionFile -or ![IO.Path]::GetFullPath($CompletionFile).StartsWith($output.TrimEnd('\') + '\installer-host-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer completion evidence path is invalid.' }
Assert-NormalPath ([IO.Path]::GetDirectoryName($CompletionFile))
Start-Transcript -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($CompletionFile)) 'transcript.txt') | Out-Null
trap {
    if ($PhysicalContext -and $CompletionFile) {
        [ordered]@{ success=$false; error=$_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $CompletionFile -Encoding utf8NoBOM
        Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
    }
    throw $_
}
$runId = [Guid]::NewGuid().ToString('N')
# Keep the installed fixture below MAX_PATH even for the frozen 1.1.2 MSI,
# whose bundled license sources have long relative filenames.
$fixture = Join-Path $output ('fixture-' + $runId.Substring(0, 12))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$debugRegistry = 'HKCU:\Software\CloudBay\Distribution\Debug'
$canonicalDebugRegistry = 'HKCU:\Software\CloudInlet\Distribution\Debug'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'CloudInlet Debug.lnk'
$state = Join-Path $env:LOCALAPPDATA 'CloudBay/Debug/Client'
$cache = Join-Path $state 'Updates'
$canonicalCache = Join-Path $env:LOCALAPPDATA 'CloudInlet/Debug/Client/Updates'
$defaultDebugInstall = Join-Path $env:LOCALAPPDATA 'Programs/CloudInlet Debug'
foreach ($protected in @($state, $cache, $canonicalCache, $defaultDebugInstall)) { Assert-NormalPath $protected }
foreach ($identityRoot in @($debugRegistry, $canonicalDebugRegistry)) {
    if ((Test-Path -LiteralPath ($identityRoot + '\Exe')) -or (Test-Path -LiteralPath ($identityRoot + '\Msi'))) { throw 'An existing Debug registration must not be replaced by fixture tests.' }
}
if ((Test-Path -LiteralPath $defaultDebugInstall) -or (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Programs/CloudBay Debug'))) { throw 'An existing Debug installation must not be replaced by fixture tests.' }
if ((Get-ItemProperty -LiteralPath $runKey -Name CloudInletDebug -ErrorAction SilentlyContinue) -or (Get-ItemProperty -LiteralPath $runKey -Name CloudBayDebug -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $desktop) -or (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'CloudBay Debug.lnk')) -or (Test-Path -LiteralPath $cache)) { throw 'Existing Debug startup, shortcut or update cache must not be overwritten.' }
if (Test-Path -LiteralPath $canonicalCache) { throw 'An existing canonical Debug update cache must not be overwritten.' }
if ((Get-ItemProperty -LiteralPath $approvedKey -Name CloudBayDebug -ErrorAction SilentlyContinue) -or (Get-ItemProperty -LiteralPath $approvedKey -Name CloudInletDebug -ErrorAction SilentlyContinue)) { throw 'Existing Debug Windows startup approval must not be overwritten.' }
$releaseRun = (Get-ItemProperty -LiteralPath $runKey -Name CloudInlet -ErrorAction SilentlyContinue).CloudInlet
$legacyReleaseRun = (Get-ItemProperty -LiteralPath $runKey -Name CloudBay -ErrorAction SilentlyContinue).CloudBay
$releaseFolders = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' | ConvertTo-Json -Compress)
$releaseHashes = @{}
foreach ($brand in @('CloudBay', 'CloudInlet')) {
    foreach ($name in @('settings.json', 'credentials.dpapi')) {
        $path = Join-Path $env:LOCALAPPDATA ($brand + '/Client/' + $name)
        if (Test-Path -LiteralPath $path) { $releaseHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
}
$ownedMarker = Join-Path $state ('installer-preservation-fixture-' + $runId + '.txt')
New-Item -ItemType Directory -Path $state, $cache -Force | Out-Null
'Settings, credentials and backups remain outside the installer tree.' | Set-Content -LiteralPath $ownedMarker -Encoding utf8NoBOM
$markerHash = (Get-FileHash -LiteralPath $ownedMarker -Algorithm SHA256).Hash
"CloudBay update cache v1`n" | Set-Content -LiteralPath (Join-Path $cache '.cloudbay-update-cache-v1') -NoNewline -Encoding utf8NoBOM
function Run-Setup([string]$File, [string[]]$Arguments, [switch]$ExpectFailure) {
    for ($attempt = 0; $attempt -le 5; $attempt++) {
        $process = Start-Process -FilePath $File -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -ne 1618 -or [IO.Path]::GetFileName($File) -ne 'msiexec.exe') { break }
        if ($attempt -eq 5) { throw 'Windows Installer stayed busy after five retries; installer acceptance could not run. No active installer was terminated.' }
        Write-Output 'Windows Installer is busy; retrying the isolated fixture in 10 seconds.'
        Start-Sleep -Seconds 10
    }
    if ($ExpectFailure) { if ($process.ExitCode -in @(0,3010)) { throw 'An unsupported installer command unexpectedly succeeded.' } }
    elseif ($process.ExitCode -notin @(0,3010)) { throw "Installer failed: $File ($($process.ExitCode))" }
}
function Assert-Identity([string]$Path, [string]$Kind, [string]$Version, [bool]$Startup, [bool]$Shortcut) {
    $metadata = Get-Content -LiteralPath (Join-Path $Path 'distribution.json') -Raw | ConvertFrom-Json
    if ($metadata.version -ne $Version -or $metadata.installerKind -ne $Kind -or $metadata.buildFlavor -ne 'Debug' -or $metadata.installDirectory.TrimEnd('\') -ne $Path.TrimEnd('\')) { throw 'Installed identity was not preserved correctly.' }
    if ($metadata.startWithWindows -ne $Startup -or $metadata.desktopShortcut -ne $Shortcut) { throw "$Kind $Version current preferences were not recorded correctly." }
    $name = 'CloudInletDebug'
    $shortcutPath = $desktop
    $runExists = !!(Get-ItemProperty -LiteralPath $runKey -Name $name -ErrorAction SilentlyContinue)
    if ($runExists -ne $Startup -or (Test-Path -LiteralPath $shortcutPath) -ne $Shortcut) { throw "$Kind $Version current startup/shortcut choices changed." }
    if ((Get-FileHash -LiteralPath $ownedMarker -Algorithm SHA256).Hash -ne $markerHash) { throw 'Private client state changed.' }
}
function Wait-File([string]$Path, [int]$Seconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (!(Test-Path -LiteralPath $Path)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Fixture timed out waiting for $Path" }
        Start-Sleep -Milliseconds 100
    }
}
function Invoke-ProgressMethod([string]$TypeName, [string]$Method, [object[]]$Arguments) {
    # Load the actual packaged protocol implementation from bytes, without
    # keeping the installation (or the fixture's package source) locked.
    $type = $script:progressProtocol.GetType('CloudInlet.Packaging.' + $TypeName, $true)
    $methodInfo = $type.GetMethod($Method, [Reflection.BindingFlags]'NonPublic,Static')
    if (!$methodInfo) { throw "The packaged installer protocol has no $TypeName.$Method method." }
    $plainArguments = [object[]]@($Arguments | ForEach-Object { [string]$_ })
    try { return $methodInfo.Invoke($null, $plainArguments) }
    catch [Reflection.TargetInvocationException] { throw $_.Exception.InnerException }
}
function Add-ProgressObservation([Collections.Generic.List[object]]$Observations, [string]$Path, [string]$Id, [string]$RequestHash, [bool]$MigrationRequested = $true) {
    $snapshot = Invoke-ProgressMethod InstallerProgressJournal Read @($Path, $Id, $RequestHash)
    if (!$snapshot) { return }
    if ($Observations.Count -gt 0) {
        $previous = $Observations[$Observations.Count - 1]
        if ($snapshot.Sequence -lt $previous.Sequence) { throw 'The authenticated installer progress sequence moved backwards.' }
        if ($snapshot.Sequence -eq $previous.Sequence) {
            if (($snapshot | ConvertTo-Json -Compress) -cne ($previous | ConvertTo-Json -Compress)) { throw 'Installer progress changed without advancing its sequence.' }
            return
        }
        if ($previous.Status -ne 'Active') { throw 'Installer progress changed after a terminal result.' }
    }
    if ($snapshot.MigrationRequested -ne $MigrationRequested) { throw 'Progress changed the reviewed migration selection.' }
    $Observations.Add($snapshot)
}
function Invoke-Worker([string]$Install, [string]$Kind, [string]$Package, [string]$TargetVersion = '1.1.3', [switch]$Corrupt, [switch]$UseCanonicalCache, [string]$MigrationDirectory, [switch]$UncommittedProgress, [switch]$CanceledProgress, [switch]$SimulatedRestart) {
    if (($UncommittedProgress -or $CanceledProgress) -and !$MigrationDirectory) { throw 'A progress handoff refusal requires an isolated migration fixture.' }
    $workerCache = if ($UseCanonicalCache) { $canonicalCache } else { $cache }
    if (!(Test-Path -LiteralPath $workerCache)) {
        New-Item -ItemType Directory -Path $workerCache | Out-Null
        "CloudBay update cache v1`n" | Set-Content -LiteralPath (Join-Path $workerCache '.cloudbay-update-cache-v1') -NoNewline -Encoding utf8NoBOM
    }
    $installedVersion = (Get-Content -LiteralPath (Join-Path $Install 'distribution.json') -Raw | ConvertFrom-Json).version
    $case = $Kind.ToLowerInvariant() + '-' + $TargetVersion + $(if ($Corrupt) { '-corrupt' } else { '-valid' }) + $(if ($UseCanonicalCache) { '-canonical' } else { '-legacy' }) + $(if ($UncommittedProgress) { '-uncommitted' } elseif ($CanceledProgress) { '-canceled' } else { '' })
    $hostRoot = Join-Path $workerCache ('fixture-host-' + $runId + '-' + $case + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $hostRoot | Out-Null
    foreach ($name in @('CloudInlet.SetupHelper.exe', 'CloudInlet.SetupHelper.exe.config')) { Copy-Item -LiteralPath (Join-Path $Install $name) -Destination $hostRoot }
    $pending = Join-Path $workerCache ('pending-CloudInlet-' + $TargetVersion + '-win-x64-debug-setup.' + $Kind.ToLowerInvariant())
    Copy-Item -LiteralPath $Package -Destination $pending
    $hash = (Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -LiteralPath $pending).Length
    if ($Corrupt) {
        $stream = [IO.File]::Open($pending, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.WriteByte(0); $stream.Flush($true) } finally { $stream.Dispose() }
    }
    $parent = Start-Process -FilePath (Join-Path $Install 'CloudInlet.exe') -ArgumentList @('--wait', '5') -PassThru -WindowStyle Hidden
    $parent.Refresh()
    $result = Join-Path $hostRoot 'result.json'
    $ready = Join-Path $hostRoot 'ready.json'
    $request = Join-Path $hostRoot 'request.json'
    $hasProgress = !!$MigrationDirectory -or $SimulatedRestart
    $progressId = if ($hasProgress) { [Guid]::NewGuid().ToString('N') } else { '' }
    $progressPath = if ($hasProgress) { Join-Path $workerCache ('host/' + $progressId + '.progress.dpapi') } else { '' }
    [ordered]@{ schemaVersion=$(if ($hasProgress) { 2 } else { 1 }); migrateInstallation=!!$MigrationDirectory; migrationInstallDirectory=$MigrationDirectory; parentProcessId=$parent.Id; parentStartUtcTicks=$parent.StartTime.ToUniversalTime().Ticks; installDirectory=$Install; buildFlavor='Debug'; installerKind=$Kind; installedVersion=$installedVersion; targetVersion=$TargetVersion; packagePath=$pending; packageSha256=$hash; packageSize=$size; resultPath=$result; readinessPath=$ready; restartBackground=$true; progressId=$progressId; progressPath=$progressPath } | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8NoBOM
    $observations = [Collections.Generic.List[object]]::new()
    if ($hasProgress) {
        New-Item -ItemType Directory -Path (Split-Path $progressPath) -Force | Out-Null
        if (!$script:progressProtocol) { $script:progressProtocol = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $hostRoot 'CloudInlet.SetupHelper.exe'))) }
        $requestHash = (Get-FileHash -LiteralPath $request -Algorithm SHA256).Hash.ToLowerInvariant()
        Invoke-ProgressMethod InstallerProgressHandoff Create @($progressPath, $progressId, $requestHash) | Out-Null
    }
    $relaunchMarker = Join-Path $hostRoot 'unexpected-relaunch.txt'
    $previousMarker = [Environment]::GetEnvironmentVariable('CLOUDINLET_INSTALLER_TEST_RELAUNCH_MARKER')
    try {
        if ($SimulatedRestart) { $env:CLOUDINLET_INSTALLER_TEST_RELAUNCH_MARKER = $relaunchMarker }
        $worker = Start-Process -FilePath (Join-Path $hostRoot 'CloudInlet.SetupHelper.exe') -ArgumentList @('--update', ('"' + $request + '"')) -PassThru -WindowStyle Hidden
    } finally {
        if ($SimulatedRestart) {
            if ($null -eq $previousMarker) { Remove-Item Env:\CLOUDINLET_INSTALLER_TEST_RELAUNCH_MARKER -ErrorAction SilentlyContinue }
            else { $env:CLOUDINLET_INSTALLER_TEST_RELAUNCH_MARKER = $previousMarker }
        }
    }
    Wait-File $ready
    $readiness = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
    if (!$readiness.ready -or $readiness.targetVersion -ne $TargetVersion) { throw 'The worker did not acknowledge the verified request.' }
    if ($hasProgress) {
        Add-ProgressObservation $observations $progressPath $progressId $requestHash (!!$MigrationDirectory)
        if ($observations.Count -eq 0 -or $observations[0].Status -ne 'Active' -or $observations[0].Stage -ne 'WaitingForAppExit') { throw 'The worker did not expose live authenticated progress before handoff.' }
        if ($CanceledProgress) { Invoke-ProgressMethod InstallerProgressHandoff Cancel @($progressPath, $progressId, $requestHash) | Out-Null }
        elseif (!$UncommittedProgress) { Invoke-ProgressMethod InstallerProgressHandoff Commit @($progressPath, $progressId, $requestHash) | Out-Null }
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        do {
            Add-ProgressObservation $observations $progressPath $progressId $requestHash (!!$MigrationDirectory)
            if ([DateTime]::UtcNow -gt $deadline) { throw 'The update fixture did not complete. Do not terminate an active installer.' }
        } while (!$worker.WaitForExit(100))
        Add-ProgressObservation $observations $progressPath $progressId $requestHash (!!$MigrationDirectory)
        # These are authenticated observations, not a claim that polling captured
        # every short stage. Sequence gaps are expected for atomic snapshots.
        [ordered]@{ case=$case; requestSha256=$requestHash; helperSha256=(Get-FileHash -LiteralPath (Join-Path $hostRoot 'CloudInlet.SetupHelper.exe') -Algorithm SHA256).Hash.ToLowerInvariant(); observations=@($observations.ToArray()) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture ('progress-' + $progressId + '.json')) -Encoding utf8NoBOM
    } elseif (!$worker.WaitForExit(120000)) { throw 'The update fixture did not complete. Do not terminate an active installer.' }
    Wait-File $result
    $record = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if ($SimulatedRestart) {
        # The fixture returns 3010 without applying bytes: subsequent installed-
        # version verification fails. That failure must not erase restart safety.
        $finalProgress = $observations[$observations.Count - 1]
        if ($record.success -or $worker.ExitCode -eq 0 -or !$finalProgress.RequiresRestart -or $finalProgress.Status -ne 'RestartRequired') { throw 'A verification failure erased the native restart requirement.' }
        Start-Sleep -Milliseconds 750
        if (Test-Path -LiteralPath $relaunchMarker) { throw 'The updater reopened the app before the required restart.' }
        if ((Get-Content -LiteralPath (Join-Path $Install 'distribution.json') -Raw | ConvertFrom-Json).version -ne $installedVersion) { throw 'The simulated restart changed the installed version.' }
        [ordered]@{ restartSignalPreservedAfterVerificationFailure=$true; applicationNotReopened=$true; actualRebootExercised=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'update-restart-validation.json') -Encoding utf8NoBOM
    } elseif ($UncommittedProgress -or $CanceledProgress) {
        if ($worker.ExitCode -eq 0 -or $record.success -or $record.migrationCompleted -or $observations[$observations.Count - 1].Status -ne 'Failed') { throw 'An uncommitted or canceled progress handoff started or completed migration.' }
        if ((Get-Content -LiteralPath (Join-Path $Install 'distribution.json') -Raw | ConvertFrom-Json).version -ne $installedVersion) { throw 'A refused progress handoff changed the installation.' }
        if ($MigrationDirectory.TrimEnd('\') -ne $Install.TrimEnd('\') -and (Test-Path -LiteralPath $MigrationDirectory)) { throw 'A refused progress handoff created the migration destination.' }
        if (!$parent.WaitForExit(10000)) { throw 'The isolated parent fixture did not finish after a refused handoff.' }
    } elseif ($Corrupt) {
        if ($worker.ExitCode -eq 0 -or $record.success -or $record.message -notmatch 'checksum changed') { throw 'The worker did not reject the corrupted package before installation.' }
        if ((Get-Content -LiteralPath (Join-Path $Install 'distribution.json') -Raw | ConvertFrom-Json).version -ne $installedVersion) { throw 'A corrupt update changed the installation.' }
    } elseif ($worker.ExitCode -notin @(0,3010) -or !$record.success) { throw "The external update worker failed: $($record.message)" }
    if ($MigrationDirectory -and !$UncommittedProgress -and !$CanceledProgress) {
        if (!$record.migrationRequested -or !$record.migrationCompleted -or $record.installDirectory.TrimEnd('\') -ne $MigrationDirectory.TrimEnd('\')) { throw 'The installer did not verify the requested migration.' }
        $finalProgress = $observations[$observations.Count - 1]
        if ($observations.Count -lt 2 -or $finalProgress.Sequence -lt 6 -or $finalProgress.Stage -ne 'Complete' -or $finalProgress.Status -ne 'Succeeded' -or !$finalProgress.MigrationCompleted -or $finalProgress.RequiresRestart) { throw 'The live progress journal did not confirm the independently verified migration.' }
    }
    Remove-Item -LiteralPath $pending
}
function Invoke-ExeMigrationFixture([string]$Package) {
    # Execute the packaged adapter itself against only this run's Debug product.
    # Reflection exposes a durable native boundary for crash recovery acceptance;
    # no fault switch is shipped in the helper or normal application protocol.
    $migrationParent = Join-Path $fixture 'ExeMigration'
    $source = Join-Path $migrationParent 'CloudBay Debug'
    $destination = Join-Path $migrationParent 'CloudInlet Debug'
    New-Item -ItemType Directory -Path $migrationParent | Out-Null
    Run-Setup $Package @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $source + '"'),'/TASKS=startup,desktopicon')
    $script:uninstallExe = Join-Path $source 'unins000.exe'
    Assert-Identity $source Exe '1.1.4' $true $true
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $source 'CloudInlet.SetupHelper.exe')))
    $workerType = $assembly.GetType('ExeMigrationWorker', $true)
    $receiptType = $assembly.GetType('CloudInlet.Packaging.ExeMigrationReceipt', $true)
    $stateType = $assembly.GetType('CloudInlet.Packaging.ExeInstallationMigration+State', $true)
    $staticFlags = [Reflection.BindingFlags]'NonPublic,Static'
    $instanceFlags = [Reflection.BindingFlags]'Public,NonPublic,Instance'
    $create = $workerType.GetMethod('Create', $staticFlags)
    $inspect = $receiptType.GetMethod('Inspect', $staticFlags)
    $hash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()
    $packageSize = (Get-Item -LiteralPath $Package).Length
    $recoveryRoot = Join-Path $env:LOCALAPPDATA 'CloudInlet/Migration/Debug/ExeInstallation'
    $receipts = [Collections.Generic.List[string]]::new()
    $current = $null
    function New-Recovery {
        try { $value = $create.Invoke($null, [object[]]@([string]$source, [string]$destination, 'Debug', '1.1.4', [string]$Package, [string]$hash, [long]$packageSize, [string]$hash, $null)) }
        catch [Reflection.TargetInvocationException] { throw $_.Exception.InnerException }
        $record = $workerType.GetField('_receipt', $instanceFlags).GetValue($value)
        $path = Join-Path $recoveryRoot ($record.Id + '/migration.dpapi')
        $receipts.Add($path)
        return [pscustomobject]@{ Worker=$value; Path=$path }
    }
    function Read-Recovery([string]$Path) {
        try { return $inspect.Invoke($null, [object[]]@([string]$Path)) }
        catch [Reflection.TargetInvocationException] { throw $_.Exception.InnerException }
    }
    function Retire-Original($Recovery) {
        $next = [Activator]::CreateInstance($stateType, $true)
        $next.Source = $source; $next.Destination = $destination; $next.Phase = 'UninstallingOriginal'; $next.Sequence = 1
        $next.RegistrationOnly = $Recovery.Worker.Read().RegistrationOnly
        $next.Detail = 'Native fixture resumes after the durable original-uninstall boundary.'
        $Recovery.Worker.Write($next)
        $Recovery.Worker.UninstallOriginal()
        $Recovery.Worker.VerifyOriginalRetired()
        $Recovery.Worker.Dispose()
    }
    function Run-Recovery([string]$Path, [switch]$Revert) {
        $record = Read-Recovery $Path
        $helper = Join-Path (Split-Path $Path) 'recovery.exe'
        $action = if ($Revert) { '--revert-exe-migration' } else { '--resume-exe-migration' }
        Run-Setup $helper @($action, ('"' + $Path + '"'))
        $after = Read-Recovery $Path
        if ($after.Failure) { throw ('Native EXE recovery failed: ' + $after.Failure) }
        return $after
    }
    try {
        New-Item -ItemType Directory -Path $destination | Out-Null
        $collision = Join-Path $destination 'independent.txt'
        'This destination does not belong to migration.' | Set-Content -LiteralPath $collision -Encoding utf8NoBOM
        $collisionHash = (Get-FileHash -LiteralPath $collision -Algorithm SHA256).Hash
        $refused = $false
        try { $unexpected = New-Recovery; $unexpected.Worker.Dispose() }
        catch { if ($_.Exception.Message -notmatch 'destination already exists') { throw }; $refused = $true }
        if (!$refused -or (Get-FileHash -LiteralPath $collision -Algorithm SHA256).Hash -ne $collisionHash) { throw 'EXE destination collision was not preserved.' }
        Remove-Item -LiteralPath $collision
        Remove-Item -LiteralPath $destination
        $personal = Join-Path $source 'personal-do-not-delete.txt'
        'Personal bytes are outside installer ownership.' | Set-Content -LiteralPath $personal -Encoding utf8NoBOM
        $personalHash = (Get-FileHash -LiteralPath $personal -Algorithm SHA256).Hash
        $current = New-Recovery
        $current.Worker.Dispose()
        $prepared = Run-Recovery $current.Path -Revert
        if ($prepared.State.Phase -ne 'Reverted') { throw 'Prepared EXE migration did not revert without uninstalling.' }
        Assert-Identity $source Exe '1.1.4' $true $true

        $current = New-Recovery
        Retire-Original $current
        $interrupted = Read-Recovery $current.Path
        if ($interrupted.State.Phase -ne 'UninstallingOriginal' -or (Test-Path -LiteralPath (Join-Path $source 'CloudInlet.exe'))) { throw 'EXE fixture did not reach its native interruption boundary.' }
        $completed = Run-Recovery $current.Path
        if ($completed.State.Phase -ne 'Completed') { throw 'EXE migration did not resume to a verified target.' }
        $script:uninstallExe = Join-Path $destination 'unins000.exe'
        Assert-Identity $destination Exe '1.1.4' $true $true
        if ((Get-FileHash -LiteralPath $personal -Algorithm SHA256).Hash -ne $personalHash) { throw 'EXE migration changed unknown personal files.' }
        if (Test-Path -LiteralPath ($debugRegistry + '\Exe')) { throw 'EXE migration retained the old distribution registration.' }
        $completedAgain = Run-Recovery $current.Path -Revert
        if ($completedAgain.State.Phase -ne 'Completed' -or $completedAgain.State.Sequence -ne $completed.State.Sequence) { throw 'Revert changed an already completed EXE migration.' }
        if (Get-ItemProperty -LiteralPath $runKey -Name CloudInletInstallerRecoveryDebug -ErrorAction SilentlyContinue) { throw 'Completed EXE migration retained its recovery startup entry.' }
        Run-Setup $script:uninstallExe @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
        $script:uninstallExe = $null
        if (Test-Path -LiteralPath $destination) {
            if (@(Get-ChildItem -LiteralPath $destination -Force).Count) { throw 'Completed fixture uninstall left unexpected destination files.' }
            Remove-Item -LiteralPath $destination
        }

        # A second native uninstall boundary restores the same newer app at its
        # original location, preserving the user's independent retained file.
        Run-Setup $Package @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $source + '"'),'/TASKS=startup,desktopicon')
        $script:uninstallExe = Join-Path $source 'unins000.exe'
        $current = New-Recovery
        Retire-Original $current
        $restored = Run-Recovery $current.Path -Revert
        if ($restored.State.Phase -ne 'Reverted') { throw 'Interrupted EXE migration did not restore the updated original installation.' }
        Assert-Identity $source Exe '1.1.4' $true $true
        if ((Get-FileHash -LiteralPath $personal -Algorithm SHA256).Hash -ne $personalHash) { throw 'EXE recovery changed the retained personal file.' }
        if (Get-ItemProperty -LiteralPath $runKey -Name CloudInletInstallerRecoveryDebug -ErrorAction SilentlyContinue) { throw 'Reverted EXE migration retained its recovery startup entry.' }
        Run-Setup $script:uninstallExe @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
        $script:uninstallExe = $null
        # A custom/canonical folder with an old registry location must use the
        # native uninstall/reinstall transaction without moving that directory.
        $registrationCases = [Collections.Generic.List[object]]::new()
        foreach ($registrationCase in @('Custom application', 'CloudInlet Debug')) {
            $source = Join-Path $migrationParent $registrationCase
            $destination = $source
            Run-Setup $exe0 @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $source + '"'),'/TASKS=startup,desktopicon')
            $script:uninstallExe = Join-Path $source 'unins000.exe'
            Run-Setup $Package @('/UPDATE','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-')
            Assert-Identity $source Exe '1.1.4' $true $true
            if (!(Test-Path -LiteralPath ($debugRegistry + '\Exe')) -or (Test-Path -LiteralPath ($canonicalDebugRegistry + '\Exe'))) { throw 'The same-folder fixture did not retain its legacy registration before consent.' }
            $personal = Join-Path $source 'personal-do-not-delete.txt'
            'Personal bytes survive same-directory installer registration migration.' | Set-Content -LiteralPath $personal -Encoding utf8NoBOM
            $personalHash = (Get-FileHash -LiteralPath $personal -Algorithm SHA256).Hash
            $current = New-Recovery
            $initial = Read-Recovery $current.Path
            if (!$initial.State.RegistrationOnly -or $initial.OriginalBrand -ne 'CloudBay' -or $initial.DestinationDirectoryIdentity -ne $initial.OriginalDirectoryIdentity) { throw 'The same-folder recovery mode or directory identity was not recorded.' }
            $current.Worker.Dispose()
            $prepared = Run-Recovery $current.Path -Revert
            if ($prepared.State.Phase -ne 'Reverted' -or !(Test-Path -LiteralPath ($debugRegistry + '\Exe'))) { throw 'Cancelling registration migration changed the existing registration.' }
            $preparedAgain = Run-Recovery $current.Path -Revert
            if ($preparedAgain.State.Sequence -ne $prepared.State.Sequence) { throw 'Terminal registration cancellation was rewritten.' }
            $current = New-Recovery
            Retire-Original $current
            $restore = $registrationCase -eq 'CloudInlet Debug'
            if ($restore) {
                # A missing survivor must stop recovery before setup writes any
                # application file. Restore the isolated file and retry normally.
                $retainedOutside = Join-Path $fixture 'held-registration-personal.txt'
                foreach ($movePath in @($personal, $retainedOutside)) {
                    if (![IO.Path]::GetFullPath($movePath).StartsWith([IO.Path]::GetFullPath($fixture).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The isolated survivor fault injection escaped its fixture.' }
                    Assert-NormalPath $movePath
                }
                Move-Item -LiteralPath $personal -Destination $retainedOutside
                $helper = Join-Path (Split-Path $current.Path) 'recovery.exe'
                Run-Setup $helper @('--revert-exe-migration', ('"' + $current.Path + '"')) -ExpectFailure
                $paused = Read-Recovery $current.Path
                if (!$paused.Failure -or $paused.State.Phase -ne 'RestoringOriginal' -or (Test-Path -LiteralPath (Join-Path $source 'CloudInlet.exe'))) { throw 'A missing retained file did not block setup before mutation.' }
                Move-Item -LiteralPath $retainedOutside -Destination $personal
            }
            $completed = Run-Recovery $current.Path -Revert:$restore
            if ($completed.State.Phase -ne 'Completed' -or !$completed.State.RegistrationOnly -or
                $completed.OriginalDirectoryIdentity -ne $initial.OriginalDirectoryIdentity -or $completed.DestinationDirectoryIdentity -ne $initial.OriginalDirectoryIdentity) { throw 'Same-folder recovery did not verify completion in the unchanged directory.' }
            Assert-Identity $source Exe '1.1.4' $true $true
            if ((Test-Path -LiteralPath ($debugRegistry + '\Exe')) -or !(Test-Path -LiteralPath ($canonicalDebugRegistry + '\Exe'))) { throw 'Same-folder recovery did not establish the canonical registration.' }
            $completedAgain = Run-Recovery $current.Path -Revert
            if ($completedAgain.State.Phase -ne 'Completed' -or $completedAgain.State.Sequence -ne $completed.State.Sequence) { throw 'Completed same-folder registration was reverted or rewritten.' }
            $refused = $false
            try { $unexpected = New-Recovery; $unexpected.Worker.Dispose() }
            catch { if ($_.Exception.Message -notmatch 'already uses the CloudInlet registry') { throw }; $refused = $true }
            if (!$refused) { throw 'Canonical same-folder registration unexpectedly allowed a second migration.' }
            Invoke-Worker $source Exe $exe3 -TargetVersion '1.1.5' -UseCanonicalCache
            Assert-Identity $source Exe '1.1.5' $true $true
            Run-Setup $exe3 @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $source + '"'),'/TASKS=startup,desktopicon')
            Assert-Identity $source Exe '1.1.5' $true $true
            Run-Setup $script:uninstallExe @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
            $script:uninstallExe = $null
            if ((Get-FileHash -LiteralPath $personal -Algorithm SHA256).Hash -ne $personalHash) { throw 'Registration migration, follow-up update, repair or uninstall changed personal bytes.' }
            if ((Test-Path -LiteralPath ($debugRegistry + '\Exe')) -or (Test-Path -LiteralPath ($canonicalDebugRegistry + '\Exe')) -or
                (Get-ItemProperty -LiteralPath $runKey -Name CloudInletInstallerRecoveryDebug -ErrorAction SilentlyContinue)) { throw 'Same-folder fixture uninstall or recovery left owned registration behind.' }
            $registrationCases.Add([ordered]@{ folder=$registrationCase; restoredAfterRetirement=$restore; missingSurvivorCheckedBeforeSetup=$restore; preparedCancelPreservedLegacy=$true; canonicalRegistration=$true; originalDirectoryIdentityPreserved=$true; completedCannotRevert=$true; subsequentUpdateRepairUninstall=$true; personalFilePreserved=$true })
        }
        # Exercise the actual pre-update durable worker. Only the boot/exit fields
        # are simulated; Windows is never restarted by this acceptance fixture.
        $source = Join-Path $migrationParent 'Deferred application update'
        $destination = $source
        Run-Setup $exe0 @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $source + '"'),'/TASKS=!startup,!desktopicon')
        $script:uninstallExe = Join-Path $source 'unins000.exe'
        Run-Setup $exe1 @('/UPDATE','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-')
        $createCore = $workerType.GetMethod('CreateCore', $staticFlags)
        $deferred = $createCore.Invoke($null, [object[]]@([string]$source, [string]$destination, 'Debug', '1.1.4', [string]$Package, [string]$hash, [long]$packageSize, [string]$hash, $null, '1.1.3'))
        $deferredRecord = $workerType.GetField('_receipt', $instanceFlags).GetValue($deferred)
        $deferredPath = Join-Path $recoveryRoot ($deferredRecord.Id + '/migration.dpapi')
        $receipts.Add($deferredPath)
        $current = [pscustomobject]@{Worker=$deferred; Path=$deferredPath}
        if ($deferredRecord.State.Phase -ne 'UpdatingOriginal' -or $deferredRecord.OriginalVersion -ne '1.1.3') { throw 'Selected migration was not durable before the original update.' }
        $deferred.UpdateOriginal()
        Assert-Identity $source Exe '1.1.4' $false $false
        if ($deferredRecord.NativeAttempt.Kind -ne 'Update' -or $deferredRecord.NativeAttempt.ExitCode -ne 0) { throw 'The native update outcome was not recorded.' }
        $deferredRecord.NativeAttempt.ExitCode = 3010
        $saveReceipt = $workerType.GetMethod('WriteReceipt', $instanceFlags)
        $saveReceipt.Invoke($deferred, [object[]]@($false)) | Out-Null
        $deferred.Dispose()
        $helper = Join-Path (Split-Path $deferredPath) 'recovery.exe'
        Run-Setup $helper @('--resume-exe-migration', ('"' + $deferredPath + '"')) -ExpectFailure
        Run-Setup $helper @('--revert-exe-migration', ('"' + $deferredPath + '"')) -ExpectFailure
        $waiting = Read-Recovery $deferredPath
        if ($waiting.State.Phase -ne 'UpdatingOriginal' -or $waiting.Failure -notmatch 'Restart Windows' -or !(Test-Path -LiteralPath ($debugRegistry + '\Exe'))) { throw 'Same-boot recovery did not preserve the pending update and legacy registration.' }
        $deferred = $workerType.GetMethod('Open', $staticFlags).Invoke($null, [object[]]@([string]$deferredPath))
        $current = [pscustomobject]@{Worker=$deferred; Path=$deferredPath}
        $deferredRecord = $workerType.GetField('_receipt', $instanceFlags).GetValue($deferred)
        $deferredRecord.NativeAttempt.BootIdentity = [Guid]::NewGuid().ToString('D')
        $saveReceipt.Invoke($deferred, [object[]]@($false)) | Out-Null
        $deferred.Dispose()
        $completed = Run-Recovery $deferredPath
        if ($completed.State.Phase -ne 'Completed' -or $completed.OriginalVersion -ne '1.1.4') { throw 'Post-boot continuation could not finish an already updated application.' }
        Assert-Identity $source Exe '1.1.4' $false $false
        Run-Setup $script:uninstallExe @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
        $script:uninstallExe = $null
        [ordered]@{ fixtureRoot=$migrationParent; nativeResumeAfterUninstall=$true; nativeRevertAfterUninstall=$true; preparedRevert=$true; completedCannotRevert=$true; destinationCollisionPreserved=$true; personalFilePreserved=$true; startupAndShortcutPreserved=$true; canonicalRegistration=$true; recoveryStartupDisarmed=$true; registrationOnly=@($registrationCases.ToArray()); migrationSavedBeforeUpdate=$true; simulatedSameBootResumeAndRevertBlocked=$true; simulatedPostBootAlreadyUpdatedContinuation=$true; actualRebootExercised=$false; productionWinUiHandshakeExercised=$false; receipts=@($receipts.ToArray()) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'exe-migration-validation.json') -Encoding utf8NoBOM
    } finally {
        if ($current) { $current.Worker.Dispose() }
        foreach ($path in $receipts) {
            $record = Read-Recovery $path
            if ($record.State.Phase -notin @('Completed', 'Reverted')) { throw "The isolated EXE migration needs recovery; evidence retained at $path" }
            Copy-Item -LiteralPath $path -Destination (Join-Path $fixture ($record.Id + '.exe-migration.dpapi'))
            $owned = [IO.Path]::GetFullPath((Split-Path $path))
            if ($owned -ne [IO.Path]::GetFullPath((Join-Path $recoveryRoot $record.Id))) { throw 'EXE fixture recovery cleanup ownership changed.' }
            Assert-NormalPath $owned
            if (Get-ChildItem -LiteralPath $owned -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'EXE fixture recovery cleanup encountered a link.' }
            Remove-Item -LiteralPath $owned -Recurse -Force
        }
    }
}
$uninstallExe = $null
$uninstallMsi = $null
try {
    # Compile the real published installer/helper source for the baseline. This is
    # a source snapshot in ignored artifacts, not another Git worktree.
    $oldSource = Join-Path $fixture 'Published-1.1.2'
    New-Item -ItemType Directory -Path $oldSource | Out-Null
    $archive = Join-Path $fixture 'published-installer-source.tar'
    git -C $repository archive v1.1.2 -o $archive packaging scripts/build-installers.ps1 scripts/build-installer-helper.ps1 scripts/get-installer-tools.ps1 CloudInlet/Assets/CloudInlet.ico Directory.Build.props Directory.Build.targets version.json LICENSE
    if ($LASTEXITCODE -ne 0) { throw 'The immutable published installer source could not be read.' }
    tar -xf $archive -C $oldSource
    if ($LASTEXITCODE -ne 0) { throw 'The published installer fixture snapshot could not be extracted.' }
    $tools = & (Join-Path $PSScriptRoot 'get-installer-tools.ps1')
    foreach ($version in @('1.1.2', '1.1.3', '1.1.4', '1.1.5')) {
        $published = $version -eq '1.1.2'
        $brand = 'CloudInlet'
        $app = Join-Path $fixture "$version/App"
        $packages = Join-Path $fixture "$version/Packages"
        New-Item -ItemType Directory -Path $app, (Join-Path $app 'Assets'), $packages -Force | Out-Null
        dotnet build (Join-Path $repository 'packaging/InstallerTestClient/CloudInlet.InstallerTestClient.csproj') -c Release -p:Version=$version -p:AssemblyName=$brand -v:minimal | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Installer fixture client build failed.' }
        $client = Join-Path $repository 'packaging/InstallerTestClient/bin/Release/net472'
        foreach ($name in @("$brand.exe", "$brand.exe.config")) { Copy-Item -LiteralPath (Join-Path $client $name) -Destination $app }
        Copy-Item -LiteralPath (Join-Path $repository 'CloudInlet/Assets/CloudInlet.ico') -Destination (Join-Path $app "Assets/$brand.ico")
        Copy-Item -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Destination $app
        $builder = if ($published) { Join-Path $oldSource 'scripts/build-installers.ps1' } else { Join-Path $PSScriptRoot 'build-installers.ps1' }
        & $builder -AppFolder $app -Version $version -Configuration Debug -OutputDirectory $packages -InnoCompiler $tools.InnoCompiler -WixToolPath $tools.WixToolPath
        if (!$published -and (Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Name -match '^CloudBay(?:\.|$)' })) { throw 'A future payload contains an obsolete CloudBay executable.' }
    }
    $exe0 = Join-Path $fixture '1.1.2/Packages/CloudInlet-1.1.2-win-x64-debug-setup.exe'
    $exe1 = Join-Path $fixture '1.1.3/Packages/CloudInlet-1.1.3-win-x64-debug-setup.exe'
    $msi0 = Join-Path $fixture '1.1.2/Packages/CloudInlet-1.1.2-win-x64-debug-setup.msi'
    $msi1 = Join-Path $fixture '1.1.3/Packages/CloudInlet-1.1.3-win-x64-debug-setup.msi'
    $exe2 = Join-Path $fixture '1.1.4/Packages/CloudInlet-1.1.4-win-x64-debug-setup.exe'
    $exe3 = Join-Path $fixture '1.1.5/Packages/CloudInlet-1.1.5-win-x64-debug-setup.exe'
    $msi2 = Join-Path $fixture '1.1.4/Packages/CloudInlet-1.1.4-win-x64-debug-setup.msi'
    $msi3 = Join-Path $fixture '1.1.5/Packages/CloudInlet-1.1.5-win-x64-debug-setup.msi'
    # Verify the test-only relaunch recorder before giving it installer ownership.
    # Empty environment values must never turn a background fixture into a crash.
    $fixtureClient = Join-Path $fixture '1.1.4/App/CloudInlet.exe'
    $fixtureMarker = Join-Path $fixture 'fixture-marker-self-check.txt'
    foreach ($markerValue in @($null, '', ' ', $fixtureMarker)) {
        $markerStart = [Diagnostics.ProcessStartInfo]::new($fixtureClient, '--background')
        $markerStart.UseShellExecute = $false
        $markerStart.CreateNoWindow = $true
        $markerStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        if ($null -eq $markerValue) { $markerStart.Environment.Remove('CLOUDINLET_INSTALLER_TEST_RELAUNCH_MARKER') | Out-Null }
        else { $markerStart.Environment['CLOUDINLET_INSTALLER_TEST_RELAUNCH_MARKER'] = $markerValue }
        $markerProcess = [Diagnostics.Process]::Start($markerStart)
        try {
            if (!$markerProcess.WaitForExit(10000) -or $markerProcess.ExitCode -ne 0) { throw 'The test-only relaunch recorder did not handle its marker path safely.' }
        } finally { $markerProcess.Dispose() }
    }
    if (!(Test-Path -LiteralPath $fixtureMarker)) { throw 'The test-only relaunch recorder did not record a valid path.' }
    # Keep an original branded folder as well as a custom folder.
    $exeRoot = Join-Path $fixture 'CloudBay Debug'
    $msiRoot = Join-Path $fixture 'Installed Msi'
    Run-Setup $exe1 @('/UPDATE','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $exeRoot + '"')) -ExpectFailure
    Run-Setup msiexec.exe @('/i', ('"' + $msi1 + '"'), '/qn','/norestart','UPDATE=1',('INSTALLDIR="' + $msiRoot + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-fresh-update-refused.log') + '"')) -ExpectFailure
    Run-Setup $exe0 @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $exeRoot + '"'),'/TASKS=startup,desktopicon')
    $uninstallExe = Join-Path $exeRoot 'unins000.exe'
    Assert-Identity $exeRoot Exe '1.1.2' $true $true
    Run-Setup msiexec.exe @('/i', ('"' + $msi0 + '"'), '/qn','/norestart',('INSTALLDIR="' + $msiRoot + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-kind-refused.log') + '"')) -ExpectFailure
    Remove-ItemProperty -LiteralPath $runKey -Name CloudInletDebug
    Remove-Item -LiteralPath $desktop
    Invoke-Worker $exeRoot Exe $exe1 -Corrupt
    if ((Get-ItemProperty -LiteralPath $runKey -Name CloudInletDebug -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $desktop)) { throw 'A rejected update changed current Windows preferences.' }
    Invoke-Worker $exeRoot Exe $exe1
    Invoke-Worker $exeRoot Exe (Join-Path $fixture '1.1.4/App/CloudInlet.exe') -TargetVersion '1.1.4' -SimulatedRestart
    Assert-Identity $exeRoot Exe '1.1.3' $false $false
    Invoke-Worker $exeRoot Exe $exe2 -TargetVersion '1.1.4' -UseCanonicalCache -Corrupt
    Invoke-Worker $exeRoot Exe $exe2 -TargetVersion '1.1.4' -UseCanonicalCache
    Assert-Identity $exeRoot Exe '1.1.4' $false $false
    # A real matching activation server proves that uninstall waits for graceful
    # exit instead of stopping unrelated processes or overwriting a running app.
    $pipeReady = Join-Path $fixture 'pipe-ready.txt'
    $running = Start-Process -FilePath (Join-Path $exeRoot 'CloudInlet.exe') -ArgumentList @('--serve', ('"' + $pipeReady + '"')) -PassThru -WindowStyle Hidden
    Wait-File $pipeReady
    Run-Setup $uninstallExe @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $fixture 'exe-uninstall.log') + '"'))
    $uninstallExe = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-Path -LiteralPath (Join-Path $exeRoot 'unins000.exe')) -or (Test-Path -LiteralPath ($debugRegistry + '\Exe'))) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The temporary EXE uninstaller did not complete.' }
        Start-Sleep -Milliseconds 100
    }
    if (!$running.WaitForExit(5000) -or $running.ExitCode -ne 0) { throw 'The installer did not request a clean client shutdown.' }
    if ($ExeMigration) { Invoke-ExeMigrationFixture $exe2 }
    Run-Setup msiexec.exe @('/i', ('"' + $msi0 + '"'), '/qn','/norestart',('INSTALLDIR="' + $msiRoot + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-install.log') + '"'))
    $uninstallMsi = $msi0
    Assert-Identity $msiRoot Msi '1.1.2' $true $false
    Run-Setup $exe0 @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $exeRoot + '"')) -ExpectFailure
    New-Item -Path $approvedKey -Force | Out-Null
    $disabledStartup = [byte[]]@(3,0,0,0,11,12,13,14,15,16,17,18)
    New-ItemProperty -LiteralPath $approvedKey -Name CloudInletDebug -Value $disabledStartup -PropertyType Binary -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($desktop); $shortcut.TargetPath = Join-Path $msiRoot 'CloudInlet.exe'; $shortcut.Save()
    Invoke-Worker $msiRoot Msi $msi1
    $uninstallMsi = $msi1
    Assert-Identity $msiRoot Msi '1.1.3' $true $true
    $approval = (Get-ItemProperty -LiteralPath $approvedKey -Name CloudInletDebug).CloudInletDebug
    if ([Convert]::ToBase64String($approval) -cne [Convert]::ToBase64String($disabledStartup)) { throw 'MSI rebrand enabled startup disabled in Task Manager.' }
    if (Get-ItemProperty -LiteralPath $runKey -Name CloudBayDebug -ErrorAction SilentlyContinue) { throw 'MSI left a duplicate legacy startup command.' }
    Invoke-Worker $msiRoot Msi $msi2 -TargetVersion '1.1.4' -UseCanonicalCache
    $uninstallMsi = $msi2
    Assert-Identity $msiRoot Msi '1.1.4' $true $true
    if (!(Test-Path -LiteralPath ($debugRegistry + '\Msi')) -or (Test-Path -LiteralPath ($canonicalDebugRegistry + '\Msi'))) { throw 'A declined migration changed the existing registry identity.' }
    Run-Setup msiexec.exe @('/x', ('"' + $msi2 + '"'), '/qn','/norestart','/l*v',('"' + (Join-Path $fixture 'msi-uninstall.log') + '"'))
    $uninstallMsi = $null
    if ((Test-Path -LiteralPath (Join-Path $exeRoot 'CloudInlet.exe')) -or (Test-Path -LiteralPath (Join-Path $msiRoot 'CloudInlet.exe'))) { throw 'Uninstall did not remove its application files.' }

    # A second independent installation exercises the opt-in transaction with a
    # CloudBay folder. The frozen worker first hands over at the unchanged path.
    $migrationParent = Join-Path $fixture 'Migration'
    $migrationSource = Join-Path $migrationParent 'CloudBay Debug'
    $migrationTarget = Join-Path $migrationParent 'CloudInlet Debug'
    New-Item -ItemType Directory -Path $migrationParent | Out-Null
    Run-Setup msiexec.exe @('/i', ('"' + $msi0 + '"'), '/qn','/norestart',('INSTALLDIR="' + $migrationSource + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-migration-baseline.log') + '"'))
    $uninstallMsi = $msi0
    Invoke-Worker $migrationSource Msi $msi1
    $uninstallMsi = $msi1
    Assert-Identity $migrationSource Msi '1.1.3' $true $false
    New-Item -ItemType Directory -Path $migrationTarget | Out-Null
    $collision = Join-Path $migrationTarget 'do-not-overwrite.txt'
    'Independent destination content' | Set-Content -LiteralPath $collision -Encoding utf8NoBOM
    $collisionHash = (Get-FileHash -LiteralPath $collision -Algorithm SHA256).Hash
    Run-Setup msiexec.exe @('/i', ('"' + $msi2 + '"'), '/qn','/norestart','UPDATE=1','MIGRATEINSTALLATION=1',('MIGRATIONDIRECTORY="' + $migrationTarget + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-migration-collision.log') + '"')) -ExpectFailure
    Assert-Identity $migrationSource Msi '1.1.3' $true $false
    if ((Get-FileHash -LiteralPath $collision -Algorithm SHA256).Hash -ne $collisionHash) { throw 'The collision was overwritten.' }
    Remove-Item -LiteralPath $collision
    Remove-Item -LiteralPath $migrationTarget
    # A ready helper is not permission to install. An observer failure/cancel or
    # parent exit before the committed handoff must leave the old install intact.
    Invoke-Worker $migrationSource Msi $msi2 -TargetVersion '1.1.4' -UseCanonicalCache -MigrationDirectory $migrationTarget -UncommittedProgress
    Assert-Identity $migrationSource Msi '1.1.3' $true $false
    Invoke-Worker $migrationSource Msi $msi2 -TargetVersion '1.1.4' -UseCanonicalCache -MigrationDirectory $migrationTarget -CanceledProgress
    Assert-Identity $migrationSource Msi '1.1.3' $true $false
    Invoke-Worker $migrationSource Msi $msi2 -TargetVersion '1.1.4' -UseCanonicalCache -MigrationDirectory $migrationTarget
    $uninstallMsi = $msi2
    Assert-Identity $migrationTarget Msi '1.1.4' $true $false
    if ((Test-Path -LiteralPath (Join-Path $migrationSource 'CloudInlet.exe')) -or (Test-Path -LiteralPath ($debugRegistry + '\Msi')) -or !(Test-Path -LiteralPath ($canonicalDebugRegistry + '\Msi'))) { throw 'MSI migration left the old application or registration active.' }
    $registeredTarget = (Get-ItemProperty -LiteralPath ($canonicalDebugRegistry + '\Msi')).InstallDirectory
    if ($registeredTarget.TrimEnd('\') -ne $migrationTarget.TrimEnd('\')) { throw 'MSI registered an unexpected migration directory.' }
    if ((Get-ItemProperty -LiteralPath $runKey -Name CloudInletDebug).CloudInletDebug -notlike ('*' + $migrationTarget + '*')) { throw 'Startup did not follow the canonical installation.' }
    # Repair from the original download after the private pending package has
    # been removed. The helper must register the public basename with MSI.
    Run-Setup msiexec.exe @('/fav', ('"' + $msi2 + '"'), '/qn','/norestart','/l*v',('"' + (Join-Path $fixture 'msi-canonical-repair.log') + '"'))
    Assert-Identity $migrationTarget Msi '1.1.4' $true $false
    Run-Setup msiexec.exe @('/i', ('"' + $msi2 + '"'), '/qn','/norestart','UPDATE=1','MIGRATEINSTALLATION=1',('MIGRATIONDIRECTORY="' + $migrationTarget + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-migration-repair-refused.log') + '"')) -ExpectFailure
    Invoke-Worker $migrationTarget Msi $msi3 -TargetVersion '1.1.5' -UseCanonicalCache
    $uninstallMsi = $msi3
    Assert-Identity $migrationTarget Msi '1.1.5' $true $false
    $approval = (Get-ItemProperty -LiteralPath $approvedKey -Name CloudInletDebug).CloudInletDebug
    if ([Convert]::ToBase64String($approval) -cne [Convert]::ToBase64String($disabledStartup)) { throw 'The migration changed Windows startup approval.' }
    Run-Setup msiexec.exe @('/x', ('"' + $msi3 + '"'), '/qn','/norestart','/l*v',('"' + (Join-Path $fixture 'msi-canonical-uninstall.log') + '"'))
    $uninstallMsi = $null
    if ((Test-Path -LiteralPath (Join-Path $migrationTarget 'CloudInlet.exe')) -or (Test-Path -LiteralPath ($canonicalDebugRegistry + '\Msi')) -or (Test-Path -LiteralPath ($debugRegistry + '\Msi'))) { throw 'Canonical MSI uninstall left installed resources.' }

    # A user-selected custom installation directory is never renamed, but its
    # owned registration can still migrate when the user explicitly selects it.
    Run-Setup msiexec.exe @('/i', ('"' + $msi0 + '"'), '/qn','/norestart',('INSTALLDIR="' + $msiRoot + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-custom-migration-baseline.log') + '"'))
    $uninstallMsi = $msi0
    Invoke-Worker $msiRoot Msi $msi1
    $uninstallMsi = $msi1
    Invoke-Worker $msiRoot Msi $msi2 -TargetVersion '1.1.4' -UseCanonicalCache -MigrationDirectory $msiRoot
    $uninstallMsi = $msi2
    Assert-Identity $msiRoot Msi '1.1.4' $true $false
    if ((Test-Path -LiteralPath ($debugRegistry + '\Msi')) -or !(Test-Path -LiteralPath ($canonicalDebugRegistry + '\Msi'))) { throw 'The custom-path registration did not migrate.' }
    Run-Setup msiexec.exe @('/x', ('"' + $msi2 + '"'), '/qn','/norestart','/l*v',('"' + (Join-Path $fixture 'msi-custom-canonical-uninstall.log') + '"'))
    $uninstallMsi = $null
    foreach ($path in $releaseHashes.Keys) { if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $releaseHashes[$path]) { throw 'Release client settings or credentials changed.' } }
    if ((Get-ItemProperty -LiteralPath $runKey -Name CloudInlet -ErrorAction SilentlyContinue).CloudInlet -ne $releaseRun) { throw 'The Release startup choice changed.' }
    if ((Get-ItemProperty -LiteralPath $runKey -Name CloudBay -ErrorAction SilentlyContinue).CloudBay -ne $legacyReleaseRun) { throw 'The legacy Release startup choice changed.' }
    if ((Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' | ConvertTo-Json -Compress) -ne $releaseFolders) { throw 'Windows folder mappings changed.' }
    if ((Get-FileHash -LiteralPath $ownedMarker -Algorithm SHA256).Hash -ne $markerHash) { throw 'Uninstall changed private client data.' }
    [ordered]@{ fixtureRoot=$fixture; msiOptInMigrationPassed=$true; authenticatedMigrationProgressPassed=$true; uncommittedProgressHandoffRefused=$true; canceledProgressHandoffRefused=$true; msiMigrationCollisionRefused=$true; msiCanonicalRepairPassed=$true; msiCanonicalFollowupUpdatePassed=$true; msiCanonicalUninstallPassed=$true; declinedLegacyRegistrationPreserved=$true; publishedCloudInlet1_1_2WorkerUsed=$true; currentWorkerUpgradePassed=$true; legacyCacheUpgradePassed=$true; canonicalCacheExeUpgradePassed=$true; canonicalCacheMsiUpgradePassed=$true; canonicalCacheCorruptionRefused=$true; windowsDisabledStartupPreserved=$true; bridgeToCanonicalOnlyPassed=$true; originalBrandedDirectoryPreserved=$true; exeWorkerUpgradePassed=$true; msiWorkerUpgradePassed=$true; checksumRecheckedAfterShutdown=$true; readinessAcknowledged=$true; currentChoicesPreserved=$true; customInstallDirectoryPreserved=$true; gracefulShutdownPassed=$true; crossKindRefused=$true; freshUpdateRefused=$true; privateStatePreserved=$true; releaseUntouched=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'installer-validation.json') -Encoding utf8NoBOM
    Write-Output "Installer/update fixture tests passed: $fixture"
} finally {
    if ($uninstallExe -and (Test-Path -LiteralPath $uninstallExe)) { Run-Setup $uninstallExe @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') }
    if ($uninstallMsi) { Run-Setup msiexec.exe @('/x', ('"' + $uninstallMsi + '"'), '/qn','/norestart') }
    # A guard regression can succeed before the expected-failure assertion. If
    # that happens, remove only this run's exact registered fixture product.
    foreach ($identityRoot in @($debugRegistry, $canonicalDebugRegistry)) {
        $remaining = Get-ItemProperty -LiteralPath ($identityRoot + '\Msi') -ErrorAction SilentlyContinue
        $allowedRoots = @((Join-Path $fixture 'Installed Msi'), (Join-Path $fixture 'Migration/CloudBay Debug'), (Join-Path $fixture 'Migration/CloudInlet Debug'))
        if ($remaining -and $remaining.InstallDirectory.TrimEnd('\') -in $allowedRoots) {
            $installedVersion = $remaining.Version
            if ($installedVersion -notin @('1.1.2','1.1.3','1.1.4','1.1.5')) { throw 'Fixture MSI cleanup encountered an unexpected product version.' }
            $installedPackage = Join-Path $fixture ($installedVersion + '/Packages/CloudInlet-' + $installedVersion + '-win-x64-debug-setup.msi')
            Run-Setup msiexec.exe @('/x', ('"' + $installedPackage + '"'), '/qn','/norestart')
        }
    }
    Remove-ItemProperty -LiteralPath $approvedKey -Name CloudBayDebug,CloudInletDebug -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $ownedMarker -ErrorAction SilentlyContinue
    # Cache was absent at entry and created solely by this run. Check its resolved
    # path and every entry before removing it; never clean a preexisting cache.
    $expectedCaches = @('CloudBay/Debug/Client/Updates', 'CloudInlet/Debug/Client/Updates') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA $_)) }
    foreach ($fixtureCache in @($cache, $canonicalCache)) {
        if ([IO.Path]::GetFullPath($fixtureCache) -notin $expectedCaches) { throw 'Fixture update cache cleanup path changed.' }
        Assert-NormalPath $fixtureCache
        if (Test-Path -LiteralPath $fixtureCache) {
            if (Get-ChildItem -LiteralPath $fixtureCache -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Fixture cache cleanup encountered a link.' }
            Remove-Item -LiteralPath $fixtureCache -Recurse -Force
        }
    }
}
[ordered]@{ success=$true; fixtureRoot=$fixture; userSid=$ExpectedUserSid } | ConvertTo-Json | Set-Content -LiteralPath $CompletionFile -Encoding utf8NoBOM
Stop-Transcript | Out-Null
