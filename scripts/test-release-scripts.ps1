param([string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts/release-script-tests'))
$ErrorActionPreference = 'Stop'
$suiteRoot = [IO.Path]::GetFullPath($OutputRoot)
$workspace = Join-Path $suiteRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace -Force | Out-Null
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "Release automation test failed: $Message" }
    $script:assertions++
}
function Assert-Rejected([scriptblock]$Operation, [string]$Message, [string]$ExpectedMessage) {
    $rejected = $false
    try { & $Operation | Out-Null } catch { $rejected = !$ExpectedMessage -or $_.Exception.Message -match $ExpectedMessage }
    Assert-True $rejected $Message
}
function Invoke-TestGit([string[]]$Arguments) {
    & git @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Isolated release test Git command failed.' }
}
function Set-FixtureVersion([string]$Version) {
    @{ version = $Version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'version.json') -Encoding utf8NoBOM
}
$fixture = Join-Path $workspace 'source'
$remote = Join-Path $workspace 'remote.git'
$assets = Join-Path $workspace 'assets'
New-Item -ItemType Directory -Path $fixture, $assets | Out-Null
Invoke-TestGit -Arguments @('init', '--quiet', '--initial-branch=main', $fixture)
Invoke-TestGit -Arguments @('-C', $fixture, 'config', 'user.name', 'CloudBay release tests')
Invoke-TestGit -Arguments @('-C', $fixture, 'config', 'user.email', 'release-tests@example.invalid')
Set-FixtureVersion '1.0.0'
Invoke-TestGit -Arguments @('-C', $fixture, 'add', 'version.json')
Invoke-TestGit -Arguments @('-C', $fixture, 'commit', '--quiet', '-m', 'Create isolated version fixture')
$savedOutput = $env:GITHUB_OUTPUT
$savedToken = $env:GH_TOKEN
$savedStoreSecret = $env:CLOUDBAY_STORE_CLIENT_SECRET
if (Test-Path Function:/global:gh) { throw 'Release tests require the normal external gh command, without a pre-existing global mock.' }
try {
    $source = & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $fixture -CheckRelease
    Assert-True ($source.version -ceq '1.0.0' -and $source.windowsVersion -ceq '1.0.0.0' -and $source.shouldRelease) 'An unreleased central version is publishable.'
    $env:GITHUB_OUTPUT = Join-Path $workspace 'github-output.txt'
    & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $fixture -GitHubOutput | Out-Null
    $output = Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw
    Assert-True ($output.Contains('shouldRelease=true') -and $output.Contains('reason=The source version')) 'GitHub outputs preserve text case and normalize booleans.'
    foreach ($bad in @('1.0', '01.0.0', '1.0.0-beta', '256.0.0', '1.256.0', '1.0.65536')) {
        Set-FixtureVersion $bad
        Assert-Rejected { & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $fixture } "Reject invalid central version $bad."
    }
    # The compiler consumes the same central version policy, rather than finding
    # an otherwise valid semver substring inside malformed or duplicated JSON.
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props'), (Join-Path $PSScriptRoot '../Directory.Build.targets') -Destination $fixture
    $validationProject = Join-Path $fixture 'VersionValidation.proj'
    '<Project><PropertyGroup><Configuration>Release</Configuration></PropertyGroup><Import Project="Directory.Build.props" /><Import Project="Directory.Build.targets" /></Project>' | Set-Content -LiteralPath $validationProject -Encoding utf8NoBOM
    foreach ($badDocument in @('garbage 1.0.0', '{"version":"1.0.0","version":"2.0.0"}', '{"version":"1.0.0-beta"}', '{"version":"256.0.0"}', '{"version":"1.0.65536"}', '{"version":"01.0.0"}')) {
        Set-Content -LiteralPath (Join-Path $fixture 'version.json') -Value $badDocument -Encoding utf8NoBOM
        Assert-Rejected { & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $fixture } 'Packaging rejects malformed, duplicated, noncanonical, or unrepresentable central versions.'
        $diagnostics = @(& dotnet msbuild $validationProject -t:ValidateCloudBayVersion -nologo -verbosity:quiet 2>&1)
        Assert-True ($LASTEXITCODE -ne 0) 'The build rejects the same invalid central version document.'
    }
    Set-FixtureVersion '255.255.65535'
    $diagnostics = @(& dotnet msbuild $validationProject -t:ValidateCloudBayVersion -nologo -verbosity:quiet 2>&1)
    Assert-True ($LASTEXITCODE -eq 0) 'The compiler accepts the maximum version shared by MSI, MSIX, and assemblies.'
    Set-FixtureVersion '1.0.0'
    Set-FixtureVersion '1.0.0'
    foreach ($component in @('Patch', 'Minor', 'Major')) {
        Set-FixtureVersion '1.0.0'
        $next = & (Join-Path $PSScriptRoot 'bump-version.ps1') -RepositoryRoot $fixture -Component $component
        $expected = @{ Patch = '1.0.1'; Minor = '1.1.0'; Major = '2.0.0' }[$component]
        Assert-True ($next.version -ceq $expected) "$component advances the right component."
    }
    Set-FixtureVersion '1.0.0'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'bump-version.ps1') -RepositoryRoot $fixture -Version '1.0.0' } 'Version bumps cannot reuse a released version.'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'bump-version.ps1') -RepositoryRoot $fixture -Push } 'Pushing requires a committed bump.'
    Invoke-TestGit -Arguments @('-C', $fixture, 'tag', 'v1.0.0')
    $source = & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $fixture -CheckRelease
    Assert-True (!$source.shouldRelease) 'An existing immutable tag suppresses another release.'
    Set-FixtureVersion '0.9.0'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $fixture -CheckRelease } 'A lower version cannot supersede a published tag.'
    Set-FixtureVersion '1.0.0'
    Invoke-TestGit -Arguments @('-C', $fixture, 'tag', '-d', 'v1.0.0')
    foreach ($flavor in @('Release', 'Debug')) {
        foreach ($ending in @('setup.exe', 'setup.msi', 'portable.zip')) {
            [IO.File]::WriteAllBytes((Join-Path $assets "CloudBay-1.0.0-win-x64-$($flavor.ToLowerInvariant())-$ending"), [Text.Encoding]::UTF8.GetBytes("fixture $flavor $ending"))
        }
    }
    & (Join-Path $PSScriptRoot 'generate-update-manifest.ps1') -Version '1.0.0' -AssetDirectory $assets | Out-Null
    $manifest = Get-Content -LiteralPath (Join-Path $assets 'updates-v1.json') -Raw | ConvertFrom-Json
    Assert-True ($manifest.assets.Count -eq 6 -and $manifest.repository -ceq 'TahsinFaiyaz30/CloudBay' -and $manifest.tag -ceq 'v1.0.0') 'The manifest carries all six immutable variants.'
    foreach ($asset in $manifest.assets) {
        $path = Join-Path $assets $asset.fileName
        Assert-True ($asset.size -eq (Get-Item -LiteralPath $path).Length -and $asset.sha256 -ceq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) 'Every manifest byte count and digest describes its actual asset.'
    }
    Assert-Rejected { & (Join-Path $PSScriptRoot 'generate-update-manifest.ps1') -Version '1.0.0' -AssetDirectory $assets -Repository 'other/repository' } 'Another repository cannot supply the trusted update feed.'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'generate-update-manifest.ps1') -Version '256.0.0' -AssetDirectory $assets } 'Manifest versions obey the installer version limits.'
    $required = Join-Path $assets 'CloudBay-1.0.0-win-x64-debug-setup.exe'
    $original = [IO.File]::ReadAllBytes($required)
    [IO.File]::WriteAllBytes($required, [byte[]]::new(0))
    Assert-Rejected { & (Join-Path $PSScriptRoot 'generate-update-manifest.ps1') -Version '1.0.0' -AssetDirectory $assets } 'Empty installers cannot become public update candidates.'
    [IO.File]::WriteAllBytes($required, $original)
    Remove-Item -LiteralPath $required
    Assert-Rejected { & (Join-Path $PSScriptRoot 'generate-update-manifest.ps1') -Version '1.0.0' -AssetDirectory $assets } 'Missing Debug assets block the entire release.'
    [IO.File]::WriteAllBytes($required, $original)

    # No remote account is used. All git pushes target this newly-created local bare repository.
    Invoke-TestGit -Arguments @('init', '--quiet', '--bare', $remote)
    Invoke-TestGit -Arguments @('-C', $fixture, 'remote', 'add', 'origin', $remote)
    Invoke-TestGit -Arguments @('-C', $fixture, 'push', '--quiet', '--set-upstream', 'origin', 'main')
    $revision = (& git -C $fixture rev-parse HEAD).Trim()
    $evidence = @{ version = '1.0.0'; sourceRevision = $revision; configurations = @(@{configuration='Debug';buildSucceeded=$true;testsPassed=1;testsFailed=0;testsSkipped=0}, @{configuration='Release';buildSucceeded=$true;testsPassed=1;testsFailed=0;testsSkipped=0}) }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $assets 'release-validation.json') -Encoding utf8NoBOM
    $env:GH_TOKEN = 'isolated-test-no-network'
    $global:CloudBayReleaseTestState = @{ exists=$false; draft=$true; assets=[Collections.Generic.List[object]]::new(); uploads=0; publications=0; lookups=0 }
    function global:gh {
        $arguments = @($args)
        $state = $global:CloudBayReleaseTestState
        $global:LASTEXITCODE = 0
        if ($arguments[0] -ceq 'api' -and $arguments[1] -ceq 'repos/TahsinFaiyaz30/CloudBay/releases/tags/v1.0.0') {
            $state.lookups++
            if (!$state.exists) { $global:LASTEXITCODE = 1; return }
            @{ draft=$state.draft;assets=@($state.assets) } | ConvertTo-Json -Depth 6 -Compress
        } elseif ($arguments[0] -ceq 'api' -and $arguments[1] -ceq 'repos/TahsinFaiyaz30/CloudBay/releases?per_page=100') {
            Assert-True ($arguments -contains '--slurp') 'Release lookup must parse all paginated JSON pages safely.'
            '[[]]'
        } elseif ($arguments[0] -ceq 'release' -and $arguments[1] -ceq 'create') {
            Assert-True ($arguments -contains '--draft') 'Release creation must remain a draft until every asset passes verification.'
            $state.exists = $true
        } elseif ($arguments[0] -ceq 'release' -and $arguments[1] -ceq 'upload') {
            $file = Get-Item -LiteralPath $arguments[3]
            $state.assets.Add(@{ name=$file.Name;size=$file.Length;digest=('sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant());state='uploaded' })
            $state.uploads++
        } elseif ($arguments[0] -ceq 'release' -and $arguments[1] -ceq 'edit') {
            Assert-True ($state.assets.Count -eq 9 -and $arguments -contains '--draft=false') 'Publication occurs only after all required files have been uploaded.'
            $state.draft = $false; $state.publications++
        } else { throw 'Unexpected gh invocation in isolated tests. Network access is forbidden.' }
    }
    $url = & (Join-Path $PSScriptRoot 'publish-github-release.ps1') -RepositoryRoot $fixture -AssetDirectory $assets -ExpectedRevision $revision
    Assert-True ($url -ceq 'https://github.com/TahsinFaiyaz30/CloudBay/releases/tag/v1.0.0' -and $global:CloudBayReleaseTestState.uploads -eq 9 -and $global:CloudBayReleaseTestState.publications -eq 1) 'A complete release creates one draft, uploads the verified inventory, and publishes once.'
    & (Join-Path $PSScriptRoot 'publish-github-release.ps1') -RepositoryRoot $fixture -AssetDirectory $assets -ExpectedRevision $revision | Out-Null
    Assert-True ($global:CloudBayReleaseTestState.uploads -eq 9 -and $global:CloudBayReleaseTestState.publications -eq 1) 'Re-running a published identical release never replaces or re-uploads bytes.'
    $global:CloudBayReleaseTestState.assets[0].digest = 'sha256:' + ('0' * 64)
    Assert-Rejected { & (Join-Path $PSScriptRoot 'publish-github-release.ps1') -RepositoryRoot $fixture -AssetDirectory $assets -ExpectedRevision $revision } 'Different remote bytes are rejected rather than clobbered.'
    Assert-True ($global:CloudBayReleaseTestState.uploads -eq 9) 'Immutable mismatch performs no asset replacement.'
    Invoke-TestGit -Arguments @('-C', $fixture, 'commit', '--quiet', '--allow-empty', '-m', 'Simulate newer main while release is building')
    Invoke-TestGit -Arguments @('-C', $fixture, 'push', '--quiet', 'origin', 'main')
    Invoke-TestGit -Arguments @('-C', $fixture, 'checkout', '--quiet', '--detach', $revision)
    Assert-Rejected { & (Join-Path $PSScriptRoot 'publish-github-release.ps1') -RepositoryRoot $fixture -AssetDirectory $assets -ExpectedRevision $revision } 'A superseded CI source cannot publish over newer main.'
    Remove-Item Function:/global:gh
    $env:CLOUDBAY_STORE_CLIENT_SECRET = $null
    Assert-Rejected { & (Join-Path $PSScriptRoot 'submit-store-package.ps1') -PackagePath (Join-Path $workspace 'absent.msix') } 'Unconfigured Store publishing stops before authentication or mutation.'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'build-store-package.ps1') -AppFolder $workspace -Version '1.0.0' -OutputDirectory (Join-Path $workspace 'store') -PackageIdentityName '' -Publisher '' -PublisherDisplayName '' } 'Missing Partner Center identity cannot silently produce a production Store package.'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'build-store-package.ps1') -AppFolder $workspace -Version '1.0.0' -OutputDirectory (Join-Path $workspace 'store') -LocalValidationIdentity -PackageIdentityName 'Production.Identity' } 'Local sample and production Store identities cannot be mixed.'
    $sampleFolder = Join-Path $workspace 'sample-manifest'
    New-Item -ItemType Directory -Path $sampleFolder | Out-Null
    '<Package><Identity Name="CloudBay.LocalValidation" Publisher="CN=CloudBay Local Validation" ProcessorArchitecture="x64" /></Package>' | Set-Content -LiteralPath (Join-Path $sampleFolder 'AppxManifest.xml') -Encoding utf8NoBOM
    $renamedSample = Join-Path $workspace 'production.msix'
    [IO.Compression.ZipFile]::CreateFromDirectory($sampleFolder, $renamedSample)
    $env:CLOUDBAY_STORE_CLIENT_SECRET = 'fixture-not-a-credential'
    Assert-Rejected {
        & (Join-Path $PSScriptRoot 'submit-store-package.ps1') -PackagePath $renamedSample -ApplicationId 'ABC123456789' -TenantId '11111111-1111-1111-1111-111111111111' -ClientId '22222222-2222-2222-2222-222222222222' -PackageIdentityName 'Production.Identity' -Publisher 'CN=Production'
    } 'Renaming a local Store sample cannot bypass the actual embedded identity check.' '^Provide the production Store package matching'

    # A message handler replaces HTTPS transport so the complete Store request graph can be tested without credentials or network access.
    if (!('CloudBay.ReleaseTests.StoreHandler' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
namespace CloudBay.ReleaseTests {
    public sealed class StoreHandler : HttpMessageHandler {
        public string Scenario = "Success";
        public int RootGets, Creates, Updates, Uploads, Commits, StatusGets;
        public string UpdatedJson = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            var uri = request.RequestUri;
            string json = "{}";
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host == "login.microsoftonline.com") json = "{\"access_token\":\"fixture-bearer\"}";
            else if (uri.Host == "fixture.blob.core.windows.net") Uploads++;
            else if (uri.Host != "manage.devcenter.microsoft.com") throw new InvalidOperationException("Test attempted an unexpected network destination.");
            else if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/ABC123456789")) {
                RootGets++;
                if (Scenario == "LongCooldown" || Scenario == "LongDateCooldown") {
                    response.StatusCode = (HttpStatusCode)429;
                    response.Headers.RetryAfter = Scenario == "LongDateCooldown"
                        ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(120))
                        : new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                } else json = Scenario == "Pending" ? "{\"pendingApplicationSubmission\":{\"id\":\"1\"}}" : "{\"pendingApplicationSubmission\":null}";
            } else if (request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/submissions")) {
                Creates++;
                if (Scenario == "Mutation503") response.StatusCode = HttpStatusCode.ServiceUnavailable;
                else json = "{\"id\":\"123\",\"fileUploadUrl\":\"https://fixture.blob.core.windows.net/package?sig=fixture-sas\",\"applicationPackages\":[{\"fileName\":\"old.msix\",\"fileStatus\":\"Uploaded\"}],\"targetPublishMode\":\"Immediate\",\"listings\":{\"en-us\":{\"description\":\"Existing listing\"}}}";
            } else if (request.Method == HttpMethod.Put && uri.AbsolutePath.EndsWith("/submissions/123")) {
                Updates++;
                UpdatedJson = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            } else if (request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/commit")) Commits++;
            else if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/status")) { StatusGets++; json = "{\"status\":\"Certification\"}"; }
            else throw new InvalidOperationException("Unexpected Store test request.");
            response.Content = new StringContent(json);
            return Task.FromResult(response);
        }
    }
}
'@
    }
    '<Package><Identity Name="Production.Identity" Publisher="CN=Production" ProcessorArchitecture="x64" /></Package>' | Set-Content -LiteralPath (Join-Path $sampleFolder 'AppxManifest.xml') -Encoding utf8NoBOM
    $productionFixture = Join-Path $workspace 'matching-production.msix'
    [IO.Compression.ZipFile]::CreateFromDirectory($sampleFolder, $productionFixture)
    $submissionArguments = @{ PackagePath=$productionFixture;ApplicationId='ABC123456789';TenantId='11111111-1111-1111-1111-111111111111';ClientId='22222222-2222-2222-2222-222222222222';PackageIdentityName='Production.Identity';Publisher='CN=Production' }
    $handler = [CloudBay.ReleaseTests.StoreHandler]::new()
    $result = & (Join-Path $PSScriptRoot 'submit-store-package.ps1') @submissionArguments -HttpHandler $handler
    Assert-True ($handler.RootGets -eq 1 -and $handler.Creates -eq 1 -and $handler.Updates -eq 1 -and $handler.Uploads -eq 1 -and $handler.Commits -eq 0 -and $result -match 'Commit was not requested') 'Preparing a Store draft follows the API request graph without committing implicitly.'
    $updated = $handler.UpdatedJson | ConvertFrom-Json
    Assert-True ($updated.targetPublishMode -ceq 'Manual' -and $updated.applicationPackages[0].fileStatus -ceq 'PendingDelete' -and $updated.applicationPackages[1].fileStatus -ceq 'PendingUpload' -and $updated.listings.'en-us'.description -ceq 'Existing listing') 'Store updates replace packages while preserving listing data and explicit publication mode.'
    $handler = [CloudBay.ReleaseTests.StoreHandler]::new()
    & (Join-Path $PSScriptRoot 'submit-store-package.ps1') @submissionArguments -HttpHandler $handler -CommitSubmission | Out-Null
    Assert-True ($handler.Commits -eq 1 -and $handler.StatusGets -eq 1) 'Explicit submission commits exactly once and confirms processing status.'
    foreach ($scenario in @('Pending','Mutation503','LongCooldown','LongDateCooldown')) {
        $handler = [CloudBay.ReleaseTests.StoreHandler]::new(); $handler.Scenario = $scenario
        Assert-Rejected { & (Join-Path $PSScriptRoot 'submit-store-package.ps1') @submissionArguments -HttpHandler $handler -CommitSubmission } "Store scenario $scenario stops with a controlled error." '^(Partner Center already|Store API returned|Store requested a long)'
        Assert-True ($handler.Creates -le 1 -and $handler.Uploads -eq 0 -and $handler.Commits -eq 0) "Store scenario $scenario performs no duplicate mutation or premature cooldown retry."
    }
    [ordered]@{ assertions=$script:assertions; passed=$true; fixture=$workspace; networkUsed=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'results.json') -Encoding utf8NoBOM
    Write-Output "Release automation passed $script:assertions assertions. Fixtures: $workspace"
} finally {
    if (Test-Path Function:/global:gh) { Remove-Item Function:/global:gh }
    Remove-Variable -Name CloudBayReleaseTestState -Scope Global -ErrorAction SilentlyContinue
    $env:GITHUB_OUTPUT = $savedOutput
    $env:GH_TOKEN = $savedToken
    $env:CLOUDBAY_STORE_CLIENT_SECRET = $savedStoreSecret
}
