param([Parameter(Mandatory=$true)][string]$Root, [switch]$SpeechOnly)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$env:PYTHONUTF8 = '1'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
New-Item -ItemType Directory -Force $Root | Out-Null
$python = $null
$candidates = @((Join-Path $env:LOCALAPPDATA 'Programs/Python/Python311/python.exe'), (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python312/python.exe'))
$candidates += @(Get-Command python.exe -ErrorAction SilentlyContinue | ForEach-Object Source)
foreach ($candidate in $candidates) {
    if (!(Test-Path $candidate)) { continue }
    & $candidate -c 'import sys; sys.exit(0 if (3,10)<=sys.version_info[:2]<(3,14) else 1)' 2>$null
    if ($LASTEXITCODE -eq 0) { $python = $candidate; break }
}
if (!$python) { throw 'Install Python 3.11 (64-bit) from python.org, then click Prepare again.' }
function Resolve-OllamaExe {
    if ($env:VB_OLLAMA_EXE -and (Test-Path $env:VB_OLLAMA_EXE)) { return $env:VB_OLLAMA_EXE }
    $cmd = Get-Command ollama.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
    if ($cmd -and (Test-Path $cmd)) { return $cmd }
    foreach ($p in @(
        (Join-Path $env:LOCALAPPDATA 'Programs/Ollama/ollama.exe'),
        (Join-Path ${env:ProgramFiles} 'Ollama/ollama.exe'),
        'D:\ollama-windows-amd64\ollama.exe'
    )) {
        if (Test-Path $p) { return $p }
    }
    return $null
}
$ollama = Resolve-OllamaExe
if (!$SpeechOnly -and !$ollama) {
    throw 'Ollama not found. Install from https://ollama.com/download/windows or put ollama.exe on PATH (also checked: LocalAppData\Programs\Ollama and D:\ollama-windows-amd64). Then click Prepare again.'
}
Write-Output 'Preparing isolated speech tools...'
$venv = Join-Path $Root 'venv'
if (!(Test-Path (Join-Path $venv 'Scripts/python.exe'))) {
    & $python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Could not create Python environment' }
}
$runtime = Join-Path $venv 'Scripts/python.exe'
& $runtime -m pip install --requirement (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Speech dependency installation failed. Check internet connection.' }
$cudaDevices = & $runtime -c 'import sys; sys.path.insert(0, sys.argv[1]); import ctranslate2; from whisper_runtime import _cuda_device_count; print(_cuda_device_count(ctranslate2))' $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'CUDA device detection failed. Check the NVIDIA driver.' }
if ([int]$cudaDevices -gt 0) {
    Write-Output 'Installing CUDA 12.6 cuBLAS and cuDNN 9 for NVIDIA speech acceleration...'
    & $runtime -m pip install 'nvidia-cublas-cu12==12.6.4.1' 'nvidia-cudnn-cu12==9.5.1.17'
    if ($LASTEXITCODE -ne 0) { throw 'NVIDIA speech runtime installation failed.' }
}
Write-Output 'Downloading speech model (small, multilingual)...'
$env:VB_HIGHLIGHTS_MODELS = Join-Path $Root 'models'
$env:HF_HUB_DISABLE_SYMLINKS = '1'
$downloadSpeech = @'
import os
from faster_whisper.utils import download_model
download_model('small', output_dir=None, cache_dir=os.environ['VB_HIGHLIGHTS_MODELS'])
'@
& $runtime -c $downloadSpeech
if ($LASTEXITCODE -ne 0) { throw 'Speech model download failed' }
if ($SpeechOnly) { Write-Output 'YouTube speech tools ready. Semantic selection uses the configured OpenRouter key.'; return }
Write-Output 'Starting local model service...'
$running = $false
try { Invoke-RestMethod 'http://127.0.0.1:11434/api/tags' -TimeoutSec 4 | Out-Null; $running = $true } catch {}
if (!$running) {
    Start-Process $ollama -ArgumentList 'serve' -WindowStyle Hidden
    for ($i=0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        try { Invoke-RestMethod 'http://127.0.0.1:11434/api/tags' -TimeoutSec 2 | Out-Null; $running = $true; break } catch {}
    }
}
if (!$running) { throw 'Ollama did not start. Open Ollama and retry.' }
Write-Output 'Downloading local editor model gemma3:4b (several GB, first setup only)...'
# Use the same explicit loopback API as the worker; ignore OLLAMA_HOST / cloud CLI config.
$body = '{"model":"gemma3:4b","stream":false}'
Invoke-RestMethod 'http://127.0.0.1:11434/api/pull' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 7200 | Out-Null
$show = Invoke-RestMethod 'http://127.0.0.1:11434/api/show' -Method Post -ContentType 'application/json' -Body '{"model":"gemma3:4b"}' -TimeoutSec 30
if ($show.capabilities -notcontains 'vision') { throw 'Downloaded model cannot read images' }
Write-Output 'Ready. Return to the app and choose Find moments.'
