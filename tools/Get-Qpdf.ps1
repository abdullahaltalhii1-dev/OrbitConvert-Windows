# Development/CI only: official portable Windows x64 build, no install or PATH change.
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'OrbitConvert\dev-tools'))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '12.4.2'
$hash = '773d2fa0c7d161e2271430734338e560a07b25d0edf11ceee8e53de435012766'
$target = Join-Path $Destination "qpdf-$version"
$bin = Join-Path $target "qpdf-$version-mingw64\bin"
if (Test-Path (Join-Path $bin 'qpdf.exe')) { Write-Output $bin; return }
$download = Join-Path ([IO.Path]::GetTempPath()) ('orbitconvert-qpdf-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $download | Out-Null
try {
    $archive = Join-Path $download 'qpdf.zip'
    Invoke-WebRequest "https://github.com/qpdf/qpdf/releases/download/v$version/qpdf-$version-mingw64.zip" -OutFile $archive
    if ((Get-Item $archive).Length -ne 24013395 -or (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'qpdf archive integrity check failed.' }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $target
}
finally {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($download)) -eq [IO.Path]::GetTempPath().TrimEnd('\')) {
        Remove-Item -LiteralPath $download -Recurse -Force
    }
}
if (-not (Test-Path (Join-Path $bin 'qpdf.exe'))) { throw 'qpdf executable absent after extraction.' }
Write-Output $bin
