#Requires -Version 5.1
param(
    [string]$OutputRoot = '',
    [string]$TemplatePath = '',
    [switch]$SkipUploadStaging,
    [switch]$SkipVideos
)

$ErrorActionPreference = 'Stop'
if (-not $OutputRoot) {
    $OutputRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts' ('VideoBatch-Portable-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if (-not $TemplatePath) {
    $TemplatePath = Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop\assembly-template.xml'
}

$srcApp = 'D:\VideoBatch\VideoBatch'
$srcData = Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop'
$srcVideos = Join-Path $env:USERPROFILE 'Videos\VideoBatch'
$srcUpload = 'C:\VideoBatch\Upload'

if (-not (Test-Path -LiteralPath $TemplatePath)) { throw "Template not found: $TemplatePath" }
[xml]$tpl = Get-Content -LiteralPath $TemplatePath -Encoding UTF8
$srcMaterials = [string]$tpl.AssemblyTemplate.Materials
if ([string]::IsNullOrWhiteSpace($srcMaterials) -or -not (Test-Path -LiteralPath $srcMaterials)) {
    throw "Materials folder from template not found: $srcMaterials"
}

foreach ($p in @($srcApp, $srcData)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Not found: $p" }
}

$materialsLeaf = Split-Path $srcMaterials -Leaf
$dirs = @{
    App       = Join-Path $OutputRoot 'app'
    UserData  = Join-Path $OutputRoot 'user-data'
    Materials = Join-Path $OutputRoot (Join-Path 'materials' $materialsLeaf)
    Videos    = Join-Path $OutputRoot 'videos\VideoBatch'
    Upload    = Join-Path $OutputRoot 'upload-staging'
}
foreach ($d in $dirs.Values) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

$excludeData = @(
    '*.tmp', '*~RF*.TMP', 'task-queue.json.*.tmp',
    'assembly-template.xml.bak*', 'settings.xml.bak*', 'settings.xml.*backup*',
    'settings.xml.broken-paths', 'youtube-live.xml.bak*'
)

function Invoke-RoboCopy {
    param([string]$From, [string]$To, [string[]]$ExtraExclude)
    $xf = @($excludeData + $ExtraExclude)
    $args = @($From, $To, '/E', '/COPY:DAT', '/R:2', '/W:2', '/MT:8', '/NFL', '/NDL', '/NP')
    foreach ($x in $xf) { if ($x) { $args += '/XF'; $args += $x } }
    & robocopy @args | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed $From -> $To (code $LASTEXITCODE)" }
}

Write-Host "Staging app -> $($dirs.App)"
Invoke-RoboCopy -From $srcApp -To $dirs.App

Write-Host "Staging user data -> $($dirs.UserData)"
Invoke-RoboCopy -From $srcData -To $dirs.UserData

Write-Host "Staging materials -> $($dirs.Materials)"
Invoke-RoboCopy -From $srcMaterials -To $dirs.Materials

if (-not $SkipVideos -and (Test-Path -LiteralPath $srcVideos)) {
    Write-Host "Staging Videos\VideoBatch -> $($dirs.Videos)"
    Invoke-RoboCopy -From $srcVideos -To $dirs.Videos
}

if (-not $SkipUploadStaging -and (Test-Path -LiteralPath $srcUpload)) {
    Write-Host "Staging C:\VideoBatch\Upload -> $($dirs.Upload)"
    Invoke-RoboCopy -From $srcUpload -To $dirs.Upload
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Restore-VideoBatchPortable.ps1') -Destination (Join-Path $OutputRoot 'Restore-VideoBatchPortable.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-Portable-RU.txt') -Destination (Join-Path $OutputRoot 'README-Portable-RU.txt') -Force
$launchBat = Join-Path $PSScriptRoot 'Launch-VideoBatch-Portable.bat'
Copy-Item -LiteralPath $launchBat -Destination (Join-Path $OutputRoot 'Launch-VideoBatch-Portable.bat') -Force
Copy-Item -LiteralPath $launchBat -Destination (Join-Path $OutputRoot 'START VideoBatch.bat') -Force
@'
VideoBatch — перенос на другой компьютер
======================================

1. Скопируйте архив или папку на другой ПК.
2. Распакуйте в любое место (ПКМ → Извлечь всё, или 7-Zip).
3. Дважды щёлкните:  Launch-VideoBatch-Portable.bat
   (то же самое: START VideoBatch.bat)

   Первый запуск 1–3 минуты — это нормально.
   PowerShell вручную не нужен.

4. На новом ПК нужен Dolphin Anty с теми же профилями.
'@ | Set-Content -LiteralPath (Join-Path $OutputRoot 'PROCHTI-MENYA.txt') -Encoding UTF8

$manifest = [ordered]@{
    created = (Get-Date).ToString('o')
    sourceComputer = $env:COMPUTERNAME
    sourceUser = $env:USERNAME
    pathMap = [ordered]@{
        oldApp = $srcApp
        oldMaterials = $srcMaterials
        oldUserProfile = $env:USERPROFILE
        oldUploadStaging = $srcUpload
        materialsLeaf = $materialsLeaf
    }
    includesUploadStaging = (-not $SkipUploadStaging)
    includesVideos = (-not $SkipVideos)
}
($manifest | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath (Join-Path $OutputRoot 'portable-manifest.json') -Encoding UTF8

Write-Host "OK: $OutputRoot"
