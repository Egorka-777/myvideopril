param(
    [string]$AppRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop\telegram'),
    [switch]$Repair,
    [int]$AccountIndex = 0
)
# Windows PowerShell 5.1. No account export, proxy rotation or direct fallback.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PYTHONUTF8 = '1'
$expected = '2026-10-19-telegram-proxy-v4'
$checks = [Collections.Generic.List[object]]::new()
$exitCode = 1
$stage = 'package'
function Add-Check($name,$state,$action) {
    $checks.Add([ordered]@{check=$name;status=$state;next_action=$action})
}
try {
    $AppRoot = [IO.Path]::GetFullPath($AppRoot)
    $Root = [IO.Path]::GetFullPath($Root)
    $env:VIDEOBATCH_HOME = $AppRoot
    $manifestPath = Join-Path $AppRoot 'TELEGRAM_PACKAGE.json'
    if (!(Test-Path $manifestPath)) { throw 'PackageManifestMissing' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.version -ne $expected) { throw 'PackageVersionMismatch' }
    foreach ($file in $manifest.files) {
        $target = [IO.Path]::GetFullPath((Join-Path $AppRoot $file.path))
        if (!$target.StartsWith($AppRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'InvalidManifestPath' }
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw 'PackageFileMissing' }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw 'PackageFileChanged' }
    }
    Add-Check 'package' 'ready' 'EXE, DLL and Telegram scripts are one verified package.'
    $asm = [Reflection.Assembly]::LoadFrom((Join-Path $AppRoot 'VideoBatch.exe'))
    $runner = $asm.GetType('VideoBatch.TelegramRunner',$true)
    if ($runner.GetField('ExpectedWorker').GetRawConstantValue() -ne $expected) { throw 'ExeWorkerMismatch' }
    $stage = 'runtime'
    $python = Join-Path $Root 'venv/Scripts/python.exe'
    $smoke = Join-Path $AppRoot 'tools/telegram/runtime_check.py'
    $ok = $false
    if (Test-Path $python) {
        $result = & $python -X utf8 $smoke 2>&1
        $ok = $LASTEXITCODE -eq 0
    }
    if (!$ok -and $Repair) {
        # Repairs dependencies only. The encrypted vault, accounts and proxies stay intact.
        if (Get-Process VideoBatch -ErrorAction SilentlyContinue) { throw 'CloseAppBeforeRepair' }
        $setup = Join-Path $AppRoot 'tools/telegram/setup.ps1'
        # Own the Process directly. Windows PowerShell Start-Process -PassThru
        # with redirected streams may return a null ExitCode (PowerShell #5421).
        $process = [Diagnostics.Process]::new()
        try {
            $setupArgs = '-NoProfile -ExecutionPolicy Bypass -File "'+$setup+'" -Root "'+$Root+'" -Repair'
            $info = [Diagnostics.ProcessStartInfo]::new('powershell.exe',$setupArgs)
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $process.StartInfo = $info
            if (!$process.Start()) { throw 'RuntimeRepairFailed' }
            $repairOutput = $process.StandardOutput.ReadToEndAsync()
            $repairErrors = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(300000)) { & taskkill.exe /PID $process.Id /T /F | Out-Null; throw 'RuntimeRepairTimeout' }
            $process.WaitForExit()
            $null = $repairOutput.GetAwaiter().GetResult()
            $null = $repairErrors.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) { throw 'RuntimeRepairFailed' }
        } finally {
            $process.Dispose()
        }
        $result = & $python -X utf8 $smoke 2>&1
        $ok = $LASTEXITCODE -eq 0
        if ($ok) { Add-Check 'repair' 'ready' 'Installed pinned dependencies and repeated runtime smoke test.' }
    }
    if (!$ok) { throw 'RuntimeNotReady' }
    $marker = Join-Path $Root 'runtime.ready'
    if (!(Test-Path $marker) -or (Get-Content $marker -Raw -Encoding UTF8).Trim() -ne $expected) {
        if (!$Repair) { throw 'RuntimeMarkerStale' }
        $result = & $python -X utf8 $smoke $Root 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'RuntimeNotReady' }
    }
    Add-Check 'runtime' 'ready' 'Pinned imports, WSS transport, runtime marker and EXE version match.'
    if ($AccountIndex -gt 0) {
        $stage = 'account'
        # Runs the same worker and encrypted store as the UI, with one chosen account.
        $passed = $asm.GetType('VideoBatch.TelegramDoctor',$true).GetMethod('CheckAccount').Invoke($null,@([string]$Root,[int]$AccountIndex))
        if (!$passed) { throw 'AccountCheckFailed' }
        Add-Check 'account' 'ready' 'Telegram get_me confirmed. See doctor-latest.json for transport.'
    } else {
        Add-Check 'account' 'not_run' 'In the app select account and click Diagnostics; or supply -AccountIndex N (1-based saved order).'
    }
    $exitCode = 0
} catch {
    # Exception messages from external programs/assembly are deliberately not exported.
    $code = if ($_.Exception.Message -match '^(Package\w+|InvalidManifestPath|ExeWorkerMismatch|CloseAppBeforeRepair|Runtime\w+|AccountCheckFailed)$') { $_.Exception.Message } else { 'CheckFailed' }
    $action = switch ($stage) {
        'package' { 'Replace the complete CI update package. Do not mix EXE and scripts from different builds.' }
        'runtime' { 'Close the app, run doctor.ps1 -Repair, then repeat diagnostics. Keep telegram.vault and source account files.' }
        default { 'Read diagnostics/doctor-latest.json. Follow next_action; no repeated retries, no proxy or account rotation.' }
    }
    Add-Check $stage $code $action
} finally {
    $folder = Join-Path $Root 'diagnostics'
    New-Item -ItemType Directory -Force $folder | Out-Null
    $report = [ordered]@{version=$expected;utc=[DateTime]::UtcNow.ToString('o');checks=$checks.ToArray();exit_code=$exitCode}
    $json = $report | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $folder 'doctor-system.json'),$json,[Text.UTF8Encoding]::new($false))
    Write-Output $json
}
exit $exitCode
