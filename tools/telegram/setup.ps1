param([string]$Root = (Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop\telegram'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PYTHONUTF8 = '1'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force $Root | Out-Null
$venv = Join-Path $Root 'venv'
$runtime = Join-Path $venv 'Scripts\python.exe'

function Resolve-Python {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python311/python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python312/python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python313/python.exe')
    )
    $candidates += @(Get-Command python.exe -ErrorAction SilentlyContinue | ForEach-Object Source)
    foreach ($candidate in $candidates) {
        if (!(Test-Path $candidate)) { continue }
        & $candidate -c 'import sys; sys.exit(0 if (3,10)<=sys.version_info[:2]<(3,14) else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { return $candidate }
    }
    return $null
}

if (!(Test-Path $runtime)) {
    $python = Resolve-Python
    if (!$python) { throw 'Install Python 3.11 or 3.12 (64-bit) from python.org, then click Prepare again.' }
    & $python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Could not create Telegram Python environment.' }
}

& $runtime -m pip install --disable-pip-version-check --upgrade pip
& $runtime -m pip install --disable-pip-version-check --only-binary=tgcrypto,PyQt5,PyQt5-Qt5,PyQt5-sip -r (Join-Path $scriptDir 'requirements.txt')
if ($LASTEXITCODE -ne 0) {
    & $runtime -m pip install --disable-pip-version-check -r (Join-Path $scriptDir 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Telegram dependencies installation failed. Check internet connection.' }
}
& $runtime -c "import telethon, opentele, python_socks"
if ($LASTEXITCODE -ne 0) { throw 'Telegram dependency verification failed after install.' }

Write-Output 'Telegram ready'
