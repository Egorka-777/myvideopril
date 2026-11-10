using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
namespace VideoBatch {
    public static class HighlightsTests {
        public static bool RunWorkerExportTest(string python, string ffmpeg, string ffprobe) {
            string old=Store.Root, oldHome=Environment.GetEnvironmentVariable("VIDEOBATCH_HOME");
            string temp=Path.Combine(Path.GetTempPath(),"vb-cutter-worker-"+Guid.NewGuid().ToString("N"));
            try {
                Store.Root=Path.Combine(temp,"data");
                Environment.SetEnvironmentVariable("VIDEOBATCH_HOME",temp);
                Directory.CreateDirectory(Path.Combine(temp,"tools"));
                File.Copy(ffmpeg,Path.Combine(temp,"tools","ffmpeg.exe"));File.Copy(ffprobe,Path.Combine(temp,"tools","ffprobe.exe"));
                var folder=Path.Combine(Store.Root,"highlights");Directory.CreateDirectory(folder);
                Core.Tool(python,new[]{"-m","venv",Path.Combine(folder,"venv")},CancellationToken.None).GetAwaiter().GetResult();
                string source=Path.Combine(temp,"исходник с пробелом.mp4"),output=Path.Combine(temp,"результаты");
                Core.Tool(ffmpeg,new[]{"-v","error","-f","lavfi","-i","color=red:s=128x72:r=25:d=26","-c:v","libx264",source},CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(folder,"preferences.json"),new JavaScriptSerializer().Serialize(new HighlightsPreferences{Source=source}));
                using(var form=new Form{ClientSize=new Size(820,620)})using(var panel=new HighlightsPanel()) {
                    form.Controls.Add(panel);form.Show();Application.DoEvents();
                    var method=typeof(HighlightsPanel).GetMethod("Worker",BindingFlags.Instance|BindingFlags.NonPublic);
                    var clips=new List<HighlightClip>{new HighlightClip{start=2,end=24,title="Момент"}};
                    var task=(Task<HighlightsResult>)method.Invoke(panel,new object[]{"export",clips,output});
                    var deadline=DateTime.UtcNow.AddMinutes(2);
                    while(!task.IsCompleted) {Application.DoEvents();Thread.Sleep(10);if(DateTime.UtcNow>deadline)throw new Exception("C# → Python export timed out");}
                    var result=task.GetAwaiter().GetResult();
                    if(result.outputs.Count!=1||!File.Exists(result.outputs[0]))throw new Exception("C# did not read worker result");
                    if(Directory.GetFiles(Path.Combine(folder,"jobs")).Length!=0)throw new Exception("Temporary jobs were not removed");
                    form.Close();
                }
                Console.WriteLine("C# → UTF-8 Python job → real FFmpeg → result JSON → cleanup PASS");return true;
            } finally {Store.Root=old;Environment.SetEnvironmentVariable("VIDEOBATCH_HOME",oldHome);try{Directory.Delete(temp,true);}catch{}}
        }
        public static bool RunYouTubeProtocolTests() {
            var job=new UploadJob {highlightsSearch=true,sourceMode="sources",sourcePeriod="week",sourceDuration="short",sourceEntries=new[]{"https://www.youtube.com/@example"},todayDate="2026-11-10",pcUtcOffsetMinutes=420};
            string payload;
            using(var stream=new MemoryStream()) {
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(UploadJob)).WriteObject(stream,job);
                payload=System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
            var json=new JavaScriptSerializer();var values=json.Deserialize<Dictionary<string,object>>(payload);
            if(!(bool)values["highlightsSearch"]||(string)values["sourceMode"]!="sources"||(string)values["sourcePeriod"]!="week"||(string)values["sourceDuration"]!="short")throw new Exception("Discovery job contract lost");
            var expected=new List<HighlightSource>{new HighlightSource{videoId="abcdefghijk",title="Пример",url="https://www.youtube.com/watch?v=abcdefghijk",channel="Канал",viewCount=1234567,durationSeconds=600}};
            var messageJson=json.Serialize(new {stage="highlight_sources",sourcesJson=json.Serialize(expected)});
            UploadMessage message;
            using(var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(messageJson)))message=(UploadMessage)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(UploadMessage)).ReadObject(stream);
            var sources=json.Deserialize<List<HighlightSource>>(message.sourcesJson);
            if(sources.Count!=1||sources[0].title!="Пример"||sources[0].viewCount!=1234567||sources[0].durationSeconds!=600)throw new Exception("Discovery result contract lost");
            var result=json.Deserialize<HighlightBatchResult>("{\"status\":\"partial\",\"sources\":[{\"videoId\":\"abcdefghijk\",\"status\":\"success\",\"outputs\":[\"clip.mp4\"]},{\"videoId\":\"12345678901\",\"status\":\"error\",\"error\":\"HTTP 403\",\"outputs\":[]}]}");
            if(result.sources.Count(x=>x.status=="success")!=1||result.sources.Sum(x=>x.outputs.Count)!=1||result.sources[1].error!="HTTP 403")throw new Exception("Batch partial result contract lost");
            Console.WriteLine("Highlights C# contracts: discovery job/result UTF-8 and explicit partial batch PASS");return true;
        }
        public static bool RunYouTubeUiTests() {
            string old=Store.Root,temp=Path.Combine(Path.GetTempPath(),"vb-youtube-highlights-"+Guid.NewGuid().ToString("N"));
            try {
                Store.Root=temp;
                var settings=new Preferences();settings.YouTubeChannels.Add(new YouTubeChannel{Name="Тестовый профиль",ProfileId="12345"});
                using(var form=new Form{ClientSize=new Size(1020,760)})using(var panel=new HighlightsPanel(settings)) {
                    form.Controls.Add(panel);form.Show();Application.DoEvents();
                    Exception failure=null;bool inspected=false;
                    using(var timer=new System.Windows.Forms.Timer{Interval=100}) {
                        timer.Tick+=(s,e)=>{
                            var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text=="YouTube → горизонтальные вырезки");
                            if(dialog==null)return;timer.Stop();
                            try {
                                var combos=All(dialog).OfType<ComboBox>().ToList();
                                if(combos.Count!=4||combos[0].Items.Count!=2||combos[1].Items.Count!=1)throw new Exception("Source modes / connected profile missing");
                                if(combos[2].Text!="Сегодня"||combos[3].Text!="3–20 минут")throw new Exception("Wrong default discovery filters");
                                combos[2].SelectedIndex=1;combos[3].SelectedIndex=1;
                                if(combos[2].Text!="За неделю"||combos[3].Text!="Менее 3 минут")throw new Exception("Alternative discovery filters missing");
                                if(All(dialog).OfType<CheckedListBox>().Single().Items.Count!=0)throw new Exception("Unexpected automatic search");
                                if(All(dialog).OfType<Button>().Single(b=>b.Text=="Скачать и нарезать").Enabled)throw new Exception("Empty selection processing enabled");
                                var list=All(dialog).OfType<CheckedListBox>().Single();
                                list.Items.Add(new HighlightSource{videoId="abcdefghijk",title="Сегодняшнее видео",channel="Канал"},true);
                                if(list.CheckedItems.Count!=1||!list.Items[0].ToString().Contains("Сегодняшнее видео"))throw new Exception("Candidate selection lost");
                                combos[2].SelectedIndex=0;
                                if(list.Items.Count!=0)throw new Exception("Changed filters retained stale candidates");
                                foreach(var size in new[]{new Size(740,600),new Size(860,660)}) {
                                    dialog.Size=size;Application.DoEvents();
                                    foreach(var button in All(dialog).OfType<Button>().Where(b=>b.Visible)) {
                                        var bounds=dialog.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                                        if(bounds.Left<0||bounds.Right>dialog.ClientSize.Width||bounds.Top<0||bounds.Bottom>dialog.ClientSize.Height)throw new Exception("YouTube button clipped: "+button.Text);
                                    }
                                }
                                inspected=true;
                            }catch(Exception ex){failure=ex;}finally{dialog.Close();}
                        };
                        timer.Start();typeof(HighlightsPanel).GetMethod("ShowYouTube",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(panel,null);
                    }
                    if(failure!=null)throw failure;if(!inspected)throw new Exception("YouTube dialog not inspected");form.Close();
                }
                Console.WriteLine("YouTube Highlights UI: modes, connected profile, empty selection, compact/wide layout PASS");return true;
            }finally{Store.Root=old;try{Directory.Delete(temp,true);}catch{}}
        }
        static IEnumerable<Control> All(Control p) {foreach(Control c in p.Controls){yield return c;foreach(var x in All(c))yield return x;}}
        public static bool RunUiTests(string screenshots) {
            string old=Store.Root, temp=Path.Combine(Path.GetTempPath(),"vb-highlights-"+Guid.NewGuid().ToString("N"));
            try {
                Store.Root=temp;Directory.CreateDirectory(temp);Directory.CreateDirectory(screenshots);
                var folder=Path.Combine(temp,"highlights");Directory.CreateDirectory(folder);
                string source=Path.Combine(temp,"example.mp4");File.WriteAllText(source,"fixture; no worker invoked");
                File.WriteAllText(Path.Combine(folder,"last-result.json"),new JavaScriptSerializer().Serialize(new HighlightsResult{source=source,duration=120,clips=new List<HighlightClip>{new HighlightClip{start=12,end=38,title="Сильная мысль с объяснением",reason="Завершённое утверждение и его вывод",text="Пример расшифровки найденного момента. Границы можно уточнить перед сохранением.",kind="speech",score=89},new HighlightClip{start=60,end=90,title="Наблюдаемое событие",reason="Видны начало действия и результат",kind="visual",score=83}}}));
                using(var form=new Form{ClientSize=new Size(820,620)})using(var panel=new HighlightsPanel()) {
                    form.Controls.Add(panel);form.Show();Application.DoEvents();
                    if(All(panel).OfType<CheckBox>().Count()!=2)throw new Exception("Restored cards missing");
                    foreach(var size in new[]{new Size(820,620),new Size(1020,760)}) {
                        form.ClientSize=size;Application.DoEvents();
                        foreach(var button in All(panel).OfType<Button>().Where(b=>b.Visible)) {
                            var origin=panel.PointToClient(button.PointToScreen(Point.Empty));
                            if(origin.Y<0||origin.Y+button.Height>panel.Height||origin.X<0||origin.X+button.Width>panel.Width)throw new Exception("Button clipped: "+button.Text);
                        }
                        using(var bmp=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bmp,panel.ClientRectangle);bmp.Save(Path.Combine(screenshots,"highlights-"+size.Width+".png"));}
                    }
                    if(NavigationService.Title(NavSection.Highlights)!="Нарезчик")throw new Exception("Wrong navigation title");
                    if(panel.HasWork)throw new Exception("Unexpected worker on startup");
                    form.Close();
                }
                Console.WriteLine("Highlights UI: restored cards, navigation, buttons in compact/wide layout PASS");return true;
            } finally {Store.Root=old;try{Directory.Delete(temp,true);}catch{}}
        }
    }
}
