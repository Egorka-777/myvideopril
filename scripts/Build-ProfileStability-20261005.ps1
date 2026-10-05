param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$OutputDirectory = ""
)
$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Desktop") { throw "Run this script with Windows powershell.exe (.NET Framework), not pwsh." }
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $Root ("artifacts\ProfileStability-20261005-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
& (Join-Path $Root "scripts\Build-Desktop-20261001.ps1") -RuntimeDirectory $RuntimeDirectory -OutputDirectory $OutputDirectory
if (!$?) { throw "Desktop build failed" }
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$uploader = Join-Path $OutputDirectory "tools\uploader"
foreach ($file in Get-ChildItem (Join-Path $Root "tools\uploader") -Filter *.js) {
    if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash (Join-Path $uploader $file.Name)).Hash) { throw "Script mismatch: $($file.Name)" }
}
Copy-Item (Join-Path $Root "docs\AGENT_PROFILE_STABILITY_20261005_RU.md") $OutputDirectory
"Read AGENT_PROFILE_STABILITY_20261005_RU.md. Worker build: 2026-10-05-profile-stability-v1." | Set-Content (Join-Path $OutputDirectory "START_HERE.txt")
# Rebuild manifest after copying this instruction. Do not hash the manifest itself.
Get-ChildItem $OutputDirectory -Recurse -File | Where-Object { $_.Name -ne "SHA256_MANIFEST.txt" } | ForEach-Object {
    $hash=Get-FileHash $_.FullName -Algorithm SHA256
    "$($hash.Hash)  $($_.FullName.Substring($OutputDirectory.Length+1))"
} | Set-Content (Join-Path $OutputDirectory "SHA256_MANIFEST.txt")
Write-Host "Verified isolated kit: $OutputDirectory\VideoBatch.exe"
