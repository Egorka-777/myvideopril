using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                if(!seen.Add(planned))throw new Exception(ctx+": дубликат имени в этом профиле «"+planned+"».");
            }
        }

        public static List<PreparedProfileBatch> Prepare(
            IList<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)> jobs,
            Func<string,string,int,string> stageFile,
            Func<YouTubeChannel,YouTubeItem,int,int,string> plannedFileName,
            Func<string,int,int,string> cleanTitleForUpload){
            ValidateBeforeStart(jobs,plannedFileName);
            string statePath=Path.Combine(Store.Root,"youtube-schedule.json");
            var byProfile=jobs
                .GroupBy(j=>(j.ch.ProfileId??"").Trim(),StringComparer.OrdinalIgnoreCase)
                .Select(g=>new{list=g.OrderBy(x=>x.index).ToList(),row=g.First().row,ch=g.First().ch,pid=g.Key})
                .ToList();
            var counts=byProfile.Select(g=>g.list.Count).ToList();
            var scheduleSlots=ScheduleGenerator.GenerateForProfileBatches(counts,statePath);
            int slotCursor=0;
            var batches=new List<PreparedProfileBatch>();
            foreach(var g in byProfile){
                var batch=new PreparedProfileBatch{Row=g.row,Channel=g.ch,ProfileId=g.pid};
                foreach(var j in g.list){
                    string staged=stageFile(j.item.Video,g.pid,j.index);
                    if(!File.Exists(staged))throw new Exception(j.ch.Name+" ["+j.index+"/"+j.total+"]: staging не удался.");
                    if(slotCursor>=scheduleSlots.Count)throw new Exception("Не хватило слотов расписания.");
                    var sched=scheduleSlots[slotCursor++];
                    batch.Items.Add(new PreparedUploadItem{
                        Source=j.item,
                        Index=j.index,
                        Total=j.total,
                        StagedVideo=staged,
                        ScheduleDate=sched.date,
                        ScheduleTime=sched.time,
                        UploadTitle=cleanTitleForUpload(j.item.Title,j.index,j.total)
                    });
                }
                batches.Add(batch);
            }
            return batches;
        }

        static string NormTitle(string title){
            if(string.IsNullOrWhiteSpace(title))return "";
            return System.Text.RegularExpressions.Regex.Replace(title.Trim(),@"\s*[·•]\s*\+\d+\s*$","").Trim();
        }
    }
}
