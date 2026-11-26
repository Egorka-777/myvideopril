param([string]$Assembly="$PSScriptRoot\..\_tmp\text-mask-build\VideoBatch.exe",[switch]$Integration)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing,System.Windows.Forms
$loaded=[Reflection.Assembly]::LoadFrom((Resolve-Path $Assembly).Path)
if (![VideoBatch.VideoTranslationTests]::RunSelfTests()) { throw 'Translation self-tests failed' }
Write-Output 'PASS translation and mask unit tests'
$oldRoot=[VideoBatch.Store]::Root
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('vb-mask-ui-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    [VideoBatch.Store]::Root=$testRoot
    $prefs=New-Object VideoBatch.Preferences
    $type=$loaded.GetType('VideoBatch.VideoTranslationWindow')
    $constructor=$type.GetConstructor([type[]]@([VideoBatch.Preferences],[bool]))
    $form=$constructor.Invoke([object[]]@($prefs.PSObject.BaseObject,[bool]$false))
    try {
        $check=$form.Controls.Find('TranslationMaskVideoText',$true)
        $scan=$form.Controls.Find('TranslationScanVideoText',$true)
        if ($check.Count -ne 1 -or $scan.Count -ne 1) { throw 'Mask UI controls missing' }
        $check[0].Checked=$true
        $type.GetMethod('ReadOptions',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($form,$null)
        if (!$prefs.TranslationOptions.MaskVideoText) { throw 'Checkbox did not persist' }
        $prefs.TranslationOptions.TextMaskImages.Add('C:\fixture\plaque.png')
        $serializer=[Xml.Serialization.XmlSerializer]::new([VideoBatch.Preferences])
        $writer=[IO.StringWriter]::new()
        try { $serializer.Serialize($writer,$prefs); $xml=$writer.ToString() } finally { $writer.Dispose() }
        $reader=[IO.StringReader]::new($xml)
        try { $restored=$serializer.Deserialize($reader) } finally { $reader.Dispose() }
        if (!$restored.TranslationOptions.MaskVideoText -or $restored.TranslationOptions.TextMaskImages[0] -ne 'C:\fixture\plaque.png') { throw 'Mask XML preferences roundtrip failed' }
        Write-Output 'PASS mask checkbox, scan button, settings persistence in actual form'
    } finally { $form.Dispose() }
} finally {
    [VideoBatch.Store]::Root=$oldRoot
    [IO.Directory]::Delete($testRoot,$true)
}
if ($Integration) {
    [Threading.SynchronizationContext]::SetSynchronizationContext($null)
    $ffmpeg=(Get-Command ffmpeg.exe).Source
    $probe=Join-Path (Split-Path $ffmpeg) 'ffprobe.exe'
    $output=Join-Path $PSScriptRoot ('..\_tmp\text-mask-integration-'+[guid]::NewGuid().ToString('N').Substring(0,8))
    if (![VideoBatch.VideoTranslationTests]::RunIntegrationTests($ffmpeg,$probe,$output)) { throw 'Translation integration tests failed' }
    Write-Output 'PASS real FFmpeg mask and translation regression integration'
}
