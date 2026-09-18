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

    $builtExe = Join-Path $OutDir "VideoBatch.exe"
    $testExe = Join-Path $OutDir "VideoBatch.AsphaltTest.exe"
    Copy-Item -Path $builtExe -Destination $testExe -Force
    Remove-Item -Path $builtExe -Force -ErrorAction SilentlyContinue

    $toolsOut = Join-Path $OutDir "tools"
    $ffmpegCandidates = @(
        (Join-Path $Root "..\VideoBatch\VideoBatch_Desktop"),
        (Join-Path $Root "..\VideoBatch_Desktop"),
        (Join-Path $env:LOCALAPPDATA "VideoBatchDesktop")
    )
    foreach ($cand in $ffmpegCandidates) {
        try {
            $cand = (Resolve-Path $cand -ErrorAction Stop).Path
        } catch { continue }
        $ff = Join-Path $cand "tools\ffmpeg.exe"
        $fp = Join-Path $cand "tools\ffprobe.exe"
        if ((Test-Path $ff) -and (Test-Path $fp)) {
            New-Item -ItemType Directory -Force -Path $toolsOut | Out-Null
            Copy-Item $ff (Join-Path $toolsOut "ffmpeg.exe") -Force
            Copy-Item $fp (Join-Path $toolsOut "ffprobe.exe") -Force
            Write-Host "Copied ffmpeg from $cand"
            break
        }
    }

    Write-Host "OK: $testExe"
} finally {
    Pop-Location
}
