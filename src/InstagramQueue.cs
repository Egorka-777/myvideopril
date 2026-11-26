using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoBatch {
    // Instagram owns these receipts; YouTube/TikTok publication state is never copied here.
    public sealed class InstagramQueueItem {
        public string ProfileId="",SourceId="",Video="",Title="";
        public string State="ready",Detail="",PublishedUrl="";
    }

    internal static class InstagramQueue {
        internal const string PublishBlocker="Автопубликация Instagram недоступна: шаги загрузки и подтверждение публикации в текущем WebView2 ещё не проверены. Файлы не отправлялись; статус «Опубликовано» не установлен.";

        internal static bool Sync(Preferences settings,IEnumerable<string> profileIds) {
            bool changed=false;
            if(settings.InstagramQueue==null){settings.InstagramQueue=new List<InstagramQueueItem>();changed=true;}
            foreach(string profileId in profileIds.Where(id=>!string.IsNullOrWhiteSpace(id)).Distinct()) {
                foreach(var source in SharedShortsQueue.ForProfile(settings,profileId)) {
                    string sourceId=source.StudioUploadId??"";
                    var row=settings.InstagramQueue.FirstOrDefault(q=>q.ProfileId==profileId&&
                        ((!string.IsNullOrWhiteSpace(sourceId)&&q.SourceId==sourceId)||SamePath(q.Video,source.Video)));
                    if(row==null) {
                        settings.InstagramQueue.Add(new InstagramQueueItem{ProfileId=profileId,SourceId=sourceId,Video=source.Video,Title=source.Title});changed=true;
                    } else {
                        // A receipt survives source metadata edits and subsequent synchronization.
                        if(row.Video!=source.Video||row.Title!=source.Title){row.Video=source.Video;row.Title=source.Title;changed=true;}
                    }
                }
            }
            return changed;
        }

        internal static List<InstagramQueueItem> ForProfile(Preferences settings,string profileId) {
            var sources=SharedShortsQueue.ForProfile(settings,profileId);
            return (settings.InstagramQueue??new List<InstagramQueueItem>()).Where(q=>q.ProfileId==profileId&&sources.Any(s=>
                (!string.IsNullOrWhiteSpace(s.StudioUploadId)&&s.StudioUploadId==q.SourceId)||SamePath(s.Video,q.Video))).ToList();
        }

        static bool SamePath(string left,string right) {
            return !string.IsNullOrWhiteSpace(left)&&!string.IsNullOrWhiteSpace(right)&&StringComparer.OrdinalIgnoreCase.Equals(left,right);
        }

        internal static bool BlockUnverifiedPublishing(IEnumerable<InstagramQueueItem> rows) {
            bool changed=false;
            foreach(var row in rows) {
                // Never clear an existing receipt or permit retry after an uncertain submission.
                if(row.State!="ready"&&row.State!="blocked")continue;
                if(row.State!="blocked"||row.Detail!=PublishBlocker){row.State="blocked";row.Detail=PublishBlocker;changed=true;}
            }
            return changed;
        }

        internal static string Status(InstagramQueueItem row) {
            if(row.State=="published")return "Опубликовано · Instagram";
            if(row.State=="submitting"||row.State=="pending")return "Нужна сверка · повтор запрещён";
            if(!File.Exists(row.Video))return "Нет исходного файла";
            if(row.State=="blocked")return "Публикация недоступна · шаги не проверены";
            return "В очереди · не отправлялось";
        }
    }
}
