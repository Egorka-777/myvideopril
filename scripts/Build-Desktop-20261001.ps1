param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$OutputDirectory = ""
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $Root ("artifacts\Desktop-20261001-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
if (Test-Path $OutputDirectory) { throw "OutputDirectory already exists. Use a new isolated folder." }
$runtime = (Resolve-Path $RuntimeDirectory).Path
foreach ($file in @("tools\uploader\node.exe", "tools\ffmpeg.exe", "tools\ffprobe.exe", "tools\uploader\node_modules\playwright-core\package.json")) {
    if (!(Test-Path (Join-Path $runtime $file))) { throw "Runtime missing: $file" }
}
$node = Join-Path $runtime "tools\uploader\node.exe"
$env:NODE_PATH = Join-Path $runtime "tools\uploader\node_modules"
Push-Location $Root
try {
    foreach ($test in Get-ChildItem tests -Filter *.js) {
        & $node $test.FullName
        if ($LASTEXITCODE -ne 0) { throw "Regression failed: $($test.Name)" }
    }
    dotnet build VideoBatch.csproj -c Release -p:ApplicationIcon= -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    $asm = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory "VideoBatch.exe"))
    $selfTests = $asm.GetType('VideoBatch.HttpRegressionSelfTests').GetMethod('RunAll')
    if (!$selfTests.Invoke($null, @())) { throw "C# self tests failed; kit must not be launched" }
    $tools = Join-Path $OutputDirectory "tools"
    $uploader = Join-Path $tools "uploader"
    New-Item -ItemType Directory -Force -Path $uploader | Out-Null
    Copy-Item (Join-Path $runtime "tools\uploader\node.exe") $uploader
    Copy-Item (Join-Path $runtime "tools\uploader\node_modules") $uploader -Recurse
    Copy-Item (Join-Path $runtime "tools\ffmpeg.exe") $tools
    Copy-Item (Join-Path $runtime "tools\ffprobe.exe") $tools
    # Copy the complete checked-out script set, never workers from the old installation.
    Get-ChildItem (Join-Path $Root "tools\uploader") -File | Copy-Item -Destination $uploader
    Copy-Item (Join-Path $Root "tools\uploader\tiktok-sign") $uploader -Recurse
    foreach ($name in @("worker.js", "worker-tiktok.js", "youtube-open-today.js", "tiktok-studio-evidence.js")) {
        if ((Get-FileHash (Join-Path $Root "tools\uploader\$name")).Hash -ne (Get-FileHash (Join-Path $uploader $name)).Hash) {
            throw "Script copy mismatch: $name"
        }
    }
    Copy-Item (Join-Path $Root "docs\ROLLOUT_20261001_RU.md") $OutputDirectory
    Copy-Item (Join-Path $Root "docs\YOUTUBE_PAUSED_GRID_20261001_RU.md") $OutputDirectory
    git rev-parse HEAD | Set-Content (Join-Path $OutputDirectory "SOURCE_COMMIT.txt")
    Get-ChildItem $OutputDirectory -Recurse -File | ForEach-Object {
        $hash=Get-FileHash $_.FullName -Algorithm SHA256
        "$($hash.Hash)  $($_.FullName.Substring($OutputDirectory.Length+1))"
    } | Set-Content (Join-Path $OutputDirectory "SHA256_MANIFEST.txt")
    Write-Host "Built isolated kit: $OutputDirectory\VideoBatch.exe"
    Write-Host "Live publication is NOT verified by this build. Follow ROLLOUT_20261001_RU.md."
} finally { Pop-Location }
