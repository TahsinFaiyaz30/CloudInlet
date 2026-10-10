param([string]$Version)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$versionInfo = & (Join-Path $PSScriptRoot 'get-release-version.ps1')
if (!$Version) { $Version = $versionInfo.version }
if ($Version -cne $versionInfo.version) { throw 'Release version must match version.json.' }
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts\release'))

function Assert-ReleasePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (!$absolute.StartsWith($releaseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release output escaped its directory.' }
    $ancestor = $absolute
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Release output contains a linked path.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $absolute
}

function Assert-ReleaseTree([string]$Path) {
    $absolute = Assert-ReleasePath $Path
    if ((Test-Path -LiteralPath $absolute) -and (Get-Item -LiteralPath $absolute).PSIsContainer) {
        if (Get-ChildItem -LiteralPath $absolute -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw 'Release output contains a linked item.'
        }
    }
    return $absolute
}
$stagingRoot = Join-Path $releaseRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stagingRoot "CloudInlet-$Version-win-x64"
$finalPackage = Assert-ReleasePath (Join-Path $releaseRoot "CloudInlet-$Version-win-x64")
$archive = Assert-ReleasePath (Join-Path $releaseRoot "CloudInlet-$Version-win-x64.zip")
$checksum = Assert-ReleasePath "$archive.sha256"
$stagingRoot = Assert-ReleasePath $stagingRoot
$appFolder = Join-Path $package 'App'
$lockHashProvider = [Security.Cryptography.SHA256]::Create()
try { $lockHash = [BitConverter]::ToString($lockHashProvider.ComputeHash([Text.Encoding]::UTF8.GetBytes($repository.ToUpperInvariant()))).Replace('-', '') }
finally { $lockHashProvider.Dispose() }
$releaseLock = [Threading.Mutex]::new($false, ('Local\CloudInlet.Release.' + $lockHash))
$ownsReleaseLock = $false
Push-Location $repository
try {
    try { $ownsReleaseLock = $releaseLock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsReleaseLock = $true }
    if (!$ownsReleaseLock) { throw 'Another CloudInlet release build is running for this repository.' }
    New-Item -ItemType Directory -Path $appFolder -Force | Out-Null
    dotnet build CloudInlet.sln -c Release -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test CloudInlet.Tests\CloudInlet.Tests.csproj -c Release --no-build --filter 'TestCategory!=LiveProvider' -v:minimal --logger 'trx;LogFileName=release-package.trx' --results-directory artifacts\validation\tests
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
    dotnet publish CloudInlet\CloudInlet.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -o $appFolder -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    & (Join-Path $PSScriptRoot 'build-installer-helper.ps1') -AppFolder $appFolder
    [ordered]@{ schemaVersion = 1; version = $Version; buildFlavor = 'Release'; installerKind = 'Portable'; architecture = 'x64'; sourceRevision = $versionInfo.sourceRevision } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $appFolder 'distribution.json') -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'copy-release-notices.ps1') -AppFolder $appFolder -AssetsPath (Join-Path $repository 'CloudInlet/obj/project.assets.json')
    Copy-Item -LiteralPath (Join-Path $repository 'packaging\Install.ps1') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'packaging\Uninstall.ps1') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'docs') -Destination $package -Recurse -Force
    $validationGuide = Join-Path $package 'tools/CloudInlet.Validation'
    New-Item -ItemType Directory -Path $validationGuide -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'tools/CloudInlet.Validation/README.md') -Destination $validationGuide -Force

    # Finish and hash the archive before replacing any prior successful output.
    $checkedPackage = Assert-ReleaseTree $package
    $stagedArchive = Assert-ReleasePath (Join-Path $stagingRoot "CloudInlet-$Version-win-x64.zip")
    Compress-Archive -LiteralPath $checkedPackage -DestinationPath $stagedArchive
    $hash = Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256
    $stagedChecksum = Assert-ReleasePath "$stagedArchive.sha256"
    "$($hash.Hash)  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath $stagedChecksum -Encoding ascii

    $generation = [Guid]::NewGuid().ToString('N')
    $destinations = @($finalPackage, $archive, $checksum)
    foreach ($destination in $destinations) { Assert-ReleaseTree $destination | Out-Null }
    $preserved = [Collections.Generic.List[object]]::new()
    $promoted = [Collections.Generic.List[string]]::new()
    try {
        foreach ($destination in $destinations) {
            if (Test-Path -LiteralPath $destination) {
                $previous = Assert-ReleasePath ($destination + '.previous-' + $generation)
                Move-Item -LiteralPath $destination -Destination $previous
                $preserved.Add(@{ Original = $destination; Previous = $previous })
            }
        }
        Move-Item -LiteralPath $checkedPackage -Destination $finalPackage
        $promoted.Add($finalPackage)
        Move-Item -LiteralPath $stagedArchive -Destination $archive
        $promoted.Add($archive)
        Move-Item -LiteralPath $stagedChecksum -Destination $checksum
        $promoted.Add($checksum)
    }
    catch {
        $promotionError = $_
        try {
            for ($index = $promoted.Count - 1; $index -ge 0; $index--) {
                $failed = Assert-ReleasePath ($promoted[$index] + '.failed-' + $generation)
                Move-Item -LiteralPath (Assert-ReleaseTree $promoted[$index]) -Destination $failed
            }
            for ($index = $preserved.Count - 1; $index -ge 0; $index--) {
                Move-Item -LiteralPath (Assert-ReleaseTree $preserved[$index].Previous) -Destination $preserved[$index].Original
            }
        }
        catch { throw "Release promotion failed and automatic rollback could not finish. Prior outputs remain with suffix .previous-$generation. $($_.Exception.Message)" }
        throw $promotionError
    }
    $checkedStaging = Assert-ReleasePath $stagingRoot
    try { Remove-Item -LiteralPath $checkedStaging }
    catch { Write-Warning 'The completed release is ready, but its empty staging directory could not be removed.' }
    Write-Output "Release: $archive"
} finally {
    if ($ownsReleaseLock) { $releaseLock.ReleaseMutex() }
    $releaseLock.Dispose()
    Pop-Location
}
