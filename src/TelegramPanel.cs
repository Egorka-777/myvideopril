using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class TelegramPanel : UserControl {
        readonly TelegramStore store;
        readonly TelegramRunner runner;
        readonly DataGridView grid;
        readonly RichTextBox log;
        readonly Label summary, status;
        readonly TextBox search, users, target;
        readonly NumericUpDown delay,inviteLimit;
        readonly ComboBox senderChoice;
        readonly Label recipientHint,targetHint;
        readonly Dictionary<string,TelegramWebWindow> webWindows=new Dictionary<string,TelegramWebWindow>();
        readonly Panel pageHost;
        readonly FlowLayoutPanel tabStrip;
        readonly List<Panel> pages=new List<Panel>();
        readonly List<Button> pageButtons=new List<Button>();
        readonly FlowLayoutPanel actions;
        readonly Button stop;
        readonly List<Button> workButtons=new List<Button>();
        bool busy, unavailable;
        Process preparation;
        public bool HasWork => busy||webWindows.Values.Any(w=>!w.IsDisposed);
        public TelegramPanel(TelegramStore storage=null,Func<Preferences> preferences=null) {
            getPreferences=preferences;
            store=storage??new TelegramStore();runner=new TelegramRunner(store);
            Dock=DockStyle.Fill;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));Controls.Add(layout);
            actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};layout.Controls.Add(actions,0,0);
            WorkButton("＋ Аккаунты",ChooseImport,true);
            WorkButton("Проверить",async()=>await RunSelected("check"));
            WorkButton("Настройки",Settings);
            actions.Controls.Add(new Label{Text="Поиск",AutoSize=true,ForeColor=Theme.TextMuted,Margin=new Padding(6,9,4,0)});
            search=Theme.MakeSearchBox();search.Width=170;search.Margin=new Padding(4,7,0,0);search.TextChanged+=(s,e)=>RefreshData();actions.Controls.Add(search);
            summary=new Label{Dock=DockStyle.Fill,ForeColor=Theme.TextSecondary,TextAlign=ContentAlignment.MiddleLeft};layout.Controls.Add(summary,0,1);
            tabStrip=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty,BackColor=Theme.Background};layout.Controls.Add(tabStrip,0,2);
            pageHost=new Panel{Dock=DockStyle.Fill,Margin=Padding.Empty,BackColor=Theme.Background};layout.Controls.Add(pageHost,0,3);
            var accountsPage=Page("Аккаунты");var proxiesPage=Page("Прокси");var invitePage=Page("Приглашения");var logsPage=Page("Журнал");
            BuildProxyPage(proxiesPage);
            grid=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,ReadOnly=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=true,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};Theme.StyleGrid(grid);
            grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;
            grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="selected",HeaderText="",AutoSizeMode=DataGridViewAutoSizeColumnMode.None,Width=40,MinimumWidth=40,DefaultCellStyle=new DataGridViewCellStyle{Padding=Padding.Empty}});
            foreach(var spec in new[]{new[]{"name","Аккаунт"},new[]{"username","Username"},new[]{"status","Состояние"},new[]{"proxy","Прокси"},new[]{"country","Страна"},new[]{"checked","Попытка"},new[]{"detail","Результат / причина"}}){var i=grid.Columns.Add(spec[0],spec[1]);grid.Columns[i].ReadOnly=true;}
            foreach(var spec in new[]{new[]{"name","95","120"},new[]{"username","80","90"},new[]{"status","90","110"},new[]{"proxy","155","170"},new[]{"country","45","60"},new[]{"checked","65","80"},new[]{"detail","170","180"}}){grid.Columns[spec[0]].FillWeight=int.Parse(spec[1]);grid.Columns[spec[0]].MinimumWidth=int.Parse(spec[2]);}
            grid.Columns["detail"].DefaultCellStyle.WrapMode=DataGridViewTriState.True;
            grid.CellDoubleClick+=(s,e)=>{if(e.RowIndex>=0)EditAccount(grid.Rows[e.RowIndex].Tag as TelegramAccount);};
            grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
            var context=new ContextMenuStrip{BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary};context.Items.Add("Карточка аккаунта",null,(s,e)=>EditAccount(grid.CurrentRow?.Tag as TelegramAccount));
            context.Items.Add("Открыть Telegram",null,(s,e)=>OpenWeb(grid.CurrentRow?.Tag as TelegramAccount));
            context.Items.Add("Удалить из Telegram-раздела",null,(s,e)=>RemoveSelected());grid.ContextMenuStrip=context;
            grid.CellMouseDown+=(s,e)=>{if(e.Button==MouseButtons.Right&&e.RowIndex>=0){grid.ClearSelection();grid.Rows[e.RowIndex].Selected=true;grid.CurrentCell=grid.Rows[e.RowIndex].Cells[1];}};
            var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};table.RowStyles.Add(new RowStyle(SizeType.Percent,100));table.RowStyles.Add(new RowStyle(SizeType.Absolute,52));table.Controls.Add(grid,0,0);
            var foot=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,6,0,0)};
            foot.Controls.Add(Theme.MakeButton("Выбрать все",ghost:true,action:()=>{foreach(DataGridViewRow row in grid.Rows)row.Cells[0].Value=true;}));
            foot.Controls.Add(Theme.MakeButton("Карточка",ghost:true,action:()=>EditAccount(grid.CurrentRow?.Tag as TelegramAccount)));
            foot.Controls.Add(Theme.MakeButton("Назначить прокси",ghost:true,action:AssignProxy));
            foot.Controls.Add(Theme.MakeButton("Открыть Telegram",ghost:true,action:OpenWeb));
            foot.Controls.Add(new Label{Text="",AutoSize=true,ForeColor=Theme.TextMuted,Margin=new Padding(6,9,0,0)});table.Controls.Add(foot,0,1);accountsPage.Controls.Add(table);
            var form=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=9,Padding=new Padding(16)};
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(var h in new[]{32,45,45,35,46})form.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
            form.RowStyles.Add(new RowStyle(SizeType.Percent,100));foreach(var h in new[]{30,52,48})form.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
            form.Controls.Add(new Label{Text="Тест приглашений",AutoSize=true,Font=Theme.FontCardTitle},0,0);
            var senderRow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};senderRow.Controls.Add(new Label{Text="Отправитель",Width=110,Margin=new Padding(0,8,0,0)});
            senderChoice=TelegramImportDialog.ProxyChoices(new TelegramData());senderChoice.Items.Clear();senderChoice.Width=390;senderChoice.Name="InviteSender";senderChoice.DisplayMember="name";senderRow.Controls.Add(senderChoice);form.Controls.Add(senderRow,0,1);
            var targetRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3};targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,170));
            targetRow.Controls.Add(new Label{Text="Группа / канал",AutoSize=true,Margin=new Padding(0,8,0,0)},0,0);
            target=Theme.MakeSearchBox();target.Name="InviteTarget";target.Dock=DockStyle.Fill;targetRow.Controls.Add(target,1,0);
            var preview=Theme.MakeButton("Проверить права",ghost:true,action:async()=>await PreviewTarget());workButtons.Add(preview);targetRow.Controls.Add(preview,2,0);form.Controls.Add(targetRow,0,2);
            targetHint=new Label{Text="Вставьте ссылку t.me/… или @username. Для теста используйте свою группу с правом добавлять участников.",Dock=DockStyle.Fill,ForeColor=Theme.TextSecondary};form.Controls.Add(targetHint,0,3);
            var fileRow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};var loadUsers=Theme.MakeButton("＋ Список из файла",ghost:true,action:LoadRecipients);workButtons.Add(loadUsers);fileRow.Controls.Add(loadUsers);fileRow.Controls.Add(new Label{Text="TXT, CSV, JSON · @username или ID пользователя",AutoSize=true,ForeColor=Theme.TextSecondary,Margin=new Padding(6,9,0,0)});form.Controls.Add(fileRow,0,4);
            users=Theme.MakeSearchBox();users.Multiline=true;users.ScrollBars=ScrollBars.Vertical;users.Dock=DockStyle.Fill;users.Name="InviteUsers";form.Controls.Add(users,0,5);
            recipientHint=new Label{Dock=DockStyle.Fill,ForeColor=Theme.TextSecondary};form.Controls.Add(recipientHint,0,6);users.TextChanged+=(s,e)=>{try{var list=TelegramRecipients.Parse(users.Text);recipientHint.Text="В списке: "+list.Users.Count+(list.Invalid>0?" · некорректных: "+list.Invalid:"");}catch{recipientHint.Text="Проверьте размер/формат списка";}};
            var options=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,5,0,0)};
            options.Controls.Add(new Label{Text="Обработать не более",AutoSize=true,Margin=new Padding(0,7,8,0)});
            inviteLimit=new NumericUpDown{Minimum=1,Maximum=10000,Value=1,Width=75,BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary,Name="InviteLimit"};options.Controls.Add(inviteLimit);
            options.Controls.Add(new Label{Text="чел.   Пауза",AutoSize=true,Margin=new Padding(5,7,8,0)});
            delay=new NumericUpDown{Minimum=10,Maximum=3600,Value=60,Width=82,BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary};options.Controls.Add(delay);
            options.Controls.Add(new Label{Text="сек",AutoSize=true,Margin=new Padding(5,7,10,0)});
            var start=Theme.MakeButton("Запустить",accent:true,action:async()=>await RunInvites());workButtons.Add(start);options.Controls.Add(start);form.Controls.Add(options,0,7);
            form.Controls.Add(new Label{Text="Один отправитель. Повторы блокируются историей; при ограничении Telegram — остановка. Лимит приложения не гарантирует отсутствие ограничений Telegram.",Dock=DockStyle.Fill,ForeColor=Theme.TextMuted},0,8);invitePage.Controls.Add(form);
            log=Theme.MakeLogBox();logsPage.Controls.Add(log);
            var bottom=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,6,0,0)};
            stop=Theme.MakeButton("Остановить",ghost:true,action:async()=>await runner.Stop());stop.Enabled=false;bottom.Controls.Add(stop);
            bottom.Controls.Add(Theme.MakeButton("Открыть логи",ghost:true,action:()=>{Directory.CreateDirectory(AppDiagnostics.Folder);Process.Start(new ProcessStartInfo(AppDiagnostics.Folder){UseShellExecute=true});}));
            status=new Label{Text="Добавьте аккаунты, затем проверьте соединение",Width=560,Height=32,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,ForeColor=Theme.TextSecondary};bottom.Controls.Add(status);layout.Controls.Add(bottom,0,4);
            SelectTab(0);
            AllowDrop=true;DragEnter+=(s,e)=>e.Effect=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;
            DragDrop+=async(s,e)=>{if(!busy&&e.Data.GetDataPresent(DataFormats.FileDrop))await Import((string[])e.Data.GetData(DataFormats.FileDrop));};
            try{SeedProxies();var data=store.Load();target.Text=data.target;delay.Value=Math.Max(10,Math.Min(3600,data.delay));inviteLimit.Value=Math.Max(1,Math.Min(10000,data.invite_limit));RefreshData();RefreshProxies();}catch(Exception e){status.Text=e.Message;actions.Enabled=false;unavailable=true;}
        }
        protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);TryAutoSync();}
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);TryAutoSync();}
        void TryAutoSync(){if(Visible&&IsHandleCreated&&proxyGrid!=null&&!unavailable&&!autoSyncTried&&getPreferences!=null){autoSyncTried=true;BeginInvoke(new Action(async()=>{if(!busy&&!string.IsNullOrEmpty(getPreferences()?.ProtectedDolphinToken))await SyncDolphin();}));}}
        Panel Page(string title){
            int index=pages.Count;var page=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Background,ForeColor=Theme.TextPrimary,Visible=false};pages.Add(page);pageHost.Controls.Add(page);
            var button=Theme.MakeButton(title,action:()=>SelectTab(index));button.MinimumSize=new Size(100,34);pageButtons.Add(button);tabStrip.Controls.Add(button);return page;
        }
        internal void SelectTab(int index){
            if(index<0||index>=pages.Count)throw new ArgumentOutOfRangeException(nameof(index));
            for(int i=0;i<pages.Count;i++){pages[i].Visible=i==index;pageButtons[i].BackColor=i==index?Theme.Selected:Theme.Background;pageButtons[i].ForeColor=i==index?Theme.TextPrimary:Theme.TextSecondary;}
            pages[index].BringToFront();if(index==2&&senderChoice!=null)RefreshSenders();
        }
        void WorkButton(string text,Action action,bool accent=false){var b=Theme.MakeButton(text,accent:accent,action:()=>{try{action();}catch(Exception e){Failure(e);}});actions.Controls.Add(b);workButtons.Add(b);}
        public void RefreshData() {
            if(grid==null||busy||unavailable)return;
            var selected=new HashSet<string>(grid.Rows.Cast<DataGridViewRow>().Where(r=>Convert.ToBoolean(r.Cells[0].Value)).Select(r=>((TelegramAccount)r.Tag).id));
            string current=(grid.CurrentRow?.Tag as TelegramAccount)?.id;
            TelegramData data;try{data=store.Load();}catch(Exception e){unavailable=true;actions.Enabled=false;status.Text=e.Message;return;}
            var all=data.accounts;string q=search.Text.Trim();grid.Rows.Clear();
            foreach(var a in all.Where(a=>string.Join(" ",a.name,a.username,a.phone,a.country,a.note).IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0)){
                int i=grid.Rows.Add(selected.Contains(a.id),a.name,string.IsNullOrEmpty(a.username)?"—":"@"+a.username,"","",a.country,"","");grid.Rows[i].Tag=a;PaintHealth(grid.Rows[i],a);
                if(a.id==current)grid.CurrentCell=grid.Rows[i].Cells[1];
            }
            RefreshSenders();
            int ready=all.Count(a=>TelegramHealth.AccountLabel(a).Contains("Работает")),uncheckedCount=all.Count(a=>TelegramHealth.AccountLabel(a).StartsWith("○"));
            summary.Text=$"Всего {all.Count}   ·   Доступны {ready}   ·   Требуют внимания {all.Count-ready-uncheckedCount}   ·   Проверить {uncheckedCount}";
        }
        void PaintHealth(DataGridViewRow row,TelegramAccount a){
            string label=TelegramHealth.AccountLabel(a);row.Cells["status"].Value=label;
            row.Cells["status"].Style.ForeColor=label.StartsWith("○")?Theme.TextMuted:a.status=="ready"&&!label.Contains("Пауза")?Theme.Success:new[]{"network","blocked","deactivated","unauthorized","configuration","error"}.Contains(a.status)?Theme.Error:Theme.Warning;
            row.Cells["proxy"].Value=string.IsNullOrWhiteSpace(a.proxy)?"○ Без прокси":TelegramHealth.ProxyLabel(a)+" · "+TelegramStore.ProxyLabel(a.proxy);row.Cells["proxy"].Style.ForeColor=string.IsNullOrWhiteSpace(a.proxy)?Theme.TextMuted:a.proxy_status=="ready"?Theme.Success:a.proxy_status=="failed"?Theme.Error:Theme.TextMuted;row.Cells["proxy"].ToolTipText=a.proxy_detail;
            row.Cells["checked"].Value=string.IsNullOrEmpty(a.checked_at)?"—":a.checked_at;
            row.Cells["detail"].Value=string.IsNullOrEmpty(a.last_error)?(a.status=="ready"?"Telegram подтвердил аккаунт":"—"):a.last_error;
            row.Cells["detail"].ToolTipText=a.last_error+(string.IsNullOrEmpty(a.last_code)?"":" · "+a.last_code);
        }
        void RefreshHealthRows(){var accounts=store.Load().accounts.ToDictionary(a=>a.id);foreach(DataGridViewRow row in grid.Rows){if(row.Tag is TelegramAccount old&&accounts.TryGetValue(old.id,out var a)){row.Tag=a;PaintHealth(row,a);}}}
        List<TelegramAccount> Selected() {
            grid.EndEdit();var list=grid.Rows.Cast<DataGridViewRow>().Where(r=>Convert.ToBoolean(r.Cells[0].Value)).Select(r=>(TelegramAccount)r.Tag).ToList();
            if(list.Count==0)list=grid.SelectedRows.Cast<DataGridViewRow>().Select(r=>(TelegramAccount)r.Tag).ToList();
            if(list.Count==0)throw new InvalidOperationException("Выберите аккаунт в списке");return list;
        }
        async void ChooseImport(){await Import(null);}
        async Task Import(string[] paths) {
            try {
                if(busy)return;
                string[] selected;string proxyId,passcode;bool check;
                using(var f=new TelegramImportDialog(store,paths)){
                    if(f.ShowDialog(this)!=DialogResult.OK)return;
                    selected=f.Paths;proxyId=f.ProxyId;passcode=f.Passcode;check=f.CheckAfter;
                }
                if(!File.Exists(Path.Combine(store.Root,"venv","Scripts","python.exe"))){await Prepare();if(!File.Exists(Path.Combine(store.Root,"venv","Scripts","python.exe")))return;}
                store.Change(d=>d.default_proxy_id=proxyId);var data=store.Load();var ids=new List<string>();
                await Run(new Dictionary<string,object>{{"action","import"},{"paths",selected},{"api_id",data.api_id},{"api_hash",data.api_hash},{"passcode",passcode}},proxyId,ids);
                RefreshProxies();
                if(check&&ids.Count>0)await Run(new Dictionary<string,object>{{"action","check"},{"accounts",store.Load().accounts.Where(a=>ids.Contains(a.id)).ToList()}});
            }catch(Exception e){Failure(e);}
        }
        async Task RunSelected(string action) {
            try {
                if(action=="warmup"){
                    using(var f=new TelegramFields("Прогрев сессий","Проверяет подключение и получает до 20 доступных диалогов. Не отправляет сообщения, не ставит реакции и не накручивает просмотры. Защиту от блокировок не гарантирует."))if(f.ShowDialog(this)!=DialogResult.OK)return;
                }
                await Run(new Dictionary<string,object>{{"action",action},{"accounts",Selected()}});
            }catch(Exception e){Failure(e);}
        }
        List<TelegramAccount> InviteSender(){if(!(senderChoice.SelectedItem is TelegramAccount a))throw new InvalidOperationException("Выберите отправителя");return new List<TelegramAccount>{store.Load().accounts.First(x=>x.id==a.id)};}
        void RefreshSenders(){if(senderChoice==null)return;string id=(senderChoice.SelectedItem as TelegramAccount)?.id??(grid.CurrentRow?.Tag as TelegramAccount)?.id;senderChoice.Items.Clear();foreach(var a in store.Load().accounts)senderChoice.Items.Add(a);var match=senderChoice.Items.Cast<TelegramAccount>().FirstOrDefault(a=>a.id==id);if(match!=null)senderChoice.SelectedItem=match;else if(senderChoice.Items.Count>0)senderChoice.SelectedIndex=0;}
        void LoadRecipients(){try{using(var picker=new OpenFileDialog{Filter="Список пользователей|*.txt;*.csv;*.json"})if(picker.ShowDialog(this)==DialogResult.OK){var list=TelegramRecipients.Read(picker.FileName);users.Text=list.Text;recipientHint.Text=$"Загружено {list.Users.Count} · повторов {list.Duplicates} · пропущено некорректных {list.Invalid}";}}catch(Exception e){Failure(e);}}
        async Task PreviewTarget(){try{if(busy)return;if(string.IsNullOrWhiteSpace(target.Text))throw new InvalidOperationException("Вставьте ссылку на группу/канал");await Run(new Dictionary<string,object>{{"action","invite_preview"},{"accounts",InviteSender()},{"target",target.Text.Trim()}});}catch(Exception e){targetHint.Text=e.Message;Failure(e);}}
        async Task RunInvites() {
            try {
                if(busy)return;var list=InviteSender();
                if(string.IsNullOrWhiteSpace(target.Text)||string.IsNullOrWhiteSpace(users.Text))throw new InvalidOperationException("Укажите группу/канал и список пользователей");
                var parsed=TelegramRecipients.Parse(users.Text);if(parsed.Invalid>0||parsed.Users.Count==0)throw new InvalidOperationException("В списке есть некорректные строки. Нужны @username, t.me/username или ID пользователя");
                string targetValue=target.Text.Trim(), userValue=parsed.Text;int seconds=(int)delay.Value,limit=(int)inviteLimit.Value;
                if(MessageBox.Show(this,$"Отправитель: {list[0].name}\nГруппа/канал: {targetValue}\nОбработать не более {Math.Min(limit,parsed.Users.Count)} пользователей. Запустить?","Telegram",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
                store.Change(d=>{d.target=targetValue;d.delay=seconds;d.invite_limit=limit;});
                await Run(new Dictionary<string,object>{{"action","invite"},{"accounts",list},{"target",targetValue},{"users",userValue},{"delay",seconds},{"limit",limit}});
            }catch(Exception e){Failure(e);}
        }
        void OpenWeb()=>OpenWeb(null);
        void OpenWeb(TelegramAccount current){try{if(busy)return;var a=current==null?Selected():new List<TelegramAccount>{current};if(a.Count!=1)throw new InvalidOperationException("Выберите один аккаунт");if(webWindows.TryGetValue(a[0].id,out var existing)&&!existing.IsDisposed){existing.Activate();return;}var window=new TelegramWebWindow(store,a[0],async token=>{if(busy)throw new InvalidOperationException("Дождитесь завершения Telegram-задачи");await Run(new Dictionary<string,object>{{"action","web_login"},{"accounts",new List<TelegramAccount>{a[0]}},{"login_token",token}});});webWindows[a[0].id]=window;window.Show(this);}catch(Exception e){Failure(e);}}
        async Task Run(Dictionary<string,object> job,string importProxyId=null,List<string> importedIds=null) {
            if(busy)return;busy=true;foreach(var b in workButtons)b.Enabled=false;stop.Enabled=true;grid.Enabled=false;search.Enabled=false;proxyGrid.Enabled=false;pages[2].Enabled=false;
            bool complete=false,failed=false;string finalSummary="";string task=Guid.NewGuid().ToString("N");status.Text="Выполняется…";
            TaskQueueStore.Upsert(task,t=>{t.Platform="Telegram";t.UploadMethod="MTProto";t.Account=job.ContainsKey("accounts")?string.Join(", ",((List<TelegramAccount>)job["accounts"]).Select(a=>a.name)):"Импорт аккаунтов";t.Stage=Convert.ToString(job["action"]);t.Status=TaskQueueStatus.Preparing;},true);
            try {
                int code=await runner.Run(job,ev=>{
                    string type=Get(ev,"event");
                    if(type=="imported") {
                        var account=new JavaScriptSerializer().ConvertToType<TelegramAccount>(ev["account"]);
                        store.Change(d=>{if(!d.accounts.Any(a=>a.id==account.id)){d.accounts.Add(account);if(importProxyId!=null)TelegramProxyCatalog.Assign(d,new[]{account.id},importProxyId);}});
                        importedIds?.Add(account.id);
                        Log(task,account.id,"import","Аккаунт импортирован");return;
                    }
                    if(type=="account"){store.Change(d=>TelegramHealth.ApplyAccount(d,ev));RefreshHealthRows();if(Get(ev,"status")!="ready")failed=true;}
                    if(type=="proxy"){store.Change(d=>TelegramHealth.ApplyProxy(d,ev));RefreshHealthRows();RefreshProxies();if(Get(ev,"status")=="failed")failed=true;}
                    if(type=="target")targetHint.Text=Get(ev,"text");
                    if(type=="stage"){foreach(DataGridViewRow row in grid.Rows)if((row.Tag as TelegramAccount)?.id==Get(ev,"id")){row.Cells["status"].Value=Get(ev,"text");row.Cells["status"].Style.ForeColor=Theme.Warning;}}
                    if(type=="summary"){finalSummary=Get(ev,"text");if(ev.TryGetValue("failures",out var failures)&&Convert.ToInt32(failures)>0)failed=true;}
                    if(type=="error"||type=="import_error")failed=true;
                    if(type=="invite"&&new[]{"flood_wait","restricted","blocked","unauthorized","permissions","network"}.Contains(Get(ev,"state")))store.Change(d=>{
                        var id=((List<TelegramAccount>)job["accounts"])[0].id;var a=d.accounts.First(x=>x.id==id);a.status=Get(ev,"state");int wait=ev.ContainsKey("seconds")?Convert.ToInt32(ev["seconds"]):0;if(wait>0)a.wait_until=DateTime.UtcNow.AddSeconds(wait).ToString("o");
                    });
                    if(type=="complete"){complete=true;return;}
                    string text=Get(ev,"text");if(ev.ContainsKey("username")&&type=="invite")text="@"+Get(ev,"username")+": "+text;
                    if(ev.ContainsKey("seconds")&&Convert.ToInt32(ev["seconds"])>0)text+=" · "+ev["seconds"]+" сек";
                    if(ev.ContainsKey("code"))text+=" · "+Get(ev,"code");if(ev.ContainsKey("source"))text+=" · "+Get(ev,"source");
                    if(text!=""){status.Text=text;Log(task,Get(ev,"id"),type,text);TaskQueueStore.Upsert(task,t=>{t.Stage=type;t.Result=text;});}
                });
                if(code==0&&!complete)throw new IOException("Worker завершился без подтверждения результата");
                if(code!=0&&code!=2)throw new IOException("Telegram: "+status.Text);
                if(code==0&&!string.IsNullOrEmpty(finalSummary))status.Text=finalSummary;if(code==2)status.Text="Остановлено";
                TaskQueueStore.Upsert(task,t=>{t.Status=code==2?TaskQueueStatus.Stopped:failed?TaskQueueStatus.Error:TaskQueueStatus.Done;t.Progress=code==0?100:0;t.Result=status.Text;},true);
            }catch{TaskQueueStore.Upsert(task,t=>{t.Status=TaskQueueStatus.Error;t.Result="Проверьте журнал Telegram";},true);throw;}finally{busy=false;foreach(var b in workButtons)b.Enabled=true;stop.Enabled=false;grid.Enabled=true;search.Enabled=true;proxyGrid.Enabled=true;pages[2].Enabled=true;RefreshData();RefreshProxies();}
        }
        static string Get(Dictionary<string,object> ev,string key)=>ev.TryGetValue(key,out var value)?Convert.ToString(value):"";
        void Log(string task,string account,string stage,string text){if(!string.IsNullOrEmpty(account)){var a=store.Load().accounts.FirstOrDefault(x=>x.id==account);if(a!=null)text=a.name+": "+text;}string line=DateTime.Now.ToString("HH:mm:ss")+" · "+text;log.AppendText(line+Environment.NewLine);log.ScrollToCaret();AppDiagnostics.Write("Telegram",account,task,stage,text,stage=="error"||stage=="import_error"?"error":"info");}
        void Failure(Exception e){status.Text=e.Message;AppDiagnostics.Write("Telegram","","","error",e.Message,"error");MessageBox.Show(this,e.Message,"Telegram",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        void EditAccount(TelegramAccount account) {
            if(account==null||busy)return;
            try{using(var f=new TelegramFields("Карточка Telegram","Сессия хранится зашифрованной. Настройки ниже относятся только к этому аккаунту.")){
                var name=f.Add("Название",account.name);var proxy=f.Add("Прокси · socks5://логин:пароль@host:port",account.proxy,true);
                var country=f.Add("Страна",account.country);var note=f.Add("Заметка",account.note);
                f.AddInfo(string.IsNullOrEmpty(account.username)?"Username не получен":"@"+account.username);
                f.AddInfo(string.IsNullOrEmpty(account.phone)?"Телефон не получен":"Телефон: "+account.phone);
                f.AddInfo("Последняя успешная проверка: "+(string.IsNullOrEmpty(account.last_success_utc)?"нет подтверждения":account.last_success_utc));f.AddInfo("Причина: "+account.last_error);f.AddInfo("Прокси: "+account.proxy_detail);
                f.AddInfo("Состояние: "+TelegramStore.Status(account.status)+(string.IsNullOrEmpty(account.wait_until)?"":" · до "+account.wait_until));
                var proxies=new List<Tuple<string,string>>();
                try{foreach(var a in new AccountLibraryStore().Load().Where(a=>!string.IsNullOrWhiteSpace(a.Proxy)))proxies.Add(Tuple.Create(a.Name,a.Proxy));}catch{}
                if(proxies.Count>0){var choices=Theme.MakeCombo(new[]{"Взять сохранённый прокси…"}.Concat(proxies.Select(p=>p.Item1+" · "+TelegramStore.ProxyLabel(p.Item2))).ToArray());choices.Width=410;choices.SelectedIndexChanged+=(s,e)=>{if(choices.SelectedIndex>0)proxy.Text=proxies[choices.SelectedIndex-1].Item2;};f.AddControl(choices);}
                if(f.ShowDialog(this)!=DialogResult.OK)return;
                store.Change(d=>{var a=d.accounts.First(x=>x.id==account.id);a.name=name.Text.Trim();string url=string.IsNullOrWhiteSpace(proxy.Text)?"":TelegramProxyCatalog.Normalize(proxy.Text);if(a.proxy!=url){a.proxy=url;a.proxy_id="";TelegramHealth.RouteChanged(a);}a.country=country.Text.Trim();a.note=note.Text.Trim();});RefreshData();RefreshProxies();
            }}catch(Exception e){Failure(e);}
        }
        void RemoveSelected() {
            if(busy)return;try{var current=grid.CurrentRow?.Tag as TelegramAccount;if(current==null)return;var list=new[]{current};if(MessageBox.Show(this,"Удалить этот аккаунт только из Telegram-раздела? Сессии в Telegram не завершаются, исходники сохранятся.","Telegram",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
                var ids=new HashSet<string>(list.Select(a=>a.id));store.Change(d=>d.accounts.RemoveAll(a=>ids.Contains(a.id)));RefreshData();}catch(Exception e){Failure(e);}
        }
        void Settings() {
            try{var data=store.Load();using(var f=new TelegramFields("Настройки Telegram","Выберите папку аккаунта, JSON, .session или ZIP. Приложение само найдёт TData. Прокси добавляются на отдельной вкладке и назначаются до проверки.")){
                f.AddControl(Theme.MakeButton("Подготовить Telegram",action:async()=>await Prepare()));
                f.AddInfo("Нужен Python 3.11 x64. Подготовка устанавливает локальные компоненты один раз.");
                var advanced=new Panel{Width=450,Height=0,Visible=false};
                var id=Theme.MakeSearchBox();id.Text=data.api_id==0?"":data.api_id.ToString();id.Dock=DockStyle.Top;
                var hash=Theme.MakeSearchBox();hash.Text=data.api_hash;hash.UseSystemPasswordChar=true;hash.Dock=DockStyle.Bottom;
                advanced.Controls.Add(id);advanced.Controls.Add(hash);
                f.AddControl(Theme.MakeButton("Дополнительно · свой API",ghost:true,action:()=>{advanced.Visible=!advanced.Visible;advanced.Height=advanced.Visible?82:0;}));
                f.AddInfo("Обычно заполнять API вручную не нужно: берём пару из JSON, иначе используем профиль совместимости Telegram Desktop. Это не определение исходного устройства.");
                f.AddControl(advanced);
                if(f.ShowDialog(this)!=DialogResult.OK)return;
                int api=0;if(id.Text.Trim()!=""&&(!int.TryParse(id.Text,out api)||api<=0))throw new InvalidOperationException("API ID должен быть положительным числом");
                string secret=hash.Text.Trim();if((api>0&&!Regex.IsMatch(secret,"^[a-fA-F0-9]{32}$"))||(api==0&&secret!=""))throw new InvalidOperationException("Укажите пару API ID / API Hash или оставьте оба поля пустыми");
                store.Change(d=>{d.api_id=api;d.api_hash=secret;});
            }}catch(Exception e){Failure(e);}
        }
        async Task Prepare() {
            if(busy)return;
            busy=true;try{store.Prepare();status.Text="Подготовка Telegram…";
                var script=Path.Combine(TelegramRunner.Scripts(),"setup.ps1");
                var info=new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File "+Core.Quote(script)+" -Root "+Core.Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};
                using(var process=Process.Start(info)){preparation=process;var output=await process.StandardOutput.ReadToEndAsync();var error=await process.StandardError.ReadToEndAsync();await Task.Run(()=>process.WaitForExit());if(process.ExitCode!=0){string detail=(error+output).Trim();if(detail.Length>400)detail=detail.Substring(0,400)+"…";throw new IOException(string.IsNullOrWhiteSpace(detail)?"Подготовка не завершена. Установите Python 3.11 или 3.12 (64-bit) с python.org и нажмите «Подготовить Telegram» снова.":detail);}}
                status.Text="Telegram подготовлен";
            }catch(Exception e){Failure(e);}finally{preparation=null;busy=false;}
        }
        public async Task StopBeforeClose(){foreach(var web in webWindows.Values.ToArray())await web.Shutdown();proxyCancel.Cancel();try{if(preparation!=null&&!preparation.HasExited){using(var kill=Process.Start(new ProcessStartInfo("taskkill.exe","/PID "+preparation.Id+" /T /F"){UseShellExecute=false,CreateNoWindow=true}))await Task.Run(()=>kill.WaitForExit());}}catch{}await runner.Stop();while(busy)await Task.Delay(50);}
    }
    internal class TelegramFields : Form {
        readonly FlowLayoutPanel body;
        public TelegramFields(string title,string hint){Text=title;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;Width=560;Height=580;MinimumSize=new Size(480,380);StartPosition=FormStartPosition.CenterParent;
            body=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(20)};Controls.Add(body);AddInfo(hint);
            var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=56,Padding=new Padding(20,8,0,0)};var save=Theme.MakeButton("Готово",accent:true);save.DialogResult=DialogResult.OK;var cancel=Theme.MakeButton("Отмена",ghost:true);cancel.DialogResult=DialogResult.Cancel;bottom.Controls.Add(save);bottom.Controls.Add(cancel);Controls.Add(bottom);AcceptButton=save;CancelButton=cancel;
        }
        public TextBox Add(string label,string value,bool secret=false){AddInfo(label);var box=Theme.MakeSearchBox();box.Width=450;box.Text=value??"";box.UseSystemPasswordChar=secret;box.Margin=new Padding(0,0,0,12);body.Controls.Add(box);return box;}
        public void AddInfo(string text){body.Controls.Add(new Label{Text=text,AutoSize=true,MaximumSize=new Size(450,0),ForeColor=Theme.TextSecondary,Margin=new Padding(0,0,0,8)});}
        public void AddControl(Control control){body.Controls.Add(control);}
    }
}
