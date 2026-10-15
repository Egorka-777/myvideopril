param([string]$Root = (Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop\telegram'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force $Root | Out-Null
$venv = Join-Path $Root 'venv'
$python = Join-Path $venv 'Scripts\python.exe'
if (!(Test-Path $python)) {
  & py -3.11 -m venv $venv
  if ($LASTEXITCODE -ne 0) { throw 'Install Python 3.11 x64 with Python Launcher first.' }
}
& $python -m pip install --disable-pip-version-check --only-binary=tgcrypto,PyQt5,PyQt5-Qt5,PyQt5-sip -r (Join-Path $scriptDir 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Telegram dependencies installation failed.' }
& $python -c 'import telethon, opentele, socks, python_socks'
if ($LASTEXITCODE -ne 0) { throw 'Telegram dependency verification failed.' }

Write-Output 'Telegram ready'
