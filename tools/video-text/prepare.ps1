param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$env:PYTHONUTF8 = '1'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
New-Item -ItemType Directory -Force $Root | Out-Null
$Root = (Resolve-Path -LiteralPath $Root).Path
$runtime = Join-Path $Root 'venv/Scripts/python.exe'
$manifest = Join-Path $Root 'runtime.json'
# Never leave a successful manifest behind after a failed preparation.
if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
if (!(Test-Path -LiteralPath $runtime)) {
    $candidates = @()
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python311/python.exe')
        $candidates += (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python312/python.exe')
    }
    $candidates += @(Get-Command python.exe -ErrorAction SilentlyContinue | ForEach-Object Source)
    $python = $null
    foreach ($candidate in $candidates) {
        if (!(Test-Path -LiteralPath $candidate)) { continue }
        & $candidate -c 'import sys; sys.exit(0 if (3,10)<=sys.version_info[:2]<(3,13) and sys.maxsize>2**32 else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { $python = $candidate; break }
    }
    if (!$python) { throw 'Install 64-bit Python 3.11 or 3.12 from python.org, then retry local video text detection.' }
    & $python -m venv (Join-Path $Root 'venv')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the local video text environment.' }
}
$pins = @('rapidocr_onnxruntime==1.4.4', 'opencv-python==4.11.0.86', 'numpy==1.26.4', 'onnxruntime==1.20.1')
$check = @'
from importlib.metadata import version, PackageNotFoundError
import sys
try:
    ready = all(version(p.split(chr(61)*2)[0]) == p.split(chr(61)*2)[1] for p in sys.argv[1:])
except PackageNotFoundError:
    ready = False
sys.exit(0 if ready else 1)
'@
& $runtime -c $check @pins
if ($LASTEXITCODE -ne 0) {
    Write-Output '{"stage":"prepare","message":"Installing local video text detector (first use requires internet)."}'
    & $runtime -m pip install @pins
    if ($LASTEXITCODE -ne 0) { throw 'Cannot install the local video text detector. See the installation log.' }
}
# The detector model ships inside the wheel; this also verifies actual ONNX loading.
& $runtime (Join-Path $PSScriptRoot 'worker.py') --prepare $Root
if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare the local video text detector. See the error above.' }
