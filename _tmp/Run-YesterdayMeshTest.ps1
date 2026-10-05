$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

$kit = "D:\VideoBatch\repo-worktrees\native-studio-20261001\artifacts\Desktop-20261001-20261002-090818"
$repo = "D:\VideoBatch\repo-worktrees\native-studio-20261001\tools\uploader"
Copy-Item "$repo\worker.js","$repo\youtube-open-today.js","$repo\youtube-playback.js" "$kit\tools\uploader\" -Force

$logFile = Join-Path $env:LOCALAPPDATA "VideoBatchDesktop\mesh-yesterday-test-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
function Write-Log($msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $msg
    Write-Host $line
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

$xml = [xml](Get-Content (Join-Path $env:LOCALAPPDATA "VideoBatchDesktop\settings.xml") -Raw)
$port = [int]$xml.Preferences.DolphinPort
$token = [Text.Encoding]::UTF8.GetString(
    [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$xml.Preferences.ProtectedDolphinToken), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
)

$datesToTry = @("2026-10-01", "2026-09-30")
$profileId = "836981176"
$jobJsonTemplate = '{"localPort":PORT,"profileId":"836981176","todayDate":"DATE","pcUtcOffsetMinutes":OFFSET,"watchMesh":true,"skipQueueDelay":true,"watchTargets":[{"channelUrl":"https://www.youtube.com/@filmrezzzka/videos","ownerName":"Lucky Tom","ownerProfileId":"836981176","meshSingleLong":true,"contentKind":"long"},{"channelUrl":"https://www.youtube.com/@traderprdp/videos","ownerName":"traderprdp","ownerProfileId":"852116605","meshSingleLong":true,"contentKind":"long"}]}'

$node = Join-Path $kit "tools\uploader\node.exe"
$worker = Join-Path $kit "tools\uploader\worker.js"
$env:VIDEOBATCH_DOLPHIN_TOKEN = $token
$offset = [int][DateTimeOffset]::Now.Offset.TotalMinutes
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

foreach ($todayDate in $datesToTry) {
    Write-Log "=== Mesh test todayDate=$todayDate profile=$profileId 2 channels ==="
    $job = $jobJsonTemplate.Replace("PORT", $port).Replace("DATE", $todayDate).Replace("OFFSET", $offset)
    $jobPath = Join-Path $env:TEMP ("upload-mesh-yesterday-" + [guid]::NewGuid().ToString("N") + ".json")
    [IO.File]::WriteAllText($jobPath, $job, $utf8NoBom)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $node
    $psi.Arguments = "`"$worker`" `"$jobPath`""
    $psi.WorkingDirectory = Split-Path $worker
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.EnvironmentVariables["VIDEOBATCH_DOLPHIN_TOKEN"] = $token

    $p = [System.Diagnostics.Process]::Start($psi)
    Write-Log "Worker PID $($p.Id) log=$logFile"

    $success = $false
    $lastLine = ""
    while (-not $p.HasExited) {
        while (($line = $p.StandardOutput.ReadLine()) -ne $null) {
            $lastLine = $line
            try {
                $msg = $line | ConvertFrom-Json
                if ($msg.text) { Write-Log ("[" + $msg.stage + "] " + $msg.text) }
                if ($msg.stage -eq "done" -and $msg.success -eq $true) { $success = $true }
            } catch { Write-Log $line }
        }
        Start-Sleep -Milliseconds 400
    }
    while (($line = $p.StandardOutput.ReadLine()) -ne $null) {
        $lastLine = $line
        try {
            $msg = $line | ConvertFrom-Json
            if ($msg.text) { Write-Log ("[" + $msg.stage + "] " + $msg.text) }
            if ($msg.stage -eq "done" -and $msg.success -eq $true) { $success = $true }
        } catch { Write-Log $line }
    }
    $err = $p.StandardError.ReadToEnd()
    if ($err) { Write-Log ("STDERR: " + $err) }
    Remove-Item $jobPath -Force -ErrorAction SilentlyContinue
    Write-Log ("Exit " + $p.ExitCode + " success=" + $success + " date=" + $todayDate)

    if ($success) {
        Write-Log ("TEST PASSED todayDate=" + $todayDate)
        exit 0
    }
    if ($lastLine -match "other_date|0/2|0/1") {
        Write-Log ("No videos for " + $todayDate + " trying next date")
        continue
    }
    Write-Log ("TEST FAILED todayDate=" + $todayDate)
    exit $p.ExitCode
}
Write-Log "TEST FAILED no matching videos"
exit 1
