#Requires -Version 7.0
param(
    [string]$OutputDirectory,
    [string]$KeyFile,
    [string]$CatalogUri,
    [switch]$Preview,
    [switch]$IncludeExample,
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ("artifacts/connector-dev/" + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory to preserve existing signed packages.' }
$catalog = Join-Path $output 'catalog'
New-Item -ItemType Directory -Path $catalog -Force | Out-Null
$toolProject = Join-Path $repo 'tools/CloudInlet.ConnectorTool/CloudInlet.ConnectorTool.csproj'
$tool = Join-Path $repo "tools/CloudInlet.ConnectorTool/bin/$Configuration/net8.0-windows/CloudInlet.ConnectorTool.dll"
function Invoke-Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed." } }
Invoke-Dotnet build $toolProject -c $Configuration '-v:q'
if (!$KeyFile) {
    if ($CatalogUri) { throw 'Supply the publisher key file when preparing a hosted catalog.' }
    $KeyFile = Join-Path $output 'development-key.dpapi'
    Invoke-Dotnet $tool keygen $KeyFile
}
[string[]]$packs = foreach ($name in 'BackblazeB2','OneDrive') {
    $project = Join-Path $repo "connectors/$name/CloudInlet.Connectors.$name.csproj"
    Invoke-Dotnet build $project -c $Configuration '-v:q' | Out-Host
    $folder = Split-Path -Parent $project
    $manifest = Join-Path $folder 'connector.json'
    $info = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
    $pack = Join-Path $catalog "$($info.id)-$($info.version).cipack"
    Invoke-Dotnet $tool pack $manifest (Join-Path $folder "bin/$Configuration/net8.0") $KeyFile $pack | Out-Host
    Invoke-Dotnet $tool validate $pack | Out-Host
    $pack
}
$signed = Join-Path $catalog 'catalog.json'
Invoke-Dotnet $tool catalog $KeyFile $signed @packs
if (!$CatalogUri) { $CatalogUri = [Uri]::new($signed).AbsoluteUri }
$bootstrap = Join-Path $output 'connector-source.json'
Invoke-Dotnet $tool bootstrap $signed $CatalogUri $bootstrap
if ($IncludeExample -or $Preview) {
    $example = Join-Path $repo 'connectors/Example'
    Invoke-Dotnet build (Join-Path $example 'ExampleConnector.csproj') -c $Configuration '-v:q'
    $exampleKey = Join-Path $output 'example-key.dpapi'
    Invoke-Dotnet $tool keygen $exampleKey
    Invoke-Dotnet $tool pack (Join-Path $example 'connector.json') (Join-Path $example "bin/$Configuration/net8.0") $exampleKey (Join-Path $output 'example.cipack')
}
if ($Preview) {
    $app = Join-Path $output 'App'
    Invoke-Dotnet publish (Join-Path $repo 'CloudInlet/CloudInlet.csproj') -c $Configuration -r win-x64 --self-contained true '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' "-p:CloudInletConnectorBootstrap=$bootstrap" "-p:CloudInletLiveDirectory=$output/marker" -o $app '-v:minimal'
    Write-Output "Preview: & '$app/CloudInlet.exe' --connector-lab --page=connectors"
}
Write-Output "Signed catalog: $signed"
