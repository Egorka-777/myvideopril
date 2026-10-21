using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
namespace VideoBatch {
    public static class YouTubeOwnerPublishTests {
        static void Check(bool ok,string message){if(!ok)throw new Exception("Owner publish: "+message);}
        static void Reject(Action action){bool failed=false;try{action();}catch{failed=true;}Check(failed,"invalid action must fail");}
        public static bool RunSelfTests(){
            Check(YouTubeOwnerPublish.RenderComment("Link?start={ddMM}\n{date} {yyyy} {MM} {dd}",new DateTime(2028,2,29))=="Link?start=2902\n2028-02-29 2028 02 29","PC date, leap day, exact replacement");
            Check(YouTubeOwnerPublish.RenderComment("A\u00a0B",DateTime.Now)=="A B","copied nonbreaking spaces match browser text verification");
            Check(YouTubeOwnerPublish.RenderComment("Text 1910",new DateTime(2026,10,21))=="Text 1910","literal digits unchanged");Reject(()=>YouTubeOwnerPublish.RenderComment("{unknown}",DateTime.Now));
            var item=new YouTubeItem{Title="Example",PublishedVideoId="video000001",StudioVisibility="scheduled"};
            var channel=new YouTubeChannel{Name="Owner",ProfileId="profile-1",OwnerCommentTemplate="Account one {ddMM}",Items=new List<YouTubeItem>{item}};
            var second=new YouTubeChannel{Name="Second",ProfileId="profile-2",OwnerCommentTemplate="Account two {date}",Items=new List<YouTubeItem>{new YouTubeItem{Title="Second video",PublishedUrl="https://www.youtube.com/watch?v=video000002"}}};
            var candidates=YouTubeOwnerPublish.Candidates(new[]{channel,second},new DateTime(2026,10,21));Check(candidates.Count==2&&candidates[0].CommentText=="Account one 2110"&&candidates[1].CommentText=="Account two 2026-10-21","separate per-account templates");
            var choice=candidates[0];Reject(()=>YouTubeOwnerPublish.EnsureAttached(new Preferences(),choice));YouTubeOwnerPublish.EnsureAttached(new Preferences{YouTubeChannels=new List<YouTubeChannel>{channel}},choice);var job=YouTubeOwnerPublish.ToJob(choice);Check(job.video==null&&job.publishedVideoId==item.PublishedVideoId,"publish existing ID without source file");
            Reject(()=>YouTubeOwnerPublish.Apply(choice,new UploadMessage{stage="owner_video_public",videoId="video000002",localJobId=item.StudioUploadId}));Check(item.StudioVisibility=="scheduled","wrong video cannot change local state");
            YouTubeOwnerPublish.Apply(choice,new UploadMessage{stage="owner_video_public",videoId=choice.VideoId,localJobId=item.StudioUploadId});Check(item.StudioVisibility=="public","persisted access recorded before commenting");
            YouTubeOwnerPublish.Apply(choice,new UploadMessage{stage="owner_comment_submitting",videoId=choice.VideoId,localJobId=item.StudioUploadId,commentText=choice.CommentText});Check(item.OwnerCommentState=="pending","checkpoint protects uncertain click");Reject(()=>YouTubeOwnerPublish.ToJob(choice));
            string old=Store.Root,temp=Path.Combine(Path.GetTempPath(),"vb-owner-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
            try{
                Store.Root=temp;var prefs=new Preferences{YouTubeChannels=new List<YouTubeChannel>{channel,second}};Store.Save(prefs);var loaded=Store.Load();
                var durable=loaded.YouTubeChannels.First(c=>c.ProfileId==channel.ProfileId);Check(durable.OwnerCommentTemplate==channel.OwnerCommentTemplate,"template survives restart");Check(durable.Items[0].OwnerCommentState=="pending"&&durable.Items[0].OwnerCommentText==choice.CommentText,"uncertain submission survives restart");
                Reject(()=>YouTubeOwnerPublish.Apply(choice,new UploadMessage{stage="owner_comment_confirmed",videoId=choice.VideoId,localJobId=item.StudioUploadId,commentText=choice.CommentText}));
                YouTubeOwnerPublish.Apply(choice,new UploadMessage{stage="owner_comment_confirmed",videoId=choice.VideoId,localJobId=item.StudioUploadId,commentText=choice.CommentText,commentId="comment-one"});
                Check(YouTubeOwnerPublish.ToJob(choice).commentState=="confirmed","repeat skips confirmed comment");Store.Save(prefs);Check(Store.Load().YouTubeChannels.First(c=>c.ProfileId==channel.ProfileId).Items[0].OwnerCommentId=="comment-one","server confirmation survives restart");
                item.StudioPending=true;Check(!YouTubeOwnerPublish.Candidates(new[]{channel},DateTime.Now).Any(),"unconfirmed upload cannot be published by guess");
            }finally{Store.Root=old;try{Directory.Delete(temp,true);}catch{}}
            Console.WriteLine("Owner publish: per-account templates, PC date, saved IDs, durable uncertain state and exact response routing PASS");return true;
        }
        public static bool RunUiTests(string folder){
            Directory.CreateDirectory(folder);
            var channel=new YouTubeChannel{Name="Пример аккаунта RU",OwnerCommentTemplate="Текст автора\nДата: {ddMM}",ProfileId="fixture",Items=new List<YouTubeItem>{new YouTubeItem{Title="Пример загруженного ролика",PublishedVideoId="video000001",StudioVisibility="scheduled"}}};
            using(var template=new OwnerCommentTemplateDialog(channel)){Check(template.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TextBox>().Single(box=>!box.ReadOnly).Lines.Length==2,"LF-only template displays as two lines");Capture(template,Path.Combine(folder,"owner-comment-template.png"));}
            using(var publish=new OwnerPublishDialog(new Preferences(),new[]{channel})){Check(publish.PreviewText.Contains("video000001")&&publish.PreviewText.Contains("Текст автора"),"preview contains exact comment before publishing");Capture(publish,Path.Combine(folder,"owner-publish.png"));}
            Console.WriteLine("Owner publish: native dialogs capture and bounds PASS");return true;
        }
        static void Capture(Form form,string file){
            form.Show();Application.DoEvents();CheckBounds(form);using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(file);}form.Close();
        }
        static void CheckBounds(Control root){
            foreach(Control child in root.Controls){if(child.Visible){Check(child.Left>=0&&child.Top>=0&&child.Right<=root.ClientSize.Width+2&&child.Bottom<=root.ClientSize.Height+2,"control clipped: "+child.GetType().Name+" "+child.Text);CheckBounds(child);}}
        }
    }
}
