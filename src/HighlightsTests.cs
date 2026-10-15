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
