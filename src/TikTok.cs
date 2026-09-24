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
        public string Video="",Title="",Description="",Caption=""; // Title/Description остаются для старых settings.xml
        public bool Published;
    }
    public class TikTokAccount {
        public bool Enabled=true;
        public string Name="",ProfileId="",ExpectedIp="",Video="",Title="",Description="",Caption="",Status="Готов";
        public string Market="RU";
        public List<TikTokItem> Items=new List<TikTokItem>();
    }

    public class TikTokUploadWindow:Form {
        const int COn=0,CName=1,CLang=2,CProfile=3,CIp=4,CVideo=5,CVideoPick=6,CCaption=7,CStatus=8,CRemove=9;
        static readonly string[] LangLabels=new[]{"RU","EN"};
        Preferences settings;DataGridView grid;RichTextBox log;
        Button setup,add,importYt,files,captionsBtn,musicBtn,check,upload,stop,marketRu,marketEn;
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
            files=Ui.Button("Добавить видео",AssignVideosToSelectedAccount);
            captionsBtn=Ui.Button("Подписи",EditCaptionBank);
            musicBtn=Ui.Button("Музыка",EditTikTokMusic);
            check=Ui.Button("Проверить",null);check.Click+=async(s,e)=>await CheckProfiles();
            var help=Ui.Button("?",()=>MessageBox.Show(this,
                "Пространство TikTok — только короткие.\n\n"+
                "Подписи — единая циклическая база. При выборе видео берутся по кругу.\n"+
                "Из YouTube — копирует Profile ID.\n"+
                "В профиле должен быть вход в TikTok.\n\n"+
                "Выберите аккаунт → Добавить видео → Проверить → Загрузить.\n"+
                "«⋯» — распределить видео по нескольким аккаунтам.",
                "TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information));
            tools.Controls.Add(setup);tools.Controls.Add(add);tools.Controls.Add(importYt);tools.Controls.Add(files);tools.Controls.Add(captionsBtn);tools.Controls.Add(musicBtn);tools.Controls.Add(check);tools.Controls.Add(help);
            root.Controls.Add(tools,0,3);

            grid=new DataGridView{Dock=DockStyle.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None,SelectionMode=DataGridViewSelectionMode.FullRowSelect};
            grid.RowTemplate.Height=38;BuildColumns();grid.CellContentClick+=CellClick;grid.CellEndEdit+=(s,e)=>SaveGrid();grid.SelectionChanged+=(s,e)=>{if(grid.CurrentRow?.Tag is TikTokAccount acc)RememberSelectedAccount(acc);};
            grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
            root.Controls.Add(grid,0,4);

            var logs=Ui.Table();logs.RowCount=2;logs.RowStyles.Add(new RowStyle(SizeType.Absolute,28));logs.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            logs.Controls.Add(new Label{Text="Журнал",AutoSize=true,Font=new Font("Segoe UI",10,FontStyle.Bold)},0,0);
            log=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,BackColor=Color.White,BorderStyle=BorderStyle.None,Font=new Font("Consolas",9),DetectUrls=true};
            logs.Controls.Add(log,0,1);root.Controls.Add(logs,0,5);

            var bottom=Ui.Flow();
            upload=Ui.Button("Загрузить",null,true);upload.MinimumSize=new Size(180,42);upload.Click+=async(s,e)=>await UploadAll(null);
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
                if(!string.IsNullOrWhiteSpace(it.Description)&&(string.IsNullOrWhiteSpace(it.Caption)||it.Description.Length>it.Caption.Length))it.Caption=it.Description;
                if(string.IsNullOrWhiteSpace(it.Caption))it.Caption=it.Title;
                it.Caption=(it.Caption??"").Trim();it.Description=it.Caption;
            }
            if(a.Items.Count==0&&(!string.IsNullOrWhiteSpace(a.Video)||!string.IsNullOrWhiteSpace(a.Title)||!string.IsNullOrWhiteSpace(a.Caption)))
                a.Items.Add(new TikTokItem{Video=a.Video??"",Title=a.Title??"",Description=!string.IsNullOrWhiteSpace(a.Caption)?a.Caption:(a.Description??""),Caption=!string.IsNullOrWhiteSpace(a.Caption)?a.Caption:(a.Description??"")});
            if(a.Items.Count>0){
                a.Video=a.Items[0].Video??"";
                a.Title=a.Items[0].Title??"";
                a.Caption=a.Items[0].Caption??"";a.Description=a.Caption;
            }
        }
        static string Short(string path){return string.IsNullOrWhiteSpace(path)?"":Path.GetFileName(path);}
        static string PackLabel(IEnumerable<string> values){
            var list=(values??Enumerable.Empty<string>()).Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            if(list.Count==0)return "";
            return list[0];
        }
        void BuildColumns(){
            grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="on",HeaderText="✓",Width=36});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="name",HeaderText="Аккаунт",Width=110});
            var lang=new DataGridViewComboBoxColumn{Name="lang",HeaderText="Рынок",Width=64,FlatStyle=FlatStyle.Flat};lang.Items.AddRange(LangLabels);grid.Columns.Add(lang);
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="profile",HeaderText="Profile ID",Width=120});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="ip",HeaderText="IP",Width=100,ReadOnly=true});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="video",HeaderText="Видео",Width=120,ReadOnly=true});
            grid.Columns.Add(new DataGridViewButtonColumn{Name="videoPick",HeaderText="",Text="…",UseColumnTextForButtonValue=true,Width=34});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="caption",HeaderText="Подпись TikTok",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,MinimumWidth=260});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="status",HeaderText="Статус",Width=120,ReadOnly=true});
            grid.Columns.Add(new DataGridViewButtonColumn{Name="remove",HeaderText="",Text="×",UseColumnTextForButtonValue=true,Width=34});
        }
        void AddGridRow(TikTokAccount a){
            a.Market=NormMarket(a.Market);if(a.Items==null)a.Items=new List<TikTokItem>();SyncPrimary(a);
            string videoLabel=a.Items.Count>1?(a.Items.Count+" файлов"):Short(a.Video);
            grid.Rows.Add(a.Enabled,a.Name,a.Market,a.ProfileId,a.ExpectedIp,videoLabel,"…",
                PackLabel(a.Items.Select(x=>x.Caption)),
                string.IsNullOrWhiteSpace(a.Status)?"Готов":a.Status,"×");
            var row=grid.Rows[grid.Rows.Count-1];row.Tag=a;
            row.Cells[CVideo].ToolTipText=a.Items.Count>1?string.Join(Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+Short(it.Video))):a.Video;
            row.Cells[CCaption].ToolTipText=string.Join(Environment.NewLine+Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+it.Caption));
        }
        void RefreshRowDisplay(DataGridViewRow row){
            var a=(TikTokAccount)row.Tag;SyncPrimary(a);
            row.Cells[COn].Value=a.Enabled;
            row.Cells[CVideo].Value=a.Items!=null&&a.Items.Count>1?(a.Items.Count+" файлов"):Short(a.Video);
            row.Cells[CCaption].Value=PackLabel(a.Items.Select(x=>x.Caption));
            row.Cells[CVideo].ToolTipText=a.Items!=null&&a.Items.Count>1?string.Join(Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+Short(it.Video))):a.Video;
            row.Cells[CCaption].ToolTipText=string.Join(Environment.NewLine+Environment.NewLine,a.Items.Select((it,i)=>(i+1)+". "+it.Caption));
        }
        void AddRow(){SaveGrid();AddGridRow(new TikTokAccount{Name=(marketView=="EN"?"Account ":"Аккаунт ")+(grid.Rows.Count+1),Market=marketView});SaveGrid();RefreshMarketUi();}
        public TikTokSyncResult SyncFromYouTube(){
            SaveGrid();
            var r=TikTokProfileSync.SyncFromYouTube(settings,marketView);
            Save();LoadGrid();
            Write("Синхронизация YouTube→TikTok: привязано "+r.Linked+", новых "+r.Imported
                +(r.RemovedEmpty>0?", убрано пустых "+r.RemovedEmpty:"")+".");
            return r;
        }
        void ImportFromYouTube(){
            var r=SyncFromYouTube();
            if(r.Linked+r.Imported==0)
                MessageBox.Show(this,"На рынке "+MarketLabel(marketView)+" нет новых профилей из YouTube (или у YouTube-каналов нет Profile ID).","TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        List<string> CaptionBank(){return marketView=="EN"?settings.TikTokDescriptionBankEn:settings.TikTokFullCaptionBankRu;}
        void EditCaptionBank(){
            SaveGrid();
            var bank=CaptionBank();
            using(var dlg=new TitleBankDialog("Подписи · TikTok · "+MarketLabel(marketView),bank)){
                if(dlg.ShowDialog(this)!=DialogResult.OK)return;
                bank.Clear();bank.AddRange(dlg.Titles??new List<string>());Save();
                Write("["+MarketLabel(marketView)+"] подписи: "+bank.Count+" шт.");
            }
        }
        void EditTikTokMusic(){
            using(var d=new MusicDialog(settings.TikTokMusicRu,settings.TikTokMusicEn,marketView,false,-7.5)){
                if(d.ShowDialog(this)!=DialogResult.OK)return;
                settings.TikTokMusicRu=d.PathsRu;settings.TikTokMusicEn=d.PathsEn;Save();RefreshMarketUi();
                Write("["+MarketLabel(marketView)+"] музыка TikTok: "+(marketView=="EN"?settings.TikTokMusicEn:settings.TikTokMusicRu).Count+" трек(ов).");
            }
        }
        List<string> AllocCaptions(int count){
            int cursor=marketView=="EN"?settings.TikTokCaptionCursorEn:settings.TikTokCaptionCursorRu;
            var result=TikTokCaptionTemplates.Allocate(CaptionBank(),count,ref cursor);
            if(marketView=="EN")settings.TikTokCaptionCursorEn=cursor;else settings.TikTokCaptionCursorRu=cursor;
            Save();return result;
        }
        void CellClick(object sender,DataGridViewCellEventArgs e){
            if(e.RowIndex<0)return;
            var row=grid.Rows[e.RowIndex];var a=(TikTokAccount)row.Tag;
            if(e.ColumnIndex==CVideoPick){
                var p=Ui.File(this,"Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*");
                if(p==null)return;
                if(a.Items!=null&&a.Items.Count>1&&MessageBox.Show(this,"Уже пачка из "+a.Items.Count+". Заменить?","TikTok",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                string caption;
                try{caption=AllocCaptions(1)[0];}catch(Exception ex){Ui.Error(this,ex);return;}
                a.Items=new List<TikTokItem>{new TikTokItem{Video=p,Description=caption,Caption=caption}};
                a.Enabled=true;row.Cells[COn].Value=true;SyncPrimary(a);RefreshRowDisplay(row);
            } else if(e.ColumnIndex==CRemove) grid.Rows.RemoveAt(e.RowIndex);
            SaveGrid();RefreshMarketUi();
        }
        DataGridViewRow CurrentAccountRow(){
            if(grid.CurrentRow!=null&&grid.CurrentRow.Visible&&grid.CurrentRow.Tag is TikTokAccount)return grid.CurrentRow;
            var sel=Selected();
            if(sel.Count>0)return sel[0];
            string pid=marketView=="EN"?settings.LastSelectedTikTokProfileIdEn:settings.LastSelectedTikTokProfileIdRu;
            if(!string.IsNullOrWhiteSpace(pid)){
                var match=VisibleRows().FirstOrDefault(r=>string.Equals((((TikTokAccount)r.Tag).ProfileId??"").Trim(),pid.Trim(),StringComparison.OrdinalIgnoreCase));
                if(match!=null)return match;
            }
            var vis=VisibleRows();
            return vis.Count>0?vis[0]:null;
        }
        void RememberSelectedAccount(TikTokAccount acc){
            if(acc==null||string.IsNullOrWhiteSpace(acc.ProfileId))return;
            if(marketView=="EN")settings.LastSelectedTikTokProfileIdEn=acc.ProfileId.Trim();
            else settings.LastSelectedTikTokProfileIdRu=acc.ProfileId.Trim();
            SafeSave();
        }
        public event Action<string> LogLine;
        public void ReloadFromSettings(){LoadGrid();RefreshMarketUi();}
        public void SetMarketView(string market){SwitchMarket(NormMarket(market));}
        public void SelectProfileById(string profileId,string market){
            if(!string.IsNullOrWhiteSpace(market))SwitchMarket(NormMarket(market));
            string pid=(profileId??"").Trim();
            if(string.IsNullOrWhiteSpace(pid))return;
            var row=VisibleRows().FirstOrDefault(r=>string.Equals((((TikTokAccount)r.Tag).ProfileId??"").Trim(),pid,StringComparison.OrdinalIgnoreCase));
            if(row!=null){grid.ClearSelection();row.Selected=true;grid.CurrentCell=row.Cells[CName];RememberSelectedAccount((TikTokAccount)row.Tag);}
        }
        public void ApplyNavigationContext(){SelectProfileById(NavigationContext.SelectedProfileId,NavigationContext.SelectedMarket);}
        public Task RunUploadAsync()=>UploadAll(null);
        public Task RunUploadAsync(IReadOnlyList<TikTokAccount> accountsFilter)=>UploadAll(accountsFilter);
        public Task RunCheckProfilesAsync()=>CheckProfiles();
        public void RequestStopUpload(){stop.Enabled=false;if(uploadCts!=null)uploadCts.Cancel();if(cancellation!=null)cancellation.Cancel();Write("Остановка по запросу…");}
        public void AssignVideosToProfile(string profileId,string market,string[] files){
            if(files==null||files.Length==0)return;
            SetMarketView(market);
            SelectProfileById(profileId,market);
            var row=CurrentAccountRow();
            if(row==null)throw new Exception("Аккаунт не найден для Profile ID "+profileId);
            AssignVideoPackToAccount((TikTokAccount)row.Tag,row,files);
            SaveGrid();LoadGrid();
            row.Cells[COn].Value=true;SaveGrid();
        }
        public string CurrentLogFile=>logFile;
        void AssignVideosToSelectedAccount(){
            SaveGrid();
            var row=CurrentAccountRow();
            if(row==null){Ui.Error(this,new Exception("Выберите аккаунт в таблице, затем нажмите «Добавить видео»."));return;}
            var acc=(TikTokAccount)row.Tag;
            RememberSelectedAccount(acc);
            using(var d=new OpenFileDialog{Multiselect=true,Title="Добавить видео · "+acc.Name,Filter="Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*"}){
                if(d.ShowDialog(this)!=DialogResult.OK||d.FileNames.Length==0)return;
                try{
                    AssignVideoPackToAccount(acc,row,d.FileNames);
                    SaveGrid();LoadGrid();
                    row.Cells[COn].Value=true;
                    SaveGrid();
                }catch(Exception ex){Ui.Error(this,ex);}
            }
        }
        void AssignVideoPackToAccount(TikTokAccount acc,DataGridViewRow row,string[] filePaths){
            var paths=filePaths.OrderBy(f=>f,StringComparer.OrdinalIgnoreCase).ToArray();
            if(acc.Items!=null&&acc.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Video))){
                if(MessageBox.Show(this,"«"+acc.Name+"» уже имеет видео. Заменить новой пачкой ("+paths.Length+")?","TikTok",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            }
            List<string> captions=AllocCaptions(paths.Length);
            var items=new List<TikTokItem>();
            for(int i=0;i<paths.Length;i++)items.Add(new TikTokItem{Video=paths[i],Description=captions[i],Caption=captions[i]});
            acc.Items=items;acc.Enabled=true;acc.Market=marketView;acc.Status=ChannelStatus.Ready;
            SyncPrimary(acc);RefreshRowDisplay(row);
            Write("«"+acc.Name+"»: "+paths.Length+" видео, подписи назначены по кругу.");
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
                        if(acc.Items!=null&&acc.Items.Any(it=>!string.IsNullOrWhiteSpace(it.Video))){
                            if(MessageBox.Show(this,"«"+acc.Name+"» уже имеет видео. Заменить?","TikTok",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)continue;
                        }
                        List<string> captions;
                        try{captions=AllocCaptions(vids.Count);}catch(Exception ex){Ui.Error(this,ex);return;}
                        var items=new List<TikTokItem>();
                        for(int i=0;i<vids.Count;i++)items.Add(new TikTokItem{Video=vids[i],Description=captions[i],Caption=captions[i]});
                        acc.Items=items;acc.Enabled=true;acc.Market=marketView;SyncPrimary(acc);
                    }
                    SaveGrid();LoadGrid();
                    foreach(DataGridViewRow row in grid.Rows)if(((TikTokAccount)row.Tag).Enabled)row.Cells[COn].Value=true;
                    SaveGrid();
                    Write("["+MarketLabel(marketView)+"] видео назначены, подписи распределены по кругу.");
                }
            }
        }
        List<DataGridViewRow> VisibleRows(){grid.EndEdit();return grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Visible).ToList();}
        List<DataGridViewRow> Selected(){grid.EndEdit();return grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Visible&&Convert.ToBoolean(r.Cells[COn].Value??false)).ToList();}
        List<DataGridViewRow> UploadTargets(){
            var sel=Selected();
            if(sel.Count>0)return sel.Where(r=>AccountReadyForUpload((TikTokAccount)r.Tag)).ToList();
            return VisibleRows().Where(r=>AccountReadyForUpload((TikTokAccount)r.Tag)).ToList();
        }
        static bool AccountReadyForUpload(TikTokAccount a){
            if(a==null||string.IsNullOrWhiteSpace(a.ProfileId))return false;
            SyncPrimary(a);
            return a.Items!=null&&a.Items.Any(it=>it!=null&&!it.Published&&!string.IsNullOrWhiteSpace(it.Video)&&!string.IsNullOrWhiteSpace(it.Caption));
        }
        DataGridViewRow FindAccountRow(TikTokAccount account){
            if(account==null)return null;
            SaveGrid();
            if(!string.IsNullOrWhiteSpace(account.ProfileId)){
                var byPid=VisibleRows().FirstOrDefault(r=>string.Equals((((TikTokAccount)r.Tag).ProfileId??"").Trim(),account.ProfileId.Trim(),StringComparison.OrdinalIgnoreCase));
                if(byPid!=null)return byPid;
            }
            return VisibleRows().FirstOrDefault(r=>ReferenceEquals(r.Tag,account));
        }
        List<DataGridViewRow> ResolveUploadRows(IReadOnlyList<TikTokAccount> accountsFilter){
            if(accountsFilter!=null&&accountsFilter.Count>0){
                SaveGrid();
                var rows=new List<DataGridViewRow>();
                foreach(var acc in accountsFilter){
                    if(acc==null)continue;
                    var row=FindAccountRow(acc);
                    if(row!=null&&!rows.Contains(row))rows.Add(row);
                }
                if(rows.Count==0)LoadGrid();
                return rows;
            }
            return UploadTargets();
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
                    string cellCaption=Convert.ToString(row.Cells[CCaption].Value??"").Trim();
                    a.Description=cellCaption;a.Caption=cellCaption;
                    if(a.Items.Count==1){a.Items[0].Description=cellCaption;a.Items[0].Caption=cellCaption;}
                    else if(!string.IsNullOrWhiteSpace(cellCaption)||!string.IsNullOrWhiteSpace(a.Video))
                        a.Items.Add(new TikTokItem{Video=a.Video??"",Description=cellCaption,Caption=cellCaption});
                } else {
                    foreach(var it in a.Items)if(it!=null){if(string.IsNullOrWhiteSpace(it.Caption))it.Caption=it.Description??"";it.Caption=(it.Caption??"").Trim();it.Description=it.Caption;}
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
            foreach(var b in new[]{setup,add,importYt,files,captionsBtn,musicBtn,check,upload,marketRu,marketEn})b.Enabled=!value;
            grid.Enabled=!value;
            stop.Visible=value||uploadsInFlight>0;stop.Enabled=value||uploadsInFlight>0;
            if(runHint!=null){runHint.Visible=value;runHint.Text=value?"Идёт проверка…":"";}
        }
        void UploadBusyStart(){
            uploadsInFlight++;upload.Enabled=true;stop.Visible=true;stop.Enabled=true;
            check.Enabled=false;setup.Enabled=false;marketRu.Enabled=false;marketEn.Enabled=false;
            add.Enabled=true;importYt.Enabled=true;files.Enabled=true;captionsBtn.Enabled=true;musicBtn.Enabled=true;grid.Enabled=true;
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
            try{LogLine?.Invoke(line);}catch{}
            log.AppendText(line+Environment.NewLine);log.ScrollToCaret();
            try{Directory.CreateDirectory(Store.Root);if(string.IsNullOrWhiteSpace(logFile))logFile=Path.Combine(Store.Root,"tiktok-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");File.AppendAllText(logFile,line+Environment.NewLine,Encoding.UTF8);}catch{}
        }
        void ValidateCommon(bool forUpload,IReadOnlyList<TikTokAccount> accountsFilter=null){
            DolphinRunner.CheckFilesTikTok();
            if(string.IsNullOrWhiteSpace(WindowsSupport.Unprotect(settings.ProtectedDolphinToken)))throw new Exception("Шаг 1: укажите API-токен Dolphin.");
            var rows=forUpload?ResolveUploadRows(accountsFilter):Selected();
            if(rows.Count==0)throw new Exception(forUpload?"Отметьте галочкой ✓ аккаунты с видео и Profile ID. Ещё → «Из YouTube» подтянет ID.":"Отметьте аккаунты галочкой.");
            foreach(var row in rows){
                var a=(TikTokAccount)row.Tag;
                if(string.IsNullOrWhiteSpace(a.ProfileId))throw new Exception("«"+a.Name+"»: нет Profile ID. Меню «Ещё» → «Из YouTube» или добавьте ID вручную.");
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
        async Task<UploadRunResult> RunUploadWithRetry(UploadJob job,TikTokAccount acc,DataGridViewRow row,List<TikTokItem> items,CancellationToken ct,string token){
            const int maxAttempts=2;
            Exception last=null;
            for(int attempt=1;attempt<=maxAttempts;attempt++){
                try{
                    return await DolphinRunner.RunTikTok(job,m=>{
                        if(!string.IsNullOrWhiteSpace(m.ip))PropagateIp(acc.ProfileId,m.ip);
                        if(m.stage=="item_done"&&m.packIndex>0&&m.packIndex<=items.Count){
                            items[m.packIndex-1].Published=true;
                            SafeSave();
                        }
                        if(!string.IsNullOrWhiteSpace(m.text))Status(row,m.text);
                    },ct).ConfigureAwait(false);
                }catch(OperationCanceledException){throw;}
                catch(Exception e){
                    last=e;
                    // После открытия TikTok повтор может создать дубль: оставляем профиль для ручной проверки.
                    if(e is UploadException uncertain&&uncertain.KeptOpen)break;
                    if(attempt>=maxAttempts)break;
                    Write(acc.Name+": ошибка, перезапуск профиля (попытка "+attempt+"/"+maxAttempts+")…");
                    Status(row,ChannelStatus.Preparing);
                    try{await DolphinRunner.StopProfile(token,settings.DolphinPort,(acc.ProfileId??"").Trim()).ConfigureAwait(false);}catch{}
                    await Task.Delay(3000,ct).ConfigureAwait(false);
                }
            }
            throw last??new Exception("Неизвестная ошибка загрузки TikTok.");
        }
        async Task UploadAll(IReadOnlyList<TikTokAccount> accountsFilter){
            if(cancellation!=null){MessageBox.Show(this,"Сначала дождитесь проверки или нажмите Стоп.","TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            string token="";bool uiStarted=false;
            try{
                SyncFromYouTube();
                SaveGrid();ValidateCommon(true,accountsFilter);
                var rows=ResolveUploadRows(accountsFilter);
                Write("К загрузке TikTok: "+rows.Count+" акк.");
                var packs=new List<(DataGridViewRow row,TikTokAccount acc,List<TikTokItem> items)>();
                foreach(var row in rows){
                    var a=(TikTokAccount)row.Tag;SyncPrimary(a);
                    var items=(a.Items??new List<TikTokItem>()).Where(it=>!it.Published&&!string.IsNullOrWhiteSpace(it.Video)&&!string.IsNullOrWhiteSpace(it.Caption)).ToList();
                    if(items.Count==0){Write(a.Name+": нет ожидающих публикации видео.");continue;}
                    foreach(var it in items){
                        if(!File.Exists(it.Video))throw new Exception(a.Name+": файл не найден — "+it.Video);
                        it.Caption=(it.Caption??"").Trim();it.Description=it.Caption;
                        if(string.IsNullOrWhiteSpace(it.Caption))throw new Exception(a.Name+": пустая подпись.");
                        if(it.Caption.Length>2200)throw new Exception(a.Name+": подпись длиннее 2200 символов — сократите её в базе подписей.");
                    }
                    packs.Add((row,a,items));
                    Status(row,ChannelStatus.Preparing);
                }
                if(packs.Count==0){Write("Нет ожидающих публикации видео.");return;}
                List<(DataGridViewRow row,TikTokAccount acc,List<TikTokItem> items)> free;
                lock(uploadLock){free=packs.Where(p=>!busyProfiles.Contains((p.acc.ProfileId??"").Trim())).ToList();}
                if(free.Count==0){MessageBox.Show(this,"Выбранные аккаунты уже загружаются.","TikTok",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}

                lock(uploadLock){
                    if(uploadCts==null)uploadCts=new CancellationTokenSource();
                    foreach(var p in free)busyProfiles.Add((p.acc.ProfileId??"").Trim());
                }
                var ct=uploadCts.Token;token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                UploadBusyStart();uiStarted=true;
                int parallel=1;
                int minMs=Math.Max(60000,settings.TikTokUploadStaggerMinMinutes*60000);
                int maxMs=Math.Max(minMs,settings.TikTokUploadStaggerMaxMinutes*60000);
                var launchGate=new ProfileLaunchGate(parallel,minMs,maxMs);
                Write("["+MarketLabel(marketView)+"] TikTok: "+free.Count+" акк. · по очереди · пауза между стартами "
                    +settings.TikTokUploadStaggerMinMinutes+"–"+settings.TikTokUploadStaggerMaxMinutes+" мин (ПК).");

                var errors=new ConcurrentBag<string>();
                var tasks=free.Select(pack=>Task.Run(async()=>{
                    var row=pack.row;var a=pack.acc;var pid=(a.ProfileId??"").Trim();
                    try{
                        await launchGate.EnterAsync(ct).ConfigureAwait(false);
                        try{
                            ct.ThrowIfCancellationRequested();
                            Status(row,ChannelStatus.Uploading);
                            var items=pack.items.Select(it=>new UploadItemJob{
                                video=it.Video,
                                caption=it.Caption,
                                description=it.Caption
                            }).ToArray();
                            var job=new UploadJob{
                                token=token,localPort=settings.DolphinPort,profileId=a.ProfileId,expectedIp=a.ExpectedIp,
                                items=items,skipQueueDelay=true,checkOnly=false,
                                video=items[0].video
                            };
                            var result=await RunUploadWithRetry(job,a,row,pack.items,ct,token).ConfigureAwait(false);
                            foreach(var it in pack.items)it.Published=true;
                            a.Status=ChannelStatus.Published;
                            Status(row,pack.items.Count>1?("Опубликовано "+pack.items.Count+" ✓"):(string.IsNullOrWhiteSpace(result.Url)?"Опубликовано ✓":"Опубликовано ✓ "+result.Url));
                            if(!string.IsNullOrWhiteSpace(result.Ip))PropagateIp(a.ProfileId,result.Ip);
                            SafeSave();
                        }finally{launchGate.Exit();}
                    }catch(OperationCanceledException){Status(row,"Остановлено");}
                    catch(Exception e){
                        a.Status=ChannelStatus.Error;
                        string msg=e.Message+(e is UploadException ue&&ue.KeptOpen?" (профиль открыт)":"");
                        errors.Add(a.Name+": "+msg);Status(row,ChannelStatus.Error);
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
