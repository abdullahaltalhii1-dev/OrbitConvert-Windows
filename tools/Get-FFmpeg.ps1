# Downloads the pinned, owner-approved FFmpeg development build, verifies its SHA-256, and extracts it.
# Used for local development and CI only. Nothing is installed system-wide and PATH is not changed.
# Prints the folder that contains ffmpeg.exe and ffprobe.exe; point ORBITCONVERT_FFMPEG_DIR at it.
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'OrbitConvert\dev-tools')
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$version = '9.0.2'
$file = "ffmpeg-$version-essentials_build.zip"
$url = "https://github.com/GyanD/codexffmpeg/releases/download/$version/$file"
$sha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$size = 114768076

$target = Join-Path $Destination "ffmpeg-$version"
$bin = Join-Path $target "ffmpeg-$version-essentials_build\bin"
if ((Test-Path (Join-Path $bin 'ffmpeg.exe')) -and (Test-Path (Join-Path $bin 'ffprobe.exe'))) {
    Write-Output $bin
    return
}

$download = Join-Path ([IO.Path]::GetTempPath()) ("orbitconvert-ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $download | Out-Null
try {
    $archive = Join-Path $download $file
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    $actualSize = (Get-Item $archive).Length
    if ($actualSize -ne $size) { throw "Unexpected size for ${file}: $actualSize bytes (expected $size)." }
    $actualHash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $sha256) { throw "SHA-256 mismatch for ${file}: $actualHash (expected $sha256)." }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Expand-Archive -Path $archive -DestinationPath $target -Force
}
finally {
    Remove-Item -LiteralPath $download -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path (Join-Path $bin 'ffprobe.exe'))) { throw "Extracted archive does not contain $bin\ffprobe.exe." }
Write-Output $bin
