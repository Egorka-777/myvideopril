using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Http;

namespace VideoBatch {
    public class YouTubeItem {
        public string Video="",Title="",Thumbnail="",PublishedUrl="",PublishedVideoId="";
    }
    public class YouTubeChannel {
        public bool Enabled=true;
        public string Name="",ProfileId="",ExpectedIp="",Video="",Title="",Thumbnail="",Status="Готов";
        public string Market="RU"; // RU | EN
        public string Kind="long"; // long | shorts
        public string ChannelUrl=""; // страница канала на YouTube (для сетки просмотра)
        public List<YouTubeItem> Items=new List<YouTubeItem>(); // пачка роликов на одном канале
    }
    [DataContract] public class UploadItemJob {
        [DataMember]public string video,title,thumbnail,scheduleDate,scheduleTime,description,publishedVideoId,publishedUrl;
    }
    [DataContract] public class CatalogVideoJob {
        [DataMember]public string title,videoId,url,kind;
    }
    [DataContract] public class WatchTargetJob {
        [DataMember]public string title,searchUrl,searchKeys,searchFullTitle,ownerName,videoId,channelUrl,ownerProfileId;
        [DataMember]public CatalogVideoJob[] catalogVideos;
    }
    [DataContract] public class UploadJob {
        [DataMember]public string token,profileId,expectedIp,video,title,thumbnail,searchUrl,searchFilter,searchKeys,searchFullTitle,scheduleDate,scheduleTime;
        [DataMember]public int localPort;
        [DataMember]public bool checkOnly,searchOnly,watchMesh,skipQueueDelay,applyThumbFrame,draftOnly;
        [DataMember]public UploadItemJob[] items; // пачка в одном профиле без перезапуска
        [DataMember]public WatchTargetJob[] watchTargets;
    }
    [DataContract] public class UploadMessage {
        [DataMember]public string stage,text,ip,url,error,channelUrl,meshOwner;
        [DataMember]public double percent;
        [DataMember]public int packIndex;
        [DataMember]public bool success,keptOpen;
    }
    public class UploadRunResult { public bool Success,KeptOpen;public string Ip="",Url="",Error=""; }

    public static class DolphinRunner {
        static readonly DataContractJsonSerializer JobSerializer=new DataContractJsonSerializer(typeof(UploadJob));
        static readonly DataContractJsonSerializer MessageSerializer=new DataContractJsonSerializer(typeof(UploadMessage));
        public static string Root{get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","uploader");}}
        public static string Node{get{return Path.Combine(Root,"node.exe");}}
        public static string Worker{get{return Path.Combine(Root,"worker.js");}}
        public static string WorkerTikTok{get{return Path.Combine(Root,"worker-tiktok.js");}}
        public static void CheckFiles(){if(!File.Exists(Node)||!File.Exists(Worker)||!Directory.Exists(Path.Combine(Root,"node_modules","playwright-core")))throw new Exception("Архив распакован не полностью: не найден модуль загрузки YouTube.");}
        public static void CheckFilesTikTok(){if(!File.Exists(Node)||!File.Exists(WorkerTikTok)||!Directory.Exists(Path.Combine(Root,"node_modules","playwright-core")))throw new Exception("Архив распакован не полностью: не найден модуль загрузки TikTok (worker-tiktok.js).");}
        public static Task<UploadRunResult> Run(UploadJob job,Action<UploadMessage> update,CancellationToken cancel){
            return RunWorker(Worker,job,update,cancel,"YouTube");
        }
        public static Task<UploadRunResult> RunTikTok(UploadJob job,Action<UploadMessage> update,CancellationToken cancel){
            return RunWorker(WorkerTikTok,job,update,cancel,"TikTok");
        }
        static async Task<UploadRunResult> RunWorker(string workerPath,UploadJob job,Action<UploadMessage> update,CancellationToken cancel,string label) {
            if(string.Equals(workerPath,WorkerTikTok,StringComparison.OrdinalIgnoreCase))CheckFilesTikTok();else CheckFiles();
            Directory.CreateDirectory(Path.Combine(Store.Root,"jobs"));
            string file=Path.Combine(Store.Root,"jobs","upload-"+Guid.NewGuid().ToString("N")+".json");
            string protectedToken=job.token;job.token=null;
            try{using(var memory=new MemoryStream()){JobSerializer.WriteObject(memory,job);File.WriteAllBytes(file,memory.ToArray());}}finally{job.token=protectedToken;}
            var result=new UploadRunResult();Process process=null;
            try {
                var info=new ProcessStartInfo(Node,Core.Quote(workerPath)+" "+Core.Quote(file)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Root};
                info.EnvironmentVariables["VIDEOBATCH_DOLPHIN_TOKEN"]=protectedToken;
                process=new Process{StartInfo=info};if(!process.Start())throw new Exception("Не удалось запустить модуль "+label+".");
                var stderr=process.StandardError.ReadToEndAsync();
                using(cancel.Register(()=>{try{if(process!=null&&!process.HasExited)process.Kill();}catch{}})) {
                    string line;
                    while((line=await process.StandardOutput.ReadLineAsync().ConfigureAwait(false))!=null) {
                        if(string.IsNullOrWhiteSpace(line))continue;
                        try{using(var m=new MemoryStream(Encoding.UTF8.GetBytes(line))){var msg=(UploadMessage)MessageSerializer.ReadObject(m);if(update!=null)update(msg);if(msg.ip!=null)result.Ip=msg.ip;if(msg.url!=null)result.Url=msg.url;if(msg.stage=="done"){result.Success=msg.success;if(!msg.success&&string.IsNullOrWhiteSpace(result.Error))result.Error=msg.text;}if(msg.stage=="error"){result.Error=msg.error??msg.text;result.KeptOpen=msg.keptOpen;}}}catch{if(update!=null)update(new UploadMessage{stage="log",text=line});}
                    }
                    await Task.Run(()=>process.WaitForExit()).ConfigureAwait(false);cancel.ThrowIfCancellationRequested();
                    string err=await stderr.ConfigureAwait(false);
                    if(process.ExitCode!=0&&!result.Success&&string.IsNullOrWhiteSpace(result.Error))result.Error=string.IsNullOrWhiteSpace(err)?"Модуль загрузки завершился с ошибкой.":err.Trim();
                    if(!result.Success)throw new UploadException(string.IsNullOrWhiteSpace(result.Error)?"Загрузчик не подтвердил завершение операции. Повторно не публикуйте, сначала проверьте профиль Dolphin.":result.Error,result.KeptOpen);
                    return result;
                }
            } finally {try{if(process!=null){if(!process.HasExited)process.Kill();process.Dispose();}}catch{}try{File.Delete(file);}catch{}}
        }
        public static async Task StopProfile(string token,int port,string profileId) {
            if(string.IsNullOrWhiteSpace(token)||string.IsNullOrWhiteSpace(profileId))return;
            try{using(var handler=new HttpClientHandler{UseProxy=false})using(var client=new HttpClient(handler)){client.Timeout=TimeSpan.FromSeconds(10);var body=new StringContent("{\"token\":\""+JsonEscape(token)+"\"}",Encoding.UTF8,"application/json");await client.PostAsync("http://127.0.0.1:"+port+"/v1.0/auth/login-with-token",body).ConfigureAwait(false);await client.GetAsync("http://127.0.0.1:"+port+"/v1.0/browser_profiles/"+Uri.EscapeDataString(profileId)+"/stop").ConfigureAwait(false);}}catch{}
        }
        static string JsonEscape(string s){return (s??"").Replace("\\","\\\\").Replace("\"","\\\"");}
    }
    public class UploadException:Exception {public bool KeptOpen;public UploadException(string message,bool kept):base(message){KeptOpen=kept;}}

    public class DolphinSetupDialog:Dialog {
        public string Token;public int Port;public int MaxParallel;public string StagingFolder;
        TextBox token,staging;NumericUpDown port,maxParallel;
        public DolphinSetupDialog(string protectedToken,int currentPort,int maxParallelUploads,string stagingFolder):base("Шаг 1 · Подключение Dolphin",560) {
            Token=WindowsSupport.Unprotect(protectedToken);Port=currentPort;
            MaxParallel=maxParallelUploads<=0?10:Math.Max(1,Math.Min(20,maxParallelUploads));
            StagingFolder=string.IsNullOrWhiteSpace(stagingFolder)?@"C:\VideoBatch\Upload":stagingFolder.Trim();
            var all=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};Body.Controls.Add(all);
            all.Controls.Add(new Label{Text="1",Font=new Font("Segoe UI",22,FontStyle.Bold),ForeColor=Ui.Blue,AutoSize=true});
            all.Controls.Add(Ui.Label("Откройте Dolphin Anty и оставьте программу запущенной."));
            all.Controls.Add(Ui.Label("2. В личном кабинете Dolphin на сайте создайте или скопируйте API-токен и вставьте ниже. Токен хранится зашифрованным только в вашей учётной записи Windows.",true));
            var fields=Ui.Fields();fields.Width=535;fields.Dock=DockStyle.None;token=new TextBox{Width=330,UseSystemPasswordChar=true,Text=Token};port=Ui.Number(currentPort,1,65535,0);maxParallel=Ui.Number(MaxParallel,1,20,0);staging=new TextBox{Width=330,Text=StagingFolder};
            Ui.Row(fields,"API-токен Dolphin",token);Ui.Row(fields,"Локальный порт",port);Ui.Row(fields,"Параллельно профилей",maxParallel);Ui.Row(fields,"Папка загрузки",staging);all.Controls.Add(fields);
            var show=new CheckBox{Text="Показать токен",AutoSize=true};show.CheckedChanged+=(s,e)=>token.UseSystemPasswordChar=!show.Checked;all.Controls.Add(show);
            all.Controls.Add(Ui.Label("Обычно порт — 3001. Параллельно — сколько профилей Dolphin открывать одновременно (по умолчанию 10). Папка — короткий путь для копий видео перед загрузкой.",true));
            all.Controls.Add(Ui.Label("3. Сохраните, затем добавьте строки каналов и нажмите «Проверить профили». Проверка сначала открывает YouTube через прокси профиля, IP узнаёт несколькими сервисами.",true));
            StandardButtons(()=>{
                Token=token.Text.Trim();Port=(int)port.Value;MaxParallel=(int)maxParallel.Value;
                StagingFolder=(staging.Text??"").Trim();if(string.IsNullOrWhiteSpace(StagingFolder))StagingFolder=@"C:\VideoBatch\Upload";
                if(string.IsNullOrWhiteSpace(Token))throw new Exception("Вставьте API-токен Dolphin.");
            });
        }
        protected override void Dispose(bool disposing){if(disposing&&token!=null)token.Text="";base.Dispose(disposing);}
    }

