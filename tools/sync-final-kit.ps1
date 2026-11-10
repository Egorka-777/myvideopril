# Sync EXE, uploader and integrity manifest together after a Release build.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$kit = Join-Path (Split-Path $repo -Parent) 'VideoBatch'
& (Join-Path $repo 'scripts\Sync-StudioUploader.ps1') -KitRoot $kit
