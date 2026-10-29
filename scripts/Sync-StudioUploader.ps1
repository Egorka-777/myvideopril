[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Low')]
param(
    [string]$KitRoot='D:\VideoBatch\VideoBatch',
    [string]$NodePath='',
    [string]$ExpectedBuild='2026-10-23-studio-two-profiles-v21'
)
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this script on the Windows PC containing the installed kit.' }
$sourceRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$KitRoot=[IO.Path]::GetFullPath($KitRoot).TrimEnd('\','/')
$source=Join-Path $sourceRoot 'tools/uploader'
$dest=Join-Path $KitRoot 'tools/uploader'
if ($sourceRoot.TrimEnd('\','/') -eq $KitRoot) { throw 'Extract the update to a separate folder before installing.' }
if (!(Test-Path -LiteralPath $dest -PathType Container)) { throw "Installed uploader directory does not exist: $dest" }
foreach ($name in @('worker.js','youtube-studio-upload.js')) {
    if (![IO.File]::ReadAllText((Join-Path $source $name)).Contains($ExpectedBuild)) { throw "BUILD mismatch in source $name" }
}
$binaryRoot=$sourceRoot
# The repository also tracks a historical root EXE. Never prefer it to the build.
if (Test-Path -LiteralPath (Join-Path $sourceRoot 'VideoBatch.csproj')) { $binaryRoot=Join-Path $sourceRoot 'bin/Release/net48' }
$exe=Join-Path $binaryRoot 'VideoBatch.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Full update requires the compiled VideoBatch.exe. Build Release or download the CI update ZIP.' }
$assembly=[Reflection.Assembly]::LoadFrom($exe)
$protocolType=$assembly.GetType('VideoBatch.StudioUploadState')
if (!$protocolType -or $protocolType.GetField('ProtocolVersion').GetRawConstantValue() -ne 2) { throw 'Source EXE does not support Studio protocol 2. Do not install JS separately.' }
$running=@(Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq 'VideoBatch.exe' -and $_.ExecutablePath -and $_.ExecutablePath.StartsWith($KitRoot+'\',[StringComparison]::OrdinalIgnoreCase)) -or
    ($_.Name -eq 'node.exe' -and $_.CommandLine -and $_.CommandLine.IndexOf($dest,[StringComparison]::OrdinalIgnoreCase) -ge 0)
})
if ($running.Count) { throw 'Stop tasks and close VideoBatch before syncing. No process has been terminated.' }
if (!$NodePath) { $NodePath=Join-Path $dest 'node.exe' }
if (!(Test-Path -LiteralPath $NodePath -PathType Leaf)) { throw 'Node runtime was not found. Pass -NodePath to the installed node.exe.' }
$js=@(Get-ChildItem -LiteralPath $source -Filter '*.js' -File)
if ($js.Count -lt 4) { throw 'Source uploader package is incomplete.' }
foreach ($file in $js) {
    & $NodePath --check $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "JavaScript syntax error: $($file.Name)" }
}
$files=New-Object 'System.Collections.Generic.List[object]'
$files.Add(@{source=$exe;relative='VideoBatch.exe'})
foreach ($file in $js) { $files.Add(@{source=$file.FullName;relative=('tools/uploader/'+$file.Name)}) }
foreach ($file in @(Get-ChildItem -LiteralPath $binaryRoot -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -eq 'VideoBatch.exe.config' })) {
    $files.Add(@{source=$file.FullName;relative=$file.Name})
}
$runtimeRoot=Join-Path $binaryRoot 'runtimes'
if (Test-Path -LiteralPath $runtimeRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File) { $files.Add(@{source=$file.FullName;relative=$file.FullName.Substring($binaryRoot.Length+1)}) }
}
foreach ($name in @('Sync-StudioUploader.ps1','Collect-StudioDiagnostics.ps1')) {
    $file=Join-Path $PSScriptRoot $name
    if (!(Test-Path -LiteralPath $file)) { throw "Missing update script: $name" }
    $files.Add(@{source=$file;relative=('scripts/'+$name)})
}
if (!$PSCmdlet.ShouldProcess($KitRoot,"Back up and install EXE + $($js.Count) JS files as one Studio v20 package")) { return }
$stamp=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$backup=Join-Path $KitRoot ('backups/studio-'+$stamp)
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$relativePaths=@($files | ForEach-Object { $_.relative })+@('tools/uploader/STUDIO_UPLOADER_VERSION.json','SOURCE_COMMIT.txt')
$existing=@{}
foreach ($relative in $relativePaths) {
    $target=Join-Path $KitRoot $relative
    $existing[$relative]=Test-Path -LiteralPath $target
    if ($existing[$relative]) {
        $saved=Join-Path $backup $relative
        New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $saved
    }
}
try {
    $checks=@()
    foreach ($file in $files) {
        $target=Join-Path $KitRoot $file.relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.source -Destination $target -Force
        $want=(Get-FileHash -LiteralPath $file.source -Algorithm SHA256).Hash
        $got=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($want -ne $got) { throw "Hash mismatch after copy: $($file.relative)" }
        $checks+=@{file=$file.relative.Replace('\','/');sha256=$got.ToLowerInvariant()}
    }
    $commitFile=Join-Path $sourceRoot 'SOURCE_COMMIT.txt'
    $commit=if (Test-Path -LiteralPath $commitFile) { ([IO.File]::ReadAllText($commitFile)).Trim() } else { (& git -C $sourceRoot rev-parse HEAD 2>$null | Out-String).Trim() }
    [IO.File]::WriteAllText((Join-Path $KitRoot 'SOURCE_COMMIT.txt'),$commit)
    $checks+=@{file='SOURCE_COMMIT.txt';sha256=(Get-FileHash -LiteralPath (Join-Path $KitRoot 'SOURCE_COMMIT.txt') -Algorithm SHA256).Hash.ToLowerInvariant()}
    $manifest=@{build=$ExpectedBuild;protocolVersion=2;sourceCommit=$commit;syncedAt=(Get-Date).ToString('o');backup=$backup;files=$checks;liveAccountValidation='NOT_RUN'}
    $manifestPath=Join-Path $dest 'STUDIO_UPLOADER_VERSION.json'
    $tempManifest=$manifestPath+'.tmp'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tempManifest -Encoding UTF8
    Move-Item -LiteralPath $tempManifest -Destination $manifestPath -Force
    Write-Host "Installed full Studio package: $ExpectedBuild. Settings, media, sessions and Node runtime preserved. Live account validation: NOT_RUN."
    Write-Output $manifestPath
} catch {
    $failure=$_
    foreach ($relative in $relativePaths) {
        $target=Join-Path $KitRoot $relative
        if ($existing[$relative]) { Copy-Item -LiteralPath (Join-Path $backup $relative) -Destination $target -Force }
        elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    throw $failure
}