    public class TitleDialog:Dialog {
        public List<string> Titles;TextBox words;ListBox preview;Label counter;int count;bool permute;
        public TitleDialog(int amount,bool permuteKeys=false):base(permuteKeys?"Заголовки из ключей":"Вставить заголовки построчно",640){
            count=amount;permute=permuteKeys;ClientSize=new Size(640,640);
            var all=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};Body.Controls.Add(all);
            if(permute){
                all.Controls.Add(Ui.Label("Ключевые фразы — строго по одной на строку. Enter = новая строка. Будет собрано заголовков: "+amount+".",true));
            } else {
                all.Controls.Add(Ui.Label("Вставьте список: один заголовок = одна строка (Enter). Сколько вставите — столько и будет назначено сверху вниз.",true));
            }
            words=new TextBox{
                Multiline=true,AcceptsReturn=true,AcceptsTab=true,WordWrap=false,
                ScrollBars=ScrollBars.Both,Width=580,Height=160,Font=new Font("Consolas",10)
            };
            words.TextChanged+=(s,e)=>RefreshPreview();
            all.Controls.Add(words);
            counter=new Label{AutoSize=true,ForeColor=Ui.Blue,Font=new Font("Segoe UI",10,FontStyle.Bold),Margin=new Padding(0,8,0,4)};
            all.Controls.Add(counter);
            all.Controls.Add(new Label{Text="Как программа видит список:",AutoSize=true,ForeColor=Ui.Muted,Margin=new Padding(0,4,0,4)});
            preview=new ListBox{Width=580,Height=200,Font=new Font("Consolas",9),IntegralHeight=false,HorizontalScrollbar=true};
            all.Controls.Add(preview);
            all.Controls.Add(Ui.Label(permute
                ?"Снизу — «Сохранить». Enter в поле ввода добавляет строку, не закрывает окно."
                :"Потом нажмите «Назначить» — все строки из списка уйдут в таблицу.",true));
            var ok=Ui.Button(permute?"Сохранить":"Назначить",()=>{try{Generate();DialogResult=DialogResult.OK;Close();}catch(Exception e){Ui.Error(this,e);}},true);
            var cancel=Ui.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();});
            Bottom.Controls.Add(ok);Bottom.Controls.Add(cancel);CancelButton=cancel;
            RefreshPreview();
        }
        static List<string> ParseLines(string text){
            if(string.IsNullOrEmpty(text))return new List<string>();
            // Нормализуем все виды переносов (Word/Excel/блокнот), пустые строки пропускаем.
            var normalized=text.Replace("\r\n","\n").Replace('\r','\n').Replace('\u2028','\n').Replace('\u2029','\n').Replace('\v','\n');
            return normalized.Split('\n').Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
        }
        void RefreshPreview(){
            var lines=ParseLines(words.Text);
            preview.BeginUpdate();preview.Items.Clear();
            for(int i=0;i<lines.Count;i++){
                string t=lines[i];
                string mark=t.Length>100?" ⚠ >100":"";
                string trunc=t.Length>90?t.Substring(0,90)+"…":t;
                preview.Items.Add((i+1)+". "+trunc+mark);
            }
            preview.EndUpdate();
            if(permute){
                counter.Text="Ключевых строк: "+lines.Count+(lines.Count>=2?" · можно сохранить":" · нужно минимум 2");
                counter.ForeColor=lines.Count>=2?Color.FromArgb(30,130,70):Color.FromArgb(180,60,40);
            } else {
                if(lines.Count==0){counter.Text="Список пустой — вставьте заголовки";counter.ForeColor=Color.FromArgb(180,60,40);}
                else{counter.Text="Будет назначено: "+lines.Count+" заголовков";counter.ForeColor=Color.FromArgb(30,130,70);}
            }
        }
        void Generate(){
            var lines=ParseLines(words.Text);
            if(permute){
                var keys=lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if(keys.Count<2)throw new Exception("Введите хотя бы две ключевые строки (каждая с новой строки по Enter).");
                Titles=new List<string>();var separators=new[]{" — "," | ",": "};
                for(int i=0;i<count;i++){var ordered=new List<string>();for(int j=0;j<keys.Count;j++)ordered.Add(keys[(j+i)%keys.Count]);if(i%2==1)ordered.Reverse();string title=string.Join(separators[i%separators.Length],ordered);if(title.Length>100)throw new Exception("Получился заголовок длиннее 100 символов. Сократите ключевые фразы.");Titles.Add(title);}
                return;
            }
            if(lines.Count==0)throw new Exception("Список пустой. Вставьте заголовки: каждый с новой строки (Enter), затем смотрите нумерованный список ниже.");
            Titles=lines;
            var tooLong=Titles.Select((t,i)=>new{t,i}).Where(x=>x.t.Length>100).ToList();
            if(tooLong.Count>0)throw new Exception("Заголовок №"+(tooLong[0].i+1)+" длиннее 100 символов ("+tooLong[0].t.Length+"). Сократите его.");
        }
    }

    public class ShortsBatchDialog:Dialog {
        public List<string> Titles;string[] files;TextBox list;TextBox shared;CheckBox useShared;
        public ShortsBatchDialog(string[] videoFiles):base("Заголовки для пачки шортсов",560){
            files=videoFiles;
            var all=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};Body.Controls.Add(all);
            all.Controls.Add(Ui.Label("Выбрано файлов: "+files.Length+". Заголовки для шортсов задаются отдельно от длинных видео.",true));
            useShared=new CheckBox{Text="Один общий заголовок на все шортсы этой пачки (рекомендуется)",AutoSize=true,Checked=true};all.Controls.Add(useShared);
            shared=new TextBox{Width=530,Enabled=true};all.Controls.Add(shared);
            all.Controls.Add(Ui.Label("Или снимите галочку и дайте список — по одному заголовку на файл (Enter = новая строка):",true));
            list=new TextBox{Multiline=true,ScrollBars=ScrollBars.Both,AcceptsReturn=true,AcceptsTab=true,WordWrap=false,Width=530,Height=200,Font=new Font("Consolas",10),Enabled=false};all.Controls.Add(list);
            useShared.CheckedChanged+=(s,e)=>{shared.Enabled=useShared.Checked;list.Enabled=!useShared.Checked;};
            var names=string.Join(Environment.NewLine,files.Select((f,i)=>(i+1)+". "+Path.GetFileName(f)));
            all.Controls.Add(Ui.Label("Порядок файлов:"+Environment.NewLine+names,true));
            var ok=Ui.Button("Готово",()=>{try{Build();DialogResult=DialogResult.OK;Close();}catch(Exception e){Ui.Error(this,e);}},true);
            var cancel=Ui.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();});
            Bottom.Controls.Add(ok);Bottom.Controls.Add(cancel);CancelButton=cancel;
        }
        void Build(){
            Titles=new List<string>();
            if(useShared.Checked){
                string t=(shared.Text??"").Trim();
                if(string.IsNullOrWhiteSpace(t))throw new Exception("Введите общий заголовок для шортсов.");
                if(t.Length>100)throw new Exception("Заголовок длиннее 100 символов.");
                for(int i=0;i<files.Length;i++)Titles.Add(files.Length==1?t:(t+" ("+(i+1)+")"));
                foreach(var x in Titles)if(x.Length>100)throw new Exception("С суффиксом номерa заголовок стал длиннее 100 символов. Сократите общий текст.");
                return;
            }
            var lines=list.Lines.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            if(lines.Count<files.Length)throw new Exception("Нужно "+files.Length+" заголовков шортсов (по числу файлов), сейчас "+lines.Count+".");
            Titles=lines.Take(files.Length).ToList();
            if(Titles.Any(t=>t.Length>100))throw new Exception("Есть заголовок длиннее 100 символов.");
        }
    }

    /// <summary>Редактор одной базы (TikTok и т.п.).</summary>
    public class TitleBankDialog:Dialog {
        public List<string> Titles;TextBox words;ListBox preview;Label counter;
        public TitleBankDialog(string caption,IList<string> current):base(caption,640){
            ClientSize=new Size(640,640);
            var all=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};Body.Controls.Add(all);
            all.Controls.Add(Ui.Label("Один заголовок = одна строка. База сохраняется и не съедается при выборе видео.",true));
            words=new TextBox{Multiline=true,AcceptsReturn=true,AcceptsTab=true,WordWrap=false,ScrollBars=ScrollBars.Both,Width=580,Height=200,Font=new Font("Consolas",10)};
            if(current!=null&&current.Count>0)words.Text=string.Join(Environment.NewLine,current);
            words.TextChanged+=(s,e)=>RefreshPreview();
            all.Controls.Add(words);
            counter=new Label{AutoSize=true,ForeColor=Ui.Blue,Font=new Font("Segoe UI",10,FontStyle.Bold),Margin=new Padding(0,8,0,4)};
            all.Controls.Add(counter);
            preview=new ListBox{Width=580,Height=180,Font=new Font("Consolas",9),IntegralHeight=false,HorizontalScrollbar=true};
            all.Controls.Add(preview);
            var ok=Ui.Button("Сохранить",()=>{try{SaveList();DialogResult=DialogResult.OK;Close();}catch(Exception e){Ui.Error(this,e);}},true);
            var cancel=Ui.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();});
            Bottom.Controls.Add(ok);Bottom.Controls.Add(cancel);CancelButton=cancel;
            RefreshPreview();
        }
        static List<string> ParseLines(string text){
            if(string.IsNullOrEmpty(text))return new List<string>();
            var normalized=text.Replace("\r\n","\n").Replace('\r','\n').Replace('\u2028','\n').Replace('\u2029','\n').Replace('\v','\n');
            return normalized.Split('\n').Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
        }
        void RefreshPreview(){
            var lines=ParseLines(words.Text);
            preview.BeginUpdate();preview.Items.Clear();
            for(int i=0;i<lines.Count;i++){
                string t=lines[i];
                preview.Items.Add((i+1)+". "+(t.Length>90?t.Substring(0,90)+"…":t)+(t.Length>100?" ⚠":""));
            }
            preview.EndUpdate();
            counter.Text=lines.Count==0?"База будет пустой":("В базе: "+lines.Count+" заголовков");
            counter.ForeColor=lines.Count==0?Color.FromArgb(180,60,40):Color.FromArgb(30,130,70);
        }
        void SaveList(){
            Titles=ParseLines(words.Text);
            var tooLong=Titles.Select((t,i)=>new{t,i}).Where(x=>x.t.Length>100).ToList();
            if(tooLong.Count>0)throw new Exception("Заголовок №"+(tooLong[0].i+1)+" длиннее 100 символов.");
        }
    }

    /// <summary>Одно окно: RU/EN × Long/Shorts. База постоянная.</summary>
    public class TitleBanksHubDialog:Dialog {
        readonly Preferences settings;TextBox words;Label counter;Button langRu,langEn,kindLong,kindShorts;
        string lang="RU",kind="long";
        readonly Dictionary<string,string> drafts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public TitleBanksHubDialog(Preferences prefs):base("Заголовки YouTube",720){
            settings=prefs;ClientSize=new Size(720,680);
            var all=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};Body.Controls.Add(all);
            all.Controls.Add(Ui.Label("Заполни один раз — заголовки остаются навсегда. При выборе видео берутся сами. «Загрузить новые» подставляет готовый комплект.",true));
            var bar=Ui.Flow();bar.WrapContents=false;bar.Margin=new Padding(0,8,0,8);
            langRu=Ui.Button("RU",()=>Switch("RU",kind));langEn=Ui.Button("EN",()=>Switch("EN",kind));
            kindLong=Ui.Button("Long",()=>Switch(lang,"long"));kindShorts=Ui.Button("Shorts",()=>Switch(lang,"shorts"));
            bar.Controls.Add(langRu);bar.Controls.Add(langEn);
            bar.Controls.Add(new Label{Text="  ",AutoSize=true});
            bar.Controls.Add(kindLong);bar.Controls.Add(kindShorts);
            all.Controls.Add(bar);
            words=new TextBox{Multiline=true,AcceptsReturn=true,AcceptsTab=true,WordWrap=false,ScrollBars=ScrollBars.Both,Width=660,Height=420,Font=new Font("Consolas",10)};
            words.TextChanged+=(s,e)=>RefreshCounter();
            all.Controls.Add(words);
            counter=new Label{AutoSize=true,Font=new Font("Segoe UI",10,FontStyle.Bold),Margin=new Padding(0,8,0,0)};
            all.Controls.Add(counter);
            SeedDraftsFromSettings();
            Switch("RU","long",commit:false);
            var ok=Ui.Button("Сохранить",()=>{try{CommitCurrent();ApplyDraftsToSettings();DialogResult=DialogResult.OK;Close();}catch(Exception e){Ui.Error(this,e);}},true);
            var reload=Ui.Button("Загрузить новые",()=>{
                if(MessageBox.Show(this,"Подставить готовый комплект заголовков (RU/EN · Long/Shorts) поверх текущих баз?","Заголовки",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                if(!Store.TryLoadBundledYouTubeTitleBanks(settings,true)){Ui.Error(this,new Exception("Файл комплекта не найден рядом с программой (tools/uploader/youtube-title-banks.json)."));return;}
                SeedDraftsFromSettings();Switch(lang,kind,commit:false);
                MessageBox.Show(this,"Комплект загружен. Курсор заголовков сохранён — следующие видео продолжат с текущей позиции. Нажмите «Сохранить».","Заголовки",MessageBoxButtons.OK,MessageBoxIcon.Information);
            });
            var cancel=Ui.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();});
            Bottom.Controls.Add(ok);Bottom.Controls.Add(reload);Bottom.Controls.Add(cancel);CancelButton=cancel;
        }
        static string KeyOf(string lang,string kind){return NormLang(lang)+"_"+NormKind(kind);}
        static string NormLang(string l){return string.Equals((l??"").Trim(),"EN",StringComparison.OrdinalIgnoreCase)?"EN":"RU";}
        static string NormKind(string k){k=(k??"").Trim().ToLowerInvariant();return k=="shorts"||k=="short"?"shorts":"long";}
        List<string> BankOf(string lang,string kind){
            lang=NormLang(lang);kind=NormKind(kind);
            if(lang=="EN")return kind=="shorts"?settings.TitleBankShortsEn:settings.TitleBankLongEn;
            return kind=="shorts"?settings.TitleBankShortsRu:settings.TitleBankLongRu;
        }
        void SeedDraftsFromSettings(){
            foreach(var L in new[]{"RU","EN"})foreach(var K in new[]{"long","shorts"})
                drafts[KeyOf(L,K)]=string.Join(Environment.NewLine,BankOf(L,K)??new List<string>());
        }
        void CommitCurrent(){drafts[KeyOf(lang,kind)]=words.Text??"";}
        void Switch(string nextLang,string nextKind,bool commit=true){
            if(commit)CommitCurrent();
            lang=NormLang(nextLang);kind=NormKind(nextKind);
            string key=KeyOf(lang,kind);
            if(!drafts.ContainsKey(key))drafts[key]=string.Join(Environment.NewLine,BankOf(lang,kind));
            words.Text=drafts[key];
            PaintSeg(langRu,lang=="RU");PaintSeg(langEn,lang=="EN");PaintSeg(kindLong,kind=="long");PaintSeg(kindShorts,kind=="shorts");
            RefreshCounter();
        }
        static void PaintSeg(Button b,bool on){b.BackColor=on?Ui.Blue:Color.FromArgb(228,235,246);b.ForeColor=on?Color.White:Ui.Ink;}
        void RefreshCounter(){
            var lines=ParseLines(words.Text);
            counter.Text=(lang=="EN"?"EN":"RU")+" · "+(kind=="shorts"?"Shorts":"Long")+" · "+(lines.Count==0?"пусто":(lines.Count+" шт."));
            counter.ForeColor=lines.Count==0?Color.FromArgb(180,60,40):Color.FromArgb(30,130,70);
        }
        static List<string> ParseLines(string text){
            if(string.IsNullOrEmpty(text))return new List<string>();
            var normalized=text.Replace("\r\n","\n").Replace('\r','\n').Replace('\u2028','\n').Replace('\u2029','\n').Replace('\v','\n');
            return normalized.Split('\n').Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
        }
        void ApplyDraftsToSettings(){
            CommitCurrent();
            foreach(var L in new[]{"RU","EN"})foreach(var K in new[]{"long","shorts"}){
                var list=ParseLines(drafts.ContainsKey(KeyOf(L,K))?drafts[KeyOf(L,K)]:"");
                var tooLong=list.Select((t,i)=>new{t,i}).Where(x=>x.t.Length>100).ToList();
                if(tooLong.Count>0)throw new Exception((L=="EN"?"EN":"RU")+" · "+(K=="shorts"?"Shorts":"Long")+": заголовок №"+(tooLong[0].i+1)+" длиннее 100 символов.");
                var bank=BankOf(L,K);bank.Clear();bank.AddRange(list);
            }
            settings.TitleBanksSeeded=true;
        }
    }

    /// <summary>Несколько видео → выбрать канал для каждого. Заголовки берутся из базы.</summary>
    public class VideoChannelAssignDialog:Dialog {
        public List<(string Path,YouTubeChannel Channel)> Result=new List<(string,YouTubeChannel)>();
        readonly string[] files;readonly List<YouTubeChannel> channels;DataGridView grid;ComboBox applyCombo;
        public VideoChannelAssignDialog(string[] videoFiles,List<YouTubeChannel> channelList):base("Выбрать видео",760){
            files=videoFiles;channels=channelList??new List<YouTubeChannel>();
            if(channels.Count==0)throw new Exception("Сначала добавьте каналы в текущем рынке.");
            ClientSize=new Size(760,560);
            var table=Ui.Table();table.RowCount=3;table.RowStyles.Add(new RowStyle(SizeType.AutoSize));table.RowStyles.Add(new RowStyle(SizeType.Absolute,44));table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            table.Controls.Add(Ui.Label("Для каждого файла выберите канал. Или один канал сразу на все видео. Заголовки — из базы (длинное / шортс).",true),0,0);
            var applyBar=Ui.Flow();applyBar.WrapContents=false;applyBar.Margin=new Padding(0,6,0,4);
            applyBar.Controls.Add(new Label{Text="Один канал на все:",AutoSize=true,Margin=new Padding(0,8,8,0),ForeColor=Ui.Muted});
            applyCombo=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=320,FlatStyle=FlatStyle.Flat};
            foreach(var c in channels)applyCombo.Items.Add(ChannelLabel(c));
            if(applyCombo.Items.Count>0)applyCombo.SelectedIndex=0;
            applyBar.Controls.Add(applyCombo);
            applyBar.Controls.Add(Ui.Button("На все видео",()=>{
                if(applyCombo.SelectedItem==null)return;
                string label=Convert.ToString(applyCombo.SelectedItem);
                for(int i=0;i<grid.Rows.Count;i++)grid.Rows[i].Cells["ch"].Value=label;
            },true));
            table.Controls.Add(applyBar,0,1);
            grid=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,SelectionMode=DataGridViewSelectionMode.FullRowSelect};
            grid.RowTemplate.Height=32;
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="file",HeaderText="Видео",ReadOnly=true,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,MinimumWidth=220});
            var chCol=new DataGridViewComboBoxColumn{Name="ch",HeaderText="Канал",FlatStyle=FlatStyle.Flat,Width=280};
            foreach(var c in channels)chCol.Items.Add(ChannelLabel(c));
            grid.Columns.Add(chCol);
            table.Controls.Add(grid,0,2);Body.Controls.Add(table);
            for(int i=0;i<files.Length;i++){
                string label=ChannelLabel(channels[Math.Min(i,channels.Count-1)]);
                grid.Rows.Add(Path.GetFileName(files[i]),label);
                grid.Rows[i].Tag=files[i];
            }
            var ok=Ui.Button("Назначить",()=>{try{Build();DialogResult=DialogResult.OK;Close();}catch(Exception e){Ui.Error(this,e);}},true);
            var cancel=Ui.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();});
            Bottom.Controls.Add(ok);Bottom.Controls.Add(cancel);CancelButton=cancel;
        }
        static string ChannelLabel(YouTubeChannel c){
            string kind=string.Equals(c.Kind,"shorts",StringComparison.OrdinalIgnoreCase)?"шортс":"длинное";
            return (c.Name??"Канал")+" · "+kind+(string.IsNullOrWhiteSpace(c.ProfileId)?"":(" · "+c.ProfileId));
        }
        void Build(){
            Result=new List<(string,YouTubeChannel)>();
            for(int i=0;i<grid.Rows.Count;i++){
                string path=Convert.ToString(grid.Rows[i].Tag)??files[i];
                string label=Convert.ToString(grid.Rows[i].Cells["ch"].Value??"");
                var ch=channels.FirstOrDefault(c=>ChannelLabel(c)==label);
                if(ch==null)throw new Exception("Строка "+(i+1)+": выберите канал.");
                Result.Add((path,ch));
            }
        }
    }

    public class UploadWindow:Form {
        const int COn=0,CName=1,CLang=2,CKind=3,CProfile=4,CIp=5,CVideo=6,CVideoPick=7,CTitle=8,CThumb=9,CThumbPick=10,CThumbClear=11,CStatus=12,CRemove=13;
        static readonly string[] FilterLabels=new[]{"Загруженные сегодня","За эту неделю","За этот месяц","За этот год","Без фильтра"};
        static readonly string[] FilterKeys=new[]{"today","week","month","year","none"};
        static readonly string[] LangLabels=new[]{"RU","EN"};
        static readonly string[] KindLabels=new[]{"Длинное","Шортс"};
        static readonly string[] KindKeys=new[]{"long","shorts"};
        Preferences settings;DataGridView grid;RichTextBox log;TextBox searchKeys,searchFullTitle,searchUrl;ComboBox searchFilter;Button setup,add,files,titlesBtn,check,search,upload,meshWatch,stop,marketRu,marketEn,kindShorts,kindLong,applyThumb,moreBtn;Label marketHint,runHint,channelsLabel;CancellationTokenSource cancellation;CancellationTokenSource uploadCts;int uploadsInFlight;readonly HashSet<string> busyProfiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);string logFile;string marketView="RU";readonly object saveLock=new object();readonly object uploadLock=new object();
        public UploadWindow(Preferences source){settings=source;marketView=NormMarket(settings.YouTubeMarketView);Text="YouTube";ClientSize=new Size(1180,780);MinimumSize=new Size(960,640);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",9.5f);BackColor=Ui.Bg;ForeColor=Ui.Ink;AutoScaleMode=AutoScaleMode.Dpi;
            try{Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}
            var root=Ui.Table();root.Padding=new Padding(18);root.RowCount=8;root.RowStyles.Add(new RowStyle(SizeType.Absolute,66));root.RowStyles.Add(new RowStyle(SizeType.Absolute,42));root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));root.RowStyles.Add(new RowStyle(SizeType.Absolute,48));root.RowStyles.Add(new RowStyle(SizeType.Absolute,145));root.RowStyles.Add(new RowStyle(SizeType.Percent,64));root.RowStyles.Add(new RowStyle(SizeType.Percent,36));root.RowStyles.Add(new RowStyle(SizeType.Absolute,54));Controls.Add(root);
            var heading=Ui.Table(2);heading.ColumnStyles[0].Width=58;heading.ColumnStyles[1].Width=42;heading.Controls.Add(new Label{Text="YouTube",AutoSize=true,Font=new Font("Segoe UI",22,FontStyle.Bold),ForeColor=Ui.Ink},0,0);var note=new Label{Text="Заголовки · видео · загрузка",TextAlign=ContentAlignment.MiddleRight,Dock=DockStyle.Fill,ForeColor=Ui.Muted};heading.Controls.Add(note,1,0);root.Controls.Add(heading,0,0);
            var marketBar=Ui.Flow();marketBar.WrapContents=false;
            marketBar.Controls.Add(new Label{Text="Работаю с:",AutoSize=true,Margin=new Padding(0,8,10,0),ForeColor=Ui.Muted});
            marketRu=Ui.Button("Русские",()=>SwitchMarket("RU"));marketEn=Ui.Button("English",()=>SwitchMarket("EN"));
            kindShorts=Ui.Button("Шортс",()=>SetAllKind("shorts"));kindLong=Ui.Button("Длинное",()=>SetAllKind("long"));
            marketHint=new Label{AutoSize=true,Margin=new Padding(16,8,0,0),ForeColor=Ui.Muted};
            marketBar.Controls.Add(marketRu);marketBar.Controls.Add(marketEn);
            marketBar.Controls.Add(new Label{Text="Тип:",AutoSize=true,Margin=new Padding(18,8,8,0),ForeColor=Ui.Muted});
            marketBar.Controls.Add(kindShorts);marketBar.Controls.Add(kindLong);marketBar.Controls.Add(marketHint);root.Controls.Add(marketBar,0,1);
            runHint=new Label{Dock=DockStyle.Fill,AutoSize=false,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Color.FromArgb(20,100,60),Font=new Font("Segoe UI",9.5f,FontStyle.Bold),Visible=false};
            root.Controls.Add(runHint,0,2);
            var tools=Ui.Flow();tools.WrapContents=true;
            files=Ui.Button("Добавить видео",AssignVideosToSelectedChannel,true);
            titlesBtn=Ui.Button("Заголовки",OpenTitleBanks);
            moreBtn=Ui.Button("⋯",ShowMoreMenu);
            var help=Ui.Button("?",()=>MessageBox.Show(this,"Рабочий процесс:\n1) Заголовки — один раз (RU/EN · Long/Shorts).\n2) Выберите канал в таблице.\n3) «Добавить видео» — файлы сразу к этому каналу.\n4) «Загрузить» — отложенная публикация пачкой.\n\n«⋯» — распределение по каналам, Dolphin, проверка.","YouTube",MessageBoxButtons.OK,MessageBoxIcon.Information));
            tools.Controls.Add(files);tools.Controls.Add(titlesBtn);tools.Controls.Add(moreBtn);tools.Controls.Add(help);root.Controls.Add(tools,0,3);
            setup=Ui.Button("Dolphin",Configure);check=Ui.Button("Проверить",null);check.Click+=async(s,e)=>await CheckProfiles();
            var searchPanel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=3,Padding=new Padding(0)};searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55));searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,170));searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));searchPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,56));searchPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,36));searchPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            searchKeys=new TextBox{Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Vertical};
            searchFullTitle=new TextBox{Dock=DockStyle.Fill};searchUrl=new TextBox{Dock=DockStyle.Fill};
            searchFilter=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};searchFilter.Items.AddRange(FilterLabels);
            search=Ui.Button("Найти",null,true);search.MinimumSize=new Size(120,32);search.Click+=async(s,e)=>await SearchOnYouTube();
            searchPanel.Controls.Add(new Label{Text="Ключи",AutoSize=true,TextAlign=ContentAlignment.MiddleLeft,Dock=DockStyle.Fill,ForeColor=Ui.Muted},0,0);
            searchPanel.Controls.Add(searchKeys,1,0);
            searchPanel.Controls.Add(searchFilter,2,0);
            searchPanel.Controls.Add(search,3,0);
            searchPanel.Controls.Add(new Label{Text="Заголовок",AutoSize=true,TextAlign=ContentAlignment.MiddleLeft,Dock=DockStyle.Fill,ForeColor=Ui.Muted},0,1);
            searchPanel.Controls.Add(searchFullTitle,1,1);searchPanel.SetColumnSpan(searchFullTitle,3);
            searchPanel.Controls.Add(new Label{Text="Ссылка",AutoSize=true,TextAlign=ContentAlignment.MiddleLeft,Dock=DockStyle.Fill,ForeColor=Ui.Muted},0,2);
            searchPanel.Controls.Add(searchUrl,1,2);searchPanel.SetColumnSpan(searchUrl,3);
            root.Controls.Add(searchPanel,0,4);
            var channelPane=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(0)};channelPane.RowStyles.Add(new RowStyle(SizeType.Absolute,36));channelPane.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var channelBar=Ui.Flow();channelBar.WrapContents=false;
            channelsLabel=new Label{Text="Каналы",AutoSize=true,Font=new Font("Segoe UI",10,FontStyle.Bold),Margin=new Padding(0,8,12,0)};
            add=Ui.Button("+",AddRow);add.MinimumSize=new Size(36,32);add.Padding=new Padding(0);
            channelBar.Controls.Add(channelsLabel);channelBar.Controls.Add(add);
            channelPane.Controls.Add(channelBar,0,0);
            grid=new DataGridView{Dock=DockStyle.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None,SelectionMode=DataGridViewSelectionMode.FullRowSelect};grid.RowTemplate.Height=38;BuildColumns();grid.CellContentClick+=CellClick;grid.CellEndEdit+=(s,e)=>SaveGrid();grid.SelectionChanged+=(s,e)=>{if(grid.CurrentRow?.Tag is YouTubeChannel ch)RememberSelectedChannel(ch);};grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
            channelPane.Controls.Add(grid,0,1);root.Controls.Add(channelPane,0,5);
            var logs=Ui.Table();logs.RowCount=2;logs.RowStyles.Add(new RowStyle(SizeType.Absolute,28));logs.RowStyles.Add(new RowStyle(SizeType.Percent,100));logs.Controls.Add(new Label{Text="Журнал работы",AutoSize=true,Font=new Font("Segoe UI",10,FontStyle.Bold)},0,0);log=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,BackColor=Color.White,BorderStyle=BorderStyle.None,Font=new Font("Consolas",9),DetectUrls=true};logs.Controls.Add(log,0,1);root.Controls.Add(logs,0,6);
            var bottom=Ui.Flow();upload=Ui.Button("Загрузить",null,true);upload.MinimumSize=new Size(180,42);upload.Click+=async(s,e)=>await UploadAll();meshWatch=Ui.Button("Сетка просмотр",null);meshWatch.MinimumSize=new Size(150,42);meshWatch.Click+=async(s,e)=>await CrossWatchMesh();applyThumb=Ui.Button("Превью шортс",null);applyThumb.MinimumSize=new Size(140,42);applyThumb.Click+=async(s,e)=>await ApplyShortsThumbFrames();stop=Ui.Button("Стоп",()=>{stop.Enabled=false;if(uploadCts!=null)uploadCts.Cancel();if(cancellation!=null)cancellation.Cancel();Write("Остановка по запросу…");});stop.Visible=false;var openLog=Ui.Button("Лог",()=>{try{if(File.Exists(logFile))Ui.Open(logFile);else throw new Exception("Лог ещё не создан.");}catch(Exception e){Ui.Error(this,e);}});bottom.Controls.Add(upload);bottom.Controls.Add(meshWatch);bottom.Controls.Add(applyThumb);bottom.Controls.Add(stop);bottom.Controls.Add(openLog);root.Controls.Add(bottom,0,7);
            LoadSearchFieldsForMarket();LoadGrid();RefreshMarketUi();
            FormClosing+=(s,e)=>{SaveGrid();SaveSearchFields();if(cancellation!=null||uploadsInFlight>0){e.Cancel=true;MessageBox.Show(this,"Сначала остановите текущую операцию (Стоп).","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);}};
            Shown+=(s,e)=>{if(string.IsNullOrWhiteSpace(WindowsSupport.Unprotect(settings.ProtectedDolphinToken)))Configure();};
        }
        static string NormMarket(string m){m=(m??"").Trim().ToUpperInvariant();return m=="EN"?"EN":"RU";}
        static string MarketLabel(string m){return NormMarket(m)=="EN"?"English":"Русские";}
        void RefreshMarketUi(){
            bool ru=marketView=="RU";
            marketRu.BackColor=ru?Ui.Blue:Color.FromArgb(228,235,246);marketRu.ForeColor=ru?Color.White:Ui.Ink;
            marketEn.BackColor=!ru?Ui.Blue:Color.FromArgb(228,235,246);marketEn.ForeColor=!ru?Color.White:Ui.Ink;
            int n=(settings.YouTubeChannels??new List<YouTubeChannel>()).Count(c=>NormMarket(c.Market)==marketView);
            marketHint.Text=MarketLabel(marketView)+" · каналов: "+n;
            Text="YouTube · "+MarketLabel(marketView);
        }
        void SwitchMarket(string next){
            next=NormMarket(next);if(next==marketView)return;
            if(cancellation!=null||uploadsInFlight>0){MessageBox.Show(this,"Сначала остановите текущую операцию.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            SaveGrid();SaveSearchFields();
            marketView=next;settings.YouTubeMarketView=marketView;Save();
            LoadSearchFieldsForMarket();LoadGrid();RefreshMarketUi();
            Write("Переключено на рынок: "+MarketLabel(marketView)+".");
        }
        void SetAllKind(string kind){
            kind=NormKind(kind);
            if(cancellation!=null||uploadsInFlight>0){MessageBox.Show(this,"Сначала остановите текущую операцию.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            SaveGrid();
            int n=0;
            foreach(var row in VisibleRows()){
                var c=(YouTubeChannel)row.Tag;c.Kind=kind;row.Cells[CKind].Value=KindLabel(kind);n++;
            }
            SaveGrid();
            kindShorts.BackColor=kind=="shorts"?Ui.Blue:Color.FromArgb(228,235,246);kindShorts.ForeColor=kind=="shorts"?Color.White:Ui.Ink;
            kindLong.BackColor=kind=="long"?Ui.Blue:Color.FromArgb(228,235,246);kindLong.ForeColor=kind=="long"?Color.White:Ui.Ink;
            Write("["+MarketLabel(marketView)+"] тип всех каналов: "+KindLabel(kind)+" ("+n+").");
        }
        void LoadSearchFieldsForMarket(){
            if(marketView=="EN"){
                searchKeys.Text=!string.IsNullOrWhiteSpace(settings.YouTubeSearchKeysEn)?settings.YouTubeSearchKeysEn:(settings.YouTubeSearchTitleEn??"");
                searchFullTitle.Text=settings.YouTubeSearchFullTitleEn??"";searchUrl.Text=settings.YouTubeSearchUrlEn??"";
                int fi=Array.IndexOf(FilterKeys,settings.YouTubeSearchFilterEn??"today");searchFilter.SelectedIndex=fi>=0?fi:0;
            }else{
                searchKeys.Text=!string.IsNullOrWhiteSpace(settings.YouTubeSearchKeysRu)?settings.YouTubeSearchKeysRu:(settings.YouTubeSearchTitleRu??"");
                searchFullTitle.Text=settings.YouTubeSearchFullTitleRu??"";searchUrl.Text=settings.YouTubeSearchUrlRu??"";
                int fi=Array.IndexOf(FilterKeys,settings.YouTubeSearchFilterRu??"today");searchFilter.SelectedIndex=fi>=0?fi:0;
            }
        }
        void BuildColumns(){
            grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="on",HeaderText="✓",Width=36});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="name",HeaderText="Канал",Width=110});
            var lang=new DataGridViewComboBoxColumn{Name="lang",HeaderText="Рынок",Width=64,FlatStyle=FlatStyle.Flat};lang.Items.AddRange(LangLabels);grid.Columns.Add(lang);
            var kind=new DataGridViewComboBoxColumn{Name="kind",HeaderText="Тип",Width=84,FlatStyle=FlatStyle.Flat};kind.Items.AddRange(KindLabels);grid.Columns.Add(kind);
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="profile",HeaderText="Profile ID",Width=140});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="ip",HeaderText="IP",Width=110,ReadOnly=true});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="video",HeaderText="Видео",Width=140,ReadOnly=true});
            grid.Columns.Add(new DataGridViewButtonColumn{Name="videoPick",HeaderText="",Text="…",UseColumnTextForButtonValue=true,Width=34});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="title",HeaderText="Заголовок",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,MinimumWidth=150});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="thumb",HeaderText="Превью",Width=90,ReadOnly=true});
            grid.Columns.Add(new DataGridViewButtonColumn{Name="thumbPick",HeaderText="",Text="…",UseColumnTextForButtonValue=true,Width=34});
            grid.Columns.Add(new DataGridViewButtonColumn{Name="thumbClear",HeaderText="",Text="×",UseColumnTextForButtonValue=true,Width=34});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="status",HeaderText="Статус",Width=130,ReadOnly=true});
            grid.Columns.Add(new DataGridViewButtonColumn{Name="remove",HeaderText="",Text="×",UseColumnTextForButtonValue=true,Width=34});
        }
        void LoadGrid(){
            grid.Rows.Clear();
            foreach(var c in settings.YouTubeChannels??new List<YouTubeChannel>()){
                if(NormMarket(c.Market)!=marketView)continue;
                AddGridRow(c);
            }
            if(grid.Rows.Count==0)AddGridRow(new YouTubeChannel{Name=marketView=="EN"?"Channel 1":"Канал 1",Market=marketView,Kind="long"});
            RefreshMarketUi();
        }
        static string NormKind(string k){k=(k??"").Trim().ToLowerInvariant();return k=="shorts"?"shorts":"long";}
        static string KindLabel(string k){return NormKind(k)=="shorts"?"Шортс":"Длинное";}
        static string KindKeyFromLabel(string label){return string.Equals((label??"").Trim(),"Шортс",StringComparison.OrdinalIgnoreCase)?"shorts":"long";}
        void AddGridRow(YouTubeChannel c){
            c.Market=NormMarket(c.Market);c.Kind=NormKind(c.Kind);
            if(c.Items==null)c.Items=new List<YouTubeItem>();
            SyncChannelPrimary(c);
            string videoLabel=c.Items.Count>1?(c.Items.Count+" файлов"):Short(c.Video);
            // В ячейку — только реальный заголовок (без « · +N»), иначе SaveGrid записывал сводку обратно в Title.
            string titleLabel=c.Items.Count>0?CleanTitle(c.Items[0].Title):CleanTitle(c.Title);
            grid.Rows.Add(c.Enabled,c.Name,c.Market,KindLabel(c.Kind),c.ProfileId,c.ExpectedIp,videoLabel,"…",titleLabel,Short(c.Thumbnail),"…","×",string.IsNullOrWhiteSpace(c.Status)?"Готов":c.Status,"×");
            var row=grid.Rows[grid.Rows.Count-1];row.Tag=c;
            row.Cells[CVideo].ToolTipText=c.Items.Count>1?string.Join(Environment.NewLine,c.Items.Select((it,i)=>(i+1)+". "+Short(it.Video))):c.Video;
            row.Cells[CThumb].ToolTipText=c.Thumbnail;
            row.Cells[CTitle].ToolTipText=c.Items.Count>1?string.Join(Environment.NewLine,c.Items.Select((it,i)=>(i+1)+". "+CleanTitle(it.Title))):(string.IsNullOrWhiteSpace(titleLabel)?"Заголовок уйдёт на YouTube":titleLabel);
        }
        static void SyncChannelPrimary(YouTubeChannel c){
            if(c.Items==null)c.Items=new List<YouTubeItem>();
            foreach(var it in c.Items){if(it!=null)it.Title=CleanTitle(it.Title);}
            c.Title=CleanTitle(c.Title);
            if(c.Items.Count==0){
                if(!string.IsNullOrWhiteSpace(c.Video)||!string.IsNullOrWhiteSpace(c.Title))
                    c.Items.Add(new YouTubeItem{Video=c.Video??"",Title=c.Title??"",Thumbnail=c.Thumbnail??""});
            }
            if(c.Items.Count>0){c.Video=c.Items[0].Video??"";c.Title=c.Items[0].Title??"";c.Thumbnail=c.Items[0].Thumbnail??"";}
        }
        static string Short(string path){return string.IsNullOrWhiteSpace(path)?"":Path.GetFileName(path);}
        /// <summary>Убирает мусор вида « · +3», который раньше попадал в заголовок из сводки пачки в таблице.</summary>
        static string CleanTitle(string title){
            string t=(title??"").Trim();
            t=System.Text.RegularExpressions.Regex.Replace(t,@"^\d+\.\s*","");
            t=System.Text.RegularExpressions.Regex.Replace(t,@"\s*[·•]\s*\+\d+\s*$","");
            t=System.Text.RegularExpressions.Regex.Replace(t,@"\s+\+\d+\s*$","");
            t=System.Text.RegularExpressions.Regex.Replace(t,@"\s*\(\d+\)\s*$","").Trim();
            t=System.Text.RegularExpressions.Regex.Replace(t,@"\s+"," ").Trim();
            return t;
        }
        static string ClampTitle(string title,int max=100){
            string t=CleanTitle(title);
            if(t.Length<=max)return t;
            return t.Substring(0,max).Trim();
        }
        /// <summary>Безопасные имена файлов Windows; в Studio выглядят как обычные заголовки с « . ».</summary>
        static string MakeWindowsSafeTitle(string title){
            string t=CleanTitle(title);
            var parts=System.Text.RegularExpressions.Regex.Split(t,@"\s*\|\s*");
            var cleanParts=new List<string>();
            foreach(var part in parts){
                string p=(part??"").Trim();
                if(p.Length==0)continue;
                p=p.Replace("?","");
                p=System.Text.RegularExpressions.Regex.Replace(p,@":\s*"," . ");
                p=p.Replace("/",".").Replace("\\",".").Replace("*","");
                p=p.Replace("<","‹").Replace(">","›");
                p=System.Text.RegularExpressions.Regex.Replace(p,"\"([^\"]+)\"","«$1»");
                p=p.Replace("\"","");
                foreach(var ch in Path.GetInvalidFileNameChars())p=p.Replace(ch,' ');
                p=System.Text.RegularExpressions.Regex.Replace(p,@"\s+"," ").Trim();
                if(p.Length>0)cleanParts.Add(p);
            }
            t=cleanParts.Count>0?string.Join(" . ",cleanParts):t;
            foreach(var ch in Path.GetInvalidFileNameChars())t=t.Replace(ch,' ');
            t=System.Text.RegularExpressions.Regex.Replace(t,@"\s+"," ").Trim();
            return t;
        }
        static void ValidateTitleLength(string title,string context){
            string t=CleanTitle(title);
            if(t.Length>100)throw new Exception(context+": заголовок длиннее 100 символов ("+t.Length+"): «"+(t.Length>48?t.Substring(0,48)+"…":t)+"»");
            t=MakeWindowsSafeTitle(t);
            if(t.Length>100)throw new Exception(context+": после замены символов заголовок длиннее 100 символов ("+t.Length+").");
        }
        static string FileNameFromTitle(string title){
            string t=MakeWindowsSafeTitle(title);
            ValidateTitleLength(t,"Имя файла");
            return string.IsNullOrWhiteSpace(t)?"video":t;
        }
        static string AppendDuplicateSuffix(string title,int n){
            string suffix=" ("+n+")";
            string baseTitle=System.Text.RegularExpressions.Regex.Replace(CleanTitle(title),@"\s*\(\d+\)\s*$","").Trim();
            if(baseTitle.Length+suffix.Length>100)baseTitle=baseTitle.Substring(0,Math.Max(1,100-suffix.Length)).Trim();
            return baseTitle+suffix;
        }
        static List<string> ResolveDuplicateTitlesInBatch(List<string> titles,string context){
            var outList=new List<string>(titles.Count);
            var usedStems=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<titles.Count;i++){
                string raw=CleanTitle(titles[i]??"");
                ValidateTitleLength(raw,context+" ["+(i+1)+"/"+titles.Count+"]");
                string candidate=MakeWindowsSafeTitle(raw);
                string stem=FileNameFromTitle(candidate);
                if(!usedStems.Contains(stem)){usedStems.Add(stem);outList.Add(raw);continue;}
                bool placed=false;
                for(int n=2;n<50;n++){
                    string alt=AppendDuplicateSuffix(raw,n);
                    ValidateTitleLength(alt,context+" ["+(i+1)+"] дубликат");
                    stem=FileNameFromTitle(MakeWindowsSafeTitle(alt));
                    if(usedStems.Add(stem)){outList.Add(alt);placed=true;break;}
                }
                if(!placed)throw new Exception(context+": не удалось разрешить дубликат заголовка «"+raw.Substring(0,Math.Min(40,raw.Length))+"».");
            }
            return outList;
        }
        static string PlannedFileName(string title,int index1Based,int total,string videoPath){
            string ext=Path.GetExtension(videoPath??"");if(string.IsNullOrWhiteSpace(ext))ext=".mp4";
            string finalTitle=PackTitleWithIndex(title,index1Based,total);
            return FileNameFromTitle(finalTitle)+ext;
        }
        static void ValidateFileReady(string path,string context){
            if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))throw new Exception(context+": файл не найден: "+path);
            var fi=new FileInfo(path);
            if(fi.Length<=0)throw new Exception(context+": файл пустой: "+Path.GetFileName(path));
            if(path.Length>240)throw new Exception(context+": слишком длинный путь ("+path.Length+" симв.)");
            try{using(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){}}
            catch(IOException ex){throw new Exception(context+": файл занят — "+Path.GetFileName(path)+" ("+ex.Message+")");}
        }
        static void EnsureUploadStagingDir(string folder){
            if(string.IsNullOrWhiteSpace(folder))folder=@"C:\VideoBatch\Upload";
            if(!Directory.Exists(folder))Directory.CreateDirectory(folder);
        }
        string StageUploadFile(string sourcePath,string profileId,int index){
            string staging=string.IsNullOrWhiteSpace(settings.UploadStagingFolder)?@"C:\VideoBatch\Upload":settings.UploadStagingFolder.Trim();
            EnsureUploadStagingDir(staging);
            string profileDir=Path.Combine(staging,SanitizeDirName(profileId));
            if(!Directory.Exists(profileDir))Directory.CreateDirectory(profileDir);
            string name=Path.GetFileName(sourcePath);
            string dest=Path.Combine(profileDir,index.ToString("D2")+"_"+name);
            if(!File.Exists(dest)||new FileInfo(sourcePath).LastWriteTimeUtc>new FileInfo(dest).LastWriteTimeUtc)
                File.Copy(sourcePath,dest,true);
            return dest;
        }
        static string SanitizeDirName(string name){
            name=(name??"").Trim();
            foreach(var ch in Path.GetInvalidFileNameChars())name=name.Replace(ch,'_');
            return string.IsNullOrWhiteSpace(name)?"profile":name;
        }
        /// <summary>Короткие ключи для поиска из заголовка (2 строки: 4 и 7 слов).</summary>
        static string SearchKeysFromTitle(string title){
            string t=CleanTitle(title);
            if(string.IsNullOrWhiteSpace(t))return "";
            var words=t.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
            if(words.Length<=5)return t;
            string k1=string.Join(" ",words.Take(4));
            string k2=string.Join(" ",words.Take(7));
            return string.Equals(k1,k2,StringComparison.Ordinal)?k1:(k1+"\n"+k2);
        }
        static void SyncPublishedMeta(YouTubeItem it){
            if(it==null)return;
            if(string.IsNullOrWhiteSpace(it.PublishedVideoId)&&!string.IsNullOrWhiteSpace(it.PublishedUrl))
                it.PublishedVideoId=Store.ExtractYouTubeVideoId(it.PublishedUrl);
        }
        static YouTubeItem PickAnchorVideo(YouTubeChannel ch){
            if(ch==null)return null;
            SyncChannelPrimary(ch);
            if(ch.Items==null||ch.Items.Count==0){
                if(string.IsNullOrWhiteSpace(ch.Title))return null;
                return new YouTubeItem{Video=ch.Video??"",Title=ch.Title,Thumbnail=ch.Thumbnail??"",PublishedUrl=""};
            }
            YouTubeItem pick=ch.Items.FirstOrDefault(it=>it!=null&&!string.IsNullOrWhiteSpace(it.PublishedVideoId)&&!string.IsNullOrWhiteSpace(it.Title));
            if(pick==null)pick=ch.Items.FirstOrDefault(it=>it!=null&&!string.IsNullOrWhiteSpace(it.PublishedUrl)&&!string.IsNullOrWhiteSpace(it.Title));
            if(pick==null)pick=ch.Items.FirstOrDefault(it=>it!=null&&!string.IsNullOrWhiteSpace(it.Title));
            SyncPublishedMeta(pick);
            return pick;
        }
        static readonly System.Text.RegularExpressions.Regex ChannelHandleRx=new System.Text.RegularExpressions.Regex(@"@([A-Za-z0-9._-]+)",System.Text.RegularExpressions.RegexOptions.Compiled);
        static string ExtractChannelHandle(string name){
            var m=ChannelHandleRx.Match(name??"");
            return m.Success?m.Groups[1].Value:"";
        }
        static string ChannelUrlFromHandle(string handle){
            handle=(handle??"").Trim().TrimStart('@');
            return string.IsNullOrWhiteSpace(handle)?"":"https://www.youtube.com/@"+handle;
        }
        static string ResolveChannelUrlFromGrid(YouTubeChannel ch){
            if(ch==null)return "";
            if(!string.IsNullOrWhiteSpace(ch.ChannelUrl))return ch.ChannelUrl.Trim();
            return ChannelUrlFromHandle(ExtractChannelHandle(ch.Name));
        }
        static void SyncChannelUrlFromName(YouTubeChannel c){
            if(c==null)return;
            string fromName=ChannelUrlFromHandle(ExtractChannelHandle(c.Name));
            if(!string.IsNullOrWhiteSpace(fromName))c.ChannelUrl=fromName;
        }
        static CatalogVideoJob[] BuildCatalogVideos(YouTubeChannel ch){
            SyncChannelPrimary(ch);
            if(ch?.Items==null||ch.Items.Count==0)return Array.Empty<CatalogVideoJob>();
            string baseKind=string.IsNullOrWhiteSpace(ch.Kind)?"long":ch.Kind.Trim().ToLowerInvariant();
            var list=new List<CatalogVideoJob>();
            for(int i=0;i<ch.Items.Count;i++){
                var it=ch.Items[i];
                if(it==null||string.IsNullOrWhiteSpace(it.Title))continue;
                SyncPublishedMeta(it);
                string kind=baseKind;
                if(ch.Items.Count>1&&baseKind=="long"&&i>0)kind="shorts";
                string vid=it.PublishedVideoId??"";
                string url=it.PublishedUrl??"";
                if(string.IsNullOrWhiteSpace(url)&&!string.IsNullOrWhiteSpace(vid))url="https://www.youtube.com/watch?v="+vid;
                list.Add(new CatalogVideoJob{
                    title=ClampTitle(it.Title),
                    videoId=vid,
                    url=url,
                    kind=kind
                });
            }
            return list.ToArray();
        }
        WatchTargetJob BuildMeshTarget(YouTubeChannel ch){
            var anchor=PickAnchorVideo(ch);
            if(anchor==null||string.IsNullOrWhiteSpace(anchor.Title))return null;
            SyncPublishedMeta(anchor);
            SyncChannelUrlFromName(ch);
            string vid=anchor.PublishedVideoId??"";
            string url=anchor.PublishedUrl??"";
            if(string.IsNullOrWhiteSpace(url)&&!string.IsNullOrWhiteSpace(vid))url="https://www.youtube.com/watch?v="+vid;
            return new WatchTargetJob{
                title=ClampTitle(anchor.Title),
                searchFullTitle=ClampTitle(anchor.Title),
                searchKeys=SearchKeysFromTitle(anchor.Title),
                searchUrl=url,
                videoId=vid,
                channelUrl=ResolveChannelUrlFromGrid(ch),
                ownerName=ch.Name??"",
                ownerProfileId=(ch.ProfileId??"").Trim(),
                catalogVideos=BuildCatalogVideos(ch)
            };
        }
        List<YouTubeChannel> CollectMeshChannels(IEnumerable<DataGridViewRow> rows){
            var list=new List<YouTubeChannel>();
            foreach(var row in rows){
                var c=(YouTubeChannel)row.Tag;
                if(c==null||!c.Enabled)continue;
                SyncChannelPrimary(c);
                if(PickAnchorVideo(c)==null)continue;
                list.Add(c);
            }
            return list;
        }
        void PropagateChannelUrl(string profileId,string channelUrl){
            if(string.IsNullOrWhiteSpace(profileId)||string.IsNullOrWhiteSpace(channelUrl))return;
            if(InvokeRequired){BeginInvoke(new Action(()=>PropagateChannelUrl(profileId,channelUrl)));return;}
            foreach(DataGridViewRow row in grid.Rows){
                if(!row.Visible)continue;
                var c=(YouTubeChannel)row.Tag;
                if(!string.Equals((c.ProfileId??"").Trim(),profileId.Trim(),StringComparison.OrdinalIgnoreCase))continue;
                c.ChannelUrl=channelUrl;
            }
            Store.SaveMeshCatalog(settings.YouTubeChannels??new List<YouTubeChannel>());
            SafeSave();
        }
        static string SanitizeFileName(string name){
            return FileNameFromTitle(name);
        }
        /// <summary>Номер в начале: «1. Заголовок» — удобно отличать файлы; база может повторяться.</summary>
        static string PackTitleWithIndex(string baseTitle,int index1Based,int total){
            string t=CleanTitle(baseTitle);
            if(string.IsNullOrWhiteSpace(t))return "";
            if(total<=1)return t;
            string prefix=index1Based+". ";
            int maxBase=Math.Max(1,100-prefix.Length);
            if(t.Length>maxBase)t=t.Substring(0,maxBase).Trim();
            return prefix+t;
        }
        static List<string> TitlesForVideoCount(IList<string> titles,int videoCount,string fallback=""){
            var clean=(titles??new string[0]).Select(CleanTitle).Where(x=>x.Length>0).ToList();
            if(clean.Count==0&&!string.IsNullOrWhiteSpace(fallback))clean.Add(CleanTitle(fallback));
            var result=new List<string>();
            if(videoCount<=0)return result;
            if(clean.Count==0){for(int i=0;i<videoCount;i++)result.Add("");return result;}
            for(int i=0;i<videoCount;i++)result.Add(clean[i%clean.Count]);
            return result;
        }
        static string RenameVideoFile(string path,string title,int index1Based,int total){
            if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))return path;
            if(string.IsNullOrWhiteSpace(title))return path;
            string finalTitle=PackTitleWithIndex(title,index1Based,total);
            ValidateTitleLength(finalTitle,"Переименование");
            string dir=Path.GetDirectoryName(path)??"";
            string ext=Path.GetExtension(path);
            string stem=FileNameFromTitle(finalTitle);
            string dest=Path.Combine(dir,stem+ext);
            if(string.Equals(Path.GetFullPath(path),Path.GetFullPath(dest),StringComparison.OrdinalIgnoreCase))return path;
            if(File.Exists(dest)){
                int n=2;
                while(n<100){
                    string dupTitle=AppendDuplicateSuffix(finalTitle,n);
                    stem=FileNameFromTitle(dupTitle);
                    dest=Path.Combine(dir,stem+ext);
                    if(!File.Exists(dest))break;
                    n++;
                }
            }
            try{File.Move(path,dest);return dest;}catch{return path;}
        }
        void ClearThumbnail(YouTubeChannel c){
            if(c==null)return;
            c.Thumbnail="";
            if(c.Items!=null)foreach(var it in c.Items)if(it!=null)it.Thumbnail="";
        }
        void RefreshRowDisplay(DataGridViewRow row){
            var c=(YouTubeChannel)row.Tag;SyncChannelPrimary(c);
            row.Cells[COn].Value=c.Enabled;
            row.Cells[CVideo].Value=c.Items!=null&&c.Items.Count>1?(c.Items.Count+" файлов"):Short(c.Video);
            row.Cells[CTitle].Value=c.Items!=null&&c.Items.Count>0?CleanTitle(c.Items[0].Title):CleanTitle(c.Title);
            row.Cells[CThumb].Value=Short(c.Thumbnail);
            row.Cells[CThumb].ToolTipText=c.Thumbnail;
            row.Cells[CVideo].ToolTipText=c.Items!=null&&c.Items.Count>1?string.Join(Environment.NewLine,c.Items.Select((it,i)=>(i+1)+". "+Short(it.Video))):c.Video;
            row.Cells[CTitle].ToolTipText=c.Items!=null&&c.Items.Count>1?string.Join(Environment.NewLine,c.Items.Select((it,i)=>(i+1)+". "+CleanTitle(it.Title))):"Заголовок уйдёт на YouTube";
            row.Cells[CKind].Value=KindLabel(c.Kind);
        }
        void AddRow(){SaveGrid();AddGridRow(new YouTubeChannel{Name=(marketView=="EN"?"Channel ":"Канал ")+(grid.Rows.Count+1),Market=marketView,Kind="long"});SaveGrid();RefreshMarketUi();}
        void CellClick(object sender,DataGridViewCellEventArgs e){if(e.RowIndex<0)return;var row=grid.Rows[e.RowIndex];var c=(YouTubeChannel)row.Tag;if(e.ColumnIndex==CVideoPick){var p=Ui.File(this,"Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*");if(p!=null){if(c.Items==null||c.Items.Count==0)c.Items=new List<YouTubeItem>{new YouTubeItem()};else if(c.Items.Count>1&&MessageBox.Show(this,"В канале уже пачка из "+c.Items.Count+" видео. Заменить первым файлом?","VideoBatch",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                string title="";
                string m=NormMarket(c.Market);
                try{title=PeekTitlesFromBank(c.Kind,1,m)[0];}catch(Exception ex){Ui.Error(this,ex);return;}
                string path=RenameVideoFile(p,title,1,1);
                c.Items=new List<YouTubeItem>{new YouTubeItem{Video=path,Title=PackTitleWithIndex(title,1,1),Thumbnail=c.Thumbnail??""}};SyncChannelPrimary(c);c.Enabled=true;row.Cells[COn].Value=true;AdvanceTitleCursor(c.Kind,1,m);Write(TitleCursorLabel(c.Kind,m));RefreshRowDisplay(row);}}else if(e.ColumnIndex==CThumbPick){var p=Ui.File(this,"Изображение|*.jpg;*.jpeg;*.png;*.webp|Все файлы|*.*");if(p!=null){c.Thumbnail=p;if(c.Items!=null&&c.Items.Count>0)c.Items[0].Thumbnail=p;RefreshRowDisplay(row);}}else if(e.ColumnIndex==CThumbClear){ClearThumbnail(c);SyncChannelPrimary(c);RefreshRowDisplay(row);}else if(e.ColumnIndex==CRemove){grid.Rows.RemoveAt(e.RowIndex);}SaveGrid();RefreshMarketUi();}
        List<string> TitleBankRef(string kind,string market=null){
            kind=NormKind(kind);bool en=NormMarket(market??marketView)=="EN";
            if(kind=="shorts")return en?settings.TitleBankShortsEn:settings.TitleBankShortsRu;
            return en?settings.TitleBankLongEn:settings.TitleBankLongRu;
        }
        int GetTitleCursor(string kind,string market=null){
            kind=NormKind(kind);bool en=NormMarket(market??marketView)=="EN";
            int cursor=kind=="shorts"?(en?settings.TitleCursorShortsEn:settings.TitleCursorShortsRu):(en?settings.TitleCursorLongEn:settings.TitleCursorLongRu);
            return Store.ClampTitleCursor(cursor,TitleBankRef(kind,market).Count);
        }
        void SetTitleCursor(string kind,int value,string market=null){
            kind=NormKind(kind);bool en=NormMarket(market??marketView)=="EN";
            int bankCount=TitleBankRef(kind,market).Count;
            value=Store.ClampTitleCursor(value,bankCount);
            if(kind=="shorts"){if(en)settings.TitleCursorShortsEn=value;else settings.TitleCursorShortsRu=value;}
            else{if(en)settings.TitleCursorLongEn=value;else settings.TitleCursorLongRu=value;}
        }
        string TitleCursorLabel(string kind,string market){
            kind=NormKind(kind);market=NormMarket(market??marketView);
            var bank=TitleBankRef(kind,market);
            if(bank.Count==0)return MarketLabel(market)+" · "+KindLabel(kind)+": база пуста";
            int next=GetTitleCursor(kind,market);
            return MarketLabel(market)+" · "+KindLabel(kind)+": следующий №"+(next+1)+" из "+bank.Count;
        }
        void OpenTitleBanks(){
            SaveGrid();
            using(var dlg=new TitleBanksHubDialog(settings)){
                if(dlg.ShowDialog(this)!=DialogResult.OK)return;
                Save();
                Write("База заголовков сохранена · RU Long "+settings.TitleBankLongRu.Count+" · RU Shorts "+settings.TitleBankShortsRu.Count+" · EN Long "+settings.TitleBankLongEn.Count+" · EN Shorts "+settings.TitleBankShortsEn.Count+".");
                Write("  "+TitleCursorLabel("long","RU")+" · "+TitleCursorLabel("shorts","RU")+" · "+TitleCursorLabel("long","EN")+" · "+TitleCursorLabel("shorts","EN"));
            }
        }
        void ShowMoreMenu(){
            var menu=new ContextMenuStrip();
            menu.Items.Add("Dolphin (токен)",null,(s,e)=>Configure());
            menu.Items.Add("Распределить видео по каналам…",null,(s,e)=>AssignVideosToChannels());
            var checkItem=new ToolStripMenuItem("Проверить профили");
            checkItem.Click+=async(s,e)=>{await CheckProfiles();};
            menu.Items.Add(checkItem);
            menu.Show(moreBtn,new Point(0,moreBtn.Height));
        }
        DataGridViewRow CurrentChannelRow(){
            if(grid.CurrentRow!=null&&grid.CurrentRow.Visible&&grid.CurrentRow.Tag is YouTubeChannel)return grid.CurrentRow;
            var sel=Selected();
            if(sel.Count>0)return sel[0];
            string pid=marketView=="EN"?settings.LastSelectedYouTubeProfileIdEn:settings.LastSelectedYouTubeProfileIdRu;
            if(!string.IsNullOrWhiteSpace(pid)){
                var match=VisibleRows().FirstOrDefault(r=>string.Equals((((YouTubeChannel)r.Tag).ProfileId??"").Trim(),pid.Trim(),StringComparison.OrdinalIgnoreCase));
                if(match!=null)return match;
            }
            var vis=VisibleRows();
            return vis.Count>0?vis[0]:null;
        }
        void RememberSelectedChannel(YouTubeChannel ch){
            if(ch==null||string.IsNullOrWhiteSpace(ch.ProfileId))return;
            if(marketView=="EN")settings.LastSelectedYouTubeProfileIdEn=ch.ProfileId.Trim();
            else settings.LastSelectedYouTubeProfileIdRu=ch.ProfileId.Trim();
            SafeSave();
        }
        void AssignVideosToSelectedChannel(){
            SaveGrid();
            var row=CurrentChannelRow();
            if(row==null){Ui.Error(this,new Exception("Выберите канал в таблице, затем нажмите «Добавить видео»."));return;}
            var ch=(YouTubeChannel)row.Tag;
            RememberSelectedChannel(ch);
            using(var d=new OpenFileDialog{Multiselect=true,Title="Добавить видео · "+ch.Name,Filter="Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*"}){
                if(d.ShowDialog(this)!=DialogResult.OK||d.FileNames.Length==0)return;
                try{
                    AssignVideoPackToChannel(ch,row,d.FileNames);
                    SaveGrid();LoadGrid();
                    row.Cells[COn].Value=true;
                    SaveGrid();
                }catch(Exception ex){Ui.Error(this,ex);}
            }
        }
        void AssignVideoPackToChannel(YouTubeChannel ch,DataGridViewRow row,string[] filePaths){
            var paths=filePaths.OrderBy(f=>f,StringComparer.OrdinalIgnoreCase).ToArray();
            string m=NormMarket(string.IsNullOrWhiteSpace(ch.Market)?marketView:ch.Market);
            List<string> titles;
            try{titles=PeekTitlesFromBank(ch.Kind,paths.Length,m);}catch(Exception ex){throw;}
            Write("«"+ch.Name+"»: "+paths.Length+" видео — "+TitleCursorLabel(ch.Kind,m)+":");
            var items=new List<YouTubeItem>();
            int renamed=0;
            for(int i=0;i<paths.Length;i++){
                string baseTitle=i<titles.Count?titles[i]:"";
                if(string.IsNullOrWhiteSpace(baseTitle))throw new Exception("«"+ch.Name+"»: нет заголовка для видео "+(i+1)+"/"+paths.Length+".");
                string finalTitle=PackTitleWithIndex(baseTitle,i+1,paths.Length);
                ValidateTitleLength(finalTitle,"«"+ch.Name+"» ["+(i+1)+"/"+paths.Length+"]");
                Write("  "+(i+1)+". «"+MakeWindowsSafeTitle(finalTitle)+"» ← "+Path.GetFileName(paths[i]));
                string path=paths[i];string before=path;
                path=RenameVideoFile(path,baseTitle,i+1,paths.Length);
                if(!string.Equals(before,path,StringComparison.OrdinalIgnoreCase))renamed++;
                items.Add(new YouTubeItem{Video=path,Title=finalTitle,Thumbnail=i==0?(ch.Thumbnail??""):""});
            }
            if(ch.Items!=null&&ch.Items.Count>0&&ch.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Video))){
                if(MessageBox.Show(this,"«"+ch.Name+"» уже имеет видео. Заменить новой пачкой ("+items.Count+")?","VideoBatch",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            }
            ch.Items=items;ch.Enabled=true;ch.Market=m;ch.Status=ChannelStatus.Ready;
            SyncChannelPrimary(ch);
            AdvanceTitleCursor(ch.Kind,paths.Length,m);
            RefreshRowDisplay(row);
            Write("  переименовано: "+renamed+". "+TitleCursorLabel(ch.Kind,m));
        }
        /// <summary>Читает N заголовков с текущей позиции курсора, не сдвигая его.</summary>
        List<string> PeekTitlesFromBank(string kind,int videoCount,string market=null){
            if(videoCount<=0)return new List<string>();
            market=NormMarket(market??marketView);
            var bank=TitleBankRef(kind,market);
            if(bank.Count==0)throw new Exception("Заголовки («"+KindLabel(kind)+"», "+MarketLabel(market)+") пусты. Откройте «Заголовки» или нажмите «Загрузить новые».");
            int cursor=GetTitleCursor(kind,market);
            var result=new List<string>();
            for(int i=0;i<videoCount;i++)result.Add(CleanTitle(bank[(cursor+i)%bank.Count]));
            return result;
        }
        /// <summary>Сдвигает курсор после успешного назначения видео.</summary>
        void AdvanceTitleCursor(string kind,int videoCount,string market=null){
            if(videoCount<=0)return;
            market=NormMarket(market??marketView);
            int cursor=GetTitleCursor(kind,market);
            var bank=TitleBankRef(kind,market);
            if(bank.Count==0)return;
            SetTitleCursor(kind,(cursor+videoCount)%bank.Count,market);
            Save();
        }
        List<string> AllocTitlesFromBank(string kind,int videoCount,string market=null){
            var titles=PeekTitlesFromBank(kind,videoCount,market);
            AdvanceTitleCursor(kind,videoCount,market);
            return titles;
        }
        void AssignVideosToChannels(){
            using(var d=new OpenFileDialog{Multiselect=true,Filter="Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*"}){
                if(d.ShowDialog(this)!=DialogResult.OK||d.FileNames.Length==0)return;
                SaveGrid();
                var files=d.FileNames.OrderBy(f=>f,StringComparer.OrdinalIgnoreCase).ToArray();
                var channelList=VisibleRows().Select(r=>(YouTubeChannel)r.Tag).ToList();
                if(channelList.Count==0){AddGridRow(new YouTubeChannel{Enabled=true,Name=marketView=="EN"?"Channel 1":"Канал 1",Market=marketView,Kind="long"});channelList=VisibleRows().Select(r=>(YouTubeChannel)r.Tag).ToList();}
                using(var assign=new VideoChannelAssignDialog(files,channelList)){
                    if(assign.ShowDialog(this)!=DialogResult.OK)return;
                    var groups=assign.Result.GroupBy(x=>x.Channel).ToList();
                    int done=0,renamed=0;
                    var cursorKinds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(var g in groups){
                        var ch=g.Key;
                        var paths=g.Select(x=>x.Path).ToList();
                        string m=NormMarket(string.IsNullOrWhiteSpace(ch.Market)?marketView:ch.Market);
                        List<string> titles;
                        try{titles=PeekTitlesFromBank(ch.Kind,paths.Count,m);}catch(Exception ex){Ui.Error(this,ex);return;}
                        Write("«"+ch.Name+"»: "+paths.Count+" видео — "+TitleCursorLabel(ch.Kind,m)+":");
                        var items=new List<YouTubeItem>();
                        for(int i=0;i<paths.Count;i++){
                            string baseTitle=i<titles.Count?titles[i]:"";
                            if(string.IsNullOrWhiteSpace(baseTitle))throw new Exception("«"+ch.Name+"»: нет заголовка для видео "+(i+1)+"/"+paths.Count+".");
                            string finalTitle=PackTitleWithIndex(baseTitle,i+1,paths.Count);
                            ValidateTitleLength(finalTitle,"«"+ch.Name+"» ["+(i+1)+"/"+paths.Count+"]");
                            Write("  "+(i+1)+". «"+MakeWindowsSafeTitle(finalTitle)+"» ← "+Path.GetFileName(paths[i]));
                            string path=paths[i];string before=path;
                            path=RenameVideoFile(path,baseTitle,i+1,paths.Count);
                            if(!string.Equals(before,path,StringComparison.OrdinalIgnoreCase))renamed++;
                            items.Add(new YouTubeItem{Video=path,Title=finalTitle,Thumbnail=i==0?(ch.Thumbnail??""):""});
                        }
                        // Если у канала уже была пачка — спрашиваем только когда добавляем поверх
                        if(ch.Items!=null&&ch.Items.Count>0&&ch.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Video))){
                            if(MessageBox.Show(this,"«"+ch.Name+"» уже имеет видео. Заменить новой пачкой ("+items.Count+")?","VideoBatch",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)continue;
                        }
                        ch.Items=items;ch.Enabled=true;ch.Market=m;SyncChannelPrimary(ch);done+=items.Count;
                        AdvanceTitleCursor(ch.Kind,paths.Count,m);
                        cursorKinds.Add(m+"|"+NormKind(ch.Kind));
                    }
                    SaveGrid();LoadGrid();
                    foreach(DataGridViewRow row in grid.Rows){var c=(YouTubeChannel)row.Tag;if(c.Enabled)row.Cells[COn].Value=true;}
                    SaveGrid();
                    Write("["+MarketLabel(marketView)+"] назначено видео: "+done+", переименовано файлов: "+renamed+".");
                    foreach(var key in cursorKinds){
                        var parts=key.Split('|');
                        Write("  "+TitleCursorLabel(parts.Length>1?parts[1]:"long",parts[0]));
                    }
                }
            }
        }
        List<DataGridViewRow> VisibleRows(){grid.EndEdit();return grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Visible).ToList();}
        DataGridViewRow PreferTemplateRow(){
            var sel=Selected();
            if(sel.Count>0)return sel[0];
            if(grid.CurrentRow!=null&&grid.CurrentRow.Visible)return grid.CurrentRow;
            var vis=VisibleRows();
            return vis.Count>0?vis[0]:null;
        }
        void CommitChecks(List<DataGridViewRow> rows,int count){
            try{grid.EndEdit();grid.CommitEdit(DataGridViewDataErrorContexts.Commit);}catch{}
            for(int i=0;i<count&&i<rows.Count;i++){
                rows[i].Cells[COn].Value=true;
                ((YouTubeChannel)rows[i].Tag).Enabled=true;
            }
        }
        void AssignShortsBatch(){
            SaveGrid();
            var baseRow=PreferTemplateRow();
            if(baseRow==null){Ui.Error(this,new Exception("Добавьте хотя бы один канал в списке «"+MarketLabel(marketView)+"»."));return;}
            var baseCh=(YouTubeChannel)baseRow.Tag;
            if(string.IsNullOrWhiteSpace(baseCh.ProfileId)){Ui.Error(this,new Exception(baseCh.Name+": сначала укажите Profile ID."));return;}
            using(var d=new OpenFileDialog{Multiselect=true,Title="Шортсы · "+MarketLabel(marketView)+" · "+baseCh.Name,Filter="Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*"}){
                if(d.ShowDialog(this)!=DialogResult.OK||d.FileNames.Length==0)return;
                var videoFiles=d.FileNames.OrderBy(f=>f,StringComparer.OrdinalIgnoreCase).ToArray();
                List<string> titles;
                string m=NormMarket(baseCh.Market);
                if(string.IsNullOrWhiteSpace(m))m=marketView;
                try{titles=PeekTitlesFromBank("shorts",videoFiles.Length,m);}catch(Exception ex){Ui.Error(this,ex);return;}
                Write("«"+baseCh.Name+"» шортс — "+TitleCursorLabel("shorts",m)+":");
                var items=new List<YouTubeItem>();
                for(int i=0;i<videoFiles.Length;i++){
                    string baseTitle=titles[i];
                    string finalTitle=PackTitleWithIndex(baseTitle,i+1,videoFiles.Length);
                    ValidateTitleLength(finalTitle,"«"+baseCh.Name+"» шортс ["+(i+1)+"/"+videoFiles.Length+"]");
                    Write("  "+(i+1)+". «"+MakeWindowsSafeTitle(finalTitle)+"»");
                    string path=RenameVideoFile(videoFiles[i],baseTitle,i+1,videoFiles.Length);
                    items.Add(new YouTubeItem{Video=path,Title=finalTitle,Thumbnail=""});
                }
                baseCh.Enabled=true;baseCh.Market=m;baseCh.Kind="shorts";baseCh.Items=items;baseCh.Status="Готов (шортс "+items.Count+")";
                AdvanceTitleCursor("shorts",videoFiles.Length,m);
                SyncChannelPrimary(baseCh);SaveGrid();LoadGrid();
                Write("["+MarketLabel(marketView)+"] пачка шортсов «"+baseCh.Name+"»: "+items.Count+" файлов.");
                Write("  "+TitleCursorLabel("shorts",m));
            }
        }
        void Configure(){using(var d=new DolphinSetupDialog(settings.ProtectedDolphinToken,settings.DolphinPort,settings.MaxParallelUploads,settings.UploadStagingFolder))if(d.ShowDialog(this)==DialogResult.OK){settings.ProtectedDolphinToken=WindowsSupport.Protect(d.Token);settings.DolphinPort=d.Port;settings.MaxParallelUploads=d.MaxParallel;settings.UploadStagingFolder=d.StagingFolder;d.Token="";Save();Write("Настройки Dolphin сохранены (параллельно: "+settings.MaxParallelUploads+", папка: "+settings.UploadStagingFolder+").");}}
        List<DataGridViewRow> Selected(){grid.EndEdit();return grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Visible&&Convert.ToBoolean(r.Cells[COn].Value??false)).ToList();}
        // Для загрузки: отмеченные каналы; если галочек нет — каналы с готовой очередью (видео+заголовок).
        List<DataGridViewRow> UploadTargets(){
            var sel=Selected();
            if(sel.Count>0)return sel;
            return VisibleRows().Where(r=>{
                var c=(YouTubeChannel)r.Tag;SyncChannelPrimary(c);
                return c.Items!=null&&c.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Video)&&!string.IsNullOrWhiteSpace(it.Title));
            }).ToList();
        }
        List<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)> ExpandUploadJobs(List<DataGridViewRow> rows){
            var jobs=new List<(DataGridViewRow,YouTubeChannel,YouTubeItem,int,int)>();
            foreach(var row in rows){
                var c=(YouTubeChannel)row.Tag;SyncChannelPrimary(c);
                var items=(c.Items??new List<YouTubeItem>()).Where(it=>!string.IsNullOrWhiteSpace(it.Video)&&!string.IsNullOrWhiteSpace(it.Title)).ToList();
                if(items.Count==0&&!string.IsNullOrWhiteSpace(c.Video)&&!string.IsNullOrWhiteSpace(c.Title))
                    items.Add(new YouTubeItem{Video=c.Video,Title=c.Title,Thumbnail=c.Thumbnail});
                for(int i=0;i<items.Count;i++)jobs.Add((row,c,items[i],i+1,items.Count));
            }
            return jobs;
        }
        void SaveSearchFields(){
            string keys=(searchKeys.Text??"").Trim(),fullTitle=(searchFullTitle.Text??"").Trim(),url=(searchUrl.Text??"").Trim();
            int i=searchFilter.SelectedIndex;string filter=(i>=0&&i<FilterKeys.Length)?FilterKeys[i]:"today";
            if(marketView=="EN"){
                settings.YouTubeSearchKeysEn=keys;settings.YouTubeSearchFullTitleEn=fullTitle;settings.YouTubeSearchUrlEn=url;settings.YouTubeSearchFilterEn=filter;
                settings.YouTubeSearchTitleEn=keys;
            }else{
                settings.YouTubeSearchKeysRu=keys;settings.YouTubeSearchFullTitleRu=fullTitle;settings.YouTubeSearchUrlRu=url;settings.YouTubeSearchFilterRu=filter;
                settings.YouTubeSearchTitleRu=keys;
            }
            settings.YouTubeSearchTitle=keys;settings.YouTubeSearchUrl=url;settings.YouTubeSearchFilter=filter;
            settings.YouTubeMarketView=marketView;Save();
        }
        void SaveGrid(){
            if(grid.IsCurrentCellInEditMode)grid.EndEdit();
            var visible=new List<YouTubeChannel>();
            foreach(DataGridViewRow row in grid.Rows){
                var c=(YouTubeChannel)row.Tag;
                if(c.Items==null)c.Items=new List<YouTubeItem>();
                c.Enabled=Convert.ToBoolean(row.Cells[COn].Value??false);
                c.Name=Convert.ToString(row.Cells[CName].Value??"").Trim();
                SyncChannelUrlFromName(c);
                c.Market=NormMarket(Convert.ToString(row.Cells[CLang].Value??marketView));
                c.Kind=KindKeyFromLabel(Convert.ToString(row.Cells[CKind].Value??"Длинное"));
                c.ProfileId=Convert.ToString(row.Cells[CProfile].Value??"").Trim();
                c.Status=Convert.ToString(row.Cells[CStatus].Value??"");
                // Ячейка заголовка при пачке не перезаписывает очередь Items.
                if(c.Items.Count<=1){
                    string cellTitle=ClampTitle(Convert.ToString(row.Cells[CTitle].Value??""));
                    c.Title=cellTitle;
                    if(c.Items.Count==1)c.Items[0].Title=cellTitle;
                    else if(!string.IsNullOrWhiteSpace(cellTitle)||!string.IsNullOrWhiteSpace(c.Video))
                        c.Items.Add(new YouTubeItem{Video=c.Video??"",Title=cellTitle,Thumbnail=c.Thumbnail??""});
                } else {
                    foreach(var it in c.Items)if(it!=null)it.Title=CleanTitle(it.Title);
                }
                SyncChannelPrimary(c);
                visible.Add(c);
            }
            var others=(settings.YouTubeChannels??new List<YouTubeChannel>()).Where(c=>NormMarket(c.Market)!=marketView).ToList();
            var moved=visible.Where(c=>NormMarket(c.Market)!=marketView).ToList();
            visible=visible.Where(c=>NormMarket(c.Market)==marketView).ToList();
            settings.YouTubeChannels=Store.NormalizeChannels(others.Concat(moved).Concat(visible).ToList());
            settings.YouTubeMarketView=marketView;Save();
            if(moved.Count>0){BeginInvoke(new Action(()=>{LoadGrid();Write("Каналы перенесены на другой рынок: "+moved.Count+".");}));}
        }
        void Save(){try{Store.Save(settings);}catch(Exception e){Write("ОШИБКА СОХРАНЕНИЯ: "+e.Message);}}
        void SafeSave(){lock(saveLock){try{Store.Save(settings);}catch(Exception e){Write("ОШИБКА СОХРАНЕНИЯ: "+e.Message);}}}
        void Busy(bool value){
            foreach(var b in new[]{setup,check,search,upload,meshWatch,applyThumb,marketRu,marketEn,kindShorts,kindLong,add,files,titlesBtn,moreBtn})if(b!=null)b.Enabled=!value;
            searchKeys.Enabled=!value;searchFullTitle.Enabled=!value;searchUrl.Enabled=!value;searchFilter.Enabled=!value;
            grid.Enabled=!value;
            stop.Visible=value||uploadsInFlight>0;stop.Enabled=value||uploadsInFlight>0;
            if(runHint!=null){runHint.Visible=value;runHint.Text=value?"Идёт проверка/поиск…":"";}
        }
        void UploadBusyStart(){
            uploadsInFlight++;
            upload.Enabled=true;if(applyThumb!=null)applyThumb.Enabled=true;
            stop.Visible=true;stop.Enabled=true;
            check.Enabled=false;search.Enabled=false;setup.Enabled=false;marketRu.Enabled=false;marketEn.Enabled=false;if(kindShorts!=null)kindShorts.Enabled=false;if(kindLong!=null)kindLong.Enabled=false;
            add.Enabled=true;files.Enabled=true;if(titlesBtn!=null)titlesBtn.Enabled=true;if(moreBtn!=null)moreBtn.Enabled=true;
            grid.Enabled=true;
            if(runHint!=null){
                runHint.Visible=true;
                runHint.Text="Идёт загрузка ("+uploadsInFlight+"). Можно отметить другой канал.";
            }
        }
        void UploadBusyEnd(){
            uploadsInFlight=Math.Max(0,uploadsInFlight-1);
            if(uploadsInFlight>0){
                if(runHint!=null)runHint.Text="Идёт загрузка ("+uploadsInFlight+"). Можно снова нажать «Загрузить».";
                upload.Enabled=true;if(applyThumb!=null)applyThumb.Enabled=true;stop.Visible=true;stop.Enabled=true;
                return;
            }
            upload.Enabled=true;if(meshWatch!=null)meshWatch.Enabled=true;if(applyThumb!=null)applyThumb.Enabled=true;check.Enabled=true;search.Enabled=true;setup.Enabled=true;marketRu.Enabled=true;marketEn.Enabled=true;if(kindShorts!=null)kindShorts.Enabled=true;if(kindLong!=null)kindLong.Enabled=true;
            stop.Visible=cancellation!=null;stop.Enabled=cancellation!=null;
            if(runHint!=null){runHint.Visible=false;runHint.Text="";}
            lock(uploadLock){
                if(uploadCts!=null){try{uploadCts.Dispose();}catch{}uploadCts=null;}
            }
        }
        void Status(DataGridViewRow row,string text){
            if(IsDisposed)return;
            if(InvokeRequired){BeginInvoke(new Action(()=>Status(row,text)));return;}
            row.Cells[CStatus].Value=text;((YouTubeChannel)row.Tag).Status=text;Write(((YouTubeChannel)row.Tag).Name+": "+text);
        }
        void Write(string text){if(InvokeRequired){BeginInvoke(new Action<string>(Write),text);return;}string line=DateTime.Now.ToString("HH:mm:ss")+"  "+text;log.AppendText(line+Environment.NewLine);log.ScrollToCaret();try{Directory.CreateDirectory(Store.Root);if(string.IsNullOrWhiteSpace(logFile))logFile=Path.Combine(Store.Root,"youtube-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");File.AppendAllText(logFile,line+Environment.NewLine,Encoding.UTF8);}catch{}}
        void ValidateCommon(bool forUpload=false){
            DolphinRunner.CheckFiles();
            if(string.IsNullOrWhiteSpace(WindowsSupport.Unprotect(settings.ProtectedDolphinToken)))throw new Exception("Шаг 1: укажите API-токен Dolphin.");
            var rows=forUpload?UploadTargets():Selected();
            if(rows.Count==0){
                if(forUpload)throw new Exception("Нет готовых строк: укажите видео и заголовок (или отметьте строки галочкой). Рынок: «"+MarketLabel(marketView)+"».");
                throw new Exception("Отметьте хотя бы один канал в списке «"+MarketLabel(marketView)+"».");
            }
            foreach(var row in rows){
                var c=(YouTubeChannel)row.Tag;
                if(string.IsNullOrWhiteSpace(c.Name))throw new Exception("Укажите название канала в каждой строке.");
                if(string.IsNullOrWhiteSpace(c.ProfileId))throw new Exception(c.Name+": вставьте Profile ID из Dolphin.");
            }
        }
        async Task<UploadRunResult> RunUploadWithRetry(UploadJob job,YouTubeChannel ch,DataGridViewRow row,List<PreparedUploadItem> list,CancellationToken ct,string token){
            const int maxAttempts=2;
            Exception last=null;
            for(int attempt=1;attempt<=maxAttempts;attempt++){
                try{
                    return await DolphinRunner.Run(job,m=>{
                        if(!string.IsNullOrWhiteSpace(m.ip))PropagateIp(ch.ProfileId,m.ip);
                        if(!string.IsNullOrWhiteSpace(m.text))Status(row,m.text);
                        if(string.Equals(m.stage,"mesh",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(m.channelUrl))
                            PropagateChannelUrl(ch.ProfileId,m.channelUrl.Trim());
                        if(m.packIndex>0&&!string.IsNullOrWhiteSpace(m.url)){
                            int ix=m.packIndex-1;
                            if(ix>=0&&ix<list.Count){
                                list[ix].Source.PublishedUrl=m.url.Trim();
                                SyncPublishedMeta(list[ix].Source);
                            }
                        }else if(!string.IsNullOrWhiteSpace(m.url)&&list.Count==1){
                            list[0].Source.PublishedUrl=m.url.Trim();
                            SyncPublishedMeta(list[0].Source);
                        }
                    },ct).ConfigureAwait(false);
                }catch(OperationCanceledException){throw;}
                catch(Exception e){
                    last=e;
                    if(attempt>=maxAttempts)break;
                    Write(ch.Name+": ошибка, перезапуск профиля (попытка "+attempt+"/"+maxAttempts+")…");
                    Status(row,ChannelStatus.Preparing);
                    try{await DolphinRunner.StopProfile(token,settings.DolphinPort,(ch.ProfileId??"").Trim()).ConfigureAwait(false);}catch{}
                    await Task.Delay(3000,ct).ConfigureAwait(false);
                }
            }
            throw last??new Exception("Неизвестная ошибка загрузки.");
        }
        async Task CheckProfiles(){if(cancellation!=null)return;string token="";try{SaveGrid();ValidateCommon(false);cancellation=new CancellationTokenSource();Busy(true);Write("["+MarketLabel(marketView)+"] параллельная проверка профилей…");token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
            var unique=Selected().GroupBy(r=>(((YouTubeChannel)r.Tag).ProfileId??"").Trim(),StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
            var errors=new ConcurrentBag<string>();
            var tasks=unique.Select(row=>Task.Run(async()=>{
                var c=(YouTubeChannel)row.Tag;
                try{
                    cancellation.Token.ThrowIfCancellationRequested();
                    Status(row,"Проверка IP…");
                    var result=await DolphinRunner.Run(new UploadJob{token=token,localPort=settings.DolphinPort,profileId=c.ProfileId,checkOnly=true,skipQueueDelay=true},m=>{if(!string.IsNullOrWhiteSpace(m.text))Status(row,m.text);},cancellation.Token).ConfigureAwait(false);
                    PropagateIp(c.ProfileId,result.Ip);
                    Status(row,"IP сохранён ✓");SafeSave();
                }catch(OperationCanceledException){throw;}
                catch(Exception e){errors.Add(c.Name+": "+e.Message);Status(row,"Ошибка проверки");}
            })).ToArray();
            try{await Task.WhenAll(tasks).ConfigureAwait(true);}catch(OperationCanceledException){Write("Проверка остановлена.");}
            if(errors.Count>0){Write("Ошибки проверки: "+string.Join("; ",errors));MessageBox.Show(this,string.Join(Environment.NewLine,errors),"Проверка",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            else Write("["+MarketLabel(marketView)+"] все выбранные профили проверены (параллельно).");
        }catch(Exception e){Write("ОШИБКА: "+e.Message);Ui.Error(this,e);}finally{Finish();}}
        void PropagateIp(string profileId,string ip){
            if(string.IsNullOrWhiteSpace(profileId)||string.IsNullOrWhiteSpace(ip))return;
            if(InvokeRequired){BeginInvoke(new Action(()=>PropagateIp(profileId,ip)));return;}
            foreach(DataGridViewRow row in grid.Rows){
                if(!row.Visible)continue;
                var c=(YouTubeChannel)row.Tag;
                if(!string.Equals((c.ProfileId??"").Trim(),profileId.Trim(),StringComparison.OrdinalIgnoreCase))continue;
                c.ExpectedIp=ip;row.Cells[CIp].Value=ip;
            }
        }
        void SyncIpsFromSiblings(){
            foreach(var g in VisibleRows().GroupBy(r=>(((YouTubeChannel)r.Tag).ProfileId??"").Trim(),StringComparer.OrdinalIgnoreCase)){
                if(string.IsNullOrWhiteSpace(g.Key))continue;
                var ip=g.Select(r=>((YouTubeChannel)r.Tag).ExpectedIp).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));
                if(string.IsNullOrWhiteSpace(ip))continue;
                PropagateIp(g.Key,ip);
            }
        }
        async Task CrossWatchMesh(){
            if(cancellation!=null)return;
            if(uploadsInFlight>0){MessageBox.Show(this,"Дождитесь завершения загрузки или нажмите Стоп.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            string token="";
            try{
                SaveGrid();SaveSearchFields();ValidateCommon(false);
                var rows=Selected();
                var meshChannels=CollectMeshChannels(rows);
                if(meshChannels.Count==0)throw new Exception("Нет каналов с заголовками. Сначала загрузите видео или назначьте заголовки.");
                Store.SaveMeshCatalog(settings.YouTubeChannels??new List<YouTubeChannel>());
                if(meshChannels.Count<1)throw new Exception("Нет каналов для сетки.");
                cancellation=new CancellationTokenSource();Busy(true);
                token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                var unique=rows.GroupBy(r=>(((YouTubeChannel)r.Tag).ProfileId??"").Trim(),StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
                Write("["+MarketLabel(marketView)+"] сетка: "+unique.Count+" аккаунтов, "+meshChannels.Count+" каналов (свои — лайк, чужие — просмотр). База: "+Store.MeshCatalogPath);
                var errors=new ConcurrentBag<string>();
                var tasks=unique.Select(row=>Task.Run(async()=>{
                    var viewer=(YouTubeChannel)row.Tag;
                    string viewerPid=(viewer.ProfileId??"").Trim();
                    var watchTargets=meshChannels.Select(BuildMeshTarget).Where(t=>t!=null).ToArray();
                    if(watchTargets.Length==0){Status(row,"Нечего смотреть");return;}
                    try{
                        cancellation.Token.ThrowIfCancellationRequested();
                        Status(row,"Сетка: "+watchTargets.Length+" каналов…");
                        var result=await DolphinRunner.Run(new UploadJob{
                            token=token,localPort=settings.DolphinPort,profileId=viewer.ProfileId,expectedIp=viewer.ExpectedIp,
                            searchFilter="none",watchMesh=true,watchTargets=watchTargets,skipQueueDelay=true,checkOnly=false
                        },m=>{
                            if(!string.IsNullOrWhiteSpace(m.text))Status(row,m.text);
                            if(string.Equals(m.stage,"mesh",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(m.channelUrl)&&!string.IsNullOrWhiteSpace(m.meshOwner))
                                PropagateChannelUrl(m.meshOwner,m.channelUrl.Trim());
                        },cancellation.Token).ConfigureAwait(false);
                        Status(row,"Сетка ✓ "+watchTargets.Length+" каналов");
                        SafeSave();
                    }catch(OperationCanceledException){throw;}
                    catch(Exception e){errors.Add(viewer.Name+": "+e.Message);Status(row,"Ошибка сетки");}
                })).ToArray();
                try{await Task.WhenAll(tasks).ConfigureAwait(true);}catch(OperationCanceledException){Write("Сетка просмотра остановлена.");}
                if(errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,errors),"Сетка просмотр",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                else{Write("["+MarketLabel(marketView)+"] сетка просмотра завершена.");MessageBox.Show(this,"Все аккаунты просмотрели ролики друг друга.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);}
            }catch(Exception e){Write("ОШИБКА: "+e.Message);Ui.Error(this,e);}finally{Finish();}
        }
        async Task SearchOnYouTube(){if(cancellation!=null)return;string token="";try{SaveGrid();SaveSearchFields();ValidateCommon(false);var rows=Selected();
                string keys=marketView=="EN"?settings.YouTubeSearchKeysEn:settings.YouTubeSearchKeysRu;
                string fullTitle=marketView=="EN"?settings.YouTubeSearchFullTitleEn:settings.YouTubeSearchFullTitleRu;
                string link=marketView=="EN"?settings.YouTubeSearchUrlEn:settings.YouTubeSearchUrlRu;
                string filter=marketView=="EN"?settings.YouTubeSearchFilterEn:settings.YouTubeSearchFilterRu;
                if(string.IsNullOrWhiteSpace(keys)&&string.IsNullOrWhiteSpace(fullTitle)){
                    string legacy=marketView=="EN"?settings.YouTubeSearchTitleEn:settings.YouTubeSearchTitleRu;
                    if(!string.IsNullOrWhiteSpace(legacy))keys=legacy;
                }
                if(string.IsNullOrWhiteSpace(keys)&&string.IsNullOrWhiteSpace(fullTitle))throw new Exception("Укажите ключи (по одному на строку) или полный заголовок ("+MarketLabel(marketView)+"). Ссылка — ориентир по ID видео.");
                cancellation=new CancellationTokenSource();Busy(true);token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                var unique=rows.GroupBy(r=>(((YouTubeChannel)r.Tag).ProfileId??"").Trim(),StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
                string keysPreview=(keys??"").Replace("\r\n"," | ").Replace('\n','|');if(keysPreview.Length>80)keysPreview=keysPreview.Substring(0,80)+"...";
                Write("["+MarketLabel(marketView)+"] поиск на "+unique.Count+" аккаунтах: ключи «"+keysPreview+"»"+(string.IsNullOrWhiteSpace(fullTitle)?"":" · запасной заголовок")+(string.IsNullOrWhiteSpace(link)?"":" · ID по ссылке")+".");
                var errors=new ConcurrentBag<string>();
                var tasks=unique.Select(row=>Task.Run(async()=>{
                    var c=(YouTubeChannel)row.Tag;
                    try{
                        cancellation.Token.ThrowIfCancellationRequested();
                        Status(row,"Поиск видео…");
                        var result=await DolphinRunner.Run(new UploadJob{token=token,localPort=settings.DolphinPort,profileId=c.ProfileId,expectedIp=c.ExpectedIp,title=keys,searchKeys=keys,searchFullTitle=fullTitle,searchUrl=link,searchFilter=filter,checkOnly=false,searchOnly=true,skipQueueDelay=true},m=>{if(!string.IsNullOrWhiteSpace(m.text))Status(row,m.text);},cancellation.Token).ConfigureAwait(false);
                        Status(row,string.IsNullOrWhiteSpace(result.Url)?"Досмотрено + лайк ✓":"Досмотрено + лайк ✓ "+result.Url);SafeSave();
                    }catch(OperationCanceledException){throw;}
                    catch(Exception e){errors.Add(c.Name+": "+e.Message);Status(row,"Ошибка поиска");}
                })).ToArray();
                try{await Task.WhenAll(tasks).ConfigureAwait(true);}catch(OperationCanceledException){Write("Поиск остановлен.");}
                if(errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,errors),"Поиск",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                else{Write("["+MarketLabel(marketView)+"] параллельный поиск завершён.");MessageBox.Show(this,"Поиск на всех выбранных аккаунтах завершён.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);}
            }catch(Exception e){Write("ОШИБКА: "+e.Message);Ui.Error(this,e);}finally{Finish();}}
        async Task ApplyShortsThumbFrames(){
            if(cancellation!=null){MessageBox.Show(this,"Сначала дождитесь проверки/поиска или нажмите Стоп.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            string token="";
            bool uiStarted=false;
            try{
                SaveGrid();SyncIpsFromSiblings();
                DolphinRunner.CheckFiles();
                if(string.IsNullOrWhiteSpace(WindowsSupport.Unprotect(settings.ProtectedDolphinToken)))throw new Exception("Шаг 1: укажите API-токен Dolphin.");
                var rows=Selected();
                if(rows.Count==0)rows=VisibleRows().Where(r=>{
                    var c=(YouTubeChannel)r.Tag;
                    return NormKind(c.Kind)=="shorts"&&c.Items!=null&&c.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Title));
                }).ToList();
                rows=rows.Where(r=>NormKind(((YouTubeChannel)r.Tag).Kind)=="shorts").ToList();
                if(rows.Count==0)throw new Exception("Отметьте каналы типа «Шортс» (или сначала «Все → Шортс»). Нужны заголовки загруженных роликов.");
                foreach(var row in rows){
                    var c=(YouTubeChannel)row.Tag;
                    if(string.IsNullOrWhiteSpace(c.ProfileId))throw new Exception(c.Name+": вставьте Profile ID из Dolphin.");
                    
                    SyncChannelPrimary(c);
                    if(c.Items==null||!c.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Title)))
                        throw new Exception(c.Name+": нет заголовков — сначала загрузите шортсы или укажите заголовки.");
                }
                List<DataGridViewRow> free;
                lock(uploadLock){free=rows.Where(r=>!busyProfiles.Contains((((YouTubeChannel)r.Tag).ProfileId??"").Trim())).ToList();}
                if(free.Count==0){MessageBox.Show(this,"Выбранные профили уже заняты.","YouTube",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
                token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                CancellationToken ct;
                lock(uploadLock){if(uploadCts==null)uploadCts=new CancellationTokenSource();ct=uploadCts.Token;}
                UploadBusyStart();uiStarted=true;
                Write("["+MarketLabel(marketView)+"] превью шортсов: первый кадр из видео · "+free.Count+" канал(ов).");
                var errors=new ConcurrentBag<string>();
                var byProfile=free.GroupBy(r=>(((YouTubeChannel)r.Tag).ProfileId??"").Trim(),StringComparer.OrdinalIgnoreCase).ToList();
                var tasks=byProfile.Select(g=>Task.Run(async()=>{
                    var row=g.First();
                    var ch=(YouTubeChannel)row.Tag;
                    string pid=(ch.ProfileId??"").Trim();
                    lock(uploadLock)busyProfiles.Add(pid);
                    try{
                        ct.ThrowIfCancellationRequested();
                        var titles=g.SelectMany(r=>{
                            var c=(YouTubeChannel)r.Tag;SyncChannelPrimary(c);
                            return (c.Items??new List<YouTubeItem>()).Where(it=>!string.IsNullOrWhiteSpace(it.Title)).Select(it=>ClampTitle(it.Title));
                        }).Where(t=>t.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if(titles.Length==0)throw new Exception(ch.Name+": пустые заголовки.");
                        Status(row,"Превью шортс…");
                        var items=titles.Select(t=>new UploadItemJob{title=t}).ToArray();
                        var result=await DolphinRunner.Run(new UploadJob{
                            token=token,localPort=settings.DolphinPort,profileId=ch.ProfileId,expectedIp=ch.ExpectedIp,
                            applyThumbFrame=true,skipQueueDelay=true,items=items
                        },m=>{if(!string.IsNullOrWhiteSpace(m.text))Status(row,m.text);},ct).ConfigureAwait(false);
                        Status(row,"Превью ✓ "+titles.Length);
                        if(!string.IsNullOrWhiteSpace(result.Ip))PropagateIp(ch.ProfileId,result.Ip);
                        SafeSave();
                    }catch(OperationCanceledException){Status(row,"Остановлено");}
                    catch(Exception e){
                        string msg=e.Message+(e is UploadException ue&&ue.KeptOpen?" (профиль открыт)":"");
                        errors.Add(ch.Name+": "+msg);Status(row,"Ошибка превью");
                    }finally{lock(uploadLock)busyProfiles.Remove(pid);}
                })).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(true);
                if(errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,errors),"Превью шортс",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                else Write("["+MarketLabel(marketView)+"] превью шортсов готово.");
            }catch(Exception e){Write("ОШИБКА: "+e.Message);Ui.Error(this,e);}
            finally{if(uiStarted)UploadBusyEnd();SaveGrid();RefreshMarketUi();}
        }
        async Task UploadAll(){
            if(cancellation!=null){MessageBox.Show(this,"Сначала дождитесь проверки/поиска или нажмите Стоп.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            string token="";
            bool uiStarted=false;
            try{
                SaveGrid();SyncIpsFromSiblings();ValidateCommon(true);
                var rows=UploadTargets();
                var flat=ExpandUploadJobs(rows);
                foreach(var job in flat)job.item.Title=CleanTitle(job.item.Title);

                List<(DataGridViewRow row,YouTubeChannel ch,YouTubeItem item,int index,int total)> free;
                lock(uploadLock){free=flat.Where(j=>!busyProfiles.Contains((j.ch.ProfileId??"").Trim())).ToList();}
                if(free.Count==0){
                    MessageBox.Show(this,"Выбранные каналы уже загружаются.","YouTube",MessageBoxButtons.OK,MessageBoxIcon.Information);
                    return;
                }

                Write("Проверка серии: "+free.Count+" ролик(ов), "+free.Select(j=>(j.ch.ProfileId??"").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()+" профил(ей)…");
                foreach(var j in free)Status(j.row,ChannelStatus.Preparing);

                var batches=QueueManager.Prepare(
                    free,
                    (src,pid,idx)=>StageUploadFile(src,pid,idx),
                    (ch,it,idx,tot)=>PlannedFileName(it.Title,idx,tot,it.Video),
                    (title,idx,tot)=>TitleCleaner.CleanForUpload(CleanTitle(title)));

                foreach(var batch in batches){
                    foreach(var it in batch.Items)
                        Write("  "+batch.Channel.Name+" ["+it.Index+"/"+it.Total+"] → "+Path.GetFileName(it.StagedVideo)+" · "+it.ScheduleDate+" "+it.ScheduleTime+" · «"+it.UploadTitle+"»");
                }

                int maxParallel=Math.Max(1,Math.Min(20,settings.MaxParallelUploads<=0?10:settings.MaxParallelUploads));
                lock(uploadLock){
                    if(uploadCts==null)uploadCts=new CancellationTokenSource();
                    foreach(var b in batches)busyProfiles.Add(b.ProfileId);
                }
                var ct=uploadCts.Token;
                token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                UploadBusyStart();uiStarted=true;
                Write("["+MarketLabel(marketView)+"] загрузка с расписанием: "+batches.Count+" акк., "+free.Count+" рол., параллельно до "+maxParallel+".");

                var errors=new ConcurrentBag<string>();
                using(var uploadSem=new SemaphoreSlim(maxParallel,maxParallel)){
                var tasks=batches.Select(batch=>Task.Run(async()=>{
                    var row=batch.Row;var ch=batch.Channel;
                    try{
                        await uploadSem.WaitAsync(ct).ConfigureAwait(false);
                        try{
                        ct.ThrowIfCancellationRequested();
                        Status(row,ChannelStatus.Uploading);
                        var job=batch.ToUploadJob(token,settings.DolphinPort);
                        var result=await RunUploadWithRetry(job,ch,row,batch.Items,ct,token).ConfigureAwait(false);
                        if(!string.IsNullOrWhiteSpace(result.Url)&&batch.Items.Count==1&&string.IsNullOrWhiteSpace(batch.Items[0].Source.PublishedUrl)){
                            batch.Items[0].Source.PublishedUrl=result.Url.Trim();
                            SyncPublishedMeta(batch.Items[0].Source);
                        }
                        Store.SaveMeshCatalog(settings.YouTubeChannels??new List<YouTubeChannel>());
                        ch.Status=ChannelStatus.Scheduled;
                        Status(row,batch.Items.Count>1?("Отложено "+batch.Items.Count+" ✓"):("Отложено ✓ "+batch.Items[0].ScheduleDate+" "+batch.Items[0].ScheduleTime));
                        if(!string.IsNullOrWhiteSpace(result.Ip))PropagateIp(ch.ProfileId,result.Ip);
                        SafeSave();
                        }finally{uploadSem.Release();}
                    }catch(OperationCanceledException){Status(row,"Остановлено");}
                    catch(Exception e){
                        ch.Status=ChannelStatus.Error;
                        string msg=e.Message+(e is UploadException ue&&ue.KeptOpen?" (профиль оставлен)":"");
                        errors.Add(ch.Name+": "+msg);
                        Status(row,ChannelStatus.Error);
                    }finally{
                        lock(uploadLock)busyProfiles.Remove(batch.ProfileId);
                    }
                })).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(true);
                }
                if(errors.Count>0){
                    Write("Ошибки: "+errors.Count);
                    MessageBox.Show(this,string.Join(Environment.NewLine,errors),"Загрузка",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                }else Write("["+MarketLabel(marketView)+"] пакет завершён ("+batches.Count+" акк.).");
            }catch(Exception e){Write("ОШИБКА: "+e.Message);Ui.Error(this,e);}
            finally{
                if(uiStarted)UploadBusyEnd();
                SaveGrid();SaveSearchFields();RefreshMarketUi();
            }
        }
        void Finish(){if(cancellation!=null){cancellation.Dispose();cancellation=null;}Busy(false);SaveGrid();SaveSearchFields();RefreshMarketUi();}
    }
}
