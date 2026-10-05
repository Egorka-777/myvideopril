param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$OutputDirectory = "",
    [string]$Python = "python"
)
$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Desktop") { throw "Use Windows powershell.exe, not pwsh." }
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$runtime = (Resolve-Path $RuntimeDirectory).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $Root ("artifacts\Processing-20261005-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
# Check the extra integration prerequisite before creating a new kit.
& $Python --version
if ($LASTEXITCODE -ne 0) { throw "Python 3 is required for the real-media processing tests." }
& (Join-Path $Root "scripts\Build-ProfileStability-20261005.ps1") -RuntimeDirectory $runtime -OutputDirectory $OutputDirectory
if (!$?) { throw "Desktop build failed" }
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
Push-Location $Root
try {
    dotnet build tests/ProcessingHost/ProcessingHost.csproj -c Release "-p:VideoBatchAssembly=$OutputDirectory\VideoBatch.exe"
    if ($LASTEXITCODE -ne 0) { throw "Processing integration host build failed" }
    & $Python tests/processing_integration.py --host tests/ProcessingHost/bin/Release/net8.0/ProcessingHost.dll --ffmpeg "$runtime\tools\ffmpeg.exe" --ffprobe "$runtime\tools\ffprobe.exe"
    if ($LASTEXITCODE -ne 0) { throw "Real-media integration failed. Do not launch the new kit." }
} finally { Pop-Location }
Copy-Item (Join-Path $Root "docs\AGENT_PROCESSING_20261005_RU.md") $OutputDirectory
"Read AGENT_PROCESSING_20261005_RU.md first. New effects are disabled by default. Previous profile stability changes are included." | Set-Content (Join-Path $OutputDirectory "START_HERE.txt")
Get-ChildItem $OutputDirectory -Recurse -File | Where-Object { $_.Name -ne "SHA256_MANIFEST.txt" } | ForEach-Object {
    $hash = Get-FileHash $_.FullName -Algorithm SHA256
    "$($hash.Hash)  $($_.FullName.Substring($OutputDirectory.Length+1))"
} | Set-Content (Join-Path $OutputDirectory "SHA256_MANIFEST.txt")
Write-Host "Processing kit verified: $OutputDirectory\VideoBatch.exe"
