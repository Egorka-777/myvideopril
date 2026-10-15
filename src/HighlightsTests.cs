using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Web.Script.Serialization;
namespace VideoBatch {
    public static class HighlightsTests {
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
