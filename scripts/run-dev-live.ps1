param([switch]$Watch)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$liveRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts\live'))
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repository 'CloudBay\bin'))
$markerPath = Join-Path $liveRoot 'build-ready.json'
$appFolder = [IO.Path]::GetFullPath((Join-Path $liveRoot 'App'))
$previousFolder = [IO.Path]::GetFullPath((Join-Path $liveRoot 'Previous'))
$logPath = Join-Path $liveRoot 'refresh.log'
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

function Refresh-LiveApp {
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    $source = [IO.Path]::GetFullPath([string]$marker.sourcePath)
    if (!$source.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Live preview requires a successful local CloudBay build.' }
    if (!(Test-Path -LiteralPath (Join-Path $source 'CloudBay.exe'))) { throw 'The successful build has no executable.' }
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

    $executable = Join-Path $appFolder 'CloudBay.exe'
    $checkedApp = Assert-LivePath $appFolder
    if (Test-Path -LiteralPath $executable) {
        if (Get-ChildItem -LiteralPath $checkedApp -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw 'The current development output contains a linked item and was retained.'
        }
        $running = @(Get-Process CloudBay -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
        if ($running.Count) {
            $shutdown = Start-Process -FilePath $executable -ArgumentList '--ui-live', '--shutdown' -WindowStyle Hidden -PassThru
            if (!$shutdown.WaitForExit(10000)) { throw 'The development shutdown request did not finish.' }
            foreach ($process in $running) {
                if (!$process.WaitForExit(30000)) { throw 'CloudBay is still preparing its files. The current preview was retained.' }
            }
        }
        Move-Item -LiteralPath $checkedApp -Destination $checkedPrevious
    }
    $checkedStaging = Assert-LivePath $staging
    try {
        Move-Item -LiteralPath $checkedStaging -Destination $checkedApp
        $preview = Start-Process -FilePath (Join-Path $appFolder 'CloudBay.exe') -ArgumentList '--ui-live' -WorkingDirectory $repository -PassThru
        if ($preview.WaitForExit(1500)) { throw 'The new development app exited during launch.' }
    }
    catch {
        $refreshError = $_
        if (Test-Path -LiteralPath $checkedPrevious) {
            if (Test-Path -LiteralPath $checkedApp) {
                $failedFolder = Assert-LivePath (Join-Path $liveRoot ('.failed-' + [Guid]::NewGuid().ToString('N')))
                Move-Item -LiteralPath $checkedApp -Destination $failedFolder
            }
            Move-Item -LiteralPath $checkedPrevious -Destination $checkedApp
            Start-Process -FilePath (Join-Path $appFolder 'CloudBay.exe') -ArgumentList '--ui-live' -WorkingDirectory $repository | Out-Null
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
