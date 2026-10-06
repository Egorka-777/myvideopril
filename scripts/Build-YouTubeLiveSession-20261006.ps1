param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$OutputDirectory = ""
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows powershell.exe, not pwsh.' }
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $Root ('artifacts\YouTubeLiveSession-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
& (Join-Path $Root 'scripts\Build-YouTubeLive-20261005.ps1') -RuntimeDirectory $RuntimeDirectory -OutputDirectory $OutputDirectory
if (!$?) { throw 'Desktop/session tests failed' }
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
foreach ($name in @('worker-youtube-live-session.js','dolphin-session.js')) {
    $source = Join-Path $Root ('tools\uploader\' + $name)
    $built = Join-Path $OutputDirectory ('tools\uploader\' + $name)
    if (!(Test-Path $built) -or (Get-FileHash $source).Hash -ne (Get-FileHash $built).Hash) { throw "Session worker copy mismatch: $name" }
}
Copy-Item (Join-Path $Root 'docs\AGENT_YOUTUBE_LIVE_SESSION_20261006_RU.md') $OutputDirectory
Get-ChildItem $OutputDirectory -Recurse -File | Where-Object {$_.Name -ne 'SHA256_MANIFEST.txt'} | ForEach-Object {
    $hash = Get-FileHash $_.FullName -Algorithm SHA256
    "$($hash.Hash)  $($_.FullName.Substring($OutputDirectory.Length+1))"
} | Set-Content (Join-Path $OutputDirectory 'SHA256_MANIFEST.txt')
Write-Host "Built session version: $OutputDirectory\VideoBatch.exe"
Write-Host 'No Google OAuth for new streams. Real Dolphin/Studio must be tested with the owner.'
