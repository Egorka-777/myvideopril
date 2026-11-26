param([string]$Assembly="$PSScriptRoot\..\_tmp\session-cookie-build\VideoBatch.exe",[switch]$LiveEvidence)
$ErrorActionPreference='Stop'
$assemblyPath=(Resolve-Path $Assembly).Path
$loaded=[Reflection.Assembly]::LoadFrom($assemblyPath)
if ($LiveEvidence) {
    # Read exactly one existing profile. No saves, cookie values, token or response payload output.
    $stream=[IO.File]::OpenRead([VideoBatch.Store]::Config)
    try { $prefs=([Xml.Serialization.XmlSerializer]::new([VideoBatch.Preferences])).Deserialize($stream) } finally { $stream.Dispose() }
    $flags=[Reflection.BindingFlags]'Static,Instance,Public,NonPublic'
    $token=[VideoBatch.WindowsSupport]::Unprotect($prefs.ProtectedDolphinToken)
    $profile=@($prefs.YouTubeChannels | Where-Object { $_.ProfileId } | Select-Object -First 1)[0]
    if (!$profile) { throw 'No existing YouTube profile available for read-only evidence' }
    $providerType=$loaded.GetType('VideoBatch.DolphinCookieSessionProvider')
    $provider=[Activator]::CreateInstance($providerType,$flags,$null,[object[]]@(,$null),$null)
    try {
        $task=$providerType.GetMethod('ReadAsync',$flags).Invoke($provider,[object[]]@($token,$profile.ProfileId,[Threading.CancellationToken]::None))
        $rows=$task.GetAwaiter().GetResult()
        foreach ($service in @('YouTube','TikTok','Instagram','Threads')) {
            $r=$rows[$service]
            $authorized=$r.GetType().GetField('Authorized',$flags).GetValue($r)
            $detail=$r.GetType().GetField('Detail',$flags).GetValue($r)
            $expires=$r.GetType().GetField('ExpiresUtc',$flags).GetValue($r)
            Write-Output ("{0}: saved={1}; expiry={2}; {3}" -f $service,$authorized,$expires,$detail)
        }
    } finally { $provider.Dispose();$token=$null;$prefs=$null }
} else {
    if (![VideoBatch.DashboardSessionTests]::RunSelfTests()) { throw 'Session self-tests failed' }
    Write-Output 'PASS: HTTP contract, service/domain auth cookie classification, expiry, saved/online distinction, unknown/error preservation, legacy records, one export/four services, batch exclusion and dispose cancellation; no live sessions/settings modified.'
}
