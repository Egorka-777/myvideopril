param([string]$AppRoot,[string]$Root)
$ErrorActionPreference='Stop'
$doctor=Join-Path $AppRoot 'tools/telegram/doctor.ps1'
function Run-Doctor($extra) {
    $script:output=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $doctor -AppRoot $AppRoot -Root $Root @extra
    $script:code=$LASTEXITCODE
    return ($script:output -join "`n" | ConvertFrom-Json)
}
$report=Run-Doctor -extra @()
if ($code -ne 0 -or $report.checks[0].status -ne 'ready' -or $report.checks[-1].status -ne 'not_run') { throw 'Offline diagnosis' }
$marker=Join-Path $Root 'runtime.ready'
'old' | Set-Content $marker
$report=Run-Doctor -extra @()
if ($code -ne 1 -or $report.checks[-1].status -ne 'RuntimeMarkerStale') { throw 'Stale marker not detected' }
$report=Run-Doctor -extra @('-Repair')
if ($code -ne 0 -or (Get-Content $marker -Raw).Trim() -ne '2026-10-19-telegram-proxy-v4') { throw 'Safe marker repair' }
$worker=Join-Path $AppRoot 'tools/telegram/worker.py'
$bytes=[IO.File]::ReadAllBytes($worker)
try {
    [IO.File]::AppendAllText($worker,"`n# deliberate package mismatch fixture")
    $report=Run-Doctor -extra @('-Repair')
    if ($code -ne 1 -or $report.checks[-1].status -ne 'PackageFileChanged') { throw 'Mixed package accepted or repair ran on mismatched code' }
} finally {[IO.File]::WriteAllBytes($worker,$bytes)}
# The previous native worker test left a synthetic account on a closed localhost
# proxy. This must produce an actionable report, never a direct Telegram request.
$report=Run-Doctor -extra @('-AccountIndex','1')
if ($code -ne 1 -or $report.checks[-1].status -ne 'AccountCheckFailed') { throw 'Network failure exit contract' }
$detail=Get-Content (Join-Path $Root 'diagnostics/doctor-latest.json') -Raw -Encoding UTF8
$account=$detail | ConvertFrom-Json
if ($account.accounts[0].status -ne 'network' -or $account.accounts[0].attempts[0].transport -ne 'wss') { throw 'Account diagnosis missing transport failure' }
if ($detail -match 'session|api_hash|proxy://|127.0.0.1|fixture-secret') { throw 'Diagnostic secret leak' }
Write-Output 'PASS: package/runtime/marker repair/mixed package rejection/account transport report'
exit 0
