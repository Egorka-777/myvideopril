param([string]$Configuration="Release")
$ErrorActionPreference="Stop"

$repo=Split-Path $PSScriptRoot -Parent
$sourceTools=Join-Path $repo "tools\uploader"
$output=Join-Path $repo "artifacts\http-test"
$project=Join-Path $repo "VideoBatch.HttpTest.csproj"

if(-not (Get-Command dotnet -ErrorAction SilentlyContinue)){throw ".NET SDK не найден. Установите .NET SDK 8.x (он собирает net48 через reference assemblies)."}
if(-not (Get-Command node -ErrorAction SilentlyContinue)){throw "Node.js не найден — нельзя запустить регрессионные проверки."}
if(-not (Test-Path (Join-Path $sourceTools "node.exe"))){throw "Не найден tools\uploader\node.exe из рабочего комплекта VideoBatch."}
if(-not (Test-Path (Join-Path $sourceTools "node_modules\playwright-core"))){throw "Не найден tools\uploader\node_modules\playwright-core из рабочего комплекта VideoBatch."}

Push-Location $repo
try {
    & node "tests\http_upload_regressions.js"
    if($LASTEXITCODE -ne 0){throw "HTTP regression tests failed."}
    & node "tests\watch_regressions.js"
    if($LASTEXITCODE -ne 0){throw "Existing worker regression tests failed."}

    [void][IO.Directory]::CreateDirectory($output)
    & dotnet build $project -c $Configuration -o $output
    if($LASTEXITCODE -ne 0){throw "Сборка VideoBatch.HttpTest завершилась ошибкой."}

    $targetTools=Join-Path $output "tools\uploader"
    [void][IO.Directory]::CreateDirectory($targetTools)
    Copy-Item (Join-Path $sourceTools "node.exe") $targetTools -Force
    Copy-Item (Join-Path $sourceTools "worker-http-test.js") $targetTools -Force
    Copy-Item (Join-Path $sourceTools "node_modules") $targetTools -Recurse -Force

    $exe=Join-Path $output "VideoBatch.HttpTest.exe"
    if(-not (Test-Path $exe)){throw "После сборки не найден VideoBatch.HttpTest.exe."}
    Write-Host "PASS: изолированная сборка готова: $exe" -ForegroundColor Green
    Write-Host "Обычный VideoBatch.exe и его файлы не изменялись." -ForegroundColor Green
} finally { Pop-Location }
