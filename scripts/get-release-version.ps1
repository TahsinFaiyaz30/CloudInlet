param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [switch]$CheckRelease,
    [switch]$GitHubOutput
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$document = Get-Content -LiteralPath (Join-Path $root 'version.json') -Raw
$versionPattern = '\A\s*\{\s*"version"\s*:\s*"((?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4}))"\s*\}\s*\z'
if ($document -cnotmatch $versionPattern) { throw 'version.json must contain exactly one version property with a canonical stable X.Y.Z value.' }
$version = $Matches[1]
$parsed = [Version]$version
if ($parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) { throw 'Version exceeds Windows Installer limits: major/minor <= 255 and patch <= 65535.' }
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'Cannot determine the release commit.' }
$shouldRelease = $true
$reason = 'The source version has not been released.'
if ($CheckRelease) {
    $tags = @(& git -C $root tag --list 'v*')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect release tags.' }
    $versions = @($tags | Where-Object { $_ -cmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' } | ForEach-Object { [Version]$_.Substring(1) })
    $latest = $versions | Sort-Object -Descending | Select-Object -First 1
    if ($latest -and $parsed -lt $latest) { throw "Source version $version is older than immutable release v$latest." }
    if ($latest -and $parsed -eq $latest) {
        $shouldRelease = $false
        $reason = "Tag v$version already exists. Released versions are immutable; use the version bump workflow for a new release."
    }
}
$result = [ordered]@{ version = $version; windowsVersion = "$version.0"; tag = "v$version"; sourceRevision = $revision; shouldRelease = $shouldRelease; reason = $reason }
if ($GitHubOutput) {
    if (!$env:GITHUB_OUTPUT) { throw 'GITHUB_OUTPUT is not configured.' }
    foreach ($entry in $result.GetEnumerator()) {
        $value = if ($entry.Value -is [bool]) { $entry.Value.ToString().ToLowerInvariant() } else { [string]$entry.Value }
        "$($entry.Key)=$value" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    }
}
[PSCustomObject]$result
