$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

$kit = "D:\VideoBatch\repo-worktrees\native-studio-20261001\artifacts\Desktop-20261001-20261002-090818"
$settingsPath = Join-Path $env:LOCALAPPDATA "VideoBatchDesktop\settings.xml"
$logDir = Join-Path $env:LOCALAPPDATA "VideoBatchDesktop"
$logFile = Join-Path $logDir ("mesh-test-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")

function Write-Log($msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $msg
    Write-Host $line
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

$xml = [xml](Get-Content $settingsPath -Raw)
$port = [int]$xml.Preferences.DolphinPort
$protected = [string]$xml.Preferences.ProtectedDolphinToken
if ([string]::IsNullOrWhiteSpace($protected)) { throw "Dolphin token missing in settings." }
$token = [Text.Encoding]::UTF8.GetString(
    [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($protected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
)
if ([string]::IsNullOrWhiteSpace($token)) { throw "Failed to decrypt Dolphin token." }

Write-Log "Kit: $kit"
Write-Log "Dolphin port: $port"

$authBody = '{"token":"' + ($token -replace '"','\"') + '"}'
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:$port/v1.0/auth/login-with-token" -Method Post -Body $authBody -ContentType "application/json" -TimeoutSec 15 | Out-Null
    Write-Log "Dolphin auth OK"
} catch {
    throw "Dolphin auth failed: $($_.Exception.Message)"
}

$today = Get-Date -Format "yyyy-MM-dd"
$offset = [int][DateTimeOffset]::Now.Offset.TotalMinutes
$profileId = "852114839"
$targets = @(
    @{
        channelUrl = "https://www.youtube.com/@fact_boom_24/videos"
        ownerName = "Jake Carter Trading"
        ownerProfileId = "852114839"
        meshSingleLong = $true
        contentKind = "long"
    },
    @{
        channelUrl = "https://www.youtube.com/@filmrezzzka/videos"
        ownerName = "Lucky Tom"
        ownerProfileId = "836981176"
        meshSingleLong = $true
        contentKind = "long"
    }
)

$job = @{
    localPort = $port
    profileId = $profileId
    todayDate = $today
    pcUtcOffsetMinutes = $offset
    watchMesh = $true
    skipQueueDelay = $true
    watchTargets = $targets
} | ConvertTo-Json -Depth 6 -Compress

$jobPath = Join-Path $env:TEMP ("upload-mesh-test-" + [guid]::NewGuid().ToString("N") + ".json")
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($jobPath, $job, $utf8NoBom)
Write-Log "Job: $jobPath · profile $profileId · $($targets.Count) channels · today $today"

$node = Join-Path $kit "tools\uploader\node.exe"
$worker = Join-Path $kit "tools\uploader\worker.js"
$env:VIDEOBATCH_DOLPHIN_TOKEN = $token

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
Write-Log "Worker PID $($p.Id) started"

while (-not $p.HasExited) {
    while (($line = $p.StandardOutput.ReadLine()) -ne $null) {
        try {
            $msg = $line | ConvertFrom-Json
            $stage = [string]$msg.stage
            $text = [string]$msg.text
            if ($text) { Write-Log ("[$stage] $text") }
        } catch {
            Write-Log ("[raw] $line")
        }
    }
    Start-Sleep -Milliseconds 500
}

$stderr = $p.StandardError.ReadToEnd()
if ($stderr) { Write-Log "STDERR: $stderr" }
Write-Log "Exit code: $($p.ExitCode)"
Remove-Item $jobPath -Force -ErrorAction SilentlyContinue

if ($p.ExitCode -ne 0) { exit $p.ExitCode }
