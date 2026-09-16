using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public class TikTokItem {
        public string Video="",Title="",Description="",Caption=""; // Caption — старое имя Title
    }
    public class TikTokAccount {
        public bool Enabled=true;
        public string Name="",ProfileId="",ExpectedIp="",Video="",Title="",Description="",Caption="",Status="Готов";
        public string Market="RU";
        public List<TikTokItem> Items=new List<TikTokItem>();
    }

    public class TikTokUploadWindow:Form {
        const int COn=0,CName=1,CLang=2,CProfile=3,CIp=4,CVideo=5,CVideoPick=6,CTitle=7,CDesc=8,CStatus=9,CRemove=10;
        static readonly string[] LangLabels=new[]{"RU","EN"};
        Preferences settings;DataGridView grid;RichTextBox log;
        Button setup,add,importYt,files,titlesBtn,descBtn,musicBtn,check,upload,stop,marketRu,marketEn;
        Label marketHint,runHint;CancellationTokenSource cancellation;CancellationTokenSource uploadCts;
        int uploadsInFlight;readonly HashSet<string> busyProfiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string logFile;string marketView="RU";readonly object saveLock=new object();readonly object uploadLock=new object();

        public TikTokUploadWindow(Preferences source){
            settings=source;marketView=NormMarket(settings.TikTokMarketView);
            Text="TikTok";ClientSize=new Size(1100,740);MinimumSize=new Size(920,600);
            StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",9.5f);BackColor=Ui.Bg;ForeColor=Ui.Ink;AutoScaleMode=AutoScaleMode.Dpi;
            try{Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}
            var root=Ui.Table();root.Padding=new Padding(18);root.RowCount=7;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,66));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,110));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,36));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
            Controls.Add(root);

            var heading=Ui.Table(2);heading.ColumnStyles[0].Width=50;heading.ColumnStyles[1].Width=50;
            heading.Controls.Add(new Label{Text="TikTok",AutoSize=true,Font=new Font("Segoe UI",22,FontStyle.Bold),ForeColor=Ui.Ink},0,0);
            heading.Controls.Add(new Label{Text="Рабочее пространство · только короткие",TextAlign=ContentAlignment.MiddleRight,Dock=DockStyle.Fill,ForeColor=Ui.Muted},1,0);
            root.Controls.Add(heading,0,0);

            var marketBar=Ui.Flow();marketBar.WrapContents=false;
            marketBar.Controls.Add(new Label{Text="Рынок:",AutoSize=true,Margin=new Padding(0,8,10,0),ForeColor=Ui.Muted});
            marketRu=Ui.Button("Русские",()=>SwitchMarket("RU"));marketEn=Ui.Button("English",()=>SwitchMarket("EN"));
            marketHint=new Label{AutoSize=true,Margin=new Padding(16,8,0,0),ForeColor=Ui.Muted};
            marketBar.Controls.Add(marketRu);marketBar.Controls.Add(marketEn);marketBar.Controls.Add(marketHint);root.Controls.Add(marketBar,0,1);

            runHint=new Label{Dock=DockStyle.Fill,AutoSize=false,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Color.FromArgb(20,100,60),Font=new Font("Segoe UI",9.5f,FontStyle.Bold),Visible=false};
            root.Controls.Add(runHint,0,2);

            var tools=Ui.Flow();tools.WrapContents=true;
            setup=Ui.Button("Dolphin",Configure);
            add=Ui.Button("+ Аккаунт",AddRow);
            importYt=Ui.Button("Из YouTube",ImportFromYouTube);
            files=Ui.Button("Выбрать видео",AssignVideosToAccounts);
            titlesBtn=Ui.Button("Заголовки",()=>EditTextBank(true));
            descBtn=Ui.Button("Описание",()=>EditTextBank(false));
            musicBtn=Ui.Button("Музыка",EditTikTokMusic);
            check=Ui.Button("Проверить",null);check.Click+=async(s,e)=>await CheckProfiles();
            var help=Ui.Button("?",()=>MessageBox.Show(this,
                "Пространство TikTok — только короткие.\n\n"+
                "Заголовки и Описание — две базы. При выборе видео берутся сами.\n"+
                "Из YouTube — копирует Profile ID.\n"+
                "В профиле должен быть вход в TikTok.\n\n"+
                "Заголовки → Описание → Выбрать видео → Проверить → Загрузить.",
                "TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information));
            tools.Controls.Add(setup);tools.Controls.Add(add);tools.Controls.Add(importYt);tools.Controls.Add(files);tools.Controls.Add(titlesBtn);tools.Controls.Add(descBtn);tools.Controls.Add(musicBtn);tools.Controls.Add(check);tools.Controls.Add(help);
            root.Controls.Add(tools,0,3);

            grid=new DataGridView{Dock=DockStyle.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None,SelectionMode=DataGridViewSelectionMode.CellSelect};
            grid.RowTemplate.Height=38;BuildColumns();grid.CellContentClick+=CellClick;grid.CellEndEdit+=(s,e)=>SaveGrid();
            grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
            root.Controls.Add(grid,0,4);

            var logs=Ui.Table();logs.RowCount=2;logs.RowStyles.Add(new RowStyle(SizeType.Absolute,28));logs.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            logs.Controls.Add(new Label{Text="Журнал",AutoSize=true,Font=new Font("Segoe UI",10,FontStyle.Bold)},0,0);
            log=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,BackColor=Color.White,BorderStyle=BorderStyle.None,Font=new Font("Consolas",9),DetectUrls=true};
            logs.Controls.Add(log,0,1);root.Controls.Add(logs,0,5);

            var bottom=Ui.Flow();
            upload=Ui.Button("Загрузить",null,true);upload.MinimumSize=new Size(180,42);upload.Click+=async(s,e)=>await UploadAll();
            stop=Ui.Button("Стоп",()=>{stop.Enabled=false;if(uploadCts!=null)uploadCts.Cancel();if(cancellation!=null)cancellation.Cancel();Write("Остановка…");});stop.Visible=false;
            var openLog=Ui.Button("Лог",()=>{try{if(File.Exists(logFile))Ui.Open(logFile);else throw new Exception("Лог ещё не создан.");}catch(Exception e){Ui.Error(this,e);}});
            bottom.Controls.Add(upload);bottom.Controls.Add(stop);bottom.Controls.Add(openLog);root.Controls.Add(bottom,0,6);

            LoadGrid();RefreshMarketUi();
            FormClosing+=(s,e)=>{SaveGrid();if(cancellation!=null||uploadsInFlight>0){e.Cancel=true;MessageBox.Show(this,"Сначала остановите операцию (Стоп).","TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information);}};
            Shown+=(s,e)=>{if(string.IsNullOrWhiteSpace(WindowsSupport.Unprotect(settings.ProtectedDolphinToken)))Configure();};
        }

        static string NormMarket(string m){m=(m??"").Trim().ToUpperInvariant();return m=="EN"?"EN":"RU";}
        static string MarketLabel(string m){return NormMarket(m)=="EN"?"English":"Русские";}
        void RefreshMarketUi(){
            bool ru=marketView=="RU";
            marketRu.BackColor=ru?Ui.Blue:Color.FromArgb(228,235,246);marketRu.ForeColor=ru?Color.White:Ui.Ink;
            marketEn.BackColor=!ru?Ui.Blue:Color.FromArgb(228,235,246);marketEn.ForeColor=!ru?Color.White:Ui.Ink;
            int n=(settings.TikTokAccounts??new List<TikTokAccount>()).Count(c=>NormMarket(c.Market)==marketView);
            marketHint.Text=MarketLabel(marketView)+" · только короткие · аккаунтов "+n;
            Text="TikTok · "+MarketLabel(marketView);
            if(musicBtn!=null){
                int mn=(marketView=="EN"?settings.TikTokMusicEn:settings.TikTokMusicRu)?.Count??0;
                musicBtn.Text="Музыка · "+mn;
            }
        }
        void SwitchMarket(string next){
            next=NormMarket(next);if(next==marketView)return;
            if(cancellation!=null||uploadsInFlight>0){MessageBox.Show(this,"Сначала остановите операцию.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            SaveGrid();marketView=next;settings.TikTokMarketView=marketView;Save();LoadGrid();RefreshMarketUi();
            Write("Рынок TikTok: "+MarketLabel(marketView)+".");
        }
        void LoadGrid(){
            grid.Rows.Clear();
            foreach(var a in settings.TikTokAccounts??new List<TikTokAccount>()){
                if(NormMarket(a.Market)!=marketView)continue;
                AddGridRow(a);
            }
            if(grid.Rows.Count==0)AddGridRow(new TikTokAccount{Name=marketView=="EN"?"Account 1":"Аккаунт 1",Market=marketView});
            RefreshMarketUi();
        }
        static void SyncPrimary(TikTokAccount a){
            if(a.Items==null)a.Items=new List<TikTokItem>();
            foreach(var it in a.Items){
                if(it==null)continue;
                if(string.IsNullOrWhiteSpace(it.Title)&&!string.IsNullOrWhiteSpace(it.Caption))it.Title=it.Caption;
                if(it.Description==null)it.Description="";
                it.Caption=it.Title??"";
            }
            if(a.Items.Count==0&&(!string.IsNullOrWhiteSpace(a.Video)||!string.IsNullOrWhiteSpace(a.Title)||!string.IsNullOrWhiteSpace(a.Caption)))
                a.Items.Add(new TikTokItem{Video=a.Video??"",Title=string.IsNullOrWhiteSpace(a.Title)?(a.Caption??""):a.Title,Description=a.Description??""});
            if(a.Items.Count>0){
                a.Video=a.Items[0].Video??"";
                a.Title=a.Items[0].Title??"";
                a.Description=a.Items[0].Description??"";
                a.Caption=a.Title;
            }
        }
        static string Short(string path){return string.IsNullOrWhiteSpace(path)?"":Path.GetFileName(path);}
        static string PackLabel(IEnumerable<string> values){
            var list=(values??Enumerable.Empty<string>()).Select(x=>Store.CleanStoredTitle(x)).Where(x=>x.Length>0).ToList();
            if(list.Count==0)return "";
            return list[0]; // без « · +N» в ячейке — иначе попадало обратно в Title
        }
        void BuildColumns(){
            grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="on",HeaderText="✓",Width=36});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="name",HeaderText="Аккаунт",Width=110});
            var lang=new DataGridViewComboBoxColumn{Name="lang",HeaderText="Рынок",Width=64,FlatStyle=FlatStyle.Flat};lang.Items.AddRange(LangLabels);grid.Columns.Add(lang);
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="profile",HeaderText="Profile ID",Width=120});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="ip",HeaderText="IP",Width=100,ReadOnly=true});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="video",HeaderText="Видео",Width=120,ReadOnly=true});
            grid.Columns.Add(new DataGridViewButtonColumn{Name="videoPick",HeaderText="",Text="…",UseColumnTextForButtonValue=true,Width=34});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="title",HeaderText="Заголовок",Width=160});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="desc",HeaderText="Описание",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,MinimumWidth=140});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="status",HeaderText="Статус",Width=120,ReadOnly=true});
            grid.Columns.Add(new DataGridViewButtonColumn{Name="remove",HeaderText="",Text="×",UseColumnTextForButtonValue=true,Width=34});
        }
        void AddGridRow(TikTokAccount a){
            a.Market=NormMarket(a.Market);if(a.Items==null)a.Items=new List<TikTokItem>();SyncPrimary(a);
            string videoLabel=a.Items.Count>1?(a.Items.Count+" файлов"):Short(a.Video);
            grid.Rows.Add(a.Enabled,a.Name,a.Market,a.ProfileId,a.ExpectedIp,videoLabel,"…",
                PackLabel(a.Items.Select(x=>x.Title)),PackLabel(a.Items.Select(x=>x.Description)),
                string.IsNullOrWhiteSpace(a.Status)?"Готов":a.Status,"×");
            var row=grid.Rows[grid.Rows.Count-1];row.Tag=a;
            row.Cells[CVideo].ToolTipText=a.Items.Count>1?string.Join(Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+Short(it.Video))):a.Video;
            row.Cells[CTitle].ToolTipText=string.Join(Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+it.Title));
            row.Cells[CDesc].ToolTipText=string.Join(Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+it.Description));
        }
        void RefreshRowDisplay(DataGridViewRow row){
            var a=(TikTokAccount)row.Tag;SyncPrimary(a);
            row.Cells[COn].Value=a.Enabled;
            row.Cells[CVideo].Value=a.Items!=null&&a.Items.Count>1?(a.Items.Count+" файлов"):Short(a.Video);
            row.Cells[CTitle].Value=PackLabel(a.Items.Select(x=>x.Title));
            row.Cells[CDesc].Value=PackLabel(a.Items.Select(x=>x.Description));
            row.Cells[CVideo].ToolTipText=a.Items!=null&&a.Items.Count>1?string.Join(Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+Short(it.Video))):a.Video;
            row.Cells[CTitle].ToolTipText=string.Join(Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+it.Title));
            row.Cells[CDesc].ToolTipText=string.Join(Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+it.Description));
        }
        void AddRow(){SaveGrid();AddGridRow(new TikTokAccount{Name=(marketView=="EN"?"Account ":"Аккаунт ")+(grid.Rows.Count+1),Market=marketView});SaveGrid();RefreshMarketUi();}
        void ImportFromYouTube(){
            SaveGrid();
            var yt=(settings.YouTubeChannels??new List<YouTubeChannel>()).Where(c=>NormMarket(c.Market)==marketView&&!string.IsNullOrWhiteSpace(c.ProfileId)).ToList();
            if(yt.Count==0){MessageBox.Show(this,"В YouTube на рынке "+MarketLabel(marketView)+" нет каналов с Profile ID.","TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            int added=0;
            foreach(var c in yt){
                string pid=(c.ProfileId??"").Trim();
                bool exists=(settings.TikTokAccounts??new List<TikTokAccount>()).Any(a=>
                    NormMarket(a.Market)==marketView&&string.Equals((a.ProfileId??"").Trim(),pid,StringComparison.OrdinalIgnoreCase));
                if(exists)continue;
                var acc=new TikTokAccount{
                    Enabled=true,Name=string.IsNullOrWhiteSpace(c.Name)?("TT "+pid):c.Name,
                    ProfileId=pid,ExpectedIp=c.ExpectedIp??"",Market=marketView,Status="Импорт из YouTube"
                };
                settings.TikTokAccounts.Add(acc);added++;
            }
            Save();LoadGrid();
            Write("Импорт из YouTube: добавлено "+added+" аккаунтов (уже существующие Profile ID пропущены).");
        }
        List<string> TitleBank(){return marketView=="EN"?settings.TikTokCaptionBankEn:settings.TikTokCaptionBankRu;}
        List<string> DescBank(){return marketView=="EN"?settings.TikTokDescriptionBankEn:settings.TikTokDescriptionBankRu;}
        void EditTextBank(bool titles){
            SaveGrid();
            var bank=titles?TitleBank():DescBank();
            using(var dlg=new TitleBankDialog((titles?"Заголовки":"Описание")+" · TikTok · "+MarketLabel(marketView),bank)){
                if(dlg.ShowDialog(this)!=DialogResult.OK)return;
                bank.Clear();bank.AddRange(dlg.Titles??new List<string>());Save();
                Write("["+MarketLabel(marketView)+"] "+(titles?"заголовки":"описания")+": "+bank.Count+" шт.");
            }
        }
        void EditTikTokMusic(){
            using(var d=new MusicDialog(settings.TikTokMusicRu,settings.TikTokMusicEn,marketView,false,-7.5)){
                if(d.ShowDialog(this)!=DialogResult.OK)return;
                settings.TikTokMusicRu=d.PathsRu;settings.TikTokMusicEn=d.PathsEn;Save();RefreshMarketUi();
                Write("["+MarketLabel(marketView)+"] музыка TikTok: "+(marketView=="EN"?settings.TikTokMusicEn:settings.TikTokMusicRu).Count+" трек(ов).");
            }
        }
        List<string> AllocFromBank(List<string> bank,int count,string label){
            if(count<=0)return new List<string>();
            if(bank.Count==0)throw new Exception(label+" TikTok ("+MarketLabel(marketView)+") пусты. Сначала нажмите «"+label+"».");
            if(bank.Count==1){
                string one=bank[0];
                var list=new List<string>();
                for(int i=0;i<count;i++)list.Add(count<=1?one:(one+" ("+(i+1)+")"));
                return list;
            }
            if(bank.Count<count)throw new Exception(label+": в базе "+bank.Count+", нужно "+count+".");
            var taken=bank.Take(count).ToList();bank.RemoveRange(0,count);Save();
            return taken;
        }
        List<string> AllocTitles(int count){return AllocFromBank(TitleBank(),count,"Заголовки");}
        List<string> AllocDescriptions(int count){return AllocFromBank(DescBank(),count,"Описание");}
        void CellClick(object sender,DataGridViewCellEventArgs e){
            if(e.RowIndex<0)return;
            var row=grid.Rows[e.RowIndex];var a=(TikTokAccount)row.Tag;
            if(e.ColumnIndex==CVideoPick){
                var p=Ui.File(this,"Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*");
                if(p==null)return;
                if(a.Items!=null&&a.Items.Count>1&&MessageBox.Show(this,"Уже пачка из "+a.Items.Count+". Заменить?","TikTok",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                string title,desc;
                try{title=AllocTitles(1)[0];desc=AllocDescriptions(1)[0];}catch(Exception ex){Ui.Error(this,ex);return;}
                a.Items=new List<TikTokItem>{new TikTokItem{Video=p,Title=title,Description=desc,Caption=title}};
                a.Enabled=true;row.Cells[COn].Value=true;SyncPrimary(a);RefreshRowDisplay(row);
            } else if(e.ColumnIndex==CRemove) grid.Rows.RemoveAt(e.RowIndex);
            SaveGrid();RefreshMarketUi();
        }
        void AssignVideosToAccounts(){
            using(var d=new OpenFileDialog{Multiselect=true,Filter="Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*"}){
                if(d.ShowDialog(this)!=DialogResult.OK||d.FileNames.Length==0)return;
                SaveGrid();
                var paths=d.FileNames.OrderBy(f=>f,StringComparer.OrdinalIgnoreCase).ToArray();
                var accounts=VisibleRows().Select(r=>(TikTokAccount)r.Tag).ToList();
                if(accounts.Count==0){AddRow();accounts=VisibleRows().Select(r=>(TikTokAccount)r.Tag).ToList();}
                using(var assign=new TikTokAssignDialog(paths,accounts)){
                    if(assign.ShowDialog(this)!=DialogResult.OK)return;
                    foreach(var g in assign.Result.GroupBy(x=>x.Account)){
                        var acc=g.Key;
                        var vids=g.Select(x=>x.Path).ToList();
                        List<string> titles,descs;
                        try{titles=AllocTitles(vids.Count);descs=AllocDescriptions(vids.Count);}catch(Exception ex){Ui.Error(this,ex);return;}
                        if(acc.Items!=null&&acc.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Video))){
                            if(MessageBox.Show(this,"«"+acc.Name+"» уже имеет видео. Заменить?","TikTok",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)continue;
                        }
                        var items=new List<TikTokItem>();
                        for(int i=0;i<vids.Count;i++)items.Add(new TikTokItem{Video=vids[i],Title=i<titles.Count?titles[i]:"",Description=i<descs.Count?descs[i]:"",Caption=i<titles.Count?titles[i]:""});
                        acc.Items=items;acc.Enabled=true;acc.Market=marketView;SyncPrimary(acc);
                    }
                    SaveGrid();LoadGrid();
                    foreach(DataGridViewRow row in grid.Rows)if(((TikTokAccount)row.Tag).Enabled)row.Cells[COn].Value=true;
                    SaveGrid();
                    Write("["+MarketLabel(marketView)+"] видео назначены, заголовки и описания из базы.");
                }
            }
        }
        List<DataGridViewRow> VisibleRows(){grid.EndEdit();return grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Visible).ToList();}
        List<DataGridViewRow> Selected(){grid.EndEdit();return grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Visible&&Convert.ToBoolean(r.Cells[COn].Value??false)).ToList();}
        List<DataGridViewRow> UploadTargets(){
            var sel=Selected();
            if(sel.Count>0)return sel;
            return VisibleRows().Where(r=>{
                var a=(TikTokAccount)r.Tag;SyncPrimary(a);
                return a.Items!=null&&a.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Video)&&!string.IsNullOrWhiteSpace(it.Title)&&!string.IsNullOrWhiteSpace(it.Description));
            }).ToList();
        }
        void Configure(){using(var d=new DolphinSetupDialog(settings.ProtectedDolphinToken,settings.DolphinPort,settings.MaxParallelUploads,settings.UploadStagingFolder))if(d.ShowDialog(this)==DialogResult.OK){settings.ProtectedDolphinToken=WindowsSupport.Protect(d.Token);settings.DolphinPort=d.Port;settings.MaxParallelUploads=d.MaxParallel;settings.UploadStagingFolder=d.StagingFolder;d.Token="";Save();Write("Настройки Dolphin сохранены.");}}
        void Save(){try{Store.Save(settings);}catch(Exception e){Write("ОШИБКА СОХРАНЕНИЯ: "+e.Message);}}
        void SafeSave(){lock(saveLock){try{Store.Save(settings);}catch(Exception e){Write("ОШИБКА СОХРАНЕНИЯ: "+e.Message);}}}
        void SaveGrid(){
            if(grid.IsCurrentCellInEditMode)grid.EndEdit();
            var visible=new List<TikTokAccount>();
            foreach(DataGridViewRow row in grid.Rows){
                var a=(TikTokAccount)row.Tag;
                if(a.Items==null)a.Items=new List<TikTokItem>();
                a.Enabled=Convert.ToBoolean(row.Cells[COn].Value??false);
                a.Name=Convert.ToString(row.Cells[CName].Value??"").Trim();
                a.Market=NormMarket(Convert.ToString(row.Cells[CLang].Value??marketView));
                a.ProfileId=Convert.ToString(row.Cells[CProfile].Value??"").Trim();
                a.Status=Convert.ToString(row.Cells[CStatus].Value??"");
                if(a.Items.Count<=1){
                    string cellTitle=Store.CleanStoredTitle(Convert.ToString(row.Cells[CTitle].Value??""));
                    string cellDesc=Convert.ToString(row.Cells[CDesc].Value??"").Trim();
                    a.Title=cellTitle;a.Description=cellDesc;a.Caption=cellTitle;
                    if(a.Items.Count==1){a.Items[0].Title=cellTitle;a.Items[0].Description=cellDesc;a.Items[0].Caption=cellTitle;}
                    else if(!string.IsNullOrWhiteSpace(cellTitle)||!string.IsNullOrWhiteSpace(cellDesc)||!string.IsNullOrWhiteSpace(a.Video))
                        a.Items.Add(new TikTokItem{Video=a.Video??"",Title=cellTitle,Description=cellDesc,Caption=cellTitle});
                } else {
                    foreach(var it in a.Items)if(it!=null){it.Title=Store.CleanStoredTitle(it.Title);it.Caption=it.Title;}
                }
                SyncPrimary(a);visible.Add(a);
            }
            var others=(settings.TikTokAccounts??new List<TikTokAccount>()).Where(c=>NormMarket(c.Market)!=marketView).ToList();
            var moved=visible.Where(c=>NormMarket(c.Market)!=marketView).ToList();
            visible=visible.Where(c=>NormMarket(c.Market)==marketView).ToList();
            settings.TikTokAccounts=others.Concat(moved).Concat(visible).ToList();
            settings.TikTokMarketView=marketView;Save();
            if(moved.Count>0)BeginInvoke(new Action(()=>{LoadGrid();Write("Аккаунты перенесены на другой рынок: "+moved.Count+".");}));
        }
        void Busy(bool value){
            foreach(var b in new[]{setup,add,importYt,files,titlesBtn,descBtn,musicBtn,check,upload,marketRu,marketEn})b.Enabled=!value;
            grid.Enabled=!value;
            stop.Visible=value||uploadsInFlight>0;stop.Enabled=value||uploadsInFlight>0;
            if(runHint!=null){runHint.Visible=value;runHint.Text=value?"Идёт проверка…":"";}
        }
        void UploadBusyStart(){
            uploadsInFlight++;upload.Enabled=true;stop.Visible=true;stop.Enabled=true;
            check.Enabled=false;setup.Enabled=false;marketRu.Enabled=false;marketEn.Enabled=false;
            add.Enabled=true;importYt.Enabled=true;files.Enabled=true;titlesBtn.Enabled=true;descBtn.Enabled=true;musicBtn.Enabled=true;grid.Enabled=true;
            if(runHint!=null){runHint.Visible=true;runHint.Text="Идёт загрузка ("+uploadsInFlight+"). Можно снова нажать «Загрузить» для другого аккаунта.";}
        }
        void UploadBusyEnd(){
            uploadsInFlight=Math.Max(0,uploadsInFlight-1);
            if(uploadsInFlight>0){upload.Enabled=true;stop.Visible=true;stop.Enabled=true;return;}
            upload.Enabled=true;check.Enabled=true;setup.Enabled=true;marketRu.Enabled=true;marketEn.Enabled=true;
            stop.Visible=cancellation!=null;stop.Enabled=cancellation!=null;
            if(runHint!=null){runHint.Visible=false;runHint.Text="";}
            lock(uploadLock){if(uploadCts!=null){try{uploadCts.Dispose();}catch{}uploadCts=null;}}
        }
        void Status(DataGridViewRow row,string text){
            if(IsDisposed)return;
            if(InvokeRequired){BeginInvoke(new Action(()=>Status(row,text)));return;}
            row.Cells[CStatus].Value=text;((TikTokAccount)row.Tag).Status=text;Write(((TikTokAccount)row.Tag).Name+": "+text);
        }
        void Write(string text){
            if(InvokeRequired){BeginInvoke(new Action<string>(Write),text);return;}
            string line=DateTime.Now.ToString("HH:mm:ss")+"  "+text;
            log.AppendText(line+Environment.NewLine);log.ScrollToCaret();
            try{Directory.CreateDirectory(Store.Root);if(string.IsNullOrWhiteSpace(logFile))logFile=Path.Combine(Store.Root,"tiktok-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");File.AppendAllText(logFile,line+Environment.NewLine,Encoding.UTF8);}catch{}
        }
        void ValidateCommon(bool forUpload){
            DolphinRunner.CheckFilesTikTok();
            if(string.IsNullOrWhiteSpace(WindowsSupport.Unprotect(settings.ProtectedDolphinToken)))throw new Exception("Шаг 1: укажите API-токен Dolphin.");
            var rows=forUpload?UploadTargets():Selected();
            if(rows.Count==0)throw new Exception(forUpload?"Отметьте аккаунты с видео, заголовком и описанием.":"Отметьте аккаунты галочкой.");
            foreach(var row in rows){
                var a=(TikTokAccount)row.Tag;
                if(string.IsNullOrWhiteSpace(a.ProfileId))throw new Exception(a.Name+": укажите Profile ID.");
            }
        }
        void PropagateIp(string profileId,string ip){
            if(string.IsNullOrWhiteSpace(profileId)||string.IsNullOrWhiteSpace(ip))return;
            if(InvokeRequired){BeginInvoke(new Action(()=>PropagateIp(profileId,ip)));return;}
            foreach(DataGridViewRow row in grid.Rows){
                if(!row.Visible)continue;
                var a=(TikTokAccount)row.Tag;
                if(!string.Equals((a.ProfileId??"").Trim(),profileId.Trim(),StringComparison.OrdinalIgnoreCase))continue;
                a.ExpectedIp=ip;row.Cells[CIp].Value=ip;
            }
        }
        async Task CheckProfiles(){
            if(cancellation!=null)return;string token="";
            try{
                SaveGrid();ValidateCommon(false);
                cancellation=new CancellationTokenSource();Busy(true);token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                var unique=Selected().GroupBy(r=>(((TikTokAccount)r.Tag).ProfileId??"").Trim(),StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
                Write("["+MarketLabel(marketView)+"] проверка TikTok на "+unique.Count+" профилях…");
                var errors=new ConcurrentBag<string>();
                var tasks=unique.Select(row=>Task.Run(async()=>{
                    var a=(TikTokAccount)row.Tag;
                    try{
                        cancellation.Token.ThrowIfCancellationRequested();
                        Status(row,"Проверка TikTok…");
                        var result=await DolphinRunner.RunTikTok(new UploadJob{token=token,localPort=settings.DolphinPort,profileId=a.ProfileId,checkOnly=true,skipQueueDelay=true},
                            m=>{if(!string.IsNullOrWhiteSpace(m.text))Status(row,m.text);},cancellation.Token).ConfigureAwait(false);
                        if(!string.IsNullOrWhiteSpace(result.Ip)){PropagateIp(a.ProfileId,result.Ip);Status(row,"IP сохранён");}
                        else Status(row,"OK");
                        SafeSave();
                    }catch(OperationCanceledException){throw;}
                    catch(Exception e){errors.Add(a.Name+": "+e.Message);Status(row,"Ошибка");}
                })).ToArray();
                try{await Task.WhenAll(tasks).ConfigureAwait(true);}catch(OperationCanceledException){Write("Проверка остановлена.");}
                if(errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,errors),"Проверка TikTok",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                else Write("["+MarketLabel(marketView)+"] профили проверены.");
            }catch(Exception e){Write("ОШИБКА: "+e.Message);Ui.Error(this,e);}
            finally{Finish();}
        }
        async Task UploadAll(){
            if(cancellation!=null){MessageBox.Show(this,"Сначала дождитесь проверки или нажмите Стоп.","TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            string token="";bool uiStarted=false;
            try{
                SaveGrid();ValidateCommon(true);
                var rows=UploadTargets();
                var packs=new List<(DataGridViewRow row,TikTokAccount acc,List<TikTokItem> items)>();
                foreach(var row in rows){
                    var a=(TikTokAccount)row.Tag;SyncPrimary(a);
                    
                    var items=(a.Items??new List<TikTokItem>()).Where(it=>!string.IsNullOrWhiteSpace(it.Video)&&!string.IsNullOrWhiteSpace(it.Title)&&!string.IsNullOrWhiteSpace(it.Description)).ToList();
                    if(items.Count==0)throw new Exception(a.Name+": нет видео + заголовок + описание.");
                    foreach(var it in items){
                        if(!File.Exists(it.Video))throw new Exception(a.Name+": файл не найден — "+it.Video);
                        it.Title=Store.CleanStoredTitle(it.Title);it.Caption=it.Title;
                        if(string.IsNullOrWhiteSpace(it.Title))throw new Exception(a.Name+": пустой заголовок.");
                        if(string.IsNullOrWhiteSpace(it.Description))throw new Exception(a.Name+": пустое описание.");
                        if(it.Description.Length>2200)it.Description=it.Description.Substring(0,2200).Trim();
                    }
                    packs.Add((row,a,items));
                }
                List<(DataGridViewRow row,TikTokAccount acc,List<TikTokItem> items)> free;
                lock(uploadLock){free=packs.Where(p=>!busyProfiles.Contains((p.acc.ProfileId??"").Trim())).ToList();}
                if(free.Count==0){MessageBox.Show(this,"Выбранные аккаунты уже загружаются.","TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}

                lock(uploadLock){
                    if(uploadCts==null)uploadCts=new CancellationTokenSource();
                    foreach(var p in free)busyProfiles.Add((p.acc.ProfileId??"").Trim());
                }
                var ct=uploadCts.Token;token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                UploadBusyStart();uiStarted=true;
                Write("["+MarketLabel(marketView)+"] старт TikTok: "+free.Count+" акк.");

                var errors=new ConcurrentBag<string>();
                var tasks=free.Select(pack=>Task.Run(async()=>{
                    var row=pack.row;var a=pack.acc;var pid=(a.ProfileId??"").Trim();
                    try{
                        ct.ThrowIfCancellationRequested();
                        Status(row,pack.items.Count>1?("Пачка "+pack.items.Count+"…"):"Запуск Dolphin…");
                        var items=pack.items.Select(it=>new UploadItemJob{video=it.Video,title=it.Title,description=it.Description}).ToArray();
                        var result=await DolphinRunner.RunTikTok(new UploadJob{
                            token=token,localPort=settings.DolphinPort,profileId=a.ProfileId,expectedIp=a.ExpectedIp,
                            items=items,skipQueueDelay=true,checkOnly=false,
                            video=items[0].video,title=items[0].title
                        },m=>{
                            if(!string.IsNullOrWhiteSpace(m.ip))PropagateIp(a.ProfileId,m.ip);
                            if(!string.IsNullOrWhiteSpace(m.text))Status(row,m.text);
                        },ct).ConfigureAwait(false);
                        Status(row,pack.items.Count>1?("Пачка "+pack.items.Count+" ✓"):(string.IsNullOrWhiteSpace(result.Url)?"Загружено ✓":"Загружено ✓ "+result.Url));
                        if(!string.IsNullOrWhiteSpace(result.Ip))PropagateIp(a.ProfileId,result.Ip);
                        SafeSave();
                    }catch(OperationCanceledException){Status(row,"Остановлено");}
                    catch(Exception e){
                        string msg=e.Message+(e is UploadException ue&&ue.KeptOpen?" (профиль открыт)":"");
                        errors.Add(a.Name+": "+msg);Status(row,"Ошибка");
                    }finally{lock(uploadLock)busyProfiles.Remove(pid);}
                })).ToArray();

                await Task.WhenAll(tasks).ConfigureAwait(true);
                if(errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,errors),"TikTok",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                else Write("["+MarketLabel(marketView)+"] загрузка TikTok завершена.");
            }catch(Exception e){Write("ОШИБКА: "+e.Message);Ui.Error(this,e);}
            finally{if(uiStarted)UploadBusyEnd();SaveGrid();RefreshMarketUi();}
        }
        void Finish(){if(cancellation!=null){cancellation.Dispose();cancellation=null;}Busy(false);SaveGrid();RefreshMarketUi();}
    }

    public class TikTokAssignDialog:Dialog {
        public List<(string Path,TikTokAccount Account)> Result=new List<(string,TikTokAccount)>();
        readonly string[] files;readonly List<TikTokAccount> accounts;DataGridView grid;ComboBox applyCombo;
        public TikTokAssignDialog(string[] videoFiles,List<TikTokAccount> accountList):base("Выбрать видео",760){
            files=videoFiles;accounts=accountList??new List<TikTokAccount>();
            if(accounts.Count==0)throw new Exception("Сначала добавьте аккаунты.");
            ClientSize=new Size(760,560);
            var table=Ui.Table();table.RowCount=3;table.RowStyles.Add(new RowStyle(SizeType.AutoSize));table.RowStyles.Add(new RowStyle(SizeType.Absolute,44));table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            table.Controls.Add(Ui.Label("Для каждого файла выберите аккаунт. Или один аккаунт сразу на все видео. Подписи — из базы.",true),0,0);
            var applyBar=Ui.Flow();applyBar.WrapContents=false;applyBar.Margin=new Padding(0,6,0,4);
            applyBar.Controls.Add(new Label{Text="Один аккаунт на все:",AutoSize=true,Margin=new Padding(0,8,8,0),ForeColor=Ui.Muted});
            applyCombo=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=320,FlatStyle=FlatStyle.Flat};
            foreach(var a in accounts)applyCombo.Items.Add(LabelOf(a));
            if(applyCombo.Items.Count>0)applyCombo.SelectedIndex=0;
            applyBar.Controls.Add(applyCombo);
            applyBar.Controls.Add(Ui.Button("На все видео",()=>{
                if(applyCombo.SelectedItem==null)return;
                string label=Convert.ToString(applyCombo.SelectedItem);
                for(int i=0;i<grid.Rows.Count;i++)grid.Rows[i].Cells["acc"].Value=label;
            },true));
            table.Controls.Add(applyBar,0,1);
            grid=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,SelectionMode=DataGridViewSelectionMode.FullRowSelect};
            grid.RowTemplate.Height=32;
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="file",HeaderText="Видео",ReadOnly=true,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,MinimumWidth=220});
            var col=new DataGridViewComboBoxColumn{Name="acc",HeaderText="Аккаунт",FlatStyle=FlatStyle.Flat,Width=280};
            foreach(var a in accounts)col.Items.Add(LabelOf(a));
            grid.Columns.Add(col);table.Controls.Add(grid,0,2);Body.Controls.Add(table);
            for(int i=0;i<files.Length;i++){
                grid.Rows.Add(Path.GetFileName(files[i]),LabelOf(accounts[Math.Min(i,accounts.Count-1)]));
                grid.Rows[i].Tag=files[i];
            }
            var ok=Ui.Button("Назначить",()=>{try{Build();DialogResult=DialogResult.OK;Close();}catch(Exception e){Ui.Error(this,e);}},true);
            var cancel=Ui.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();});
            Bottom.Controls.Add(ok);Bottom.Controls.Add(cancel);CancelButton=cancel;
        }
        static string LabelOf(TikTokAccount a){return (a.Name??"Аккаунт")+(string.IsNullOrWhiteSpace(a.ProfileId)?"":(" · "+a.ProfileId));}
        void Build(){
            Result=new List<(string,TikTokAccount)>();
            for(int i=0;i<grid.Rows.Count;i++){
                string path=Convert.ToString(grid.Rows[i].Tag)??files[i];
                string label=Convert.ToString(grid.Rows[i].Cells["acc"].Value??"");
                var acc=accounts.FirstOrDefault(a=>LabelOf(a)==label);
                if(acc==null)throw new Exception("Строка "+(i+1)+": выберите аккаунт.");
                Result.Add((path,acc));
            }
        }
    }
}
