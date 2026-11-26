# Read-only live API evidence. Never writes or prints tokens, proxy credentials, or raw responses.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http,System.Security
$settingsPath = Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop\settings.xml'
[xml]$settings = [IO.File]::ReadAllText($settingsPath)
$token = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($settings.Preferences.ProtectedDolphinToken),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
$handler = New-Object Net.Http.HttpClientHandler
$handler.UseProxy = $false
$handler.AllowAutoRedirect = $false
$client = New-Object Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)
$client.DefaultRequestHeaders.Authorization = New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer',$token)
function Get-Dolphin([string]$path) {
    $response = $client.GetAsync('https://dolphin-anty-api.com'+$path).GetAwaiter().GetResult()
    try {
        if (-not $response.IsSuccessStatusCode) { throw ('Dolphin HTTP '+[int]$response.StatusCode) }
        return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
    } finally { $response.Dispose() }
}
function Fingerprint([string]$value) {
    if ($value -eq '') { return '' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))).Replace('-','').Substring(0,12)) } finally { $sha.Dispose() }
}
try {
    $folders = Get-Dolphin '/folders?limit=100&page=1'
    'FOLDER_SHAPE '+(($folders.PSObject.Properties.Name) -join ',')
    foreach ($f in $folders.data) { 'FOLDER '+(@{id=$f.id;name=$f.name;keys=@($f.PSObject.Properties.Name)} | ConvertTo-Json -Compress -Depth 3) }
    $profiles = @()
    for ($page=1;$page -le 100;$page++) {
        $body = Get-Dolphin ('/browser_profiles?limit=50&page='+$page)
        if ($page -eq 1) { 'LIST_SHAPE '+(($body.PSObject.Properties.Name) -join ',') }
        $profiles += @($body.data)
        if (-not $body.next_page_url -and (!$body.last_page -or $page -ge $body.last_page)) { break }
    }
    foreach ($p in $profiles) {
        $body = Get-Dolphin ('/browser_profiles/'+$p.id)
        $detail = if ($body.data) { $body.data } else { $body }
        $proxy=$detail.proxy
        $same = $p.proxy -and $p.proxy.id -eq $proxy.id -and $p.proxy.type -eq $proxy.type -and $p.proxy.host -eq $proxy.host -and $p.proxy.port -eq $proxy.port -and $p.proxy.login -ceq $proxy.login -and $p.proxy.password -ceq $proxy.password
        'PROFILE '+(@{id=$p.id;folder=$p.folder;tags=$p.tags;proxyId=$proxy.id;protocol=$proxy.type;host=$proxy.host;port=$proxy.port;loginHash=(Fingerprint $proxy.login);listProxyKeys=@($p.proxy.PSObject.Properties.Name);listProxyId=$p.proxy.id;listProxyIdField=$p.proxyId;listHost=$p.proxy.host;listPort=$p.proxy.port;listLoginPresent=($null -ne $p.proxy.login);listMatchesDetail=[bool]$same} | ConvertTo-Json -Compress -Depth 5)
    }
} finally { $token=$null;$client.Dispose();$handler.Dispose() }
