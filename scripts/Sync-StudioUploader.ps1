[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Low')]
param(
    [string]$KitRoot='D:\VideoBatch\VideoBatch',
    [string]$NodePath='',
    [string]$ExpectedBuild='2026-10-22-studio-shorts-queue-v19g'
)
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this script on the Windows PC containing the installed kit.' }
$sourceRoot=Split-Path $PSScriptRoot -Parent
$source=Join-Path $sourceRoot 'tools/uploader'
$dest=Join-Path $KitRoot 'tools/uploader'
if (!(Test-Path -LiteralPath $dest -PathType Container)) { throw "Installed uploader directory does not exist: $dest" }
foreach ($name in @('worker.js','youtube-studio-upload.js')) {
    $text=[IO.File]::ReadAllText((Join-Path $source $name))
    if (!$text.Contains($ExpectedBuild)) { throw "BUILD mismatch in source $name" }
}
# Do not replace files used by running jobs or terminate browsers/accounts.
$running=@(Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq 'VideoBatch.exe' -and $_.ExecutablePath -and $_.ExecutablePath.StartsWith($KitRoot,[StringComparison]::OrdinalIgnoreCase)) -or
    ($_.Name -eq 'node.exe' -and $_.CommandLine -and $_.CommandLine.IndexOf($dest,[StringComparison]::OrdinalIgnoreCase) -ge 0)
})
if ($running.Count) { throw 'Stop tasks and close VideoBatch before syncing. No process has been terminated.' }
if (!$NodePath) { $NodePath=Join-Path $dest 'node.exe' }
if (!(Test-Path -LiteralPath $NodePath -PathType Leaf)) { throw 'Node runtime was not found. Pass -NodePath to the installed node.exe.' }
$files=@(Get-ChildItem -LiteralPath $source -Filter '*.js' -File)
if ($files.Count -lt 4) { throw 'Source uploader package is incomplete.' }
foreach ($file in $files) {
    & $NodePath --check $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "JavaScript syntax error: $($file.Name)" }
}
if (!$PSCmdlet.ShouldProcess($dest,"Back up and sync $($files.Count) uploader JS files; keep EXE, node.exe, node_modules and user data")) { return }
$stamp=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$backup=Join-Path $KitRoot ('backups/uploader-'+$stamp)
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$existing=@{}
foreach ($file in $files) {
    $target=Join-Path $dest $file.Name
    $existing[$file.Name]=Test-Path -LiteralPath $target
    if ($existing[$file.Name]) { Copy-Item -LiteralPath $target -Destination (Join-Path $backup $file.Name) }
}
try {
    $checks=@()
    foreach ($file in $files) {
        $target=Join-Path $dest $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $want=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $got=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($want -ne $got) { throw "Hash mismatch after copy: $($file.Name)" }
        $checks+=@{file=$file.Name;sha256=$got.ToLowerInvariant()}
    }
    $commitFile=Join-Path $sourceRoot 'SOURCE_COMMIT.txt'
    $commit=if (Test-Path -LiteralPath $commitFile) { ([IO.File]::ReadAllText($commitFile)).Trim() } else { (& git -C $sourceRoot rev-parse HEAD 2>$null | Out-String).Trim() }
    $manifest=@{build=$ExpectedBuild;sourceCommit=$commit;syncedAt=(Get-Date).ToString('o');backup=$backup;files=$checks;liveAccountValidation='NOT_RUN'}
    $manifestPath=Join-Path $dest 'STUDIO_UPLOADER_VERSION.json'
    $tempManifest=$manifestPath+'.tmp'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tempManifest -Encoding UTF8
    Move-Item -LiteralPath $tempManifest -Destination $manifestPath -Force
    Write-Host "Uploader synced: $ExpectedBuild. Live account validation has NOT been run."
    Write-Output $manifestPath
} catch {
    $failure=$_
    foreach ($file in $files) {
        $target=Join-Path $dest $file.Name
        if ($existing[$file.Name]) { Copy-Item -LiteralPath (Join-Path $backup $file.Name) -Destination $target -Force }
        elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    throw $failure
}
