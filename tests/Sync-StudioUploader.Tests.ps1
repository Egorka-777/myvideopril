$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$kit=Join-Path ([IO.Path]::GetTempPath()) ('vb-sync-'+[Guid]::NewGuid().ToString('N'))
$dest=Join-Path $kit 'tools/uploader'
try {
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $kit 'settings.xml'),'keep-settings')
    [IO.File]::WriteAllText((Join-Path $kit 'VideoBatch.exe'),'old-exe')
    [IO.File]::WriteAllText((Join-Path $dest 'node.exe'),'keep-runtime')
    [IO.File]::WriteAllText((Join-Path $dest 'worker.js'),'old-worker')
    $node=(Get-Command node.exe).Source
    & (Join-Path $root 'scripts/Sync-StudioUploader.ps1') -KitRoot $kit -NodePath $node -WhatIf
    if ([IO.File]::ReadAllText((Join-Path $dest 'worker.js')) -ne 'old-worker') { throw 'WhatIf modified installed files' }
    & (Join-Path $root 'scripts/Sync-StudioUploader.ps1') -KitRoot $kit -NodePath $node | Out-Host
    $manifest=Get-Content -LiteralPath (Join-Path $dest 'STUDIO_UPLOADER_VERSION.json') -Raw | ConvertFrom-Json
    if ($manifest.build -ne '2026-10-22-studio-upload-recovery-v20' -or $manifest.protocolVersion -ne 2 -or $manifest.liveAccountValidation -ne 'NOT_RUN') { throw 'Invalid manifest' }
    if ([IO.File]::ReadAllText((Join-Path $manifest.backup 'tools/uploader/worker.js')) -ne 'old-worker') { throw 'Worker backup missing' }
    if ([IO.File]::ReadAllText((Join-Path $manifest.backup 'VideoBatch.exe')) -ne 'old-exe') { throw 'EXE backup missing' }
    foreach ($pair in @(@('settings.xml','keep-settings'),@('tools/uploader/node.exe','keep-runtime'))) {
        if ([IO.File]::ReadAllText((Join-Path $kit $pair[0])) -ne $pair[1]) { throw 'Unrelated kit file changed' }
    }
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $kit $file.file) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw 'Installed hash mismatch' }
    }
    $data=Join-Path $kit 'test-data';New-Item -ItemType Directory -Path "$data/diagnostics" -Force | Out-Null
    foreach($name in @('youtube-test.log','settings.xml')){[IO.File]::WriteAllText((Join-Path $data $name),'fixture')}
    foreach($name in @('app-test.jsonl','studio-trace-test.jsonl','youtube-studio-test.json','youtube-studio-test.png','session.json')){[IO.File]::WriteAllText((Join-Path $data ('diagnostics/'+$name)),'fixture')}
    $zip=& (Join-Path $root 'scripts/Collect-StudioDiagnostics.ps1') -KitRoot $kit -DataRoot $data -OutputDirectory $kit
    $unzip=Join-Path $kit 'collected';Expand-Archive -LiteralPath $zip -DestinationPath $unzip
    foreach($name in @('logs/youtube-test.log','diagnostics/studio-trace-test.jsonl','diagnostics/youtube-studio-test.png','collection.json')){if(!(Test-Path -LiteralPath (Join-Path $unzip $name))){throw "Collection missing $name"}}
    if(Get-ChildItem -LiteralPath $unzip -Recurse -File | Where-Object { $_.Name -in @('settings.xml','session.json','VideoBatch.exe') }){throw 'Collection included unrelated private files'}
    Write-Host 'PASS full EXE+JS sync: WhatIf, rollback backups, hashes, protocol manifest; settings/runtime preserved; diagnostic ZIP excludes settings/sessions; no account actions.'
} finally { if (Test-Path -LiteralPath $kit) { Remove-Item -LiteralPath $kit -Recurse -Force } }
