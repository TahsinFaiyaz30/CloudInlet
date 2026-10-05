param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SourceRevision,
    [switch]$HostedTests
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$info = & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $repository
if ($Version -cne $info.version) { throw 'Package version must match version.json.' }
if (!$SourceRevision) { $SourceRevision = $info.sourceRevision }
if ($SourceRevision -cne $info.sourceRevision) { throw 'Package source revision must match the checked-out source.' }
$work = Join-Path $repository "artifacts/release-build/$Configuration"
$app = Join-Path $work 'App'
$testDirectory = Join-Path $work 'Tests'
if (Test-Path -LiteralPath $work) { throw 'Use a fresh artifacts/release-build directory for an immutable release build.' }
New-Item -ItemType Directory -Path $app, $testDirectory, $output -Force | Out-Null
Push-Location $repository
try {
    dotnet build CloudBay.sln -c $Configuration -p:Version=$Version -p:CloudBayBuildFlavor=$Configuration -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "$Configuration solution build failed." }
    $testArguments = @('test', 'CloudBay.Tests/CloudBay.Tests.csproj', '-c', $Configuration, '--no-build', '-v:minimal', '--logger', "trx;LogFileName=$Configuration.trx", '--results-directory', $testDirectory)
    if ($HostedTests) { $testArguments += @('--filter', 'FullyQualifiedName!~Native') }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw "$Configuration test gate failed." }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $testDirectory "$Configuration.trx") -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ([int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0 -or [int]$counts.passed -lt 1) { throw 'Release test results contain a failure, skip, or no executed tests.' }
    dotnet publish CloudBay/CloudBay.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -p:CloudBayBuildFlavor=$Configuration -o $app -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "$Configuration self-contained publish failed." }
    $portableIdentity = [ordered]@{ schemaVersion = 1; version = $Version; buildFlavor = $Configuration; installerKind = 'Portable'; architecture = 'x64'; installScope = 'perUser'; installDirectory = ''; startWithWindows = $false; desktopShortcut = $false; sourceRevision = $SourceRevision }
    $portableIdentity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $app 'distribution.json') -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'copy-release-notices.ps1') -AppFolder $app -AssetsPath (Join-Path $repository 'CloudBay/obj/project.assets.json')
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE'), (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Destination $app
    & (Join-Path $PSScriptRoot 'build-installer-helper.ps1') -AppFolder $app
    if ($env:CLOUDBAY_SIGNING_PFX_BASE64) { & (Join-Path $PSScriptRoot 'sign-release-artifacts.ps1') -Directory $app -ApplicationBinaries }
    & (Join-Path $PSScriptRoot 'build-installers.ps1') -AppFolder $app -Version $Version -Configuration $Configuration -OutputDirectory $output -SourceRevision $SourceRevision -HelperPrepared
    if ($env:CLOUDBAY_SIGNING_PFX_BASE64) { & (Join-Path $PSScriptRoot 'sign-release-artifacts.ps1') -Directory $output -Version $Version -Configuration $Configuration }
    $portableFolder = Join-Path $work 'Portable'
    New-Item -ItemType Directory -Path $portableFolder | Out-Null
    Get-ChildItem -LiteralPath $app -Force | Copy-Item -Destination $portableFolder -Recurse
    $portableIdentity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $portableFolder 'distribution.json') -Encoding utf8NoBOM
    $zip = Join-Path $output "CloudBay-$Version-win-x64-$($Configuration.ToLowerInvariant())-portable.zip"
    if (Test-Path -LiteralPath $zip) { throw 'Portable release output already exists.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($portableFolder, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $validation = [ordered]@{ configuration = $Configuration; version = $Version; sourceRevision = $SourceRevision; buildSucceeded = $true; testsPassed = [int]$counts.passed; testsFailed = [int]$counts.failed; testsSkipped = [int]$counts.notExecuted; hostedTests = [bool]$HostedTests; signed = [bool]$env:CLOUDBAY_SIGNING_PFX_BASE64 }
    $validation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output "$Configuration-validation.json") -Encoding utf8NoBOM
    Write-Output $app
} finally { Pop-Location }
