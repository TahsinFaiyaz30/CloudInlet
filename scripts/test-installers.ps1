param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/validation'),
    [switch]$PhysicalContext,
    [string]$ExpectedUserSid,
    [string]$CompletionFile
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
    [ordered]@{ success=$false; error=$_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $CompletionFile -Encoding utf8NoBOM
    Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
    throw $_
}
$runId = [Guid]::NewGuid().ToString('N')
$fixture = Join-Path $output ('installer-fixture-' + $runId)
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$debugRegistry = 'HKCU:\Software\CloudBay\Distribution\Debug'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'CloudInlet Debug.lnk'
$state = Join-Path $env:LOCALAPPDATA 'CloudBay/Debug/Client'
$cache = Join-Path $state 'Updates'
$defaultDebugInstall = Join-Path $env:LOCALAPPDATA 'Programs/CloudInlet Debug'
foreach ($protected in @($state, $cache, $defaultDebugInstall)) { Assert-NormalPath $protected }
if ((Test-Path -LiteralPath ($debugRegistry + '\Exe')) -or (Test-Path -LiteralPath ($debugRegistry + '\Msi')) -or (Test-Path -LiteralPath $defaultDebugInstall) -or (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Programs/CloudBay Debug'))) { throw 'An existing Debug installation must not be replaced by fixture tests.' }
if ((Get-ItemProperty -LiteralPath $runKey -Name CloudInletDebug -ErrorAction SilentlyContinue) -or (Get-ItemProperty -LiteralPath $runKey -Name CloudBayDebug -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $desktop) -or (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'CloudBay Debug.lnk')) -or (Test-Path -LiteralPath $cache)) { throw 'Existing Debug startup, shortcut or update cache must not be overwritten.' }
if ((Get-ItemProperty -LiteralPath $approvedKey -Name CloudBayDebug -ErrorAction SilentlyContinue) -or (Get-ItemProperty -LiteralPath $approvedKey -Name CloudInletDebug -ErrorAction SilentlyContinue)) { throw 'Existing Debug Windows startup approval must not be overwritten.' }
$releaseRun = (Get-ItemProperty -LiteralPath $runKey -Name CloudInlet -ErrorAction SilentlyContinue).CloudInlet
$legacyReleaseRun = (Get-ItemProperty -LiteralPath $runKey -Name CloudBay -ErrorAction SilentlyContinue).CloudBay
$releaseFolders = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' | ConvertTo-Json -Compress)
$releaseHashes = @{}
foreach ($name in @('settings.json', 'credentials.dpapi')) {
    $path = Join-Path $env:LOCALAPPDATA ('CloudBay/Client/' + $name)
    if (Test-Path -LiteralPath $path) { $releaseHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
$ownedMarker = Join-Path $state ('installer-preservation-fixture-' + $runId + '.txt')
New-Item -ItemType Directory -Path $state, $cache -Force | Out-Null
'Settings, credentials and backups remain outside the installer tree.' | Set-Content -LiteralPath $ownedMarker -Encoding utf8NoBOM
$markerHash = (Get-FileHash -LiteralPath $ownedMarker -Algorithm SHA256).Hash
"CloudBay update cache v1`n" | Set-Content -LiteralPath (Join-Path $cache '.cloudbay-update-cache-v1') -NoNewline -Encoding utf8NoBOM
function Run-Setup([string]$File, [string[]]$Arguments, [switch]$ExpectFailure) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($ExpectFailure) { if ($process.ExitCode -in @(0,3010)) { throw 'An unsupported installer command unexpectedly succeeded.' } }
    elseif ($process.ExitCode -notin @(0,3010)) { throw "Installer failed: $File ($($process.ExitCode))" }
}
function Assert-Identity([string]$Path, [string]$Kind, [string]$Version, [bool]$Startup, [bool]$Shortcut) {
    $metadata = Get-Content -LiteralPath (Join-Path $Path 'distribution.json') -Raw | ConvertFrom-Json
    if ($metadata.version -ne $Version -or $metadata.installerKind -ne $Kind -or $metadata.buildFlavor -ne 'Debug' -or $metadata.installDirectory.TrimEnd('\') -ne $Path.TrimEnd('\')) { throw 'Installed identity was not preserved correctly.' }
    if ($metadata.startWithWindows -ne $Startup -or $metadata.desktopShortcut -ne $Shortcut) { throw "$Kind $Version current preferences were not recorded correctly." }
    $name = if ($Version -eq '1.1.1') { 'CloudBayDebug' } else { 'CloudInletDebug' }
    $shortcutPath = if ($Version -eq '1.1.1') { Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'CloudBay Debug.lnk' } else { $desktop }
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
function Test-CompatibilityLauncher([string]$Install) {
    $capture = Join-Path $fixture 'launcher-arguments.txt'
    $values = @('', 'a path with spaces', 'embedded"quote', 'C:\trailing slash\', '\\server\folder with space\', 'cloudinlet://open?name=日本語')
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $Install 'CloudBay.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($value in @('--capture-arguments', $capture) + $values) { $start.ArgumentList.Add($value) }
    $launcher = [Diagnostics.Process]::Start($start)
    if (!$launcher.WaitForExit(10000) -or $launcher.ExitCode -ne 0) { throw 'The compatibility launcher could not start CloudInlet.' }
    Wait-File ($capture + '.ready')
    $lines = [IO.File]::ReadAllLines($capture)
    if ($lines[0] -cne 'CloudInlet' -or $lines.Length -ne $values.Count + 1) { throw 'The compatibility launcher did not start the branded process.' }
    for ($index = 0; $index -lt $values.Count; $index++) {
        if ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($lines[$index + 1])) -cne $values[$index]) { throw 'The compatibility launcher changed activation arguments.' }
    }
}
function Invoke-Worker([string]$Install, [string]$Kind, [string]$Package, [switch]$Corrupt) {
    $case = $Kind.ToLowerInvariant() + $(if ($Corrupt) { '-corrupt' } else { '-valid' })
    $hostRoot = Join-Path $cache ('fixture-host-' + $runId + '-' + $case)
    New-Item -ItemType Directory -Path $hostRoot | Out-Null
    foreach ($name in @('CloudBay.SetupHelper.exe', 'CloudBay.SetupHelper.exe.config')) { Copy-Item -LiteralPath (Join-Path $Install $name) -Destination $hostRoot }
    $pending = Join-Path $cache ('pending-CloudBay-1.1.2-win-x64-debug-setup.' + $Kind.ToLowerInvariant())
    Copy-Item -LiteralPath $Package -Destination $pending
    $hash = (Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -LiteralPath $pending).Length
    if ($Corrupt) {
        $stream = [IO.File]::Open($pending, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.WriteByte(0); $stream.Flush($true) } finally { $stream.Dispose() }
    }
    $parent = Start-Process -FilePath (Join-Path $Install 'CloudBay.exe') -ArgumentList @('--wait', '5') -PassThru -WindowStyle Hidden
    $parent.Refresh()
    $result = Join-Path $hostRoot 'result.json'
    $ready = Join-Path $hostRoot 'ready.json'
    $request = Join-Path $hostRoot 'request.json'
    [ordered]@{ schemaVersion=1; parentProcessId=$parent.Id; parentStartUtcTicks=$parent.StartTime.ToUniversalTime().Ticks; installDirectory=$Install; buildFlavor='Debug'; installerKind=$Kind; installedVersion='1.1.1'; targetVersion='1.1.2'; packagePath=$pending; packageSha256=$hash; packageSize=$size; resultPath=$result; readinessPath=$ready; restartBackground=$true } | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8NoBOM
    $worker = Start-Process -FilePath (Join-Path $hostRoot 'CloudBay.SetupHelper.exe') -ArgumentList @('--update', ('"' + $request + '"')) -PassThru -WindowStyle Hidden
    Wait-File $ready
    $readiness = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
    if (!$readiness.ready -or $readiness.targetVersion -ne '1.1.2') { throw 'The worker did not acknowledge the verified request.' }
    if (!$worker.WaitForExit(120000)) { throw 'The update fixture did not complete. Do not terminate an active installer.' }
    Wait-File $result
    $record = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if ($Corrupt) {
        if ($worker.ExitCode -eq 0 -or $record.success -or $record.message -notmatch 'checksum changed') { throw 'The worker did not reject the corrupted package before installation.' }
        if ((Get-Content -LiteralPath (Join-Path $Install 'distribution.json') -Raw | ConvertFrom-Json).version -ne '1.1.1') { throw 'A corrupt update changed the installation.' }
    } elseif ($worker.ExitCode -notin @(0,3010) -or !$record.success) { throw "The external update worker failed: $($record.message)" }
    Remove-Item -LiteralPath $pending
}
$uninstallExe = $null
$uninstallMsi = $null
try {
    # Compile the real published installer/helper source for the baseline. This is
    # a source snapshot in ignored artifacts, not another Git worktree.
    $oldSource = Join-Path $fixture 'Published-1.1.1'
    New-Item -ItemType Directory -Path $oldSource | Out-Null
    $archive = Join-Path $fixture 'published-installer-source.tar'
    git -C $repository archive v1.1.1 -o $archive packaging scripts/build-installers.ps1 scripts/build-installer-helper.ps1 scripts/get-installer-tools.ps1 Directory.Build.props Directory.Build.targets version.json LICENSE
    if ($LASTEXITCODE -ne 0) { throw 'The immutable published installer source could not be read.' }
    tar -xf $archive -C $oldSource
    if ($LASTEXITCODE -ne 0) { throw 'The published installer fixture snapshot could not be extracted.' }
    $tools = & (Join-Path $PSScriptRoot 'get-installer-tools.ps1')
    foreach ($version in @('1.1.1', '1.1.2')) {
        $legacy = $version -eq '1.1.1'
        $brand = if ($legacy) { 'CloudBay' } else { 'CloudInlet' }
        $app = Join-Path $fixture "$version/App"
        $packages = Join-Path $fixture "$version/Packages"
        New-Item -ItemType Directory -Path $app, (Join-Path $app 'Assets'), $packages -Force | Out-Null
        dotnet build (Join-Path $repository 'packaging/InstallerTestClient/CloudInlet.InstallerTestClient.csproj') -c Release -p:Version=$version -p:AssemblyName=$brand -v:minimal | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Installer fixture client build failed.' }
        $client = Join-Path $repository 'packaging/InstallerTestClient/bin/Release/net472'
        foreach ($name in @("$brand.exe", "$brand.exe.config")) { Copy-Item -LiteralPath (Join-Path $client $name) -Destination $app }
        Copy-Item -LiteralPath (Join-Path $repository 'CloudInlet/Assets/CloudInlet.ico') -Destination (Join-Path $app "Assets/$brand.ico")
        Copy-Item -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Destination $app
        $builder = if ($legacy) { Join-Path $oldSource 'scripts/build-installers.ps1' } else { Join-Path $PSScriptRoot 'build-installers.ps1' }
        & $builder -AppFolder $app -Version $version -Configuration Debug -OutputDirectory $packages -InnoCompiler $tools.InnoCompiler -WixToolPath $tools.WixToolPath
    }
    $exe0 = Join-Path $fixture '1.1.1/Packages/CloudBay-1.1.1-win-x64-debug-setup.exe'
    $exe1 = Join-Path $fixture '1.1.2/Packages/CloudInlet-1.1.2-win-x64-debug-setup.exe'
    $msi0 = Join-Path $fixture '1.1.1/Packages/CloudBay-1.1.1-win-x64-debug-setup.msi'
    $msi1 = Join-Path $fixture '1.1.2/Packages/CloudInlet-1.1.2-win-x64-debug-setup.msi'
    $exeRoot = Join-Path $fixture 'Installed Exe'
    $msiRoot = Join-Path $fixture 'Installed Msi'
    Run-Setup $exe1 @('/UPDATE','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $exeRoot + '"')) -ExpectFailure
    Run-Setup msiexec.exe @('/i', ('"' + $msi1 + '"'), '/qn','/norestart','UPDATE=1',('INSTALLDIR="' + $msiRoot + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-fresh-update-refused.log') + '"')) -ExpectFailure
    Run-Setup $exe0 @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $exeRoot + '"'),'/TASKS=startup,desktopicon')
    $uninstallExe = Join-Path $exeRoot 'unins000.exe'
    Assert-Identity $exeRoot Exe '1.1.1' $true $true
    Run-Setup msiexec.exe @('/i', ('"' + $msi0 + '"'), '/qn','/norestart',('INSTALLDIR="' + $msiRoot + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-kind-refused.log') + '"')) -ExpectFailure
    Remove-ItemProperty -LiteralPath $runKey -Name CloudBayDebug
    Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'CloudBay Debug.lnk')
    Invoke-Worker $exeRoot Exe $exe1 -Corrupt
    if ((Get-ItemProperty -LiteralPath $runKey -Name CloudInletDebug -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $desktop)) { throw 'A rejected update changed current Windows preferences.' }
    Invoke-Worker $exeRoot Exe $exe1
    Assert-Identity $exeRoot Exe '1.1.2' $false $false
    Test-CompatibilityLauncher $exeRoot
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
    Run-Setup msiexec.exe @('/i', ('"' + $msi0 + '"'), '/qn','/norestart',('INSTALLDIR="' + $msiRoot + '"'),'/l*v',('"' + (Join-Path $fixture 'msi-install.log') + '"'))
    $uninstallMsi = $msi0
    Assert-Identity $msiRoot Msi '1.1.1' $true $false
    Run-Setup $exe0 @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $exeRoot + '"')) -ExpectFailure
    New-Item -Path $approvedKey -Force | Out-Null
    $disabledStartup = [byte[]]@(3,0,0,0,11,12,13,14,15,16,17,18)
    New-ItemProperty -LiteralPath $approvedKey -Name CloudBayDebug -Value $disabledStartup -PropertyType Binary -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($desktop); $shortcut.TargetPath = Join-Path $msiRoot 'CloudInlet.exe'; $shortcut.Save()
    Invoke-Worker $msiRoot Msi $msi1
    $uninstallMsi = $msi1
    Assert-Identity $msiRoot Msi '1.1.2' $true $true
    $approval = (Get-ItemProperty -LiteralPath $approvedKey -Name CloudInletDebug).CloudInletDebug
    if ([Convert]::ToBase64String($approval) -cne [Convert]::ToBase64String($disabledStartup)) { throw 'MSI rebrand enabled startup disabled in Task Manager.' }
    if (Get-ItemProperty -LiteralPath $runKey -Name CloudBayDebug -ErrorAction SilentlyContinue) { throw 'MSI left a duplicate legacy startup command.' }
    Run-Setup msiexec.exe @('/x', ('"' + $msi1 + '"'), '/qn','/norestart','/l*v',('"' + (Join-Path $fixture 'msi-uninstall.log') + '"'))
    $uninstallMsi = $null
    if ((Test-Path -LiteralPath (Join-Path $exeRoot 'CloudInlet.exe')) -or (Test-Path -LiteralPath (Join-Path $msiRoot 'CloudInlet.exe'))) { throw 'Uninstall did not remove its application files.' }
    foreach ($path in $releaseHashes.Keys) { if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $releaseHashes[$path]) { throw 'Release client settings or credentials changed.' } }
    if ((Get-ItemProperty -LiteralPath $runKey -Name CloudInlet -ErrorAction SilentlyContinue).CloudInlet -ne $releaseRun) { throw 'The Release startup choice changed.' }
    if ((Get-ItemProperty -LiteralPath $runKey -Name CloudBay -ErrorAction SilentlyContinue).CloudBay -ne $legacyReleaseRun) { throw 'The legacy Release startup choice changed.' }
    if ((Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' | ConvertTo-Json -Compress) -ne $releaseFolders) { throw 'Windows folder mappings changed.' }
    if ((Get-FileHash -LiteralPath $ownedMarker -Algorithm SHA256).Hash -ne $markerHash) { throw 'Uninstall changed private client data.' }
    [ordered]@{ fixtureRoot=$fixture; publishedLegacyWorkerUsed=$true; windowsDisabledStartupPreserved=$true; legacy1_1_1ToCloudInlet1_1_2Passed=$true; exeWorkerUpgradePassed=$true; msiWorkerUpgradePassed=$true; checksumRecheckedAfterShutdown=$true; readinessAcknowledged=$true; currentChoicesPreserved=$true; customInstallDirectoryPreserved=$true; gracefulShutdownPassed=$true; crossKindRefused=$true; freshUpdateRefused=$true; privateStatePreserved=$true; releaseUntouched=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'installer-validation.json') -Encoding utf8NoBOM
    Write-Output "Installer/update fixture tests passed: $fixture"
} finally {
    if ($uninstallExe -and (Test-Path -LiteralPath $uninstallExe)) { Run-Setup $uninstallExe @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') }
    if ($uninstallMsi) { Run-Setup msiexec.exe @('/x', ('"' + $uninstallMsi + '"'), '/qn','/norestart') }
    # A guard regression can succeed before the expected-failure assertion. If
    # that happens, remove only this run's exact registered fixture product.
    $remaining = Get-ItemProperty -LiteralPath ($debugRegistry + '\Msi') -ErrorAction SilentlyContinue
    if ($remaining -and $remaining.InstallDirectory.TrimEnd('\') -eq (Join-Path $fixture 'Installed Msi')) {
        $installedVersion = $remaining.Version
        if ($installedVersion -notin @('1.1.1','1.1.2')) { throw 'Fixture MSI cleanup encountered an unexpected product version.' }
        $cleanupBrand = if ($installedVersion -eq '1.1.1') { 'CloudBay' } else { 'CloudInlet' }
        $installedPackage = Join-Path $fixture ($installedVersion + '/Packages/' + $cleanupBrand + '-' + $installedVersion + '-win-x64-debug-setup.msi')
        Run-Setup msiexec.exe @('/x', ('"' + $installedPackage + '"'), '/qn','/norestart')
    }
    Remove-ItemProperty -LiteralPath $approvedKey -Name CloudBayDebug,CloudInletDebug -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $ownedMarker -ErrorAction SilentlyContinue
    # Cache was absent at entry and created solely by this run. Check its resolved
    # path and every entry before removing it; never clean a preexisting cache.
    $expectedCache = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CloudBay/Debug/Client/Updates'))
    if ([IO.Path]::GetFullPath($cache) -ne $expectedCache) { throw 'Fixture update cache cleanup path changed.' }
    Assert-NormalPath $cache
    if (Test-Path -LiteralPath $cache) {
        if (Get-ChildItem -LiteralPath $cache -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Fixture cache cleanup encountered a link.' }
        Remove-Item -LiteralPath $cache -Recurse -Force
    }
}
[ordered]@{ success=$true; fixtureRoot=$fixture; userSid=$ExpectedUserSid } | ConvertTo-Json | Set-Content -LiteralPath $CompletionFile -Encoding utf8NoBOM
Stop-Transcript | Out-Null
