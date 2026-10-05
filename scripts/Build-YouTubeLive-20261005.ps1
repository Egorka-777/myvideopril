param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$OutputDirectory = ""
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows powershell.exe, not pwsh.' }
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $Root ('artifacts\YouTubeLive-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
& (Join-Path $Root 'scripts\Build-Desktop-20261001.ps1') -RuntimeDirectory $RuntimeDirectory -OutputDirectory $OutputDirectory
if (!$?) { throw 'Desktop build failed' }
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'VideoBatch.exe'))
$tests = $asm.GetType('VideoBatch.YouTubeLiveSelfTests')
if (!$tests.GetMethod('RunSelfTests').Invoke($null,@())) { throw 'Live lifecycle/API tests failed' }
if (!$tests.GetMethod('RunMediaSelfTests').Invoke($null,@((Join-Path $OutputDirectory 'tools\ffmpeg.exe'),(Join-Path $OutputDirectory 'tools\ffprobe.exe')))) { throw 'Live media tests failed' }
Copy-Item (Join-Path $Root 'docs\AGENT_YOUTUBE_LIVE_20261005_RU.md') $OutputDirectory
Get-ChildItem $OutputDirectory -Recurse -File | Where-Object {$_.Name -ne 'SHA256_MANIFEST.txt'} | ForEach-Object {
    $hash = Get-FileHash $_.FullName -Algorithm SHA256
    "$($hash.Hash)  $($_.FullName.Substring($OutputDirectory.Length+1))"
} | Set-Content (Join-Path $OutputDirectory 'SHA256_MANIFEST.txt')
Write-Host "Built and tested: $OutputDirectory\VideoBatch.exe"
Write-Host 'Real YouTube broadcast is not validated by self-tests. Follow the included instructions.'
