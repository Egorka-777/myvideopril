$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

$kit = "D:\VideoBatch\repo-worktrees\native-studio-20261001\artifacts\Desktop-20261001-20261002-090818"
$repo = "D:\VideoBatch\repo-worktrees\native-studio-20261001\tools\uploader"
Copy-Item "$repo\worker.js", "$repo\youtube-open-today.js", "$repo\youtube-playback.js" "$kit\tools\uploader\" -Force

$logFile = Join-Path $env:LOCALAPPDATA "VideoBatchDesktop\mesh-5parallel-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
function Write-Log($msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $msg
    Write-Host $line
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

$xml = [xml](Get-Content (Join-Path $env:LOCALAPPDATA "VideoBatchDesktop\settings.xml") -Raw)
$port = [int]$xml.Preferences.DolphinPort
$staggerMin = [int]$xml.Preferences.ProfileLaunchStaggerMinMs
$staggerMax = [int]$xml.Preferences.ProfileLaunchStaggerMaxMs
if ($staggerMin -lt 500) { $staggerMin = 3000 }
if ($staggerMax -lt $staggerMin) { $staggerMax = 5000 }

$token = [Text.Encoding]::UTF8.GetString(
    [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$xml.Preferences.ProtectedDolphinToken),
        $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser
    )
)

$authBody = '{"token":"' + ($token -replace '"', '\"') + '"}'
Invoke-RestMethod -Uri "http://127.0.0.1:$port/v1.0/auth/login-with-token" -Method Post -Body $authBody -ContentType "application/json" -TimeoutSec 15 | Out-Null
Write-Log "Dolphin auth OK · port $port · stagger ${staggerMin}-${staggerMax}ms"

$viewers = @(
    @{ profileId = "527453777"; name = "OMONXONFAN @OMONXONFAN55"; channelUrl = "https://www.youtube.com/@OMONXONFAN55/videos" },
    @{ profileId = "797494773"; name = "Omar Punjabi @bladefans95"; channelUrl = "https://www.youtube.com/@bladefans95/videos" },
    @{ profileId = "536875123"; name = "Mr. Smart Trader @Mr.SmartTrader"; channelUrl = "https://www.youtube.com/@Mr.SmartTrader/videos" },
    @{ profileId = "856164722"; name = "Leo Rich @Kooperfieldleo12"; channelUrl = "https://www.youtube.com/@Kooperfieldleo12/videos" },
    @{ profileId = "852114839"; name = "Jake Carter Trading"; channelUrl = "https://www.youtube.com/@fact_boom_24/videos" }
)

$targets = $viewers | ForEach-Object {
    @{
        channelUrl = $_.channelUrl
        ownerName = $_.name
        ownerProfileId = $_.profileId
        meshSingleLong = $true
        contentKind = "long"
    }
}

$todayDate = "2026-10-01"
$offsetMinutes = [int][DateTimeOffset]::Now.Offset.TotalMinutes
$node = Join-Path $kit "tools\uploader\node.exe"
$worker = Join-Path $kit "tools\uploader\worker.js"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$rng = New-Object System.Random

Write-Log "Mesh 5 parallel · todayDate=$todayDate · $($targets.Count) channels/profile · log=$logFile"

foreach ($v in $viewers) {
    try { Invoke-RestMethod -Uri "http://127.0.0.1:$port/v1.0/browser_profiles/$($v.profileId)/stop" -Method Get -TimeoutSec 10 | Out-Null } catch {}
}
Start-Sleep -Seconds 3

