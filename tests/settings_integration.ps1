param([string]$OldExe)
$ErrorActionPreference='Stop'
$exe=Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'VideoBatch_Desktop/VideoBatch.exe'
$work=Join-Path ([IO.Path]::GetTempPath()) ('videobatch-settings-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
try {
 # Serialize a genuine v3 preferences object in another process.
 $oldScript=Join-Path $work 'old.ps1'
 [IO.File]::WriteAllText($oldScript,@'
param($Exe,$Root)
[void][Reflection.Assembly]::LoadFrom($Exe)
[VideoBatch.Store]::Root=$Root
$p=[VideoBatch.Preferences]::new();$p.Shorts=$false;$p.VideoCount=2;$p.Output='saved-output';$p.Music.Add('saved-music.mp3');$p.ProtectedKey='saved-protected-key'
$b=[VideoBatch.Binding]::new();$b.Video='saved-video.mp4';$b.Audio='saved-voice.wav';$b.Slot=2;$p.Narrations.Add($b)
[VideoBatch.Store]::Save($p)
'@)
 & /tmp/pwsh-videobatch/pwsh -NoProfile -File $oldScript -Exe $OldExe -Root $work
 if($LASTEXITCODE -ne 0){throw 'Old preferences creation failed'}
 [void][Reflection.Assembly]::LoadFrom($exe)
 [VideoBatch.Store]::Root=$work
 $p=[VideoBatch.Store]::Load()
 if($p.Profiles.Length -ne 10 -or $p.VideoCount -ne 2 -or -not $p.AutoProfiles -or $p.SettingsVersion -ne 1){throw 'Migration failed'}
 if($p.Output -ne 'saved-output' -or $p.Music[0] -ne 'saved-music.mp3' -or $p.ProtectedKey -ne 'saved-protected-key' -or $p.Narrations[0].Audio -ne 'saved-voice.wav'){throw 'Existing preferences lost'}
 if(-not [IO.File]::Exists([VideoBatch.Store]::Config+'.v3-backup')){throw 'Backup missing'}
 if($p.Profiles[0].CustomDate -eq $p.Profiles[1].CustomDate -or $p.Profiles[0].BitratePercent -eq $p.Profiles[1].BitratePercent){throw 'Profiles did not vary'}
 $p.BackgroundMusic.Add('background.mp3');$p.BackgroundDb=-11.5;$p.AutoProfiles=$false;$p.VideoCount=10;$p.Profiles[9].Crop=4.321
 [VideoBatch.Store]::Save($p)
 $q=[VideoBatch.Store]::Load();$q=[VideoBatch.Store]::Clone($q)
 if($q.AutoProfiles -or $q.VideoCount -ne 10 -or $q.BackgroundDb -ne -11.5 -or $q.BackgroundMusic[0] -ne 'background.mp3' -or $q.Profiles[9].Crop -ne 4.321){throw 'Manual preferences roundtrip failed'}
 'PASS: v3 migration preserves files, music, narration, key and output; backup exists; new background and 10 manual profiles survive save/load.'
}finally{Remove-Item -Recurse -Force $work}
