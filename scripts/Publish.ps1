param([ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64")

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputPath = Join-Path $projectRoot "dist\$Runtime"

dotnet publish (Join-Path $projectRoot "HoshinoEditor.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $outputPath

Write-Host "Hoshino Editor published to $outputPath"
