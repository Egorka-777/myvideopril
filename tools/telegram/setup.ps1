param([string]$Root = (Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop\telegram'), [switch]$Repair)
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
    if ($env:pythonLocation) { $candidates = @((Join-Path $env:pythonLocation 'python.exe')) + $candidates }
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
        try {
            & $candidate -I -c 'import sys; sys.exit(0 if sys.version_info[:2]==(3,11) and sys.maxsize>2**32 else 1)' 2>$null
            if ($LASTEXITCODE -eq 0) { return $candidate }
        } catch { } # Broken installations / Store aliases must not abort discovery.
    }
    return $null
}

if (Test-Path $runtime) {
    & $runtime -I -c 'import sys; sys.exit(0 if sys.version_info[:2]==(3,11) and sys.maxsize>2**32 else 1)' 2>$null
    if ($LASTEXITCODE -ne 0) {
        if (!$Repair) { throw 'Existing runtime must be Python 3.11 x64. Close the app and run doctor.ps1 -Repair.' }
        Move-Item -LiteralPath $venv -Destination ($venv+'.previous-'+[Guid]::NewGuid().ToString('N'))
    }
}
if (!(Test-Path $runtime)) {
    $python = Resolve-Python
    if (!$python) { throw 'Install Python 3.11 x64 from python.org, then click Prepare again. This runtime uses prebuilt Windows dependencies for Python 3.11.' }
    & $python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Could not create Telegram Python environment.' }
}

& $runtime -m pip install --disable-pip-version-check --upgrade pip
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare pip. Check internet connection.' }
$repairArgs = @()
if ($Repair) { $repairArgs += '--force-reinstall' }
& $runtime -m pip install --disable-pip-version-check @repairArgs --only-binary=tgcrypto,PyQt5,PyQt5-Qt5,PyQt5-sip -r (Join-Path $scriptDir 'requirements.txt')
if ($LASTEXITCODE -ne 0) {
    & $runtime -m pip install --disable-pip-version-check @repairArgs -r (Join-Path $scriptDir 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Telegram dependencies installation failed. Check internet connection.' }
}
& $runtime (Join-Path $scriptDir 'runtime_check.py') $Root
if ($LASTEXITCODE -ne 0) { throw 'Telegram runtime smoke test failed. Do not start accounts until preparation succeeds.' }

Write-Output 'Telegram ready'
