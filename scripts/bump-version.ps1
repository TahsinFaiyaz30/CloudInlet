param(
    [ValidateSet('Patch', 'Minor', 'Major')][string]$Component = 'Patch',
    [string]$Version,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [switch]$Commit,
    [switch]$Push
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$current = & (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $root
$value = [Version]$current.version
if (!$Version) {
    $Version = switch ($Component) {
        'Major' { "$($value.Major + 1).0.0" }
        'Minor' { "$($value.Major).$($value.Minor + 1).0" }
        'Patch' { "$($value.Major).$($value.Minor).$($value.Build + 1)" }
    }
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'The next version must have three stable numeric components.' }
$next = [Version]$Version
if ($next -le $value) { throw "The next version must be greater than $value." }
if ($next.Major -gt 255 -or $next.Minor -gt 255 -or $next.Build -gt 65535) { throw 'The next version exceeds Windows Installer component limits.' }
if ($Push -and !$Commit) { throw '-Push requires -Commit.' }
if ($Commit) {
    $status = @(& git -C $root status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $status.Count) { throw 'Commit a version bump from a clean tracked working tree.' }
    $branch = (& git -C $root branch --show-current).Trim()
    if ($Push -and $branch -ne 'main') { throw 'Automatic release version pushes must target main.' }
}
@{ version = $Version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'version.json') -Encoding utf8NoBOM
if ($Commit) {
    & git -C $root add -- version.json
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage version.json.' }
    & git -C $root commit -m "Prepare CloudBay $Version release" -m "Advance the central version from $value to $Version. CI validates the release before publishing its immutable tag and matching Debug and Release installer assets."
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit the version bump.' }
    if ($Push) { & git -C $root push origin main; if ($LASTEXITCODE -ne 0) { throw 'Could not push the version bump.' } }
}
& (Join-Path $PSScriptRoot 'get-release-version.ps1') -RepositoryRoot $root
