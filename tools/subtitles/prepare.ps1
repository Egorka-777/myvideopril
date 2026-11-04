param([Parameter(Mandatory=$true)][string]$Root, [string]$SharedRoot='')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$env:PYTHONUTF8 = '1'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$env:HF_HUB_DISABLE_SYMLINKS_WARNING = '1'
New-Item -ItemType Directory -Force $Root | Out-Null
$runtime = $null
# Reuse the Narезчик speech environment when available, without starting Ollama.
$candidates = @((Join-Path $Root 'venv/Scripts/python.exe'))
if ($SharedRoot) { $candidates += (Join-Path $SharedRoot 'venv/Scripts/python.exe') }
foreach ($candidate in $candidates) {
    if (!(Test-Path -LiteralPath $candidate)) { continue }
    & $candidate -c 'import faster_whisper, av' 2>$null
    if ($LASTEXITCODE -eq 0) { $runtime = $candidate; break }
}
if (!$runtime) {
    $python = $null
    $candidates = @()
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python311/python.exe')
        $candidates += (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python312/python.exe')
    }
    $candidates += @(Get-Command python.exe -ErrorAction SilentlyContinue | ForEach-Object Source)
    foreach ($candidate in $candidates) {
        if (!(Test-Path -LiteralPath $candidate)) { continue }
        & $candidate -c 'import sys; sys.exit(0 if (3,10)<=sys.version_info[:2]<(3,14) and sys.maxsize>2**32 else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { $python = $candidate; break }
    }
    if (!$python) { throw 'Установите Python 3.11 (64-bit) с python.org с галочкой Add Python to PATH, затем повторите «Распознать аудио». Ollama и API-ключи не нужны.' }
    $venv = Join-Path $Root 'venv'
    if (!(Test-Path (Join-Path $venv 'Scripts/python.exe'))) {
        & $python -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать окружение распознавания.' }
    }
    $runtime = Join-Path $venv 'Scripts/python.exe'
    Write-Output 'Первый запуск: устанавливаю локальное распознавание речи…'
    & $runtime -m pip install --requirement (Join-Path $PSScriptRoot 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось установить распознавание. Проверьте подключение к интернету.' }
}
$models = Join-Path $Root 'models'
if ($SharedRoot -and (Test-Path (Join-Path $SharedRoot 'models'))) { $models = Join-Path $SharedRoot 'models' }
& $runtime (Join-Path $PSScriptRoot 'worker.py') --prepare $Root --models $models
if ($LASTEXITCODE -ne 0) { throw 'Не удалось подготовить модель речи. Причина указана выше в журнале.' }
