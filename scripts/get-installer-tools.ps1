param(
    [string]$ToolsDirectory = (Join-Path $PSScriptRoot '../artifacts/tools/installers'),
    [string]$InnoCompiler,
    [string]$WixToolPath
)
$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath($ToolsDirectory)
for ($ancestor = [IO.DirectoryInfo]$toolRoot; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installer tools directory contains a link.' }
}
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null

# Pin both the upstream executable and its SHA-256. Never use a floating
# winget/chocolatey latest version on the release runner.
$innoVersion = '6.7.3'
$innoSha256 = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
if (!$InnoCompiler) {
    $innoDirectory = Join-Path $toolRoot "inno-$innoVersion"
    $InnoCompiler = Join-Path $innoDirectory 'ISCC.exe'
    if (!(Test-Path -LiteralPath $InnoCompiler)) {
        $download = Join-Path $toolRoot "innosetup-$innoVersion.exe"
        if (!(Test-Path -LiteralPath $download)) {
            Invoke-WebRequest -Uri "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-$innoVersion.exe" -OutFile $download
        }
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $innoSha256) { throw 'The pinned Inno Setup download hash did not match.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $download
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B\.V\.') { throw 'The Inno Setup download signature is invalid.' }
        $install = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', ('/DIR="' + $innoDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
        if ($install.ExitCode -ne 0) { throw "The pinned Inno compiler setup failed ($($install.ExitCode))." }
    }
}
$InnoCompiler = [IO.Path]::GetFullPath($InnoCompiler)
if (!(Test-Path -LiteralPath $InnoCompiler)) {
    throw "Release EXE installers require Inno Setup $innoVersion."
}
# Inno intentionally leaves PE FileVersion at 0.0.0.0; its documented ISPP
# `Ver` constant is checked by the compilation probe and the product script.
$probe = Join-Path $toolRoot ('version-probe-' + [Guid]::NewGuid().ToString('N') + '.iss')
@'
#if Ver != 0x06070300
  #error CloudBay requires Inno Setup 6.7.3.
#endif
[Setup]
AppName=CloudBay compiler version check
AppVersion=1.0.0
CreateAppDir=no
Output=no
Uninstallable=no
CreateUninstallRegKey=no
'@ | Set-Content -LiteralPath $probe -Encoding utf8NoBOM
try {
    & $InnoCompiler '/Q' $probe | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Release EXE installers require Inno Setup $innoVersion." }
} finally { Remove-Item -LiteralPath $probe -Force }

$wixVersion = '5.0.2'
if (!$WixToolPath) {
    $wixDirectory = Join-Path $toolRoot 'wix'
    $WixToolPath = Join-Path $wixDirectory 'wix.exe'
    if (!(Test-Path -LiteralPath $WixToolPath)) {
        dotnet tool install wix --version $wixVersion --tool-path $wixDirectory | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'The pinned WiX tool could not be installed.' }
    }
}
$WixToolPath = [IO.Path]::GetFullPath($WixToolPath)
$actualWix = (& $WixToolPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $actualWix -notmatch '^5\.0\.2([+.].*)?$') { throw "Release MSI installers require WiX $wixVersion; found $actualWix." }
foreach ($extension in @('WixToolset.UI.wixext', 'WixToolset.Util.wixext')) {
    & $WixToolPath extension add -g "$extension/$wixVersion" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not acquire pinned $extension." }
}
[pscustomobject]@{ InnoCompiler = $InnoCompiler; WixToolPath = $WixToolPath; InnoVersion = $innoVersion; WixVersion = $wixVersion }
