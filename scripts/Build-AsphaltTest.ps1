# Build isolated asphalt test artifact without overwriting Desktop VideoBatch.exe
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$OutDir = Join-Path $Root "artifacts\asphalt-test"
$UploaderSrc = Join-Path $Root "tools\uploader"

Write-Host "Building VideoBatch asphalt test..."
Push-Location $Root
try {
    dotnet build VideoBatch.csproj -c Release -o $OutDir -p:ApplicationIcon=
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    $destTools = Join-Path $OutDir "tools\uploader"
    New-Item -ItemType Directory -Force -Path $destTools | Out-Null
    foreach ($name in @("worker.js", "worker-http.js", "worker-http-test.js", "worker-tiktok.js")) {
        $src = Join-Path $UploaderSrc $name
        if (Test-Path $src) { Copy-Item $src (Join-Path $destTools $name) -Force }
    }
    if (Test-Path (Join-Path $UploaderSrc "node.exe")) {
        Copy-Item (Join-Path $UploaderSrc "node.exe") (Join-Path $destTools "node.exe") -Force
    }
    if (Test-Path (Join-Path $UploaderSrc "node_modules")) {
        Copy-Item (Join-Path $UploaderSrc "node_modules") (Join-Path $destTools "node_modules") -Recurse -Force
    }

    Rename-Item -Path (Join-Path $OutDir "VideoBatch.exe") -NewName "VideoBatch.AsphaltTest.exe" -Force
    Write-Host "OK: $(Join-Path $OutDir 'VideoBatch.AsphaltTest.exe')"
} finally {
    Pop-Location
}
