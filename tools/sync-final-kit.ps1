# Sync the single final runtime folder (run from repo after Release build).
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$kit = Join-Path $root 'VideoBatch'
$src = Join-Path $root 'artifacts\VideoBatch-Assembly-20261008-main'
$exe = Join-Path $root 'repo\bin\Release\net48\VideoBatch.exe'
if (-not (Test-Path $exe)) { throw "Build first: dotnet build VideoBatch.csproj -c Release -p:ApplicationIcon=" }
if (Test-Path $src) { robocopy $src $kit /MIR /XD 'editor-preview' /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null }
Copy-Item -Force $exe (Join-Path $kit 'VideoBatch.exe')
$commit = git -C (Join-Path $root 'repo') rev-parse --short HEAD
Set-Content (Join-Path $kit 'SOURCE_COMMIT.txt') $commit -Encoding utf8
Set-Content (Join-Path $kit 'install-root.txt') $kit -Encoding utf8
Write-Host "Updated $kit ($commit)"
