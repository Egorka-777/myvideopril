using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class HighlightClip {
        public double start { get; set; }
        public double end { get; set; }
        public string title { get; set; }
        public string reason { get; set; }
        public string text { get; set; }
        public string kind { get; set; }
        public double score { get; set; }
    }
    public sealed class HighlightsResult {
        public string source { get; set; }
        public string fingerprint { get; set; }
        public double duration { get; set; }
        public List<HighlightClip> clips { get; set; } = new List<HighlightClip>();
        public List<string> outputs { get; set; } = new List<string>();
    }
    public sealed class HighlightsPreferences {
        public string Source { get; set; } = "";
        public int Mode { get; set; }
        public int Count { get; set; } = 10;
        public string Output { get; set; } = "";
    }
    public sealed class HighlightsPanel : UserControl {
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64*1024*1024 };
        readonly string root = Path.Combine(Store.Root,"highlights");
        readonly HighlightsPreferences prefs;
        readonly Label sourceLabel, status;
        readonly Button load, find, setup, stop, export, results;
        readonly ComboBox mode;
        readonly NumericUpDown count;
        readonly ProgressBar progress;
        readonly FlowLayoutPanel cards;
        readonly FlowLayoutPanel toolbar;
        readonly List<Tuple<CheckBox, HighlightClip, NumericUpDown, NumericUpDown>> selections = new List<Tuple<CheckBox, HighlightClip, NumericUpDown, NumericUpDown>>();
        HighlightsResult analysis;
        Process active;
        string cancellation;
        bool busy, stopping;
        public bool HasWork => busy;
        public HighlightsPanel() {
            Dock = DockStyle.Fill; BackColor = Theme.Background; ForeColor = Theme.TextPrimary; Font = Theme.FontBody;
            Directory.CreateDirectory(root);
            try { prefs = json.Deserialize<HighlightsPreferences>(File.ReadAllText(Path.Combine(root,"preferences.json"))); } catch { }
            prefs = prefs ?? new HighlightsPreferences();
            var layout = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=4, Margin=Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,76));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,74));
            Controls.Add(layout);
            var intro = new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown, WrapContents=false, Margin=Padding.Empty };
            intro.Controls.Add(new Label {Text="Сильные моменты из длинного видео", AutoSize=true, Font=Theme.FontCardTitle, Margin=new Padding(0,4,0,8)});
            intro.Controls.Add(new Label {Text="Законченные мысли, цитаты и события · 20–40 секунд · обработка на вашем ПК", AutoSize=true, ForeColor=Theme.TextSecondary});
            layout.Controls.Add(intro,0,0);
            toolbar = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=true, Margin=Padding.Empty };
            load=Theme.MakeButton("＋ Видео",action:ChooseSource);
            sourceLabel = new Label {AutoSize=false, Width=300, Height=36, AutoEllipsis=true, TextAlign=ContentAlignment.MiddleLeft, Text=string.IsNullOrEmpty(prefs.Source)?"Выберите длинное видео":Path.GetFileName(prefs.Source)};
            mode=Theme.MakeCombo(new[]{"Авто: речь и события","Сильные мысли и цитаты","События без речи"}); mode.Width=224; mode.SelectedIndex=Math.Max(0,Math.Min(2,prefs.Mode));
            count = Number(1,50,Math.Max(1,Math.Min(50,prefs.Count))); count.Width=58;
            find=Theme.MakeButton("Найти моменты",accent:true); find.Click+=async(s,e)=>await Search();
            setup=Theme.MakeButton("Подготовка",ghost:true,action:ShowSetup);
            toolbar.Controls.Add(load); toolbar.Controls.Add(sourceLabel); toolbar.Controls.Add(mode);
            toolbar.Controls.Add(new Label {Text="До",AutoSize=true,Margin=new Padding(4,9,4,0)}); toolbar.Controls.Add(count); toolbar.Controls.Add(find); toolbar.Controls.Add(setup);
            layout.Controls.Add(toolbar,0,1);
            cards=new FlowLayoutPanel {Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown, WrapContents=false, AutoScroll=true, Margin=Padding.Empty};
            cards.SizeChanged+=(s,e)=>{foreach(Control c in cards.Controls)c.Width=Math.Max(250,cards.ClientSize.Width-26);};
            layout.Controls.Add(cards,0,2);
            var footer=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute,28));footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            status=new Label {Dock=DockStyle.Fill,Text="Первый запуск? Подготовка → Подготовить. Затем выберите видео.", AutoEllipsis=true,ForeColor=Theme.TextSecondary,TextAlign=ContentAlignment.MiddleLeft};
            progress=new ProgressBar {Dock=DockStyle.Top,Height=4,Visible=false,Margin=new Padding(0,18,12,0)};
            stop=Theme.MakeButton("Остановить",ghost:true,action:CancelWork);stop.Enabled=false;stop.Visible=false;
            export=Theme.MakeButton("Сохранить выбранное",accent:true);export.Enabled=false;export.Click+=async(s,e)=>await Export();
            results=Theme.MakeButton("Результаты",ghost:true,action:()=>OpenFolder(prefs.Output));results.Enabled=Directory.Exists(prefs.Output);
            var actions=new FlowLayoutPanel {AutoSize=true,WrapContents=false,Dock=DockStyle.Fill};actions.Controls.Add(stop);actions.Controls.Add(export);actions.Controls.Add(results);
            footer.Controls.Add(status,0,0);footer.SetColumnSpan(status,2);footer.Controls.Add(progress,0,1);footer.Controls.Add(actions,1,1);
            layout.Controls.Add(footer,0,3);
            var last=Path.Combine(root,"last-result.json");
            try { analysis=json.Deserialize<HighlightsResult>(File.ReadAllText(last)); if(analysis?.clips?.Count>0 && File.Exists(analysis.source)){prefs.Source=analysis.source;sourceLabel.Text=Path.GetFileName(prefs.Source);DrawCards();status.Text="Сохранённый поиск: "+analysis.clips.Count+". Проверьте границы и сохраните выбранное.";} } catch { }
        }
        static NumericUpDown Number(decimal min, decimal max, decimal value) {
            return new NumericUpDown {Minimum=min,Maximum=max,Value=value,BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.FixedSingle,Height=30,Margin=new Padding(0,6,8,0)};
        }
        void Save() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"preferences.json"),json.Serialize(prefs),new UTF8Encoding(false)); }
        void ChooseSource() {
            using(var dialog=new OpenFileDialog {Filter="Видео|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v|Все файлы|*.*"})if(dialog.ShowDialog(this)==DialogResult.OK){
                prefs.Source=dialog.FileName;sourceLabel.Text=Path.GetFileName(dialog.FileName);analysis=null;ClearCards();export.Enabled=false;Save();
            }
        }
        string Scripts() {
            foreach(var candidate in AppPaths.CandidateInstallRoots()) {var dir=Path.Combine(candidate,"tools","highlights");if(File.Exists(Path.Combine(dir,"worker.py")))return dir;}
            throw new Exception("Не найдена папка tools/highlights рядом с приложением. Агент должен скопировать её из новой ветки.");
        }
        void SetBusy(bool value) {
            busy=value;stopping=false;toolbar.Enabled=!value;cards.Enabled=!value;export.Enabled=!value&&analysis?.clips?.Count>0;stop.Enabled=value;stop.Visible=value;results.Enabled=!value&&Directory.Exists(prefs.Output);progress.Visible=value;
            progress.Style=value?ProgressBarStyle.Marquee:ProgressBarStyle.Continuous;if(!value)progress.Value=0;
        }
        void OnUi(Action action) {if(!IsDisposed && IsHandleCreated)try{BeginInvoke(action);}catch(InvalidOperationException){}}
        void ProgressLine(string line) {
            try {
                var record=json.Deserialize<Dictionary<string,object>>(line);
                string message=Convert.ToString(record["message"]), stage=Convert.ToString(record["stage"]);
                OnUi(()=>{status.Text=message;if(record.ContainsKey("percent")&&record["percent"]!=null){progress.Style=ProgressBarStyle.Continuous;progress.Value=Math.Max(0,Math.Min(100,Convert.ToInt32(record["percent"])));}});
                AppDiagnostics.Write("Нарезка","","highlights",stage,message,stage=="error"?"error":"info");
            }catch {AppDiagnostics.Write("Нарезка","","highlights","worker",line);}
        }
        async Task<HighlightsResult> Worker(string action, List<HighlightClip> clips=null, string output=null) {
            if(!ToolsLocator.TryResolve(out var ffmpeg,out var ffprobe,out var hint))throw new Exception(hint);
            var python=Path.Combine(root,"venv","Scripts","python.exe");
            if(!File.Exists(python))throw new Exception("Сначала откройте «Подготовка» и нажмите «Подготовить». Это нужно один раз.");
            var id=Guid.NewGuid().ToString("N");var jobs=Path.Combine(root,"jobs");Directory.CreateDirectory(jobs);
            cancellation=Path.Combine(jobs,id+".cancel");var result=Path.Combine(jobs,id+".result.json");var job=Path.Combine(jobs,id+".json");var workdir=Path.Combine(jobs,id+".work");
            var input=new Dictionary<string,object>{{"source",prefs.Source},{"expected_fingerprint",analysis?.fingerprint},{"root",root},{"ffmpeg",ffmpeg},{"ffprobe",ffprobe},{"minimum",20},{"maximum",40},{"count",(int)count.Value},{"mode",new[]{"auto","speech","visual"}[mode.SelectedIndex]},{"action",action},{"result",result},{"workdir",workdir},{"cancel",cancellation},{"clips",clips},{"output",output}};
            File.WriteAllText(job,json.Serialize(input),new UTF8Encoding(false));
            try {
                var exit=await RunProcess(python,new[]{Path.Combine(Scripts(),"worker.py"),"--job",job},true);
                if(stopping || exit==2)throw new OperationCanceledException();
                if(exit!=0)throw new Exception("Нарезка остановлена: "+status.Text);
                var raw=File.ReadAllText(result);
                var parsed=json.Deserialize<HighlightsResult>(raw);
                if(action=="analyze") File.WriteAllText(Path.Combine(root,"last-result.json"),raw,new UTF8Encoding(false));
                return parsed;
            } finally {
                foreach(var file in new[]{job,result,cancellation})try{if(File.Exists(file))File.Delete(file);}catch{}
                try{if(Directory.Exists(workdir))Directory.Delete(workdir,true);}catch{}
                cancellation=null;
            }
        }
        async Task<int> RunProcess(string exe,string[] args,bool protocol) {
            var start=new ProcessStartInfo(exe,string.Join(" ",args.Select(Core.Quote))) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            start.EnvironmentVariables["PYTHONUTF8"]="1";
            using(var process=new Process{StartInfo=start}) {
                active=process; var errors=new StringBuilder();
                Action<string> line=text=>{if(string.IsNullOrWhiteSpace(text))return;if(protocol)ProgressLine(text);else OnUi(()=>status.Text=text);};
                try {
                    process.Start();
                    var stdout=Task.Run(async()=>{string text;while((text=await process.StandardOutput.ReadLineAsync())!=null)line(text);});
                    var stderr=Task.Run(async()=>{string text;while((text=await process.StandardError.ReadLineAsync())!=null){if(errors.Length<5000)errors.AppendLine(text);AppDiagnostics.Write("Нарезка","","highlights","runtime",text);if(!protocol)OnUi(()=>status.Text=text);}});
                    await Task.Run(()=>process.WaitForExit());await Task.WhenAll(stdout,stderr);
                    if(process.ExitCode!=0 && !protocol && !stopping)throw new Exception(errors.Length>0?errors.ToString():"Не удалось подготовить модели. Проверьте лог и подключение к интернету.");
                    return process.ExitCode;
                } finally {active=null;}
            }
        }
        async Task Search() {
            if(!File.Exists(prefs.Source)){ChooseSource();if(!File.Exists(prefs.Source))return;}
            prefs.Count=(int)count.Value;prefs.Mode=mode.SelectedIndex;Save();SetBusy(true);
            try {
                analysis=await Worker("analyze");DrawCards();status.Text=analysis.clips.Count==0?"Подходящих законченных фрагментов не найдено. Попробуйте другое видео или режим.":"Найдено: "+analysis.clips.Count+". Проверьте смысл и границы перед сохранением.";
            }catch(OperationCanceledException){status.Text="Поиск остановлен";}catch(Exception ex){ShowError(ex);}finally{SetBusy(false);}
        }
        void ClearCards() {while(cards.Controls.Count>0){var c=cards.Controls[0];cards.Controls.Remove(c);c.Dispose();}selections.Clear();}
        void DrawCards() {
            ClearCards();
            foreach(var clip in analysis.clips) {
                var card=new TableLayoutPanel {Width=Math.Max(250,cards.ClientSize.Width-26),Height=190,ColumnCount=1,RowCount=3,Padding=new Padding(14),BackColor=Theme.Card,Margin=new Padding(0,0,0,10)};
                card.RowStyles.Add(new RowStyle(SizeType.Absolute,30));card.RowStyles.Add(new RowStyle(SizeType.Percent,100));card.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
                var check=new CheckBox {Text=clip.title,Checked=true,Dock=DockStyle.Fill,Font=Theme.FontCardTitle,AutoEllipsis=true};card.Controls.Add(check,0,0);
                var description=new TextBox {Text=(clip.kind=="visual"?"Событие · тайминг приблизительный. ":"Речь · ")+clip.reason+Environment.NewLine+clip.text,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,BackColor=Theme.Card,ForeColor=Theme.TextSecondary};card.Controls.Add(description,0,1);
                var row=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
                var start=Number(0,(decimal)analysis.duration,(decimal)clip.start);start.DecimalPlaces=2;start.Increment=.1m;start.Width=95;
                var end=Number(0,(decimal)analysis.duration,(decimal)clip.end);end.DecimalPlaces=2;end.Increment=.1m;end.Width=95;
                row.Controls.Add(new Label {Text="Секунды: от",AutoSize=true,Margin=new Padding(0,10,6,0)});row.Controls.Add(start);row.Controls.Add(new Label {Text="до",AutoSize=true,Margin=new Padding(0,10,6,0)});row.Controls.Add(end);
                var preview=Theme.MakeButton("▶ Просмотр",ghost:true);preview.Click+=async(s,e)=>await Preview(clip,(double)start.Value,(double)end.Value);row.Controls.Add(preview);
                card.Controls.Add(row,0,2);cards.Controls.Add(card);selections.Add(Tuple.Create(check,clip,start,end));
            }
            export.Enabled=analysis.clips.Count>0;
        }
        HighlightClip Adjust(HighlightClip clip,double start,double end) {
            if(end-start<20 || end-start>40)throw new Exception("Длительность фрагмента должна быть от 20 до 40 секунд.");
            return new HighlightClip{start=start,end=end,title=clip.title,reason=clip.reason,text=clip.text,kind=clip.kind,score=clip.score};
        }
        async Task Preview(HighlightClip clip,double start,double end) {
            SetBusy(true);
            try {
                var dir=Path.Combine(root,"preview");Directory.CreateDirectory(dir);
                foreach(var old in Directory.GetFiles(dir,"moment_*.mp4"))try{File.Delete(old);}catch{}
                var result=await Worker("export",new List<HighlightClip>{Adjust(clip,start,end)},dir);
                if(result.outputs.Count>0)Process.Start(new ProcessStartInfo(result.outputs[0]){UseShellExecute=true});
                status.Text="Просмотр открыт в видеоплеере. Можно уточнить границы и сохранить.";
            }catch(OperationCanceledException){status.Text="Просмотр остановлен";}catch(Exception ex){ShowError(ex);}finally{SetBusy(false);}
        }
        async Task Export() {
            var chosen=new List<HighlightClip>();
            try{foreach(var item in selections.Where(x=>x.Item1.Checked))chosen.Add(Adjust(item.Item2,(double)item.Item3.Value,(double)item.Item4.Value));if(chosen.Count==0)throw new Exception("Выберите хотя бы один момент.");}catch(Exception ex){ShowError(ex);return;}
            using(var dialog=new FolderBrowserDialog {Description="Куда сохранить нарезку",SelectedPath=Directory.Exists(prefs.Output)?prefs.Output:""}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;prefs.Output=dialog.SelectedPath;Save();
            }
            SetBusy(true);
            try {var result=await Worker("export",chosen,prefs.Output);status.Text="Сохранено: "+result.outputs.Count+". Нажмите «Результаты».";}
            catch(OperationCanceledException){status.Text="Сохранение остановлено. Готовые фрагменты остались в папке результатов.";}catch(Exception ex){ShowError(ex);}finally{SetBusy(false);}
        }
        void ShowSetup() {
            using(var dialog=new Form {Text="Первый запуск нарезки",ClientSize=new Size(580,320),StartPosition=FormStartPosition.CenterParent,BackColor=Theme.Background,ForeColor=Theme.TextPrimary,Font=Theme.FontBody,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}) {
                var body=new Label {Text="Всё работает на вашем ПК, без подписки и платного API.\n\nОдин раз установите Python 3.11 (64-bit) и Ollama, затем нажмите «Подготовить». Скачаются модели для речи и анализа кадров — несколько ГБ. Нужен интернет; после скачивания файлы никуда не отправляются.\n\nНа слабом ПК анализ может быть долгим. Для локальной модели желательно 8–16 ГБ свободной памяти. Качество отбора проверяйте в просмотре.",Dock=DockStyle.Fill,Padding=new Padding(22)};
                var actions=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=62,Padding=new Padding(16,8,0,0),WrapContents=false};
                actions.Controls.Add(Theme.MakeButton("Python",ghost:true,action:()=>Process.Start("https://www.python.org/downloads/windows/")));
                actions.Controls.Add(Theme.MakeButton("Ollama",ghost:true,action:()=>Process.Start("https://ollama.com/download/windows")));
                actions.Controls.Add(Theme.MakeButton("Подготовить",accent:true,action:()=>{dialog.DialogResult=DialogResult.OK;dialog.Close();}));
                dialog.Controls.Add(body);dialog.Controls.Add(actions);
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            }
            Prepare();
        }
        async void Prepare() {
            SetBusy(true);status.Text="Подготавливаю модели. Скачивание может занять время…";
            try {await RunProcess("powershell.exe",new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(Scripts(),"prepare.ps1"),"-Root",root},false);status.Text=stopping?"Подготовка остановлена":"Всё готово. Выберите видео и нажмите «Найти моменты».";}
            catch(Exception ex){if(!stopping)ShowError(ex);}finally{SetBusy(false);}
        }
        public async Task StopBeforeClose() {CancelWork();while(busy)await Task.Delay(100);}
        public async void CancelWork() {
            if(!busy || stopping)return;stopping=true;stop.Enabled=false;status.Text="Останавливаю…";
            if(cancellation!=null)try{File.WriteAllText(cancellation,"");}catch{}
            var process=active;
            await Task.Delay(2500);
            try {if(process!=null&&!process.HasExited){using(var killer=Process.Start(new ProcessStartInfo("taskkill.exe","/PID "+process.Id+" /T /F"){UseShellExecute=false,CreateNoWindow=true}))await Task.Run(()=>killer.WaitForExit());}}catch{}
        }
        void ShowError(Exception ex) {status.Text=ex.Message;AppDiagnostics.Write("Нарезка","","highlights","error",ex.ToString(),"error");MessageBox.Show(this,ex.Message,"Нарезка",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        static void OpenFolder(string path) {if(Directory.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    }
}
