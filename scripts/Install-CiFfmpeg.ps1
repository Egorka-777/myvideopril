# CI only. Gyan Windows builds are linked by https://ffmpeg.org/download.html.
# Version and SHA256 are pinned together; do not use a moving "latest" archive.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (!$env:ChocolateyInstall -or !$env:RUNNER_TEMP -or !$env:GITHUB_PATH) { throw 'This installer is for the isolated Windows GitHub Actions runner.' }
$version = '9.0.2'
$uri = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip'
# https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip.sha256
$expected = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$archive = Join-Path $env:RUNNER_TEMP ('ffmpeg-' + $version + '.zip')
$installRoot = Join-Path $env:ChocolateyInstall 'lib/ffmpeg/tools/ci-pinned'
Invoke-WebRequest -Uri $uri -OutFile $archive -MaximumRetryCount 3 -RetryIntervalSec 5 -TimeoutSec 120
if ((Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'FFmpeg archive SHA256 mismatch; installation stopped.' }
New-Item -ItemType Directory -Force $installRoot | Out-Null
Expand-Archive -Path $archive -DestinationPath $installRoot -Force
$binary = Get-ChildItem $installRoot -Filter ffmpeg.exe -Recurse -File | Where-Object Length -gt 1000000 | Select-Object -First 1
if (!$binary) { throw 'FFmpeg binary missing from verified archive.' }
$probe = Join-Path $binary.DirectoryName 'ffprobe.exe'
if (!(Test-Path $probe)) { throw 'FFprobe binary missing from verified archive.' }
$encoderVersion = & $binary.FullName -version
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg failed to start.' }
$probeVersion = & $probe -version
if ($LASTEXITCODE -ne 0) { throw 'FFprobe failed to start.' }
Write-Host $encoderVersion[0]
Write-Host $probeVersion[0]
$binary.DirectoryName | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
$env:Path = $binary.DirectoryName + ';' + $env:Path
