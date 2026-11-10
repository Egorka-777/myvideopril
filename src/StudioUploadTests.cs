using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
namespace VideoBatch {
    public static class StudioUploadTests {
        static void Check(bool ok,string message){if(!ok)throw new Exception("Studio contract: "+message);}
        static void CheckStudioLaunchPolicy(){
            Check(ProfileLaunchGate.ResolveStudioMaxConcurrent(null)==3,"Studio default concurrency is three");
            foreach(int configured in new[]{-1,0,1,2,3,4,16}){
                var prefs=new Preferences{MaxParallelUploads=configured,ProfileLaunchStaggerMinMs=500,ProfileLaunchStaggerMaxMs=12000};
                int expected=configured<=0?3:Math.Min(3,configured);
                Check(ProfileLaunchGate.ResolveStudioMaxConcurrent(prefs)==expected,"Studio concurrency clamp: "+configured);
                var gate=ProfileLaunchGate.ForStudioUpload(prefs);
                const System.Reflection.BindingFlags fields=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                var running=(System.Threading.SemaphoreSlim)typeof(ProfileLaunchGate).GetField("_running",fields).GetValue(gate);
                Check(running.CurrentCount==expected,"Studio gate capacity: "+configured);
                Check((int)typeof(ProfileLaunchGate).GetField("_minGapMs",fields).GetValue(gate)==5000&&(int)typeof(ProfileLaunchGate).GetField("_maxGapMs",fields).GetValue(gate)==5000,"Studio fixed five-second gap ignores generic stagger settings");
                gate.EnterAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                try{
                    Check(running.CurrentCount==expected-1,"active Studio upload holds a slot");
                    using(var cancel=new System.Threading.CancellationTokenSource()){
                        var pending=gate.EnterAsync(cancel.Token);
                        Check(!pending.IsCompleted,"next Studio launch waits instead of starting immediately");
                        cancel.Cancel();
                        bool cancelled=false;try{pending.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
                        Check(cancelled&&running.CurrentCount==expected-1,"cancelled launch releases only its own slot");
                    }
                }finally{gate.Exit();}
                Check(running.CurrentCount==expected,"completed Studio upload releases its slot");
                running.Dispose();
            }
        }
        public static bool RunSelfTests(){
            string oldRoot=Store.Root,root=Path.Combine(Path.GetTempPath(),"vb-studio-contract-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                Store.Root=root;
                CheckStudioLaunchPolicy();
                var prefs=new Preferences{YouTubeDescriptionBankRu=new List<string>{"Описание первого варианта","Описание второго варианта"},YouTubeDescriptionBankEn=new List<string>{"First English description","Second English description"},YouTubeStudioPaidPromotion=false,YouTubeStudioAlteredContent=false};
                var channel=new YouTubeChannel{Name="Account RU",ProfileId="profile-1",Market="RU",ChannelUrl="https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv"};
                var batch=new PreparedProfileBatch{Channel=channel,ProfileId=channel.ProfileId};
                for(int i=0;i<5;i++){
                    var source=new YouTubeItem{Video=Path.Combine(root,"video "+i+".mp4"),Title="Мой заголовок "+i};File.WriteAllText(source.Video,"fixture");channel.Items.Add(source);
                    var at=DateTime.Now.AddDays(2).Date.AddHours(10+i);
                    batch.Items.Add(new PreparedUploadItem{Source=source,Index=i+1,Total=5,StagedVideo=source.Video,UploadTitle=source.Title,ScheduleDate=at.ToString("yyyy-MM-dd"),ScheduleTime=at.ToString("HH:mm")});
                }
                var job=batch.ToUploadJob("not-saved",3001,prefs);
                Check(job.studioProtocolVersion==2,"durable Studio protocol advertised by EXE");
                Check(job.items.Length==5,"five separate items");
                Check(!job.studioPaidPromotion&&!job.studioAlteredContent,"explicit No settings");
                Check(job.items.All(x=>!string.IsNullOrWhiteSpace(x.tags)&&x.description==null),"Studio leaves descriptions untouched");
                for(int i=0;i<5;i++){
                    var item=job.items[i];var slot=ScheduleGenerator.ParseSlot(item.scheduleDate,item.scheduleTime);
                    Check(item.description==null,"description is not sent for any item");
                    Check(item.video==channel.Items[i].Video,"original video path without copies");
                    Check(item.localJobId==channel.Items[i].StudioUploadId,"stable per-item id");
                    Check(item.scheduledUnixSeconds==new DateTimeOffset(slot).ToUnixTimeSeconds(),"PC instant");
                    Check(item.scheduledUtcOffsetMinutes==(int)TimeZoneInfo.Local.GetUtcOffset(slot).TotalMinutes,"offset at planned date");
                }
                Check(YouTubeMetadata.ShortsDescription("Ссылка в профиле\n\nПодпись\n#трейдинг")=="Подпись\n#трейдинг","RU profile header removed");
                Check(YouTubeMetadata.ShortsDescription("Link in bio\r\n\r\nCaption")=="Caption","EN profile header removed");
                Check(YouTubeMetadata.ShortsDescription("Обычная первая строка\nСсылка в профиле")=="Обычная первая строка\nСсылка в профиле","non-header text preserved");
                foreach(string market in new[]{"RU","EN"}) {
                    channel.Market=market;
                    batch.Items[0].Kind="shorts";
                    var mixed=batch.ToUploadJob("not-saved",3001,prefs);
                    Check(!string.IsNullOrWhiteSpace(mixed.items[0].description),"Shorts caption restored "+market);
                    Check(mixed.items.Skip(1).All(it=>it.description==null),"Long descriptions untouched in mixed batch");
                }
                channel.Market="RU";batch.Items[0].Kind="long";
                string keys=YouTubeWarmupPanel.NormalizeKeywords("  трейдинг\r\nbinodex\nBINODEX\n\n бинодекс ");
                Check(keys=="трейдинг\nbinodex\nбинодекс","warmup key normalization");
                prefs.YouTubeWarmupKeywordsRu=keys;prefs.YouTubeWarmupKeywordsEn="pocket option ai";Store.Save(prefs);
                var reloaded=Store.Load();
                Check(reloaded.YouTubeScheduleMode=="random"&&reloaded.YouTubeScheduleMinMinutes==2&&reloaded.YouTubeScheduleMaxMinutes==5&&reloaded.YouTubeScheduleLongMinMinutes==2&&reloaded.YouTubeScheduleLongMaxMinutes==5,"existing settings migrate to 2–5 minute scheduled publishing");
                Check(HttpWorkerSettings.ResolvePublishMode(reloaded,"shorts")=="scheduled"&&HttpWorkerSettings.ResolvePublishMode(reloaded,"long")=="scheduled","both HTTP kinds scheduled");
                reloaded.YouTubeScheduleLongMinMinutes=8;reloaded.YouTubeScheduleLongMaxMinutes=12;Store.Save(reloaded);
                var savedIntervals=Store.Load();
                Check(savedIntervals.YouTubeScheduleLongMinMinutes==8&&savedIntervals.YouTubeScheduleLongMaxMinutes==12,"custom Long interval survives restart");
                Check(YouTubeWarmupPanel.KeywordsFor(reloaded,"RU")==keys&&YouTubeWarmupPanel.KeywordsFor(reloaded,"EN")=="pocket option ai","warmup RU/EN keys survive restart");
                var serializer=new DataContractJsonSerializer(typeof(UploadJob));
                var warmup=new UploadJob{shortsWarmup=true,warmupKeywords=keys,warmupMinMinutes=10,warmupMaxMinutes=10,keepProfileOpen=true,warmupLikePercent=37,warmupLikesEnabled=true};
                using(var memory=new MemoryStream()){serializer.WriteObject(memory,warmup);memory.Position=0;var round=(UploadJob)serializer.ReadObject(memory);Check(round.warmupKeywords==keys&&round.warmupMinMinutes==10&&round.warmupMaxMinutes==10&&round.keepProfileOpen&&round.warmupLikePercent==37,"warmup settings wire roundtrip");}
                Console.WriteLine("Warmup: keyword normalization, restart persistence and wire settings PASS");
                using(var memory=new MemoryStream()){serializer.WriteObject(memory,job);string raw=Encoding.UTF8.GetString(memory.ToArray());Check(raw.Contains("\"scheduledUtcOffsetMinutes\"")&&raw.Contains("\"tags\""),"wire field names");memory.Position=0;var round=(UploadJob)serializer.ReadObject(memory);Check(round.items[4].description==job.items[4].description,"metadata serializer roundtrip");}
                var en=new YouTubeChannel{Market="EN",ProfileId="profile-en"};Check(YouTubeMetadata.Build(prefs,en,1).description.Contains("English"),"English description bank");
                prefs.YouTubeChannels=new List<YouTubeChannel>{channel};channel.Items[0].StudioPending=true;channel.Items[0].StudioVideoId="abcdefghijk";channel.Items[0].StudioPhase="editing";Store.Save(prefs);
                var loaded=Store.Load();Check(!StudioUploadState.WasSubmitted(loaded.YouTubeChannels[0].Items[0]),"draft with video id can resume transfer");Check(!StudioUploadState.WasSubmitted(loaded.YouTubeChannels[0].Items[2]),"next untouched video remains eligible");Check(StudioUploadState.WasSubmitted(new YouTubeItem{StudioPending=true}),"interrupted transfer without ID never repeats automatically");Check(loaded.YouTubeChannels[0].Items[0].StudioPending,"uncertain upload survives restart");Check(loaded.YouTubeChannels[0].Items[0].StudioUploadId==job.items[0].localJobId,"id survives restart");Check(loaded.YouTubeDescriptionBankEn.Count==2,"English bank survives restart");
                var retry=batch.ToUploadJob("not-saved",3001,prefs);
                Check(retry.items[0].studioPending&&retry.items[0].studioVideoId=="abcdefghijk"&&retry.items[0].studioPhase=="editing","pending server ID reaches worker without resetting state");
                using(var memory=new MemoryStream()){serializer.WriteObject(memory,retry);memory.Position=0;var round=(UploadJob)serializer.ReadObject(memory);Check(round.items[0].studioVideoId=="abcdefghijk"&&round.items[0].studioPending&&round.items[0].studioPhase=="editing"&&round.studioProtocolVersion==2,"pending server ID wire roundtrip");}
                var restarted=channel.Items[0];
                bool duplicateBlocked=false;try{StudioUploadState.Apply(restarted,new UploadMessage{stage="item_submitting",localJobId=restarted.StudioUploadId});}catch{duplicateBlocked=true;}
                Check(duplicateBlocked&&restarted.StudioVideoId=="abcdefghijk"&&restarted.StudioPending,"uncertain ID survives blocked duplicate transfer");
                var durable=channel.Items[1];
                foreach(var stage in new[]{"item_submitting","item_accepted","item_publishing","item_confirmed"}){
                    Check(StudioUploadState.RequiresAck(stage),"durable stage waits for host ack: "+stage);
                    var msg=new UploadMessage{stage=stage,localJobId=durable.StudioUploadId,videoId=stage=="item_submitting"?null:"safevideo01",url=stage=="item_confirmed"?"https://www.youtube.com/watch?v=safevideo01":null,visibility="public"};
                    Check(StudioUploadState.Apply(durable,msg),"applied "+stage);Store.Save(prefs);
                    var disk=Store.Load().YouTubeChannels[0].Items[1];
                    string phase=stage=="item_submitting"?"transferring":stage=="item_accepted"?"editing":stage=="item_publishing"?"publishing":"confirmed";
                    Check(disk.StudioPhase==phase&&disk.StudioPending==(stage!="item_confirmed"),"state is on disk before ack: "+stage);
                    if(stage!="item_submitting")Check(disk.StudioVideoId=="safevideo01","ID persists before ack");
                    if(stage=="item_confirmed")Check(disk.PublishedVideoId=="safevideo01"&&disk.StudioVisibility=="public","confirmed result persists");
                }
                foreach(var msg in new[]{new UploadMessage{stage="item_accepted",localJobId="other-job",videoId="safevideo01"},new UploadMessage{stage="item_accepted",localJobId=durable.StudioUploadId,videoId="othervideo1"},new UploadMessage{stage="item_pending_cleared",localJobId=durable.StudioUploadId}}){
                    bool rejected=false;try{StudioUploadState.Apply(durable,msg);}catch{rejected=true;}Check(rejected,"reject wrong job, changed ID or blind clear");
                }
                batch.Items[0].Kind="shorts";batch.Items[0].Source.Thumbnail=Path.Combine(root,"missing-thumbnail.png");
                var shortJob=batch.ToUploadJob("not-saved",3001,prefs);
                Check(shortJob.items[0].thumbnail==null&&shortJob.thumbnail==null,"Shorts do not send thumbnail images");
                Check(shortJob.items[0].publishMode=="immediate","unfinished known Studio ID resumes as public");
                var fresh=batch.Items[2].Source;
                Check(shortJob.items[2].publishMode=="scheduled","untouched video keeps scheduling");
                fresh.StudioRetryImmediate=true;Store.Save(prefs);
                Check(batch.ToUploadJob("not-saved",3001,prefs).items[2].publishMode=="immediate","failed batch remainder publishes without scheduling");
                fresh.StudioRetryImmediate=false;
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
                string preparedCopy=Path.Combine(root,"prepared-copy.mp4");File.WriteAllText(preparedCopy,"prepared");
                string keptBecauseAssigned=Path.Combine(root,"still-assigned.mp4");File.WriteAllText(keptBecauseAssigned,"shared");
                prefs.YouTubeChannels=new List<YouTubeChannel>{new YouTubeChannel{Items=new List<YouTubeItem>{new YouTubeItem{Video=keptBecauseAssigned}}}};
                string takenRoot=Path.Combine(root,"taken");
                var consumed=UploadPreparation.ConsumeSourcesDetailed(new[]{renamed,keptBecauseAssigned},new[]{preparedCopy},path=>UploadPreparation.IsAssignedVideo(prefs,path),takenRoot);
                Check(consumed.Removed==1&&consumed.Failed.Count==0&&consumed.KeptAssigned.Count==1,"taken source leaves the shared folder");
                Check(!File.Exists(renamed)&&File.Exists(keptBecauseAssigned)&&File.Exists(preparedCopy),"only unreferenced originals are removed");
                // Immediate claim: files leave the shared folder before preparation finishes.
                string pickA=Path.Combine(root,"pick-a.mp4");File.WriteAllText(pickA,"a");
                string pickB=Path.Combine(root,"pick-b.mp4");File.WriteAllText(pickB,"b");
                string claim=Path.Combine(root,"claimed");
                var claimed=UploadPreparation.ClaimFromSharedFolder(new[]{pickA,pickB},claim);
                Check(claimed.Count==2&&!File.Exists(pickA)&&!File.Exists(pickB),"picked files disappear from the shared folder immediately");
                Check(claimed.All(c=>File.Exists(c.ClaimedPath)),"claimed copies stay available for preparation");
                UploadPreparation.RestoreClaimed(claimed);
                Check(Directory.GetFiles(root,"pick-*.mp4").Length==2,"failed prepare can restore claimed originals");
                claimed=UploadPreparation.ClaimFromSharedFolder(Directory.GetFiles(root,"pick-*.mp4"),claim);
                Check(Directory.GetFiles(root,"pick-*.mp4").Length==0&&claimed.Count==2&&claimed.All(c=>File.Exists(c.ClaimedPath)),"successful prepare keeps claimed originals until upload");
                UploadPreparation.DiscardClaimed(claimed);
                Check(Directory.GetFiles(claim).Length==0,"explicit discard removes claimed originals");
                foreach(var stage in new[]{"studio_date","studio_zone","studio_time","metadata_title","metadata_description","metadata_options","metadata_tags","studio_steps","studio_schedule","studio_publish","studio_close","studio_checks","studio_verify"})
                    Check(TaskQueueStore.MapStage(stage)==TaskQueueStatus.Scheduling,"Studio phase remains active: "+stage);
                Check(TaskQueueStore.MapStage("upload_transfer")==TaskQueueStatus.Uploading,"upload transfer keeps active queue status");
                Check(YouTubeOwnerPublishTests.RunSelfTests(),"author publication contract");
                Console.WriteLine("Studio: five-item metadata + PC-time contract, durable pending state, XML and wire roundtrip PASS");return true;
            }finally{Store.Root=oldRoot;try{Directory.Delete(root,true);}catch{}}
        }
    }
}
