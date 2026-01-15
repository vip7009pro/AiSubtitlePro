param(
  [string]$FfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl-shared.zip",
  [string]$LibAssZipUrl = ""
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot 'AiSubtitlePro\Native\win-x64'

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('AiSubtitlePro.NativeDeps.' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

function Download-And-Extract-Dlls {
  param(
    [Parameter(Mandatory=$true)][string]$ZipUrl,
    [Parameter(Mandatory=$true)][string]$Name,
    [Parameter(Mandatory=$true)][string]$ExtractSubdirHint
  )

  $zipPath = Join-Path $tempRoot ($Name + '.zip')
  Write-Host "Downloading $Name..." -ForegroundColor Cyan
  Invoke-WebRequest -Uri $ZipUrl -OutFile $zipPath

  $extractDir = Join-Path $tempRoot $Name
  New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
  Write-Host "Extracting $Name..." -ForegroundColor Cyan
  Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

  $dlls = Get-ChildItem -Path $extractDir -Recurse -Filter '*.dll' | Where-Object {
    $p = $_.FullName.ToLowerInvariant()
    $p.Contains($ExtractSubdirHint.ToLowerInvariant())
  }

  if (-not $dlls -or $dlls.Count -eq 0) {
    # Fallback: take all dlls under extracted folder
    $dlls = Get-ChildItem -Path $extractDir -Recurse -Filter '*.dll'
  }

  foreach ($d in $dlls) {
    Copy-Item -Force -Path $d.FullName -Destination $targetDir
  }

  Write-Host "Copied $($dlls.Count) DLL(s) for $Name to: $targetDir" -ForegroundColor Green
}

# FFmpeg (shared build)
Download-And-Extract-Dlls -ZipUrl $FfmpegZipUrl -Name 'ffmpeg' -ExtractSubdirHint '\bin\'

# libass
if ([string]::IsNullOrWhiteSpace($LibAssZipUrl)) {
  Write-Host "" 
  Write-Host "libass.dll is not downloaded automatically because the release assets vary by version." -ForegroundColor Yellow
  Write-Host "Please do ONE of the following:" -ForegroundColor Yellow
  Write-Host "  1) Download a prebuilt libass x64 package from ShiftMediaProject:" -ForegroundColor Yellow
  Write-Host "     https://github.com/ShiftMediaProject/libass/releases" -ForegroundColor Yellow
  Write-Host "     Then copy libass.dll (and its dependency DLLs, if any) into:" -ForegroundColor Yellow
  Write-Host "       $targetDir" -ForegroundColor Yellow
  Write-Host "  2) Re-run this script with -LibAssZipUrl <direct zip url>" -ForegroundColor Yellow
} else {
  Download-And-Extract-Dlls -ZipUrl $LibAssZipUrl -Name 'libass' -ExtractSubdirHint ''
}

Write-Host "" 
Write-Host "Done. Rebuild and run the app. If you still get missing DLL errors, copy the missing DLL into $targetDir." -ForegroundColor Green
Write-Host "Note: You must use x64 FFmpeg + x64 libass.dll (your project is configured for x64)." -ForegroundColor Green
