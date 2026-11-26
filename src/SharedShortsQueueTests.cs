using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace VideoBatch {
    public static class SharedShortsQueueTests {
        static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        public static bool RunSelfTests(){
            var shorts=new YouTubeItem{Video="short.mp4",Title="Short"};
            var longs=new YouTubeItem{Video="long.mp4",Title="Long",ContentKind="long"};
            var channel=new YouTubeChannel{ProfileId="p1",Kind="shorts",Items=new List<YouTubeItem>{shorts,longs}};
            var settings=new Preferences{YouTubeChannels=new List<YouTubeChannel>{channel}};
            var account=new TikTokAccount{ProfileId="p1"};
            Check(SharedShortsQueue.SyncTikTok(settings,account)==1,"Only Shorts must import");
            Check(account.Items.Single().Video==shorts.Video,"Prepared file should be reused");
            var snapshot=SharedShortsQueue.ForProfile(settings,"p1");snapshot[0].PublishedVideoId="other-network";
            Check(string.IsNullOrEmpty(shorts.PublishedVideoId),"Consumers must not share publication receipts");
            shorts.PublishedVideoId="youtube-id";account.Items[0].Published=true;
            channel.Kind="long";
            Check(SharedShortsQueue.SyncTikTok(settings,account)==0,"Repeated sync must not duplicate");
            Check(account.Items[0].Published,"Sync must preserve TikTok state");
            shorts.Video="renamed-short.mp4";
            Check(SharedShortsQueue.SyncTikTok(settings,account)==0&&account.Items[0].Video==shorts.Video,"Source path update must not reset publication state or duplicate");
            Check(SharedShortsQueue.SyncTikTok(settings,new TikTokAccount())==0,"Unlinked accounts must not import");
            Check(shorts.ContentKind=="shorts","Shorts identity must survive channel kind changes");
            Check(SharedShortsQueue.ForProfile(settings,"p2").Count==0,"Profiles must remain isolated");
            channel.Items=new List<YouTubeItem>{new YouTubeItem{Video="next.mp4",Title="Next",ContentKind="shorts"}};
            Check(SharedShortsQueue.SyncTikTok(settings,account)==1,"New batch must append once");
            Check(SharedShortsQueue.ForProfile(settings,"p1").Count==2,"Old Shorts survive YouTube batch replacement");
            Check(InstagramQueue.Sync(settings,new[]{"p1"}),"Instagram must import the same Shorts");
            var instagram=InstagramQueue.ForProfile(settings,"p1");
            Check(instagram.Count==2&&instagram.All(i=>i.State=="ready"),"Instagram state must be independent of YouTube/TikTok");
            instagram[0].State="published";
            Check(!InstagramQueue.Sync(settings,new[]{"p1"})&&instagram[0].State=="published","Instagram receipts must survive repeated sync");
            channel.Items.Clear();
            var serializer=new XmlSerializer(typeof(Preferences));
            using(var stream=new MemoryStream()){
                serializer.Serialize(stream,settings);stream.Position=0;
                var loaded=(Preferences)serializer.Deserialize(stream);
                Check(SharedShortsQueue.ForProfile(loaded,"p1").Count==2,"Shared queue survives settings reload");
            }
            return true;
        }
    }
}
