$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'Kutu.csproj'
$outputDir = Join-Path $projectRoot 'publish\win-x64'
$localDotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$dotnet = if ($env:KUTU_DOTNET) { $env:KUTU_DOTNET } elseif (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }

& $dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -o $outputDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Published: $outputDir\Kutu.exe"
