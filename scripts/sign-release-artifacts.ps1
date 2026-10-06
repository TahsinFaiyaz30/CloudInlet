param(
    [Parameter(Mandatory)][string]$Directory,
    [string]$Version,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$ApplicationBinaries,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$signingPfx = if ($env:CLOUDINLET_SIGNING_PFX_BASE64) { $env:CLOUDINLET_SIGNING_PFX_BASE64 } else { $env:CLOUDBAY_SIGNING_PFX_BASE64 }
$signingPassword = if ($env:CLOUDINLET_SIGNING_PFX_PASSWORD) { $env:CLOUDINLET_SIGNING_PFX_PASSWORD } else { $env:CLOUDBAY_SIGNING_PFX_PASSWORD }
if (!$signingPfx -or !$signingPassword) { throw 'Configure both signing PFX and password environment secrets before signing.' }
$folder = [IO.Path]::GetFullPath($Directory)
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$signtool = Get-ChildItem -LiteralPath $sdk -Directory | Where-Object { $_.Name -match '^10\.0\.\d+\.0$' } | Sort-Object { [Version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$signtool) { throw 'Windows SDK SignTool is missing.' }
$temporary = Join-Path $folder ('.signing-' + [Guid]::NewGuid().ToString('N') + '.pfx')
$imported = @()
$existing = @(Get-ChildItem Cert:/CurrentUser/My | Select-Object -ExpandProperty Thumbprint)
try {
    [IO.File]::WriteAllBytes($temporary, [Convert]::FromBase64String($signingPfx))
    $password = ConvertTo-SecureString $signingPassword -AsPlainText -Force
    $imported = @(Import-PfxCertificate -FilePath $temporary -Password $password -CertStoreLocation Cert:/CurrentUser/My)
    $cert = @($imported | Where-Object { $_.HasPrivateKey -and $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3' })
    if ($cert.Count -ne 1) { throw 'Signing PFX must contain one code-signing certificate with a private key.' }
    $files = if ($ApplicationBinaries) { @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '^Cloud(Inlet|Bay)(\..+)?\.(exe|dll)$' }) } else {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Installer signing requires the release version.' }
        @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -in @("CloudInlet-$Version-win-x64-$($Configuration.ToLowerInvariant())-setup.exe", "CloudInlet-$Version-win-x64-$($Configuration.ToLowerInvariant())-setup.msi") })
    }
    if (!$files.Count -or (!$ApplicationBinaries -and $files.Count -ne 2)) { throw 'The expected signing payload is incomplete.' }
    foreach ($file in $files) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Signing payload contains a linked file.' }
        & $signtool sign /sha1 $cert[0].Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $file.FullName
        if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
        & $signtool verify /pa $file.FullName
        if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification failed.' }
    }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    foreach ($certificate in $imported) { if ($certificate.Thumbprint -notin $existing) { Remove-Item -LiteralPath ("Cert:/CurrentUser/My/" + $certificate.Thumbprint) } }
}
