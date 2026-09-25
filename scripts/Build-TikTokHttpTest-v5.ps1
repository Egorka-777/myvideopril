param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent
$sourceTools = Join-Path $repo "tools\uploader"
$output = Join-Path $repo "artifacts\tiktok-http-test-v5"
$project = Join-Path $repo "VideoBatch.TikTokHttpTest.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET SDK not found." }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw "Node.js not found." }
if (-not (Test-Path (Join-Path $sourceTools "node.exe"))) { throw "Missing tools\uploader\node.exe." }
if (-not (Test-Path (Join-Path $sourceTools "node_modules\playwright-core"))) { throw "Missing playwright-core." }
if (-not (Test-Path (Join-Path $sourceTools "tiktok-aws-sigv4.js"))) { throw "Missing tiktok-aws-sigv4.js." }

Push-Location $repo
try {
    & node "tests\tiktok_caption_regressions.js"
    if ($LASTEXITCODE -ne 0) { throw "tiktok_caption_regressions failed." }
    & node "tests\tiktok_http_regressions.js"
    if ($LASTEXITCODE -ne 0) { throw "tiktok_http_regressions failed." }
    & node "tests\tiktok_schedule_regressions.js"
    if ($LASTEXITCODE -ne 0) { throw "tiktok_schedule_regressions failed." }

    [void][IO.Directory]::CreateDirectory($output)
    & dotnet build $project -c $Configuration -o $output
    if ($LASTEXITCODE -ne 0) { throw "VideoBatch.TikTokHttpTest build failed." }

    $targetTools = Join-Path $output "tools\uploader"
    [void][IO.Directory]::CreateDirectory($targetTools)
    Copy-Item (Join-Path $sourceTools "node.exe") $targetTools -Force
    foreach ($name in @("worker-tiktok-http.js", "tiktok-aws-sigv4.js", "worker-tiktok.js")) {
        Copy-Item (Join-Path $sourceTools $name) (Join-Path $targetTools $name) -Force
    }
    Copy-Item (Join-Path $sourceTools "node_modules") $targetTools -Recurse -Force

    $exe = Join-Path $output "VideoBatch.TikTokHttpTest.exe"
    if (-not (Test-Path $exe)) { throw "VideoBatch.TikTokHttpTest.exe not found." }
    Write-Host "PASS: $exe" -ForegroundColor Green
    Write-Host "TikTok HTTP v5 isolated test build." -ForegroundColor Green
    Write-Host "STOP FOR CURSOR: do not run live TikTok upload from agent." -ForegroundColor Yellow
} finally { Pop-Location }
