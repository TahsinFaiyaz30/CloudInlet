#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = 'TahsinFaiyaz30/CloudInlet-Connectors'
$environment = 'connector-production'
$pin = Join-Path $PSScriptRoot '../connectors/official-source.json'
$uri = 'https://raw.githubusercontent.com/TahsinFaiyaz30/CloudInlet-Connectors/main/catalog.json'
$vault = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CloudInlet-Publishing/connector-production'
$protectedKey = Join-Path $vault 'publisher-v1.dpapi'
$immutable = gh api "repos/$repo/immutable-releases" --jq .enabled
if ($LASTEXITCODE -or $immutable -ne 'true') { throw 'Enable immutable releases in the distribution repository first.' }
$entropy = [Text.Encoding]::UTF8.GetBytes('CloudInlet-Connectors.ProductionPublisher.v1')
$branches = @(gh api "repos/$repo/environments/$environment/deployment-branch-policies" --jq '.branch_policies[] | (.type + ":" + .name)')
if ($LASTEXITCODE -or $branches.Count -ne 1 -or $branches[0] -cne 'branch:main') { throw 'Restrict the connector-production environment to the main branch first.' }
$secrets = @(gh secret list --repo $repo --env $environment --json name --jq '.[].name')
if ($LASTEXITCODE) { throw 'Cannot inspect the signing environment.' }
$existingPin = if (Test-Path -LiteralPath $pin) { Get-Content -LiteralPath $pin -Raw | ConvertFrom-Json }
if (!(Test-Path -LiteralPath $protectedKey) -and ($existingPin -or 'CONNECTOR_PUBLISHER_PKCS8' -in $secrets)) { throw 'Restore the existing publisher credential. Refusing to replace an existing publisher identity.' }
New-Item -ItemType Directory -Path $vault -Force | Out-Null
$signer = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    if (Test-Path -LiteralPath $protectedKey) {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($protectedKey), $entropy, 'CurrentUser')
        $read = 0
        $signer.ImportPkcs8PrivateKey($bytes, [ref]$read)
        if ($read -ne $bytes.Length) { throw 'Invalid publisher credential.' }
    } else {
        $bytes = $signer.ExportPkcs8PrivateKey()
        $stream = [IO.File]::Open($protectedKey, 'CreateNew', 'Write', 'None')
        try { $stream.Write([Security.Cryptography.ProtectedData]::Protect($bytes, $entropy, 'CurrentUser')); $stream.Flush($true) }
        finally { $stream.Dispose() }
    }
    $public = [Convert]::ToBase64String($signer.ExportSubjectPublicKeyInfo())
    if ($existingPin -and ($existingPin.publicKey -cne $public -or $existingPin.location -cne $uri)) { throw 'Credential does not match the existing official pin.' }
    # gh encrypts standard input for the target environment. Only the public pin leaves this script as output.
    [Convert]::ToBase64String($bytes) | gh secret set CONNECTOR_PUBLISHER_PKCS8 --repo $repo --env $environment
    if ($LASTEXITCODE) { throw 'GitHub could not seal the publisher credential in the protected environment.' }
    if (!$existingPin) { [ordered]@{ location=$uri; publicKey=$public } | ConvertTo-Json | Set-Content -LiteralPath $pin -Encoding utf8NoBOM }
    'Production publisher SHA-256 ' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signer.ExportSubjectPublicKeyInfo()))
} finally { if ($bytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }; $signer.Dispose() }
