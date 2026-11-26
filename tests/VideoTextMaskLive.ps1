param([string]$Assembly="$PSScriptRoot\..\_tmp\text-mask-release\VideoBatch.exe",[Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Plaque,[Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
[Reflection.Assembly]::LoadFrom((Resolve-Path $Assembly).Path) | Out-Null
[Threading.SynchronizationContext]::SetSynchronizationContext($null)
$ffmpeg=(Get-Command ffmpeg.exe).Source
$probe=Join-Path (Split-Path $ffmpeg) 'ffprobe.exe'
$sourcePath=(Resolve-Path $Source).Path
$outputPath=[IO.Path]::GetFullPath($Output)
$work=Join-Path (Split-Path $outputPath) 'live-text-mask-work'
[IO.Directory]::CreateDirectory($work) | Out-Null
$ct=[Threading.CancellationToken]::None
$media=[VideoBatch.Core]::VideoInfo([VideoBatch.Core]::Probe($probe,$sourcePath,$ct).GetAwaiter().GetResult())
$project=New-Object VideoBatch.VideoTranslationProject
$project.Source=$sourcePath; $project.Directory=$work; $project.Duration=$media.VideoDuration; $project.SourceHash=[VideoBatch.AssemblyFiles]::Hash($sourcePath)
$regions=[VideoBatch.VideoTextMask]::ScanAsync($project,$probe,$ct,$null).GetAwaiter().GetResult()
if ($regions.Count -eq 0) { throw 'Real text video produced no detections' }
[VideoBatch.VideoTextMask]::ApplyAsync($sourcePath,$outputPath,$ffmpeg,$regions,[string[]]@((Resolve-Path $Plaque).Path),(Join-Path $work 'raster'),$media.Width,$media.Height,$project.Duration,$ct,$null).GetAwaiter().GetResult()
$result=[VideoBatch.Core]::VideoInfo([VideoBatch.Core]::Probe($probe,$outputPath,$ct).GetAwaiter().GetResult())
if ([Math]::Abs($result.VideoDuration-$project.Duration) -gt .15) { throw 'Final duration mismatch' }
foreach ($time in @(.5,1.2,2.5,3.2)) {
    $frame=Join-Path $work 'pixel-check.png'
    & $ffmpeg -v error -y -ss $time -i $outputPath -frames:v 1 -threads 1 $frame
    if ($LASTEXITCODE -ne 0) { throw 'Cannot extract verification frame' }
    $image=[Drawing.Bitmap]::new($frame)
    try {
        $pixel=$image.GetPixel(150,270)
        $visible=$pixel.R -gt 180 -and $pixel.G -lt 50
        if ($visible -ne ($time -ge 1 -and $time -lt 3)) { throw ('Real detection/overlay timing wrong at '+$time) }
    } finally { $image.Dispose() }
}
Write-Output ('PASS real C# -> detector -> raster -> FFmpeg: regions={0}, duration={1}, output={2}' -f $regions.Count,$result.VideoDuration,$outputPath)
