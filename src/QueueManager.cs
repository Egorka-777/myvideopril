using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class PreparedUploadItem {
        public YouTubeItem Source;
        public int Index;
        public int Total;
        public string StagedVideo;
        public string ScheduleDate;
        public string ScheduleTime;
        public string UploadTitle;
    }

    public sealed class PreparedProfileBatch {
        public DataGridViewRow Row;
        public YouTubeChannel Channel;
        public string ProfileId;
        public List<PreparedUploadItem> Items=new List<PreparedUploadItem>();

        public UploadJob ToUploadJob(string token,int localPort){
            var first=Items[0];
            return new UploadJob{
                token=token,
                localPort=localPort,
                profileId=Channel.ProfileId,
                expectedIp=Channel.ExpectedIp,
                items=Items.Select(it=>new UploadItemJob{
                    video=it.StagedVideo,
                    title=it.UploadTitle,
                    thumbnail=it.Source.Thumbnail,
                    scheduleDate=it.ScheduleDate,
                    scheduleTime=it.ScheduleTime,
                    publishedVideoId=it.Source.PublishedVideoId??"",
                    publishedUrl=it.Source.PublishedUrl??""
                }).ToArray(),
                skipQueueDelay=true,
                checkOnly=false,
                draftOnly=false,
                video=first.StagedVideo,
                title=first.UploadTitle,
                thumbnail=first.Source.Thumbnail,
                scheduleDate=first.ScheduleDate,
                scheduleTime=first.ScheduleTime
            };
        }
    }

    /// <summary>Слой подготовки: канал → профиль → видео → заголовок → время → UploadJob.</summary>
    public static class QueueManager {
        public static void ValidateBeforeStart(
            IList<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)> jobs,
            Func<YouTubeChannel,YouTubeItem,int,int,string> plannedFileName){
            if(jobs==null||jobs.Count==0)throw new Exception("Нет готовых роликов: выберите канал и добавьте видео.");
            var seenPerProfile=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach(var job in jobs){
                string ctx=job.ch.Name+" ["+job.index+"/"+job.total+"]";
                if(string.IsNullOrWhiteSpace(job.ch.ProfileId))throw new Exception(job.ch.Name+": вставьте Profile ID из Dolphin.");
                if(string.IsNullOrWhiteSpace(NormTitle(job.item.Title)))throw new Exception(ctx+": введите заголовок.");
                if(string.IsNullOrWhiteSpace(job.item.Video)||!File.Exists(job.item.Video))throw new Exception(ctx+": файл видео не найден.");
                if(!string.IsNullOrWhiteSpace(job.item.Thumbnail)&&!File.Exists(job.item.Thumbnail))throw new Exception(ctx+": файл превью не найден.");
                string uploadTitle=TitleCleaner.CleanForUpload(NormTitle(job.item.Title));
                if(string.IsNullOrWhiteSpace(uploadTitle))throw new Exception(ctx+": пустой заголовок после очистки.");
                string planned=plannedFileName(job.ch,job.item,job.index,job.total);
                string pid=(job.ch.ProfileId??"").Trim();
                if(!seenPerProfile.TryGetValue(pid,out var seen)){
                    seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    seenPerProfile[pid]=seen;
                }
                EnsureUniquePlannedName(seen,planned,job.index);
            }
        }

        /// <summary>Same profile + same video file — keep one row (duplicate selection / merged channels).</summary>
        static List<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)> DeduplicateJobs(
            IList<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)> jobs){
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list=new List<(DataGridViewRow,YouTubeChannel,YouTubeItem,int,int)>();
            foreach(var job in jobs){
                string key=(job.ch.ProfileId??"").Trim()+"|"+(job.item.Video??"").Trim();
                if(!seen.Add(key))continue;
                list.Add(job);
            }
            return list;
        }

        static string EnsureUniquePlannedName(HashSet<string> seen,string planned,int index){
            if(seen.Add(planned))return planned;
            string ext=Path.GetExtension(planned);
            string stem=Path.GetFileNameWithoutExtension(planned);
            for(int n=2;n<100;n++){
                string candidate=stem+" ("+n+")"+ext;
                if(seen.Add(candidate))return candidate;
            }
            string fallback=stem+" ["+index+"]"+ext;
            seen.Add(fallback);
            return fallback;
        }

        static List<string> ResolveDuplicateUploadTitles(IList<string> titles){
            var outList=new List<string>(titles?.Count??0);
            var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<(titles?.Count??0);i++){
                string raw=(titles[i]??"").Trim();
                if(string.IsNullOrWhiteSpace(raw)){outList.Add(raw);continue;}
                if(used.Add(raw)){outList.Add(raw);continue;}
                bool placed=false;
                for(int n=2;n<50;n++){
                    string alt=AppendDuplicateSuffix(raw,n);
                    if(used.Add(alt)){outList.Add(alt);placed=true;break;}
                }
                if(!placed){
                    string alt=AppendDuplicateSuffix(raw,i+2);
                    used.Add(alt);
                    outList.Add(alt);
                }
            }
            return outList;
        }

        static string AppendDuplicateSuffix(string title,int n){
            string suffix=" ("+n+")";
            string baseTitle=Regex.Replace((title??"").Trim(),@"\s*\(\d+\)\s*$","").Trim();
            if(baseTitle.Length+suffix.Length>100)baseTitle=baseTitle.Substring(0,Math.Max(1,100-suffix.Length)).Trim();
            return baseTitle+suffix;
        }

        public static List<PreparedProfileBatch> Prepare(
            IList<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)> jobs,
            Func<string,string,int,int,string,string,string> stageFile,
            Func<YouTubeChannel,YouTubeItem,int,int,string> plannedFileName,
            Func<string,int,int,string> cleanTitleForUpload,
            Preferences schedulePrefs=null){
            jobs=DeduplicateJobs(jobs);
            ValidateBeforeStart(jobs,plannedFileName);
            var grouped=new Dictionary<string,List<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)>>(StringComparer.OrdinalIgnoreCase);
            var profileOrder=new List<string>();
            foreach(var j in jobs){
                string pid=(j.ch.ProfileId??"").Trim();
                if(!grouped.TryGetValue(pid,out var list)){
                    list=new List<(DataGridViewRow,YouTubeChannel,YouTubeItem,int,int)>();
                    grouped[pid]=list;
                    profileOrder.Add(pid);
                }
                list.Add(j);
            }
            var byProfile=new List<(List<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)> list,DataGridViewRow row,YouTubeChannel ch,string pid)>();
            foreach(var pid in profileOrder){
                var list=grouped[pid].OrderBy(x=>x.index).ToList();
                byProfile.Add((list,list[0].row,list[0].ch,pid));
            }
            var batches=new List<PreparedProfileBatch>();
            foreach(var g in byProfile){
                var batch=new PreparedProfileBatch{Row=g.row,Channel=g.ch,ProfileId=g.pid};
                var uploadTitles=ResolveDuplicateUploadTitles(
                    g.list.Select(j=>cleanTitleForUpload(j.item.Title,j.index,j.total)).ToList());
                var seenStagedNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for(int ti=0;ti<g.list.Count;ti++){
                    var j=g.list[ti];
                    string planned=plannedFileName(j.ch,j.item,j.index,j.total);
                    planned=EnsureUniquePlannedName(seenStagedNames,planned,j.index);
                    string staged=stageFile(j.item.Video,g.pid,j.index,j.total,planned,uploadTitles[ti]);
                    if(!File.Exists(staged))throw new Exception(j.ch.Name+" ["+j.index+"/"+j.total+"]: staging не удался.");
                    batch.Items.Add(new PreparedUploadItem{
                        Source=j.item,
                        Index=j.index,
                        Total=j.total,
                        StagedVideo=staged,
                        UploadTitle=uploadTitles[ti]
                    });
                }
                batches.Add(batch);
            }
            ScheduleGenerator.AssignCrossBatchSchedule(batches,schedulePrefs);
            ScheduleGenerator.EnsureValidYouTubeSchedule(batches,schedulePrefs);
            return batches;
        }

        static readonly Random ShuffleRng=new Random();

        public static List<PreparedProfileBatch> ShuffleBatches(IList<PreparedProfileBatch> batches){
            if(batches==null||batches.Count<=1)return batches?.ToList()??new List<PreparedProfileBatch>();
            var list=batches.ToList();
            for(int i=list.Count-1;i>0;i--){
                int j=ShuffleRng.Next(i+1);
                var tmp=list[i];list[i]=list[j];list[j]=tmp;
            }
            return list;
        }

        /// <summary>2–12 sec random stagger before starting another Dolphin profile.</summary>
        public static int InterProfileStartDelayMs(){
            return ShuffleRng.Next(2,13)*1000;
        }

        static string NormTitle(string title){
            if(string.IsNullOrWhiteSpace(title))return "";
            return System.Text.RegularExpressions.Regex.Replace(title.Trim(),@"\s*[·•]\s*\+\d+\s*$","").Trim();
        }
    }
}
