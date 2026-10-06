param([switch]$Watch)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$liveRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts\live'))
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repository 'CloudInlet\bin'))
$markerPath = Join-Path $liveRoot 'build-ready.json'
$appFolder = [IO.Path]::GetFullPath((Join-Path $liveRoot 'App'))
$previousFolder = [IO.Path]::GetFullPath((Join-Path $liveRoot 'Previous'))
$logPath = Join-Path $liveRoot 'refresh.log'
$liveState = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CloudBay\UiLive\Client'
$readinessPath = Join-Path $liveState 'ui-ready.json'
New-Item -ItemType Directory -Path $liveRoot -Force | Out-Null
if ($Watch) { [IO.File]::WriteAllText((Join-Path $liveRoot 'watch.enabled'), 'Refresh after successful builds.') }

function Assert-LivePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (!$absolute.StartsWith($liveRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Development output escaped its directory.' }
    $current = $absolute
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Development output contains a linked path.' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $absolute
}

function Wait-PreviewReady([Diagnostics.Process]$Preview, [DateTimeOffset]$LaunchedUtc) {
    # Process survival alone is insufficient: an exception dialog can leave a
    # main window alive before its notification icon has been registered.
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    $failurePath = Join-Path $liveState ('ui-startup-failure-' + $Preview.Id + '.txt')
    do {
        $Preview.Refresh()
        if ($Preview.HasExited) { throw 'The new development app exited before its tray was ready.' }
        if (Test-Path -LiteralPath $failurePath) {
            if ((Get-Item -LiteralPath $failurePath).LastWriteTimeUtc -ge $LaunchedUtc.UtcDateTime.AddSeconds(-1)) {
                throw ('The new development app failed during startup. See ' + $failurePath)
            }
        }
        if (Test-Path -LiteralPath $readinessPath) {
            $ready = $null
            $readyStartup = [DateTimeOffset]::MinValue
            try {
                $ready = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json
                # PowerShell 7 may deserialize JSON timestamps as DateTime;
                # stringifying them drops the UTC offset. Preserve the instant
                # before comparing it with the time this child was launched.
                if ($ready.startupUtc -is [DateTimeOffset]) { $readyStartup = $ready.startupUtc }
                elseif ($ready.startupUtc -is [DateTime]) { $readyStartup = [DateTimeOffset]::new($ready.startupUtc).ToUniversalTime() }
                else {
                    $readyStartup = [DateTimeOffset]::Parse([string]$ready.startupUtc,
                        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None)
                }
            }
            catch [System.ArgumentException] { }
            catch [System.FormatException] { }
            catch [System.IO.IOException] { }
            catch [System.Management.Automation.RuntimeException] { }
            if ($null -ne $ready -and [int]$ready.processId -eq $Preview.Id -and $ready.trayReady -eq $true -and
                $ready.controllerReady -eq $true -and $ready.navigationReady -eq $true -and
                ![string]::IsNullOrWhiteSpace([string]$ready.visibleRoute) -and $readyStartup -ge $LaunchedUtc.AddSeconds(-1)) {
                # Also catch an immediate failure dispatched after startup.
                if ($Preview.WaitForExit(500)) { throw 'The new development app exited immediately after becoming ready.' }
                if ((Test-Path -LiteralPath $failurePath) -and
                    (Get-Item -LiteralPath $failurePath).LastWriteTimeUtc -ge $LaunchedUtc.UtcDateTime.AddSeconds(-1)) {
                    throw ('The new development app failed immediately after startup. See ' + $failurePath)
                }
                return
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The new development app did not confirm that its tray, controller, and visible page were ready within two minutes.'
}

function Stop-FailedPreview([Diagnostics.Process]$Preview, [string]$Executable) {
    $Preview.Refresh()
    if ($Preview.HasExited) { return }
    $owned = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $Preview.Id)
    if (!$owned) {
        # An isolated startup failure may finish exiting between the first
        # HasExited check and the native process lookup. No child remains to
        # stop, so continue restoring the known working output.
        if ($Preview.WaitForExit(1000)) { return }
        throw 'The failed preview process is still exiting; its output was retained.'
    }
    if (![string]::Equals([string]$owned.ExecutablePath, $Executable, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$owned.CommandLine -notmatch '(?i)(^|\s)--ui-live(?=\s|$)') {
        # CIM can return a terminating process with empty path/command data.
        # Its original Process handle still safely identifies the child.
        if ($Preview.WaitForExit(1000)) { return }
        throw 'The failed preview process could not be confirmed as the isolated child started by this refresh; it was retained.'
    }
    # Only this refresh's isolated child may be terminated. A constructor
    # failure can happen before the normal shutdown pipe exists.
    $shutdown = Start-Process -FilePath $Executable -ArgumentList '--ui-live', '--shutdown' -WindowStyle Hidden -PassThru
    if (!$shutdown.WaitForExit(5000)) { throw 'The failed preview shutdown request did not finish.' }
    if (!$Preview.WaitForExit(5000)) {
        $Preview.Kill()
        if (!$Preview.WaitForExit(10000)) { throw 'The failed preview still owns its files and was retained.' }
    }
}

function Refresh-LiveApp {
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    $source = [IO.Path]::GetFullPath([string]$marker.sourcePath)
    if (!$source.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Live preview requires a successful local CloudInlet build.' }
    if (!(Test-Path -LiteralPath (Join-Path $source 'CloudInlet.exe'))) { throw 'The successful build has no executable.' }
    $sourceAncestor = $source
    while ($sourceAncestor) {
        if ((Get-Item -LiteralPath $sourceAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The build path is linked.' }
        $sourceAncestor = [IO.Path]::GetDirectoryName($sourceAncestor)
    }
    $sourceItems = @(Get-ChildItem -LiteralPath $source -Recurse -Force)
    if ($sourceItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'The build contains a linked item.' }
    $staging = Assert-LivePath (Join-Path $liveRoot ('.staging-' + [Guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Path $staging | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse -Force
    }

    # Keep one complete prior build for rollback. Check and remove that old
    # rollback copy before touching the currently running preview.
    $checkedPrevious = Assert-LivePath $previousFolder
    if (Test-Path -LiteralPath $checkedPrevious) {
        if (Get-ChildItem -LiteralPath $checkedPrevious -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw 'The previous development backup contains a linked item and was retained.'
        }
        Remove-Item -LiteralPath $checkedPrevious -Recurse -Force
    }

    $executable = Join-Path $appFolder 'CloudInlet.exe'
    $checkedApp = Assert-LivePath $appFolder
    if (Test-Path -LiteralPath $executable) {
        if (Get-ChildItem -LiteralPath $checkedApp -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw 'The current development output contains a linked item and was retained.'
        }
        $running = @(Get-Process CloudInlet -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
        if ($running.Count) {
            $shutdown = Start-Process -FilePath $executable -ArgumentList '--ui-live', '--shutdown' -WindowStyle Hidden -PassThru
            if (!$shutdown.WaitForExit(10000)) { throw 'The development shutdown request did not finish.' }
            foreach ($process in $running) {
                if (!$process.WaitForExit(30000)) { throw 'CloudInlet is still preparing its files. The current preview was retained.' }
            }
        }
        Move-Item -LiteralPath $checkedApp -Destination $checkedPrevious
    }
    $checkedStaging = Assert-LivePath $staging
    $preview = $null
    try {
        Move-Item -LiteralPath $checkedStaging -Destination $checkedApp
        $launchedUtc = [DateTimeOffset]::UtcNow
        $preview = Start-Process -FilePath (Join-Path $appFolder 'CloudInlet.exe') -ArgumentList '--ui-live' -WorkingDirectory $repository -PassThru
        Wait-PreviewReady $preview $launchedUtc
    }
    catch {
        $refreshError = $_
        if ($null -ne $preview) { Stop-FailedPreview $preview (Join-Path $appFolder 'CloudInlet.exe') }
        if (Test-Path -LiteralPath $checkedPrevious) {
            if (Test-Path -LiteralPath $checkedApp) {
                $failedFolder = Assert-LivePath (Join-Path $liveRoot ('.failed-' + [Guid]::NewGuid().ToString('N')))
                Move-Item -LiteralPath $checkedApp -Destination $failedFolder
            }
            Move-Item -LiteralPath $checkedPrevious -Destination $checkedApp
            Start-Process -FilePath (Join-Path $appFolder 'CloudInlet.exe') -ArgumentList '--ui-live' -WorkingDirectory $repository | Out-Null
        }
        throw $refreshError
    }
    Add-Content -LiteralPath $logPath -Value ([DateTimeOffset]::Now.ToString('o') + ' Refreshed the development app from successful build ' + $marker.builtUtc)
}

$lastMarker = ''
try { do {
    if (Test-Path -LiteralPath $markerPath) {
        $currentMarker = Get-Content -LiteralPath $markerPath -Raw
        if ($currentMarker -ne $lastMarker) {
            try { Refresh-LiveApp; $lastMarker = $currentMarker }
            catch {
                Add-Content -LiteralPath $logPath -Value ([DateTimeOffset]::Now.ToString('o') + ' Refresh deferred: ' + $_.Exception.Message)
                if (!$Watch) { throw }
                # Retry only after a newer successful build marker; retain the open app on failure.
                $lastMarker = $currentMarker
            }
        }
    }
    if ($Watch) { Start-Sleep -Milliseconds 750 }
} while ($Watch) }
finally {
    if ($Watch) { Remove-Item -LiteralPath (Join-Path $liveRoot 'watch.enabled') -ErrorAction SilentlyContinue }
}
