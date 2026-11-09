param([Parameter(Mandatory)][string]$AppFolder)
$ErrorActionPreference = 'Stop'
$payloadRoot = [IO.Path]::GetFullPath($AppFolder)
$inventoryPath = Join-Path $payloadRoot 'installer-progress-payload.json'
if (!(Test-Path -LiteralPath (Join-Path $payloadRoot 'CloudInlet.exe') -PathType Leaf)) { throw 'The published application executable is missing.' }
foreach ($entry in @(Get-Item -LiteralPath $payloadRoot) + @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force)) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The progress runtime inventory cannot follow linked files or directories.' }
}
$files = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse | Where-Object {
    $_.FullName -ne $inventoryPath -and $_.Name -ne 'distribution.json' -and $_.Name -notmatch '^unins'
} | Sort-Object FullName | ForEach-Object {
    if ($_.Name -match '^(settings\.json|.*\.(dpapi|sqlite|sqlite-wal|sqlite-shm))$') { throw 'The application runtime contains private client state.' }
    [ordered]@{ Path=[IO.Path]::GetRelativePath($payloadRoot, $_.FullName); Size=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
if ($files.Count -eq 0 -or $files.Count -gt 10000) { throw 'The application runtime inventory size is unsupported.' }
[ordered]@{ SchemaVersion=1; Files=$files } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $inventoryPath -Encoding utf8NoBOM
