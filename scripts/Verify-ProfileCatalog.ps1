# Offline audit: settings.xml profile merge (no Dolphin, no upload)
$ErrorActionPreference = "Stop"
$settingsPath = Join-Path $env:LOCALAPPDATA "VideoBatchDesktop\settings.xml"
if (-not (Test-Path $settingsPath)) {
    Write-Host "FAIL: settings.xml not found at $settingsPath"
    exit 1
}

[xml]$doc = Get-Content $settingsPath -Encoding UTF8
$ytNodes = @()
if ($doc.Preferences.YouTubeChannels.YouTubeChannel) {
    $ytNodes = @($doc.Preferences.YouTubeChannels.YouTubeChannel)
}
$tkNodes = @()
if ($doc.Preferences.TikTokAccounts.TikTokAccount) {
    $tkNodes = @($doc.Preferences.TikTokAccounts.TikTokAccount)
}

$map = @{}
$orphanYt = 0
$orphanTk = 0
foreach ($ch in $ytNodes) {
    $profileId = ("$($ch.ProfileId)").Trim()
    if ([string]::IsNullOrWhiteSpace($profileId)) { $orphanYt++; continue }
    if (-not $map.ContainsKey($profileId)) { $map[$profileId] = @{ YouTube = 0; TikTok = 0 } }
    $map[$profileId].YouTube++
}
foreach ($acc in $tkNodes) {
    $profileId = ("$($acc.ProfileId)").Trim()
    if ([string]::IsNullOrWhiteSpace($profileId)) { $orphanTk++; continue }
    if (-not $map.ContainsKey($profileId)) { $map[$profileId] = @{ YouTube = 0; TikTok = 0 } }
    $map[$profileId].TikTok++
}

$merged = ($map.GetEnumerator() | Where-Object { $_.Value.YouTube -gt 0 -and $_.Value.TikTok -gt 0 }).Count

Write-Host "Settings: $settingsPath"
Write-Host "YouTube accounts: $($ytNodes.Count)"
Write-Host "TikTok accounts: $($tkNodes.Count)"
Write-Host "Unique Profile IDs: $($map.Count)"
Write-Host "Merged YT+TikTok profiles: $merged"
Write-Host "Orphans without Profile ID: YouTube=$orphanYt TikTok=$orphanTk"
Write-Host "OK"
