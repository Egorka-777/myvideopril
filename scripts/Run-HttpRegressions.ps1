$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Push-Location $Root
try {
    Write-Host "Node: http_upload_regressions.js"
    node tests/http_upload_regressions.js
    Write-Host "Node: http_log_regression.js"
    node tests/http_log_regression.js
    Write-Host "Node: http_pool_regression.js"
    node tests/http_pool_regression.js
    Write-Host "dotnet build"
    dotnet build VideoBatch.csproj -c Release -o (Join-Path $Root "bin\Release\net48-test") -p:ApplicationIcon= | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    Write-Host "C#: HttpRegressionSelfTests"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    $exe = Join-Path $Root "bin\Release\net48-test\VideoBatch.exe"
    if (-not (Test-Path $exe)) { $exe = Join-Path $Root "bin\Release\net48\VideoBatch.exe" }
    if (-not (Test-Path $exe)) { $exe = Join-Path $Root "bin\Debug\net48\VideoBatch.exe" }
    $asm = [Reflection.Assembly]::LoadFrom($exe)
    $type = $asm.GetType("VideoBatch.HttpRegressionSelfTests")
    if ($null -eq $type) { throw "HttpRegressionSelfTests type not found" }
    $ok = [bool]$type.GetMethod("RunAll").Invoke($null, $null)
    if (-not $ok) { throw "HttpRegressionSelfTests.RunAll failed" }
    Write-Host "OK: all HTTP regression checks passed"
} finally {
    Pop-Location
}
