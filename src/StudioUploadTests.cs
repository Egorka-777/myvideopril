using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
namespace VideoBatch {
    public static class StudioUploadTests {
        static void Check(bool ok,string message){if(!ok)throw new Exception("Studio contract: "+message);}
        public static bool RunSelfTests(){
            string oldRoot=Store.Root,root=Path.Combine(Path.GetTempPath(),"vb-studio-contract-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                Store.Root=root;
                var prefs=new Preferences{YouTubeDescriptionBankRu=new List<string>{"Описание первого варианта","Описание второго варианта"},YouTubeDescriptionBankEn=new List<string>{"First English description","Second English description"},YouTubeStudioPaidPromotion=false,YouTubeStudioAlteredContent=false};
                var channel=new YouTubeChannel{Name="Account RU",ProfileId="profile-1",Market="RU",ChannelUrl="https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv"};
                var batch=new PreparedProfileBatch{Channel=channel,ProfileId=channel.ProfileId};
                for(int i=0;i<5;i++){
                    var source=new YouTubeItem{Video=Path.Combine(root,"video "+i+".mp4"),Title="Мой заголовок "+i};File.WriteAllText(source.Video,"fixture");channel.Items.Add(source);
                    var at=DateTime.Now.AddDays(2).Date.AddHours(10+i);
                    batch.Items.Add(new PreparedUploadItem{Source=source,Index=i+1,Total=5,StagedVideo=source.Video,UploadTitle=source.Title,ScheduleDate=at.ToString("yyyy-MM-dd"),ScheduleTime=at.ToString("HH:mm")});
                }
                var job=batch.ToUploadJob("not-saved",3001,prefs);
                Check(job.items.Length==5,"five separate items");
                Check(!job.studioPaidPromotion&&!job.studioAlteredContent,"explicit No settings");
                Check(job.items.All(x=>!string.IsNullOrWhiteSpace(x.tags)&&!string.IsNullOrWhiteSpace(x.description)),"Configured descriptions reach the Studio worker");
                for(int i=0;i<5;i++){
                    var item=job.items[i];var slot=ScheduleGenerator.ParseSlot(item.scheduleDate,item.scheduleTime);
                    Check(item.description==YouTubeMetadata.Build(prefs,channel,i+1).description,"description snapshot for each item");
                    Check(item.video==channel.Items[i].Video,"original video path without copies");
                    Check(item.localJobId==channel.Items[i].StudioUploadId,"stable per-item id");
                    Check(item.scheduledUnixSeconds==new DateTimeOffset(slot).ToUnixTimeSeconds(),"PC instant");
                    Check(item.scheduledUtcOffsetMinutes==(int)TimeZoneInfo.Local.GetUtcOffset(slot).TotalMinutes,"offset at planned date");
                }
                var serializer=new DataContractJsonSerializer(typeof(UploadJob));
                using(var memory=new MemoryStream()){serializer.WriteObject(memory,job);string raw=Encoding.UTF8.GetString(memory.ToArray());Check(raw.Contains("\"scheduledUtcOffsetMinutes\"")&&raw.Contains("\"tags\""),"wire field names");memory.Position=0;var round=(UploadJob)serializer.ReadObject(memory);Check(round.items[4].description==job.items[4].description,"metadata serializer roundtrip");}
                var en=new YouTubeChannel{Market="EN",ProfileId="profile-en"};Check(YouTubeMetadata.Build(prefs,en,1).description.Contains("English"),"English description bank");
                prefs.YouTubeChannels=new List<YouTubeChannel>{channel};channel.Items[0].StudioPending=true;channel.Items[0].StudioVideoId="abcdefghijk";Store.Save(prefs);
                var loaded=Store.Load();Check(loaded.YouTubeChannels[0].Items[0].StudioPending,"uncertain upload survives restart");Check(loaded.YouTubeChannels[0].Items[0].StudioUploadId==job.items[0].localJobId,"id survives restart");Check(loaded.YouTubeDescriptionBankEn.Count==2,"English bank survives restart");
                var retry=batch.ToUploadJob("not-saved",3001,prefs);
                Check(retry.items[0].studioPending&&retry.items[0].studioVideoId=="abcdefghijk","pending server ID reaches worker without resetting state");
                using(var memory=new MemoryStream()){serializer.WriteObject(memory,retry);memory.Position=0;var round=(UploadJob)serializer.ReadObject(memory);Check(round.items[0].studioVideoId=="abcdefghijk"&&round.items[0].studioPending,"pending server ID wire roundtrip");}
                batch.Items[0].Kind="shorts";batch.Items[0].Source.Thumbnail=Path.Combine(root,"missing-thumbnail.png");
                var shortJob=batch.ToUploadJob("not-saved",3001,prefs);
                Check(shortJob.items[0].thumbnail==null&&shortJob.thumbnail==null,"Shorts do not send thumbnail images");
                Check(shortJob.items[0].publishMode=="immediate"&&shortJob.items[0].scheduleDate==null&&shortJob.items[0].scheduleTime==null,"Shorts skip scheduling");
                batch.Items[0].Kind="long";batch.Items[0].Source.Thumbnail="";
                Check(TitleCleaner.RunSelfTests(),"clean title regressions");
                Check(TitleCleaner.CleanForUpload("001. 3 правила 2026 (2).mp4")=="3 правила 2026","strip service indices, keep meaningful numbers");
                Check(TitleCleaner.CleanForUpload("12.34 стратегия 2026")=="12.34 стратегия 2026","preserve decimal punctuation");
                var slotWithSeconds=ScheduleGenerator.MakeSlot(DateTime.Now.AddHours(2),DateTime.MinValue);
                Check(slotWithSeconds.time.Length==5&&slotWithSeconds.time[2]==':',"schedule slots use HH:mm for Studio worker");
                batch.Items[0].ScheduleTime="11:36:02";
                var normalizedJob=batch.ToUploadJob("not-saved",3001,prefs);
                Check(normalizedJob.items[0].scheduleTime=="11:36","ToUploadJob normalizes HH:mm:ss");
                string sourceVideo=Path.Combine(root,"original-001.mp4");File.WriteAllText(sourceVideo,"source bytes");
                string occupied=Path.Combine(root,"Новый заголовок.mp4");File.WriteAllText(occupied,"existing bytes");
                string renamed=UploadWindow.RenameVideoFile(sourceVideo,"Новый заголовок",1,1);
                Check(!File.Exists(sourceVideo)&&File.Exists(renamed),"source renamed immediately in its original directory");
                Check(Path.GetDirectoryName(renamed)==root,"rename does not create a staging copy");
                Check(File.ReadAllText(occupied)=="existing bytes"&&File.ReadAllText(renamed)=="source bytes","collision keeps both files intact");
                Check(UploadWindow.RenameVideoFile(renamed,"Новый заголовок",1,1)==renamed,"reselecting same title is idempotent");
                foreach(var stage in new[]{"studio_date","studio_zone","studio_time","metadata_title","metadata_description","metadata_options","metadata_tags","studio_steps","studio_schedule","studio_publish","studio_close","studio_verify"})
                    Check(TaskQueueStore.MapStage(stage)==TaskQueueStatus.Scheduling,"Studio phase remains active: "+stage);
                Check(TaskQueueStore.MapStage("upload_transfer")==TaskQueueStatus.Uploading,"upload transfer keeps active queue status");
                Check(YouTubeOwnerPublishTests.RunSelfTests(),"author publication contract");
                Console.WriteLine("Studio: five-item metadata + PC-time contract, durable pending state, XML and wire roundtrip PASS");return true;
            }finally{Store.Root=oldRoot;try{Directory.Delete(root,true);}catch{}}
        }
    }
}
