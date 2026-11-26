using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    public sealed class SharedShortsEntry {
        public string ProfileId="", Market="RU", SourceId="", Video="", Title="";
    }

    /// <summary>One prepared Shorts file, independent delivery state in each social queue.</summary>
    public static class SharedShortsQueue {
        public static void Capture(Preferences settings) {
            if(settings.SharedShorts==null)settings.SharedShorts=new List<SharedShortsEntry>();
            foreach(var channel in settings.YouTubeChannels??new List<YouTubeChannel>()) {
                if(channel==null||string.IsNullOrWhiteSpace(channel.ProfileId))continue;
                foreach(var item in channel.Items??new List<YouTubeItem>()) {
                    if(item==null||string.IsNullOrWhiteSpace(item.Video))continue;
                    if(string.IsNullOrWhiteSpace(item.ContentKind))item.ContentKind=channel.Kind;
                    if(!string.Equals(item.ContentKind,"shorts",StringComparison.OrdinalIgnoreCase))continue;
                    if(string.IsNullOrWhiteSpace(item.StudioUploadId))item.StudioUploadId=Guid.NewGuid().ToString("N");
                    var entry=settings.SharedShorts.FirstOrDefault(e=>e.ProfileId==channel.ProfileId.Trim()&&e.SourceId==item.StudioUploadId);
                    if(entry==null){entry=new SharedShortsEntry{ProfileId=channel.ProfileId.Trim(),SourceId=item.StudioUploadId};settings.SharedShorts.Add(entry);}
                    entry.Market=channel.Market;entry.Video=item.Video;entry.Title=item.Title;
                }
            }
        }
        public static List<YouTubeItem> ForProfile(Preferences settings,string profileId) {
            Capture(settings);
            return settings.SharedShorts.Where(e=>string.Equals(e.ProfileId,(profileId??"").Trim(),StringComparison.OrdinalIgnoreCase))
                .Select(e=>new YouTubeItem{StudioUploadId=e.SourceId,Video=e.Video,Title=e.Title}).ToList();
        }
        public static int SyncTikTok(Preferences settings,TikTokAccount account) {
            if(string.IsNullOrWhiteSpace(account?.ProfileId))return 0;
            if(account.Items==null)account.Items=new List<TikTokItem>();
            int added=0;
            foreach(var source in ForProfile(settings,account.ProfileId)) {
                var existing=account.Items.FirstOrDefault(i=>i!=null&&(i.SharedShortsSourceId==source.StudioUploadId||string.Equals(i.Video,source.Video,StringComparison.OrdinalIgnoreCase)));
                if(existing!=null){existing.SharedShortsSourceId=source.StudioUploadId;existing.Video=source.Video;continue;}
                bool en=string.Equals(account.Market,"EN",StringComparison.OrdinalIgnoreCase);
                int cursor=en?settings.TikTokCaptionCursorEn:settings.TikTokCaptionCursorRu;
                var bank=en?settings.TikTokDescriptionBankEn:settings.TikTokFullCaptionBankRu;
                string caption=bank!=null&&bank.Count>0?TikTokCaptionTemplates.Allocate(bank,1,ref cursor)[0]:source.Title;
                if(en)settings.TikTokCaptionCursorEn=cursor;else settings.TikTokCaptionCursorRu=cursor;
                account.Items.Add(new TikTokItem{SharedShortsSourceId=source.StudioUploadId,Video=source.Video,Caption=caption});added++;
            }
            if(added>0&&string.IsNullOrWhiteSpace(account.Video)){var first=account.Items.First();account.Video=first.Video;account.Caption=first.Caption;}
            return added;
        }
    }
}
