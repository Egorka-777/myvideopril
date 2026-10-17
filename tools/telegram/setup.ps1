param([string]$Root = (Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop\telegram'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PYTHONUTF8 = '1'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force $Root | Out-Null
$venv = Join-Path $Root 'venv'
$runtime = Join-Path $venv 'Scripts\python.exe'
Remove-Item (Join-Path $Root 'runtime.ready') -ErrorAction SilentlyContinue

function Resolve-Python {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python311/python.exe'),
        (Join-Path $env:ProgramFiles 'Python311/python.exe')
    )
    $candidates += @(Get-Command python.exe -ErrorAction SilentlyContinue | ForEach-Object Source)
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        try {
            $found = & $launcher.Source -3.11 -c 'import sys; print(sys.executable)' 2>$null
            if ($LASTEXITCODE -eq 0) { $candidates += $found }
        } catch { } # No 3.11 registered: continue with known paths.
    }
    foreach ($candidate in $candidates) {
        if (!(Test-Path $candidate)) { continue }
        & $candidate -c 'import sys,struct; sys.exit(0 if sys.version_info[:2]==(3,11) and struct.calcsize("P")==8 else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { return $candidate }
    }
    return $null
}

if (!(Test-Path $runtime)) {
    $python = Resolve-Python
    if (!$python) { throw 'Install Python 3.11 x64 from python.org, then click Prepare again. This runtime uses prebuilt Windows dependencies for Python 3.11.' }
    & $python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Could not create Telegram Python environment.' }
}

& $runtime -m pip install --disable-pip-version-check --upgrade pip
& $runtime -m pip install --disable-pip-version-check --only-binary=tgcrypto,PyQt5,PyQt5-Qt5,PyQt5-sip -r (Join-Path $scriptDir 'requirements.txt')
if ($LASTEXITCODE -ne 0) {
    & $runtime -m pip install --disable-pip-version-check -r (Join-Path $scriptDir 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Telegram dependencies installation failed. Check internet connection.' }
}
& $runtime (Join-Path $scriptDir 'runtime_check.py') $Root
if ($LASTEXITCODE -ne 0) { throw 'Telegram runtime smoke test failed. Do not start accounts until preparation succeeds.' }

Write-Output 'Telegram ready'
