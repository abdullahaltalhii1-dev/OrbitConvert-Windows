# Development/CI acquisition only. No global install, runtime download or PATH modification.
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'OrbitConvert\dev-tools'))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '3.12.1'
$hash = '6ef431cb20b2c24a0371fc19200dc49d452be54b1b5c9239f3462841ef53e587'
$target = Join-Path $Destination "pandoc-$version"
$bin = Join-Path $target "pandoc-$version"
if (Test-Path (Join-Path $bin 'pandoc.exe')) { Write-Output $bin; return }
$download = Join-Path ([IO.Path]::GetTempPath()) ('orbitconvert-pandoc-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $download | Out-Null
try {
    $archive = Join-Path $download 'pandoc.zip'
    Invoke-WebRequest "https://github.com/jgm/pandoc/releases/download/$version/pandoc-$version-windows-x86_64.zip" -OutFile $archive
    if ((Get-Item $archive).Length -ne 42154914 -or (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Pandoc archive integrity check failed.' }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $target
}
finally {
    # Only the exact operation-owned temporary directory, validated against its parent.
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($download)) -eq [IO.Path]::GetTempPath().TrimEnd('\')) {
        Remove-Item -LiteralPath $download -Recurse -Force
    }
}
if (-not (Test-Path (Join-Path $bin 'pandoc.exe'))) { throw 'Pandoc executable absent after extraction.' }
Write-Output $bin
