param([int]$Count,[string]$Output)
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'VideoBatch_Desktop/VideoBatch.exe'))
$profiles=[VideoBatch.Profile]::Automatic($Count)
$ser=[Runtime.Serialization.Json.DataContractJsonSerializer]::new([VideoBatch.Profile[]])
$stream=[IO.File]::Create($Output)
try{$ser.WriteObject($stream,$profiles)}finally{$stream.Dispose()}