$processes = @()
$batchStart = 0
for ($index = 0; $index -lt $viewers.Count; $index++) {
    if ($index -gt 0) {
        $gap = $rng.Next($staggerMin, $staggerMax + 1)
        Write-Log "Stagger ${gap}ms before profile $($index + 1)/5"
        Start-Sleep -Milliseconds $gap
    }

    $viewer = $viewers[$index]
    $rotOffset = ($batchStart + $index) % $targets.Count
    $ordered = for ($i = 0; $i -lt $targets.Count; $i++) { $targets[($rotOffset + $i) % $targets.Count] }

    $job = @{
        localPort = $port
        profileId = $viewer.profileId
        todayDate = $todayDate
        pcUtcOffsetMinutes = $offsetMinutes
        watchMesh = $true
        skipQueueDelay = $true
        watchTargets = $ordered
    } | ConvertTo-Json -Depth 6 -Compress

    $jobPath = Join-Path $env:TEMP ("upload-mesh5-$($viewer.profileId)-" + [guid]::NewGuid().ToString("N") + ".json")
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
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    $psi.EnvironmentVariables["VIDEOBATCH_DOLPHIN_TOKEN"] = $token

    $p = [System.Diagnostics.Process]::Start($psi)
    $processes += [PSCustomObject]@{
        Process = $p
        JobPath = $jobPath
        ProfileId = $viewer.profileId
        Name = $viewer.name
        RotOffset = $rotOffset
    }
    Write-Log "Started PID $($p.Id) · $($viewer.name) ($($viewer.profileId)) · rotation offset $rotOffset"
}

Write-Log "All 5 workers launched · waiting for completion (5 channels each, full watch)..."

$results = @()
foreach ($entry in $processes) {
    $p = $entry.Process
    $success = $false
    $lastText = ""
    while (-not $p.HasExited) {
        while (($line = $p.StandardOutput.ReadLine()) -ne $null) {
            try {
                $msg = $line | ConvertFrom-Json
                $text = [string]$msg.text
                if ($text) {
                    $lastText = $text
                    Write-Log ("[$($entry.ProfileId)] [" + $msg.stage + "] " + $text)
                }
                if ($msg.stage -eq "done" -and $msg.success -eq $true) { $success = $true }
            } catch {
                Write-Log ("[$($entry.ProfileId)] [raw] $line")
            }
        }
        Start-Sleep -Milliseconds 400
    }
    while (($line = $p.StandardOutput.ReadLine()) -ne $null) {
        try {
            $msg = $line | ConvertFrom-Json
            if ($msg.text) {
                $lastText = $msg.text
                Write-Log ("[$($entry.ProfileId)] [" + $msg.stage + "] " + $msg.text)
            }
            if ($msg.stage -eq "done" -and $msg.success -eq $true) { $success = $true }
        } catch {
            Write-Log ("[$($entry.ProfileId)] [raw] $line")
        }
    }
    $stderr = $p.StandardError.ReadToEnd()
    if ($stderr) { Write-Log ("[$($entry.ProfileId)] STDERR: $stderr") }
    Remove-Item $entry.JobPath -Force -ErrorAction SilentlyContinue

    try {
        Invoke-RestMethod -Uri "http://127.0.0.1:$port/v1.0/browser_profiles/$($entry.ProfileId)/stop" -Method Get -TimeoutSec 30 | Out-Null
        Write-Log ("[$($entry.ProfileId)] Dolphin stop OK")
    } catch {
        Write-Log ("[$($entry.ProfileId)] Dolphin stop failed: $($_.Exception.Message)")
    }

    $results += [PSCustomObject]@{
        Name = $entry.Name
        ProfileId = $entry.ProfileId
        ExitCode = $p.ExitCode
        Success = $success
        LastText = $lastText
    }
    Write-Log ("[$($entry.ProfileId)] Finished exit=$($p.ExitCode) success=$success last=$lastText")
}

$ok = ($results | Where-Object { $_.Success -or $_.ExitCode -eq 0 }).Count
Write-Log "=== SUMMARY: $ok/5 profiles OK ==="
foreach ($r in $results) {
    Write-Log ("  $($r.Name) ($($r.ProfileId)): exit=$($r.ExitCode) success=$($r.Success)")
}

if ($ok -lt 5) { exit 1 }
exit 0
