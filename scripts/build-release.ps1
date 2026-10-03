param([string]$Version = '2.0.0')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][a-zA-Z0-9.]+)?$') { throw 'Invalid release version.' }
$releaseRoot = Join-Path $repository 'artifacts\release'
$stagingRoot = Join-Path $releaseRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stagingRoot "CloudBay-$Version-win-x64"
$finalPackage = [IO.Path]::GetFullPath((Join-Path $releaseRoot "CloudBay-$Version-win-x64"))
if (!$finalPackage.StartsWith([IO.Path]::GetFullPath($releaseRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid release package destination.' }
$appFolder = Join-Path $package 'App'
New-Item -ItemType Directory -Path $appFolder -Force | Out-Null
Push-Location $repository
try {
    dotnet build CloudBay.sln -c Release -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test CloudBay.Tests\CloudBay.Tests.csproj -c Release --no-build -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
    dotnet publish CloudBay\CloudBay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -o $appFolder -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repository 'packaging\Install.ps1') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'packaging\Uninstall.ps1') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $package -Force
    Copy-Item -LiteralPath (Join-Path $repository 'docs') -Destination $package -Recurse -Force
    if (Test-Path -LiteralPath $finalPackage) {
        $previousPackage = [IO.Path]::GetFullPath($finalPackage + '.previous-' + [Guid]::NewGuid().ToString('N'))
        if (!$previousPackage.StartsWith([IO.Path]::GetFullPath($releaseRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid previous package destination.' }
        Move-Item -LiteralPath $finalPackage -Destination $previousPackage
    }
    Move-Item -LiteralPath $package -Destination $finalPackage
    Remove-Item -LiteralPath $stagingRoot
    $archive = Join-Path $releaseRoot "CloudBay-$Version-win-x64.zip"
    Compress-Archive -LiteralPath $finalPackage -DestinationPath $archive -Force
    $hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    "$($hash.Hash)  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Output "Release: $archive"
} finally { Pop-Location }
