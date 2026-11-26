using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    internal sealed class InstagramPanel : UserControl {
        readonly Preferences settings;
        readonly DataGridView grid;
        readonly Label status;
        readonly Button open;
        readonly Dictionary<string,InstagramWebWindow> windows=new Dictionary<string,InstagramWebWindow>();
        bool loading;
        List<DolphinBrowserProfile> dolphinProfiles=new List<DolphinBrowserProfile>();
        readonly Button addAccount;
        readonly DataGridView queue;
        bool rendering;
        public InstagramPanel(Preferences settings) {
            this.settings=settings;Dock=DockStyle.Fill;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;
            DashboardSessionState.Bind(this,()=>RefreshProfiles());
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=54,Padding=new Padding(8),WrapContents=false};
            addAccount=Theme.MakeButton("+ Добавить аккаунт",accent:true,action:async()=>await AddAccount());addAccount.Name="AddMetaAccount";bar.Controls.Add(addAccount);
            open=Theme.MakeButton("Instagram / Threads / Meta",ghost:true,action:async()=>await OpenSelected());bar.Controls.Add(open);
            var profileBar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8,8,8,0),WrapContents=true};
            var fillProfile=Theme.MakeButton("Оформить профиль · Instagram",accent:true,action:()=>OpenProfilePresets("Instagram"));
            fillProfile.Name="FillInstagramProfile";profileBar.Controls.Add(fillProfile);
            var fillThreads=Theme.MakeButton("Оформить профиль · Threads",accent:true,action:()=>OpenProfilePresets("Threads"));
            fillThreads.Name="FillThreadsProfile";profileBar.Controls.Add(fillThreads);
            var batch=Theme.MakeButton("Пакетное оформление Instagram",accent:true,action:async()=>await RunPresetBatch());
            batch.Name="InstagramPresetBatch";profileBar.Controls.Add(batch);
            bar.Controls.Add(Theme.MakeButton("Обновить из Dolphin",ghost:true,action:async()=>await FetchProfiles()));
            status=new Label{Dock=DockStyle.Bottom,Height=70,Padding=new Padding(10),Text="YouTube Shorts появляются по Dolphin ID автоматически; отметьте аккаунты слева. Статусы Instagram независимы. Автопубликация недоступна: загрузка и подтверждение в WebView2 ещё не проверены.",ForeColor=Theme.TextSecondary};
            grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Theme.Background,RowHeadersVisible=false,AutoGenerateColumns=false};
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="publish",HeaderText="Выбрать",ReadOnly=false,FillWeight=35});grid.ReadOnly=false;
            grid.Columns.Add("name","Профиль Dolphin");grid.Columns.Add("market","Instagram · последнее наблюдение");grid.Columns.Add("profile","Dolphin ID");
            foreach(DataGridViewColumn column in grid.Columns)if(column.Name!="publish")column.ReadOnly=true;
            grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
            grid.CellValueChanged+=(s,e)=>{if(!rendering&&e.RowIndex>=0&&e.ColumnIndex==0)SaveSelection();};
            grid.SelectionChanged+=(s,e)=>{if(!rendering)RefreshQueue();};
            grid.CellDoubleClick+=async(s,e)=>{if(e.RowIndex>=0&&e.ColumnIndex!=0)await OpenSelected();};
            queue=new DataGridView{Name="InstagramShortsQueue",Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,BackgroundColor=Theme.Background};
            Theme.StyleGrid(queue);queue.Columns.Add("title","YouTube Shorts · заголовок");queue.Columns.Add("video","Исходный файл");queue.Columns.Add("state","Instagram · отдельный статус");
            var queueActions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=46,Padding=new Padding(8,4,8,4),WrapContents=false};
            queueActions.Controls.Add(Theme.MakeButton("Обновить Shorts",ghost:true,action:RefreshProfiles));
            var publish=Theme.MakeButton("Опубликовать выбранные · недоступно",ghost:true,action:PublishSelected);publish.Name="PublishInstagramQueue";queueActions.Controls.Add(publish);
            var queueBody=new Panel{Dock=DockStyle.Fill};queueBody.Controls.Add(queue);queueBody.Controls.Add(queueActions);
            var split=new SplitContainer{Size=new Size(800,450),Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=190,Panel1MinSize=80,Panel2MinSize=80};
            split.Panel1.Controls.Add(grid);split.Panel2.Controls.Add(queueBody);
            Controls.Add(split);Controls.Add(bar);Controls.Add(profileBar);Controls.Add(status);RefreshProfiles();
            Disposed+=(s,e)=>{foreach(var w in windows.Values.ToArray())if(!w.IsDisposed)w.Close();};
        }
        async Task RunPresetBatch() {
            if(loading)return;
            string path=InstagramPresetBatchRunner.JobsPath;
            if(MessageBox.Show(this,"Оформить профили из списка?\n"+path+"\n\nНужен сохранённый вход Instagram в каждом профиле.","Пакетное оформление",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            loading=true;grid.Enabled=false;
            try{
                var lines=await InstagramPresetBatchRunner.RunAllAsync(line=>{if(!IsDisposed)status.Text=line;});
                int ok=lines.Count(l=>l.StartsWith("OK ",StringComparison.Ordinal));
                int fail=lines.Count(l=>l.StartsWith("FAIL ",StringComparison.Ordinal));
                status.Text="Пакетное оформление завершено: успешно "+ok+", ошибок "+fail+".";
                MessageBox.Show(this,status.Text+"\n\nСписок: "+path,"Готово",MessageBoxButtons.OK,fail>0?MessageBoxIcon.Warning:MessageBoxIcon.Information);
            }catch(Exception error){
                status.Text=error is InvalidOperationException?error.Message:"Пакетное оформление прервано.";
                MessageBox.Show(this,status.Text+"\n\nСписок: "+path,"Ошибка",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }finally{loading=false;grid.Enabled=true;}
        }
        void OpenProfilePresets(string network) {
            var profiles=(settings.YouTubeChannels??new List<YouTubeChannel>()).Where(c=>c!=null&&!string.IsNullOrWhiteSpace(c.ProfileId))
                .GroupBy(c=>c.ProfileId.Trim()).Select(g=>g.First());
            var account=ProfilePresetActions.ChooseAccount(this,network,profiles,c=>c.Name+" · "+c.Market+" · Dolphin "+c.ProfileId.Trim());
            if(account!=null)ProfilePresetActions.OpenAndroid(this,network,account.ProfileId.Trim(),account.Name,line=>{if(!IsDisposed)status.Text=line;});
        }
        public void RefreshProfiles() {
            if(loading)return;
            string selected=grid.CurrentRow?.Tag as string;rendering=true;
            try {
                grid.Rows.Clear();
                var observations=MetaProfileState.Load();
                var profiles=dolphinProfiles.Concat(MetaProfileState.LocalProfiles(settings,observations)).GroupBy(p=>p.Id).Select(g=>g.Last()).ToList();
                if(InstagramQueue.Sync(settings,profiles.Select(p=>p.Id)))Store.Save(settings);
                foreach(var c in profiles) {
                    int i=grid.Rows.Add((settings.InstagramSelectedProfiles??new List<string>()).Contains(c.Id),c.Name,MetaProfileState.InstagramStatus(observations.FirstOrDefault(p=>p.Id==c.Id),DateTime.UtcNow),c.Id);grid.Rows[i].Tag=c.Id;
                    Theme.MarkConnected(grid.Rows[i].DefaultCellStyle,DashboardSessionState.IsConnected(c.Id,"Instagram"));
                    grid.Rows[i].Cells["name"].ToolTipText=DashboardSessionState.Summary(c.Id,"Instagram");
                    if(c.Id==selected)grid.CurrentCell=grid.Rows[i].Cells[1];
                }
            } catch(Exception e){status.Text="Не удалось обновить очередь Instagram: "+e.Message;AppDiagnostics.Write("Instagram","","","queue_refresh_error",e.GetType().Name,"error");}
            finally{rendering=false;RefreshQueue();}
        }
        void RefreshQueue() {
            if(queue==null)return;
            queue.Rows.Clear();string id=grid.CurrentRow?.Tag as string;if(id==null)return;
            foreach(var item in InstagramQueue.ForProfile(settings,id)) {
                int index=queue.Rows.Add(item.Title,item.Video,InstagramQueue.Status(item));
                queue.Rows[index].Tag=item;queue.Rows[index].Cells[2].ToolTipText=item.Detail;
            }
        }
        void SaveSelection() {
            try {
                settings.InstagramSelectedProfiles=grid.Rows.Cast<DataGridViewRow>().Where(r=>Convert.ToBoolean(r.Cells[0].Value)).Select(r=>(string)r.Tag).ToList();
                Store.Save(settings);
            }catch(Exception error){status.Text="Не удалось сохранить выбор аккаунтов: "+error.Message;AppDiagnostics.Write("Instagram","","","queue_selection_error",error.GetType().Name,"error");}
        }
        void PublishSelected() {
            try {
                var ids=grid.Rows.Cast<DataGridViewRow>().Where(r=>Convert.ToBoolean(r.Cells[0].Value)).Select(r=>(string)r.Tag).ToList();
                if(ids.Count==0){status.Text="Отметьте аккаунты Instagram флажками слева.";return;}
                bool changed=InstagramQueue.Sync(settings,ids);
                var rows=ids.SelectMany(id=>InstagramQueue.ForProfile(settings,id)).ToList();
                if(rows.Count==0){status.Text="Для выбранных Dolphin-профилей нет назначенных YouTube Shorts. Long-видео не импортируются.";return;}
                changed=InstagramQueue.BlockUnverifiedPublishing(rows)||changed;
                if(changed)Store.Save(settings);
                status.Text=InstagramQueue.PublishBlocker;
                AppDiagnostics.Write("Instagram","","","publish_blocked","Автопубликация не запускалась: загрузка и подтверждение в WebView2 не проверены.","warning");
                RefreshQueue();
            }catch(Exception error){status.Text="Не удалось обновить состояние очереди: "+error.Message;AppDiagnostics.Write("Instagram","","","queue_publish_error",error.GetType().Name,"error");}
        }
        async Task FetchProfiles() {
            if(loading)return;loading=true;addAccount.Enabled=false;
            try{var fetched=await DolphinProfileCatalog.FetchAll(WindowsSupport.Unprotect(settings.ProtectedDolphinToken));if(!IsDisposed){dolphinProfiles=fetched;status.Text="Профили Dolphin загружены. Вход выполняется вручную.";}}
            catch(Exception e){if(!IsDisposed)status.Text="Не удалось загрузить Dolphin: "+e.Message;AppDiagnostics.Write("Instagram","","","profiles_error",e.GetType().Name,"error");}
            finally{loading=false;if(!IsDisposed){addAccount.Enabled=true;RefreshProfiles();}}
        }
        async Task AddAccount() {
            if(loading)return;
            await FetchProfiles();
            if(IsDisposed)return;
            var choices=grid.Rows.Cast<DataGridViewRow>().Select(r=>new DolphinBrowserProfile{Id=(string)r.Tag,Name=Convert.ToString(r.Cells[1].Value)});
            var profile=MetaProfileState.Choose(this,choices);if(profile==null)return;
            foreach(DataGridViewRow row in grid.Rows)if((string)row.Tag==profile.Id){grid.CurrentCell=row.Cells[1];break;}
            await OpenSelected();
        }
        internal async Task ConnectProfile(string id,string name,string network) {
            if(!dolphinProfiles.Any(p=>p.Id==id))dolphinProfiles.Add(new DolphinBrowserProfile{Id=id,Name=name});
            RefreshProfiles();
            foreach(DataGridViewRow row in grid.Rows)if((string)row.Tag==id){grid.CurrentCell=row.Cells[1];break;}
            await OpenSelected();
            if(windows.TryGetValue(id,out var window)&&!window.IsDisposed)window.OpenNetwork(network);
        }
        async Task OpenSelected() {
            if(loading)return;
            string id=grid.CurrentRow?.Tag as string;if(id==null){status.Text="Выберите профиль";return;}
            if(windows.TryGetValue(id,out var existing)&&!existing.IsDisposed){
                var view=typeof(InstagramWebWindow).GetField("view",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(existing) as Microsoft.Web.WebView2.WinForms.WebView2;
                if(view!=null&&!view.IsDisposed&&view.CoreWebView2!=null){existing.Show();existing.Activate();return;}
                existing.Close();while(!existing.IsDisposed)await Task.Delay(100);windows.Remove(id);
            }
            loading=true;open.Enabled=false;grid.Enabled=false;
            try{
                status.Text="Получаю назначенный этому профилю прокси Dolphin…";
                string name=Convert.ToString(grid.CurrentRow.Cells[1].Value);
                var result=await TelegramProxyCatalog.FetchDolphin(WindowsSupport.Unprotect(settings.ProtectedDolphinToken),new[]{new KeyValuePair<string,string>(id,name)},includeCatalog:false);
                var proxy=result.Proxies.SingleOrDefault();
                if(proxy==null)throw new InvalidOperationException(result.Warnings.Count>0?string.Join("; ",result.Warnings):"В Dolphin этому профилю не назначен прокси. Прямое соединение не используется.");
                var window=new InstagramWebWindow(id,name,proxy.url);windows[id]=window;
                window.FormClosed+=(s,e)=>{if(windows.TryGetValue(id,out var current)&&ReferenceEquals(current,window))windows.Remove(id);if(!IsDisposed)RefreshProfiles();};
                window.Show(FindForm());status.Text="Открыто: "+name+". Instagram, Threads и Центр аккаунтов Meta доступны в одном окне; сессия сохраняется в профиле.";
            }catch(Exception e){status.Text=e is InvalidOperationException?e.Message:"Не удалось получить профиль или открыть окно. Подробности в журнале Instagram.";AppDiagnostics.Write("Instagram",id,"","open_error",e.GetType().Name,"error");}
            finally{loading=false;open.Enabled=true;grid.Enabled=true;}
        }
    }
}
