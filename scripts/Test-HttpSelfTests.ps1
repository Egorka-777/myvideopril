$ErrorActionPreference = "Stop"
$exe = "D:\VideoBatch\repo\bin\Release\net48-test\VideoBatch.exe"
if (-not (Test-Path $exe)) { $exe = "D:\VideoBatch\repo\bin\Release\net48\VideoBatch.exe" }
$asm = [Reflection.Assembly]::LoadFrom($exe)
$type = $asm.GetType("VideoBatch.HttpRegressionSelfTests")
Write-Host "Running RunAll..."
try {
    $allOk = [bool]$type.GetMethod("RunAll").Invoke($null, $null)
    Write-Host "RunAll => $allOk"
} catch {
    Write-Host "RunAll FAIL: $($_.Exception.InnerException.Message)"
}
$methods = @("RunLog160617Scenario", "RunWorkerPoolScenario")
foreach ($m in $methods) {
    Write-Host "Running $m..."
    try {
        $ok = [bool]$type.GetMethod($m).Invoke($null, $null)
        Write-Host "$m => $ok"
        if ($m -eq "RunWorkerPoolScenario") {
            $err = $type.GetField("LastPoolError").GetValue($null)
            if ($err) { Write-Host "LastPoolError: $err" }
        }
    } catch {
        Write-Host "FAIL $m : $($_.Exception.InnerException.Message)"
    }
}
Write-Host "TaskQueueWriter test..."
$tw = $asm.GetType("VideoBatch.TaskQueueWriter")
$ok2 = [bool]$tw.GetMethod("RunConcurrentWriteSelfTest").Invoke($null, @(40, 25))
Write-Host "TaskQueueWriter => $ok2"
