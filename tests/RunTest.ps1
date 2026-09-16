param([string]$JobPath,[string]$Kind='video')
$ErrorActionPreference='Stop'
$exe=Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'VideoBatch_Desktop/VideoBatch.exe'
[void][Reflection.Assembly]::LoadFrom($exe)
$type=if($Kind -eq 'audio'){[VideoBatch.AudioJob]}else{[VideoBatch.BatchJob]}
$serializer=[Runtime.Serialization.Json.DataContractJsonSerializer]::new($type)
$stream=[IO.File]::OpenRead($JobPath)
try{$job=$serializer.ReadObject($stream)}finally{$stream.Dispose()}
$ct=[Threading.CancellationTokenSource]::new()
$progress=[Progress[VideoBatch.Update]]::new()
$work=Split-Path $JobPath -Parent
try{
 $task=if($Kind -eq 'audio'){[VideoBatch.AudioEngine]::Run($job,$progress,$ct.Token)}else{[VideoBatch.Core]::Run($job,$progress,$ct.Token)}
 [IO.File]::WriteAllText((Join-Path $work 'progress.txt'),'running')
 while(-not $task.IsCompleted){if([IO.File]::Exists((Join-Path $work 'cancel'))){$ct.Cancel()};Start-Sleep -Milliseconds 100}
 if($Kind -eq 'audio'){$result=[VideoBatch.BatchResult]::new();$result.Outputs.Add($task.GetAwaiter().GetResult())}else{$result=$task.GetAwaiter().GetResult()}
}catch{
 $result=[VideoBatch.BatchResult]::new()
 if($ct.IsCancellationRequested){$result.Cancelled=$true}else{$result.Errors.Add($_.Exception.Message)}
}finally{$ct.Dispose()}
[IO.File]::WriteAllText((Join-Path $work 'result.json'),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
