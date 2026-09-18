param([string]$Configuration="Release")
$ErrorActionPreference="Stop"

$repo=Split-Path $PSScriptRoot -Parent
$sourceTools=Join-Path $repo "tools\uploader"
$output=Join-Path $repo "artifacts\http-test"
$project=Join-Path $repo "VideoBatch.HttpTest.csproj"

if(-not (Get-Command dotnet -ErrorAction SilentlyContinue)){throw ".NET SDK not found. Install .NET SDK 8.x (builds net48 via reference assemblies)."}
if(-not (Get-Command node -ErrorAction SilentlyContinue)){throw "Node.js not found - cannot run regression checks."}
if(-not (Test-Path (Join-Path $sourceTools "node.exe"))){throw "Missing tools\uploader\node.exe from the local VideoBatch bundle."}
if(-not (Test-Path (Join-Path $sourceTools "node_modules\playwright-core"))){throw "Missing tools\uploader\node_modules\playwright-core from the local VideoBatch bundle."}

Push-Location $repo
try {
    & node "tests\http_upload_regressions.js"
    if($LASTEXITCODE -ne 0){throw "HTTP regression tests failed."}
    & node "tests\watch_regressions.js"
    if($LASTEXITCODE -ne 0){throw "Existing worker regression tests failed."}

    [void][IO.Directory]::CreateDirectory($output)
    & dotnet build $project -c $Configuration -o $output
    if($LASTEXITCODE -ne 0){throw "VideoBatch.HttpTest build failed."}

    $targetTools=Join-Path $output "tools\uploader"
    [void][IO.Directory]::CreateDirectory($targetTools)
    Copy-Item (Join-Path $sourceTools "node.exe") $targetTools -Force
    Copy-Item (Join-Path $sourceTools "worker-http-test.js") $targetTools -Force
    Copy-Item (Join-Path $sourceTools "node_modules") $targetTools -Recurse -Force

    $exe=Join-Path $output "VideoBatch.HttpTest.exe"
    if(-not (Test-Path $exe)){throw "VideoBatch.HttpTest.exe was not found after build."}
    Write-Host "PASS: isolated build ready: $exe" -ForegroundColor Green
    Write-Host "Main VideoBatch.exe and its files were not modified." -ForegroundColor Green
    Write-Host "STOP FOR CURSOR: do not run EXE or start/stop Dolphin. Owner runs the live test manually." -ForegroundColor Yellow
} finally { Pop-Location }
