$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$kit=Join-Path ([IO.Path]::GetTempPath()) ('vb-sync-'+[Guid]::NewGuid().ToString('N'))
$dest=Join-Path $kit 'tools/uploader'
try {
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $kit 'settings.xml'),'keep-settings')
    [IO.File]::WriteAllText((Join-Path $kit 'VideoBatch.exe'),'keep-exe')
    [IO.File]::WriteAllText((Join-Path $dest 'node.exe'),'keep-runtime')
    [IO.File]::WriteAllText((Join-Path $dest 'worker.js'),'old-worker')
    $node=(Get-Command node.exe).Source
    & (Join-Path $root 'scripts/Sync-StudioUploader.ps1') -KitRoot $kit -NodePath $node -WhatIf
    if ([IO.File]::ReadAllText((Join-Path $dest 'worker.js')) -ne 'old-worker') { throw 'WhatIf modified installed files' }
    & (Join-Path $root 'scripts/Sync-StudioUploader.ps1') -KitRoot $kit -NodePath $node | Out-Host
    $manifest=Get-Content -LiteralPath (Join-Path $dest 'STUDIO_UPLOADER_VERSION.json') -Raw | ConvertFrom-Json
    if ($manifest.build -ne '2026-10-21-studio-shorts-queue-v19f' -or $manifest.liveAccountValidation -ne 'NOT_RUN') { throw 'Invalid manifest' }
    if ([IO.File]::ReadAllText((Join-Path $manifest.backup 'worker.js')) -ne 'old-worker') { throw 'Backup missing' }
    foreach ($pair in @(@('settings.xml','keep-settings'),@('VideoBatch.exe','keep-exe'),@('tools/uploader/node.exe','keep-runtime'))) {
        if ([IO.File]::ReadAllText((Join-Path $kit $pair[0])) -ne $pair[1]) { throw 'Unrelated kit file changed' }
    }
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $dest $file.file) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw 'Installed hash mismatch' }
    }
    Write-Host 'PASS sync: WhatIf, backups, hashes, version manifest; EXE/settings/runtime preserved; no account actions.'
} finally { if (Test-Path -LiteralPath $kit) { Remove-Item -LiteralPath $kit -Recurse -Force } }
