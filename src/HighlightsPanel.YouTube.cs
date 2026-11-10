using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class HighlightSource {
        public string videoId { get; set; }
        public string title { get; set; }
        public string url { get; set; }
        public string channel { get; set; }
        public string publishedDate { get; set; }
        public long viewCount { get; set; }
        public double durationSeconds { get; set; }
        public override string ToString() { return viewCount.ToString("N0") + " просм. · " + (title ?? url) + " · " + channel + " · " + publishedDate; }
    }
    public sealed class HighlightSourceResult {
        public string videoId { get; set; }
        public string title { get; set; }
        public string status { get; set; }
        public string error { get; set; }
        public string output { get; set; }
        public List<string> outputs { get; set; } = new List<string>();
    }
    public sealed class HighlightBatchResult {
        public string status { get; set; }
        public string summary { get; set; }
        public List<HighlightSourceResult> sources { get; set; } = new List<HighlightSourceResult>();
    }
    public sealed partial class HighlightsPanel {
        readonly Preferences youtubeSettings;
        CancellationTokenSource youtubeCancellation;
        bool youtubeBatch;
        Action<string> youtubeProgress;
        void ShowYouTube() {
            if(busy)return;
            using(var dialog=new Form {Text="YouTube → горизонтальные вырезки",ClientSize=new Size(860,660),MinimumSize=new Size(740,600),StartPosition=FormStartPosition.CenterParent,BackColor=Theme.Background,ForeColor=Theme.TextPrimary,Font=Theme.FontBody}) {
                var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(16)};
                foreach(int height in new[]{42,92,82,36})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,58));
                dialog.Controls.Add(layout);
                var top=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
                var type=Theme.MakeCombo(new[]{"Ключевые фразы","Ссылки на каналы / видео"});type.Width=240;type.SelectedIndex=prefs.YouTubeSourceMode=="sources"?1:0;
                var profile=Theme.MakeCombo(new string[0]);profile.Width=380;
                var channels=ProfileCatalog.Build(youtubeSettings).Where(c=>!string.IsNullOrWhiteSpace(c.ProfileId)).Select(c=>new YouTubeChannel {Name=c.DisplayName,ProfileId=c.ProfileId,ExpectedIp=c.ExpectedIp}).ToList();
                foreach(var item in channels)profile.Items.Add(item);
                if(profile.Items.Count>0)profile.SelectedIndex=Math.Max(0,channels.FindIndex(c=>c.ProfileId==prefs.YouTubeProfileId));
                top.Controls.Add(type);top.Controls.Add(profile);layout.Controls.Add(top,0,0);
                var filters=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true};
                var period=Theme.MakeCombo(new[]{"Сегодня","За неделю"});period.Width=140;period.SelectedIndex=prefs.YouTubePeriod=="week"?1:0;
                var duration=Theme.MakeCombo(new[]{"3–20 минут","Менее 3 минут"});duration.Width=155;duration.SelectedIndex=prefs.YouTubeDuration=="short"?1:0;
                filters.Controls.Add(period);filters.Controls.Add(duration);
                filters.Controls.Add(new Label {AutoSize=true,Text="Видео:",Margin=new Padding(8,9,0,0),ForeColor=Theme.TextSecondary});
                var videoCount=new NumericUpDown {Minimum=1,Maximum=int.MaxValue,Value=Math.Max(1,prefs.YouTubeVideoCount),Width=80};
                filters.Controls.Add(videoCount);
                filters.Controls.Add(new Label {AutoSize=true,Text="Просмотры ↓ · язык запроса",Margin=new Padding(8,9,0,0),ForeColor=Theme.TextSecondary});
                filters.Controls.Add(new Label {AutoSize=false,Width=650,Height=40,Text="Период по времени ПК. Неделя — последние 7 суток. Одна фраза или ссылка на строку.\nБез Shorts/эфиров. Исходник менее 2 минут не подходит для вырезок 2–5 минут.",ForeColor=Theme.TextSecondary});
                layout.Controls.Add(filters,0,1);
                var entries=new TextBox {Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary,Text=prefs.YouTubeEntries};layout.Controls.Add(entries,0,2);
                var searchRow=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
                var discover=Theme.MakeButton("Найти и нарезать",accent:true);searchRow.Controls.Add(discover);layout.Controls.Add(searchRow,0,3);
                var candidates=new CheckedListBox {Dock=DockStyle.Fill,CheckOnClick=true,HorizontalScrollbar=true,BackColor=Theme.Card,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.FixedSingle};layout.Controls.Add(candidates,0,4);
                var outputRow=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
                var destination=new Label {AutoSize=false,Width=540,Height=40,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Text=prefs.Output};
                var folder=Theme.MakeButton("Папка…",ghost:true,action:()=>{using(var picker=new FolderBrowserDialog {SelectedPath=prefs.Output,Description="Папка результатов нарезки"})if(picker.ShowDialog(dialog)==DialogResult.OK){prefs.Output=picker.SelectedPath;destination.Text=prefs.Output;Save();}});
                outputRow.Controls.Add(destination);outputRow.Controls.Add(folder);layout.Controls.Add(outputRow,0,5);
                var bottom=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
                var process=Theme.MakeButton("Скачать и нарезать",accent:true);process.Enabled=false;
                var cancel=Theme.MakeButton("Стоп",ghost:true,action:CancelWork);cancel.Enabled=false;
                var open=Theme.MakeButton("Результаты",ghost:true,action:()=>OpenFolder(prefs.Output));
                bottom.Controls.Add(process);bottom.Controls.Add(cancel);bottom.Controls.Add(open);layout.Controls.Add(bottom,0,6);
                var details=new Label {AutoSize=false,Width=320,Height=52,Text="До 4 фрагментов по 2–5 минут на видео. Whisper — локально; текст — в OpenRouter.",ForeColor=Theme.TextSecondary};bottom.Controls.Add(details);
                bool running=false;
                Action invalidateSearch=()=>{if(running)return;candidates.Items.Clear();process.Enabled=false;};
                period.SelectedIndexChanged+=(s,e)=>invalidateSearch();duration.SelectedIndexChanged+=(s,e)=>invalidateSearch();videoCount.ValueChanged+=(s,e)=>invalidateSearch();
                type.SelectedIndexChanged+=(s,e)=>invalidateSearch();profile.SelectedIndexChanged+=(s,e)=>invalidateSearch();entries.TextChanged+=(s,e)=>invalidateSearch();
                Action<bool> working=value=>{running=value;discover.Enabled=!value;process.Enabled=!value&&candidates.Items.Count>0;type.Enabled=!value;profile.Enabled=!value;period.Enabled=!value;duration.Enabled=!value;videoCount.Enabled=!value;entries.Enabled=!value;candidates.Enabled=!value;folder.Enabled=!value;cancel.Enabled=value;};
                dialog.FormClosing+=(s,e)=>{if(running){e.Cancel=true;details.Text="Сначала остановите обработку кнопкой «Стоп».";}};
                bool startAfterSearch=false;
                discover.Click+=async(s,e)=>{
                    if(!(profile.SelectedItem is YouTubeChannel selected)){MessageBox.Show(dialog,"Добавьте профиль Dolphin в приложение.");return;}
                    var lines=entries.Lines.Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct().ToArray();
                    if(lines.Length==0){MessageBox.Show(dialog,"Введите фразы или ссылки.");return;}
                    prefs.YouTubeEntries=entries.Text;prefs.YouTubeSourceMode=type.SelectedIndex==1?"sources":"keywords";prefs.YouTubeProfileId=selected.ProfileId;prefs.YouTubePeriod=period.SelectedIndex==1?"week":"today";prefs.YouTubeDuration=duration.SelectedIndex==1?"short":"medium";prefs.YouTubeVideoCount=(int)videoCount.Value;Save();
                    candidates.Items.Clear();working(true);SetBusy(true);youtubeCancellation=new CancellationTokenSource();details.Text="Открываю профиль: "+period.Text+" · "+duration.Text+" · по популярности…";
                    try {
                        var token=WindowsSupport.Unprotect(youtubeSettings.ProtectedDolphinToken);
                        if(string.IsNullOrWhiteSpace(token))throw new Exception("Укажите API-токен Dolphin в настройках приложения.");
                        var port=await DolphinRunner.EnsureApiAvailableAsync(token,youtubeSettings.DolphinPort,youtubeCancellation.Token);
                        var found=new List<HighlightSource>();bool received=false;
                        await DolphinRunner.Run(new UploadJob {token=token,profileId=selected.ProfileId,expectedIp=selected.ExpectedIp,localPort=port,highlightsSearch=true,sourceMode=prefs.YouTubeSourceMode,sourcePeriod=prefs.YouTubePeriod,sourceDuration=prefs.YouTubeDuration,sourceVideoCount=prefs.YouTubeVideoCount,sourceEntries=lines,todayDate=DateTime.Now.ToString("yyyy-MM-dd"),sourceNow=DateTimeOffset.Now.ToString("o"),pcUtcOffsetMinutes=(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,keepProfileOpen=youtubeSettings.KeepDolphinProfileOpenAfterUpload},message=>{
                            if(message.stage=="highlight_sources") {found=json.Deserialize<List<HighlightSource>>(message.sourcesJson);received=true;}
                            OnUi(()=>{details.Text=message.text;status.Text=message.text;});
                        },youtubeCancellation.Token);
                        if(!received)throw new Exception("Поиск не вернул список видео. Подробности — в журнале.");
                        foreach(var candidate in found)candidates.Items.Add(candidate,true);
                        Directory.CreateDirectory(root);
                        File.WriteAllText(Path.Combine(root,"last-youtube-sources.json"),json.Serialize(found),new UTF8Encoding(false));
                        startAfterSearch=found.Count>0;
                        details.Text="Найдено: "+found.Count+" из "+prefs.YouTubeVideoCount+". "+(found.Count<prefs.YouTubeVideoCount?"Подходящих видео меньше запрошенного. ":"")+(startAfterSearch?"Запускаю скачивание и нарезку…":"Подходящих видео нет.");
                        status.Text=details.Text;
                    }catch(OperationCanceledException){details.Text="Поиск остановлен";status.Text=details.Text;}
                    catch(Exception ex){details.Text=ex.Message;ShowError(ex);}
                    finally{youtubeCancellation.Dispose();youtubeCancellation=null;SetBusy(false);working(false);}
                    if(startAfterSearch){startAfterSearch=false;process.PerformClick();}
                };
                process.Click+=async(s,e)=>{
                    var chosen=candidates.CheckedItems.Cast<HighlightSource>().ToList();
                    if(chosen.Count==0){MessageBox.Show(dialog,"Отметьте видео для обработки.");return;}
                    working(true);SetBusy(true);youtubeProgress=text=>{details.Text=text;cancel.Enabled=!stopping;};details.Text="Скачивание → Whisper → смысловой отбор → вырезки…";
                    try {
                        var result=await RunYouTubeBatch(chosen);
                        int ok=result.sources.Count(x=>x.status=="success"), failed=result.sources.Count-ok, clips=result.sources.Sum(x=>x.outputs?.Count??0);
                        details.Text="Видео готово: "+ok+"; не завершено: "+failed+". Вырезок: "+clips+".";
                        status.Text=details.Text;
                        if(failed>0)MessageBox.Show(dialog,string.Join(Environment.NewLine,result.sources.Where(x=>x.status!="success").Select(x=>(x.title??x.videoId)+": "+(x.error??x.status))),"Обработка завершена не полностью",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    }catch(OperationCanceledException){details.Text="Остановлено. Готовые файлы сохранены.";status.Text=details.Text;}
                    catch(Exception ex){details.Text=ex.Message;ShowError(ex);}
                    finally{youtubeProgress=null;SetBusy(false);working(false);}
                };
                dialog.ShowDialog(this);
            }
        }
        async Task<HighlightBatchResult> RunYouTubeBatch(List<HighlightSource> sources) {
            if(!ToolsLocator.TryResolve(out var ffmpeg,out var ffprobe,out var hint))throw new Exception(hint);
            var python=Path.Combine(root,"venv","Scripts","python.exe");
            if(!File.Exists(python))throw new Exception("Откройте «Подготовка» в Нарезчике и нажмите «Для YouTube».");
            var key=WindowsSupport.Unprotect(youtubeSettings?.ProtectedOpenRouterKey);
            if(string.IsNullOrWhiteSpace(key))throw new Exception("Укажите ключ OpenRouter в настройках приложения.");
            var id=Guid.NewGuid().ToString("N");var jobs=Path.Combine(root,"jobs");Directory.CreateDirectory(jobs);
            var job=Path.Combine(jobs,id+".json");var result=Path.Combine(jobs,id+".result.json");var workdir=Path.Combine(jobs,id+".work");cancellation=Path.Combine(jobs,id+".cancel");
            var input=new Dictionary<string,object> {{"action","youtube_batch"},{"sources",sources},{"root",root},{"output",prefs.Output},{"ffmpeg",ffmpeg},{"ffprobe",ffprobe},{"node",DolphinRunner.Node},{"minimum",120},{"maximum",300},{"count",4},{"export_format","original"},{"mode","speech"},{"openrouter_model",youtubeSettings.OpenRouterModel},{"result",result},{"workdir",workdir},{"cancel",cancellation}};
            File.WriteAllText(job,json.Serialize(input),new UTF8Encoding(false));
            youtubeBatch=true;
            try {
                int code=await RunProcess(python,new[]{Path.Combine(Scripts(),"worker.py"),"--job",job},true);
                if(stopping||code==2)throw new OperationCanceledException();
                if(!File.Exists(result))throw new Exception("Обработка не вернула результат: "+status.Text);
                var raw=File.ReadAllText(result);var parsed=json.Deserialize<HighlightBatchResult>(raw);
                if(parsed?.sources==null||parsed.sources.Count!=sources.Count)throw new Exception("Обработка вернула неполный отчёт. Готовые файлы сохранены; подробности — в журнале.");
                if(code!=0&&parsed.sources.All(x=>x.status=="success"))throw new Exception("Обработка завершилась с ошибкой: "+status.Text);
                File.WriteAllText(Path.Combine(root,"last-youtube-result.json"),raw,new UTF8Encoding(false));
                return parsed;
            }finally{
                youtubeBatch=false;
                foreach(var file in new[]{job,result,cancellation})try{if(File.Exists(file))File.Delete(file);}catch{}
                try{if(Directory.Exists(workdir))Directory.Delete(workdir,true);}catch{}
                cancellation=null;
            }
        }
    }
}
