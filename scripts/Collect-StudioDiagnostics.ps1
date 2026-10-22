[CmdletBinding()]
param(
    [string]$KitRoot='D:\VideoBatch\VideoBatch',
    [string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop'),
    [int]$LastHours=24,
    [string]$OutputDirectory=[Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference='Stop'
if ($LastHours -lt 1 -or $LastHours -gt 720) { throw 'LastHours must be between 1 and 720.' }
$since=(Get-Date).AddHours(-$LastHours)
$stamp=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
$temp=Join-Path ([IO.Path]::GetTempPath()) ('VideoBatch-Studio-'+$stamp)
$zip=Join-Path $OutputDirectory ('VideoBatch-Studio-'+$stamp+'.zip')
$included=New-Object 'System.Collections.Generic.List[string]'
function Copy-Diagnostic($file,$relative) {
    $target=Join-Path $temp $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    # A running logger may append while the snapshot is being copied.
    $inputStream=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try { $outputStream=[IO.File]::Create($target); try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() } } finally { $inputStream.Dispose() }
    $included.Add($relative)
}
try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    if (Test-Path -LiteralPath $DataRoot) {
        foreach ($file in Get-ChildItem -LiteralPath $DataRoot -Filter 'youtube-*.log' -File | Where-Object LastWriteTime -ge $since) { Copy-Diagnostic $file.FullName ('logs/'+$file.Name) }
        $diagnostics=Join-Path $DataRoot 'diagnostics'
        if (Test-Path -LiteralPath $diagnostics) {
            foreach ($file in Get-ChildItem -LiteralPath $diagnostics -File | Where-Object { $_.LastWriteTime -ge $since -and ($_.Name -like 'app-*.jsonl' -or $_.Name -like 'studio-trace-*.jsonl' -or $_.Name -match '^youtube-studio-.*\.(json|png)$') }) { Copy-Diagnostic $file.FullName ('diagnostics/'+$file.Name) }
        }
    }
    foreach ($relative in @('SOURCE_COMMIT.txt','tools/uploader/STUDIO_UPLOADER_VERSION.json')) {
        $file=Join-Path $KitRoot $relative
        if (Test-Path -LiteralPath $file) { Copy-Diagnostic $file ('kit/'+$relative) }
    }
    $buildFiles=@()
    foreach ($relative in @('VideoBatch.exe','tools/uploader/worker.js','tools/uploader/youtube-studio-upload.js','tools/uploader/studio-editor-controls.js')) {
        $file=Join-Path $KitRoot $relative
        if (Test-Path -LiteralPath $file) { $buildFiles+=@{file=$relative;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant();modified=(Get-Item -LiteralPath $file).LastWriteTimeUtc.ToString('o')} }
    }
    @{collectedAt=(Get-Date).ToString('o');since=$since.ToString('o');timeZone=[TimeZoneInfo]::Local.Id;files=$included;installed=$buildFiles;settingsIncluded=$false;accountSessionsIncluded=$false} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $temp 'collection.json') -Encoding UTF8
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Compress-Archive -Path (Join-Path $temp '*') -DestinationPath $zip
    Write-Host 'Saved diagnostic snapshot locally. No upload or account action performed.'
    Write-Output $zip
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
