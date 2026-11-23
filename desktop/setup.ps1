# Downloads the tools the Kairos Studio desktop app uses: FFmpeg (with NVENC and libass) and yt-dlp.
# Usage:  powershell -ExecutionPolicy Bypass -File desktop\setup.ps1
# They go to desktop\tools\ (never committed). Clean builds use the pinned release tools.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$tools = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory -Force $tools | Out-Null

if (-not (Test-Path (Join-Path $tools 'ffmpeg.exe')) -or -not(Test-Path (Join-Path $tools 'ffprobe.exe'))) {
    Write-Host 'Downloading FFmpeg (GPL build by BtbN, with NVENC and libass)...'
    $scratchRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    $token=[guid]::NewGuid().ToString('N')
    $zip = Join-Path $scratchRoot ('ffmpeg-clipper-'+$token+'.zip')
    $tmp = [IO.Path]::GetFullPath((Join-Path $scratchRoot ('ffmpeg-clipper-'+$token)))
    if(-not $tmp.StartsWith($scratchRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe temporary extraction path.'}
    Invoke-WebRequest 'https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-11-23-12-43/ffmpeg-N-129241-g05c674c913-win64-gpl.zip' -OutFile $zip
    if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne '694e2e119adff51b6c7b7db260173760ea308f343cbff88c04883dba7a8b1a1c'){
        Remove-Item -LiteralPath $zip -Force
        throw 'FFmpeg archive failed SHA-256 verification.'
    }
    Expand-Archive $zip -DestinationPath $tmp
    Get-ChildItem $tmp -Recurse -Include ffmpeg.exe, ffprobe.exe | Copy-Item -Destination $tools
    Remove-Item -LiteralPath $tmp -Recurse -Force
    Remove-Item -LiteralPath $zip -Force
}
if (-not (Test-Path (Join-Path $tools 'yt-dlp.exe'))) {
    Write-Host 'Downloading yt-dlp...'
    $exe=Join-Path $tools 'yt-dlp.exe'
    Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe' -OutFile $exe
    if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne 'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d'){
        Remove-Item -LiteralPath $exe -Force
        throw 'yt-dlp failed SHA-256 verification.'
    }
}
& (Join-Path $tools 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
$enc = & (Join-Path $tools 'ffmpeg.exe') -hide_banner -encoders 2>$null | Select-String 'nvenc'
Write-Host ("Available NVIDIA encoders: " + (($enc | ForEach-Object { ($_ -split '\s+')[2] }) -join ', '))
