param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ffmpegPath = Join-Path $projectRoot "Tools\ffmpeg.exe"
$licenseDirectory = Join-Path $projectRoot "dist\licenses"
$gplLicensePath = Join-Path $licenseDirectory "FFmpeg-GPL-3.0.txt"
$ffmpegSourcePath = Join-Path $projectRoot "dist\installer\ffmpeg-8.1.2-source-38b88335f9.zip"
$installerScript = Join-Path $projectRoot "installer\HoshinoEditor.iss"
$installerDirectory = Join-Path $projectRoot "dist\installer"
$installerPath = Join-Path $installerDirectory "HoshinoEditor-Setup-v0.9.0-beta-1-win-x64.exe"
$checksumPath = Join-Path $installerDirectory "SHA256SUMS.txt"

if (-not (Test-Path -LiteralPath $ffmpegPath)) {
    & (Join-Path $PSScriptRoot "Get-Ffmpeg.ps1")
}

New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $gplLicensePath)) {
    Invoke-WebRequest -Uri "https://www.gnu.org/licenses/gpl-3.0.txt" -OutFile $gplLicensePath
}

dotnet build (Join-Path $projectRoot "HoshinoEditor.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "The Release build failed." }

& (Join-Path $PSScriptRoot "Publish.ps1") -Runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw "The self-contained publish failed." }

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup -e"
}

& $iscc $installerScript
if ($LASTEXITCODE -ne 0) { throw "The installer compiler failed." }
if (-not (Test-Path -LiteralPath $installerPath)) { throw "The expected installer was not created." }

$sourceArchiveValid = $false
if (Test-Path -LiteralPath $ffmpegSourcePath) {
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ffmpegSourcePath)
        $sourceArchiveValid = $archive.Entries.Count -gt 0
        $archive.Dispose()
    }
    catch { $sourceArchiveValid = $false }
}
if (-not $sourceArchiveValid) {
    $sourceDownloadPath = "$ffmpegSourcePath.download"
    Invoke-WebRequest -Uri "https://github.com/FFmpeg/FFmpeg/archive/38b88335f99e76ed89ff3c93f877fdefce736c13.zip" -OutFile $sourceDownloadPath
    Move-Item -LiteralPath $sourceDownloadPath -Destination $ffmpegSourcePath -Force
}

$checksumLines = foreach ($path in @($installerPath, $ffmpegSourcePath)) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($path))"
}
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Installer: $installerPath"
Write-Host "FFmpeg source: $ffmpegSourcePath"
Write-Host "Checksum:  $checksumPath"
