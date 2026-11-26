$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path ([IO.Path]::GetTempPath()) ('VideoBatchInstagramQueue-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($folder) | Out-Null
try {
    # Only queue state logic is exercised. Shared source is an in-memory fixture, not an integration test.
    $source=@'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
namespace VideoBatch {
    public class Preferences {
        public List<InstagramQueueItem> InstagramQueue=new List<InstagramQueueItem>();
        public List<YouTubeItem> Sources=new List<YouTubeItem>();
    }
    public class YouTubeItem { public string StudioUploadId="",Video="",Title=""; }
    public static class SharedShortsQueue {
        public static List<YouTubeItem> ForProfile(Preferences settings,string id){return settings.Sources;}
    }
    public static class InstagramQueueTargeted {
        static void Check(bool condition,string text){if(!condition)throw new Exception(text);}
        public static int Main(){
            try {
                var p=new Preferences();
                p.Sources.Add(new YouTubeItem{StudioUploadId="short-1",Video=@"C:\fixture\missing.mp4",Title="Short"});
                Check(InstagramQueue.Sync(p,new[]{"101","101","202"}),"Initial import");
                Check(p.InstagramQueue.Count==2,"One receipt per profile");
                Check(!InstagramQueue.Sync(p,new[]{"101","202"}),"Sync must be idempotent");
                var first=InstagramQueue.ForProfile(p,"101").Single();
                Check(first.State=="ready"&&first.PublishedUrl=="","YouTube source is not Instagram success");
                Check(InstagramQueue.Status(first).Contains("\u041d\u0435\u0442"),"Missing source must be visible");
                Check(InstagramQueue.BlockUnverifiedPublishing(new[]{first}),"Block unverified publishing");
                Check(first.State=="blocked"&&first.PublishedUrl=="","Block must not publish");
                Check(!InstagramQueue.BlockUnverifiedPublishing(new[]{first}),"Repeated blocked click is idempotent");
                Check(p.InstagramQueue.Single(q=>q.ProfileId=="202").State=="ready","Another account state must remain independent");
                first.State="pending";first.Detail="Receipt uncertain";
                Check(!InstagramQueue.BlockUnverifiedPublishing(new[]{first})&&first.Detail=="Receipt uncertain","Never reset uncertain receipt");
                first.State="published";first.PublishedUrl="https://www.instagram.com/reel/fixture/";
                p.Sources[0].Title="Edited title";
                Check(InstagramQueue.Sync(p,new[]{"101"})&&first.State=="published"&&first.PublishedUrl.EndsWith("fixture/"),"Sync must preserve success receipt");
                p.Sources[0].StudioUploadId="another-source-id";
                InstagramQueue.Sync(p,new[]{"101"});
                Check(p.InstagramQueue.Count==2,"Same source file must not requeue after ID change");
                var serializer=new XmlSerializer(typeof(Preferences));
                using(var stream=new MemoryStream()){
                    serializer.Serialize(stream,p);stream.Position=0;
                    var restored=(Preferences)serializer.Deserialize(stream);
                    Check(restored.InstagramQueue[0].State=="published"&&restored.InstagramQueue[0].PublishedUrl==first.PublishedUrl,"Independent receipts persist through XML");
                }
                p.Sources.Clear();
                Check(InstagramQueue.ForProfile(p,"101").Count==0&&p.InstagramQueue.Count==2,"Hidden source must retain historical receipts");
                Console.WriteLine("PASS: Instagram queue profile isolation, idempotent sync, missing media, honest blocker, pending/published receipt preservation, XML roundtrip. Shared source fixture only; no network, WebView2, or publication tested.");return 0;
            }catch(Exception e){Console.WriteLine(e);return 1;}
        }
    }
}
'@
    $harness=Join-Path $folder 'Harness.cs'
    [IO.File]::WriteAllText($harness,$source)
    $sdk=(& dotnet --version).Trim()
    $compiler=Join-Path $env:ProgramFiles ('dotnet\sdk\'+$sdk+'\Roslyn\bincore\csc.dll')
    $framework=Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
    $exe=Join-Path $folder 'InstagramQueueTargeted.exe'
    & dotnet $compiler /nologo /target:exe /langversion:latest "/out:$exe" "/reference:$framework\mscorlib.dll" "/reference:$framework\System.dll" "/reference:$framework\System.Core.dll" "/reference:$framework\System.Xml.dll" (Join-Path $root 'src\InstagramQueue.cs') $harness
    if($LASTEXITCODE -ne 0){throw 'Instagram queue fixture compilation failed'}
    & $exe
    if($LASTEXITCODE -ne 0){throw 'Instagram queue fixtures failed'}
} finally { if([IO.Directory]::Exists($folder)){[IO.Directory]::Delete($folder,$true)} }
