param([string]$Destination = "$PSScriptRoot\..\Tools")

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not $destinationPath.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Destination must stay inside the Hoshino Editor project."
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("hoshino-ffmpeg-" + [guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $temporaryRoot "ffmpeg.zip"
$extractPath = Join-Path $temporaryRoot "extract"
New-Item -ItemType Directory -Path $temporaryRoot, $extractPath -Force | Out-Null

try {
    Write-Host "Downloading the FFmpeg essentials build..."
    Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $archivePath
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
    $ffmpeg = Get-ChildItem -LiteralPath $extractPath -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    if (-not $ffmpeg) { throw "The downloaded archive did not contain ffmpeg.exe." }
    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    Copy-Item -LiteralPath $ffmpeg.FullName -Destination (Join-Path $destinationPath "ffmpeg.exe") -Force
    Write-Host "FFmpeg is ready in $destinationPath"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
