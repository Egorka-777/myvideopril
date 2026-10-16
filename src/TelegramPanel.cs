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
    public sealed class TelegramPanel : UserControl {
        readonly TelegramStore store;
        readonly TelegramRunner runner;
        readonly DataGridView grid;
        readonly RichTextBox log;
        readonly Label summary, status;
        readonly TextBox search, users, target;
        readonly NumericUpDown delay;
        readonly Panel pageHost;
        readonly FlowLayoutPanel tabStrip;
        readonly List<Panel> pages=new List<Panel>();
        readonly List<Button> pageButtons=new List<Button>();
        readonly FlowLayoutPanel actions;
        readonly Button stop;
        readonly List<Button> workButtons=new List<Button>();
        bool busy, unavailable;
        Process preparation;
        public bool HasWork => busy;
        public TelegramPanel(TelegramStore storage=null) {
            store=storage??new TelegramStore();runner=new TelegramRunner(store);
            Dock=DockStyle.Fill;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));Controls.Add(layout);
            actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};layout.Controls.Add(actions,0,0);
            WorkButton("＋ Аккаунты",ChooseImport,true);
            WorkButton("Проверить",async()=>await RunSelected("check"));
            WorkButton("Прогрев",async()=>await RunSelected("warmup"));
            WorkButton("Настройки",Settings);
            actions.Controls.Add(new Label{Text="Поиск",AutoSize=true,ForeColor=Theme.TextMuted,Margin=new Padding(6,9,4,0)});
            search=Theme.MakeSearchBox();search.Width=170;search.Margin=new Padding(4,7,0,0);search.TextChanged+=(s,e)=>RefreshData();actions.Controls.Add(search);
            summary=new Label{Dock=DockStyle.Fill,ForeColor=Theme.TextSecondary,TextAlign=ContentAlignment.MiddleLeft};layout.Controls.Add(summary,0,1);
            tabStrip=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty,BackColor=Theme.Background};layout.Controls.Add(tabStrip,0,2);
            pageHost=new Panel{Dock=DockStyle.Fill,Margin=Padding.Empty,BackColor=Theme.Background};layout.Controls.Add(pageHost,0,3);
            var accountsPage=Page("Аккаунты");var invitePage=Page("Приглашения");var logsPage=Page("Журнал");
            grid=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,ReadOnly=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=true,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};Theme.StyleGrid(grid);
            grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;
            grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="selected",HeaderText="",AutoSizeMode=DataGridViewAutoSizeColumnMode.None,Width=40,MinimumWidth=40,DefaultCellStyle=new DataGridViewCellStyle{Padding=Padding.Empty}});
            foreach(var spec in new[]{new[]{"name","Аккаунт"},new[]{"username","Username"},new[]{"status","Состояние"},new[]{"proxy","Прокси"},new[]{"country","Страна"},new[]{"checked","Проверен"}}){var i=grid.Columns.Add(spec[0],spec[1]);grid.Columns[i].ReadOnly=true;}
            grid.CellDoubleClick+=(s,e)=>{if(e.RowIndex>=0)EditAccount(grid.Rows[e.RowIndex].Tag as TelegramAccount);};
            grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
            var context=new ContextMenuStrip{BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary};context.Items.Add("Карточка аккаунта",null,(s,e)=>EditAccount(grid.CurrentRow?.Tag as TelegramAccount));
            context.Items.Add("Удалить из Telegram-раздела",null,(s,e)=>RemoveSelected());grid.ContextMenuStrip=context;
            grid.CellMouseDown+=(s,e)=>{if(e.Button==MouseButtons.Right&&e.RowIndex>=0){grid.ClearSelection();grid.Rows[e.RowIndex].Selected=true;grid.CurrentCell=grid.Rows[e.RowIndex].Cells[1];}};
            var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};table.RowStyles.Add(new RowStyle(SizeType.Percent,100));table.RowStyles.Add(new RowStyle(SizeType.Absolute,42));table.Controls.Add(grid,0,0);
            var foot=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,6,0,0)};
            foot.Controls.Add(Theme.MakeButton("Выбрать все",ghost:true,action:()=>{foreach(DataGridViewRow row in grid.Rows)row.Cells[0].Value=true;}));
            foot.Controls.Add(Theme.MakeButton("Карточка",ghost:true,action:()=>EditAccount(grid.CurrentRow?.Tag as TelegramAccount)));
            foot.Controls.Add(new Label{Text="Двойной щелчок — прокси, страна и заметка",AutoSize=true,ForeColor=Theme.TextMuted,Margin=new Padding(6,9,0,0)});table.Controls.Add(foot,0,1);accountsPage.Controls.Add(table);
            var form=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(16)};
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(var h in new[]{40,42,34})form.RowStyles.Add(new RowStyle(SizeType.Absolute,h));form.RowStyles.Add(new RowStyle(SizeType.Percent,100));form.RowStyles.Add(new RowStyle(SizeType.Absolute,52));form.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
            form.Controls.Add(new Label{Text="Приглашения от одного выбранного аккаунта",AutoSize=true,Font=Theme.FontCardTitle},0,0);
            target=Theme.MakeSearchBox();target.Dock=DockStyle.Fill;form.Controls.Add(target,0,1);
            form.Controls.Add(new Label{Text="Группа/канал: @username, t.me/username или ID. Пользователи — ниже, по одному @username в строке.",Dock=DockStyle.Fill,ForeColor=Theme.TextSecondary},0,2);
            users=Theme.MakeSearchBox();users.Multiline=true;users.ScrollBars=ScrollBars.Vertical;users.Dock=DockStyle.Fill;form.Controls.Add(users,0,3);
            var options=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,8,0,0)};
            options.Controls.Add(new Label{Text="Пауза между приглашениями",AutoSize=true,Margin=new Padding(0,7,8,0)});
            delay=new NumericUpDown{Minimum=10,Maximum=3600,Value=60,Width=82,BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary};options.Controls.Add(delay);
            options.Controls.Add(new Label{Text="сек",AutoSize=true,Margin=new Padding(5,7,10,0)});
            var start=Theme.MakeButton("Запустить",accent:true,action:async()=>await RunInvites());workButtons.Add(start);options.Controls.Add(start);form.Controls.Add(options,0,4);
            form.Controls.Add(new Label{Text="Нужны права приглашения в вашу группу. История защищает от повторов. При ограничении Telegram задача останавливается; незавершённую попытку проверьте вручную.",Dock=DockStyle.Fill,ForeColor=Theme.TextMuted},0,5);invitePage.Controls.Add(form);
            log=Theme.MakeLogBox();logsPage.Controls.Add(log);
            var bottom=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,6,0,0)};
            stop=Theme.MakeButton("Остановить",ghost:true,action:async()=>await runner.Stop());stop.Enabled=false;bottom.Controls.Add(stop);
            bottom.Controls.Add(Theme.MakeButton("Открыть логи",ghost:true,action:()=>{Directory.CreateDirectory(AppDiagnostics.Folder);Process.Start(new ProcessStartInfo(AppDiagnostics.Folder){UseShellExecute=true});}));
            status=new Label{Text="Добавьте аккаунты, затем проверьте соединение",Width=560,Height=32,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,ForeColor=Theme.TextSecondary};bottom.Controls.Add(status);layout.Controls.Add(bottom,0,4);
            SelectTab(0);
            AllowDrop=true;DragEnter+=(s,e)=>e.Effect=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;
            DragDrop+=async(s,e)=>{if(!busy&&e.Data.GetDataPresent(DataFormats.FileDrop))await Import((string[])e.Data.GetData(DataFormats.FileDrop));};
            try{var data=store.Load();target.Text=data.target;delay.Value=Math.Max(10,Math.Min(3600,data.delay));RefreshData();}catch(Exception e){status.Text=e.Message;actions.Enabled=false;unavailable=true;}
        }
        Panel Page(string title){
            int index=pages.Count;var page=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Background,ForeColor=Theme.TextPrimary,Visible=false};pages.Add(page);pageHost.Controls.Add(page);
            var button=Theme.MakeButton(title,action:()=>SelectTab(index));button.MinimumSize=new Size(100,34);pageButtons.Add(button);tabStrip.Controls.Add(button);return page;
        }
        internal void SelectTab(int index){
            if(index<0||index>=pages.Count)throw new ArgumentOutOfRangeException(nameof(index));
            for(int i=0;i<pages.Count;i++){pages[i].Visible=i==index;pageButtons[i].BackColor=i==index?Theme.Selected:Theme.Background;pageButtons[i].ForeColor=i==index?Theme.TextPrimary:Theme.TextSecondary;}
            pages[index].BringToFront();
        }
        void WorkButton(string text,Action action,bool accent=false){var b=Theme.MakeButton(text,accent:accent,action:()=>{try{action();}catch(Exception e){Failure(e);}});actions.Controls.Add(b);workButtons.Add(b);}
        public void RefreshData() {
            if(grid==null||busy||unavailable)return;
            var selected=new HashSet<string>(grid.Rows.Cast<DataGridViewRow>().Where(r=>Convert.ToBoolean(r.Cells[0].Value)).Select(r=>((TelegramAccount)r.Tag).id));
            string current=(grid.CurrentRow?.Tag as TelegramAccount)?.id;
            TelegramData data;try{data=store.Load();}catch(Exception e){unavailable=true;actions.Enabled=false;status.Text=e.Message;return;}
            var all=data.accounts;string q=search.Text.Trim();grid.Rows.Clear();
            foreach(var a in all.Where(a=>string.Join(" ",a.name,a.username,a.phone,a.country,a.note).IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0)){
                int i=grid.Rows.Add(selected.Contains(a.id),a.name,string.IsNullOrEmpty(a.username)?"—":"@"+a.username,TelegramStore.Status(a.status),TelegramStore.ProxyLabel(a.proxy),a.country,string.IsNullOrEmpty(a.checked_at)?"—":a.checked_at);grid.Rows[i].Tag=a;
                grid.Rows[i].Cells["status"].Style.ForeColor=a.status=="ready"?Theme.Success:a.status=="unchecked"?Theme.TextMuted:Theme.Warning;
                if(a.id==current)grid.CurrentCell=grid.Rows[i].Cells[1];
            }
            summary.Text=$"Всего {all.Count}   ·   Работают {all.Count(a=>a.status=="ready")}   ·   Требуют внимания {all.Count(a=>a.status!="ready"&&a.status!="unchecked")}   ·   Не проверены {all.Count(a=>a.status=="unchecked")}";
        }
        List<TelegramAccount> Selected() {
            grid.EndEdit();var list=grid.Rows.Cast<DataGridViewRow>().Where(r=>Convert.ToBoolean(r.Cells[0].Value)).Select(r=>(TelegramAccount)r.Tag).ToList();
            if(list.Count==0)list=grid.SelectedRows.Cast<DataGridViewRow>().Select(r=>(TelegramAccount)r.Tag).ToList();
            if(list.Count==0)throw new InvalidOperationException("Выберите аккаунт в списке");return list;
        }
        void ChooseImport() {
            var menu=new ContextMenuStrip{BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary};menu.Items.Add(".session + JSON / ZIP",null,async(s,e)=>{using(var picker=new OpenFileDialog{Filter="Telegram|*.session;*.json;*.zip",Multiselect=true})if(picker.ShowDialog(this)==DialogResult.OK)await Import(picker.FileNames);});
            menu.Items.Add("Папка TData / аккаунтов",null,async(s,e)=>{using(var picker=new FolderBrowserDialog{Description="Выберите TData или папку с аккаунтами"})if(picker.ShowDialog(this)==DialogResult.OK)await Import(new[]{picker.SelectedPath});});
            menu.Show(actions, new Point(0,actions.Height));
        }
        async Task Import(string[] paths) {
            try {
                // JSON selection selects the paired SQLite source, not another overlay/account.
                paths=paths.Select(p=>Path.GetExtension(p).Equals(".json",StringComparison.OrdinalIgnoreCase)?Path.ChangeExtension(p,".session"):p).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var data=store.Load();string passcode="";
                using(var f=new TelegramFields("Импорт Telegram","Исходники останутся без изменений. Для защищённой TData укажите локальный код-пароль; обычно поле пустое.")){
                    var input=f.Add("Код-пароль TData", "",true);if(f.ShowDialog(this)!=DialogResult.OK)return;passcode=input.Text;
                }
                await Run(new Dictionary<string,object>{{"action","import"},{"paths",paths},{"api_id",data.api_id},{"api_hash",data.api_hash},{"passcode",passcode}});
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
        async Task RunInvites() {
            try {
                var list=Selected();if(list.Count!=1)throw new InvalidOperationException("Для этой задачи выберите ровно один аккаунт во вкладке «Аккаунты»");
                if(string.IsNullOrWhiteSpace(target.Text)||string.IsNullOrWhiteSpace(users.Text))throw new InvalidOperationException("Укажите группу/канал и список пользователей");
                string targetValue=target.Text.Trim(), userValue=users.Text;int seconds=(int)delay.Value;
                if(MessageBox.Show(this,$"Аккаунт: {list[0].name}\nГруппа/канал: {targetValue}\nНачать приглашения по указанному списку?","Telegram",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
                store.Change(d=>{d.target=targetValue;d.delay=seconds;});
                await Run(new Dictionary<string,object>{{"action","invite"},{"accounts",list},{"target",targetValue},{"users",userValue},{"delay",seconds}});
            }catch(Exception e){Failure(e);}
        }
        async Task Run(Dictionary<string,object> job) {
            if(busy)return;busy=true;foreach(var b in workButtons)b.Enabled=false;stop.Enabled=true;grid.Enabled=false;search.Enabled=false;
            bool complete=false;string task=Guid.NewGuid().ToString("N");status.Text="Выполняется…";
            TaskQueueStore.Upsert(task,t=>{t.Platform="Telegram";t.UploadMethod="MTProto";t.Account=job.ContainsKey("accounts")?string.Join(", ",((List<TelegramAccount>)job["accounts"]).Select(a=>a.name)):"Импорт аккаунтов";t.Stage=Convert.ToString(job["action"]);t.Status=TaskQueueStatus.Preparing;},true);
            try {
                int code=await runner.Run(job,ev=>{
                    string type=Get(ev,"event");
                    if(type=="imported") {
                        var account=new JavaScriptSerializer().ConvertToType<TelegramAccount>(ev["account"]);
                        store.Change(d=>{if(!d.accounts.Any(a=>a.id==account.id))d.accounts.Add(account);});
                        Log(task,account.id,"import","Аккаунт импортирован");return;
                    }
                    if(type=="account")store.Change(d=>{
                        var a=d.accounts.FirstOrDefault(x=>x.id==Get(ev,"id"));if(a==null)return;
                        a.status=Get(ev,"status");a.checked_at=DateTime.Now.ToString("dd.MM HH:mm");
                        if(ev.ContainsKey("name"))a.name=Get(ev,"name");if(ev.ContainsKey("phone"))a.phone=Get(ev,"phone");if(ev.ContainsKey("username"))a.username=Get(ev,"username");if(ev.ContainsKey("user_id"))a.user_id=Convert.ToInt64(ev["user_id"]);
                        int wait=ev.ContainsKey("seconds")?Convert.ToInt32(ev["seconds"]):0;if(wait>0)a.wait_until=DateTime.UtcNow.AddSeconds(wait).ToString("o");
                    });
                    if(type=="invite"&&new[]{"flood_wait","restricted","blocked","unauthorized","permissions","network"}.Contains(Get(ev,"state")))store.Change(d=>{
                        var id=((List<TelegramAccount>)job["accounts"])[0].id;var a=d.accounts.First(x=>x.id==id);a.status=Get(ev,"state");int wait=ev.ContainsKey("seconds")?Convert.ToInt32(ev["seconds"]):0;if(wait>0)a.wait_until=DateTime.UtcNow.AddSeconds(wait).ToString("o");
                    });
                    if(type=="complete")complete=true;
                    string text=Get(ev,"text");if(ev.ContainsKey("username")&&type=="invite")text="@"+Get(ev,"username")+": "+text;
                    if(ev.ContainsKey("seconds")&&Convert.ToInt32(ev["seconds"])>0)text+=" · "+ev["seconds"]+" сек";
                    if(ev.ContainsKey("code"))text+=" · "+Get(ev,"code");if(ev.ContainsKey("source"))text+=" · "+Get(ev,"source");
                    if(text!=""){status.Text=text;Log(task,Get(ev,"id"),type,text);TaskQueueStore.Upsert(task,t=>{t.Stage=type;t.Result=text;});}
                });
                if(code==0&&!complete)throw new IOException("Worker завершился без подтверждения результата");
                if(code!=0&&code!=2)throw new IOException("Telegram-задача не завершена. Подробности — в журнале.");
                TaskQueueStore.Upsert(task,t=>{t.Status=code==2?TaskQueueStatus.Stopped:TaskQueueStatus.Done;t.Progress=code==0?100:0;t.Result=status.Text;},true);
            }catch{TaskQueueStore.Upsert(task,t=>{t.Status=TaskQueueStatus.Error;t.Result="Проверьте журнал Telegram";},true);throw;}finally{busy=false;foreach(var b in workButtons)b.Enabled=true;stop.Enabled=false;grid.Enabled=true;search.Enabled=true;RefreshData();}
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
                f.AddInfo("Состояние: "+TelegramStore.Status(account.status)+(string.IsNullOrEmpty(account.wait_until)?"":" · до "+account.wait_until));
                var proxies=new List<Tuple<string,string>>();
                try{foreach(var a in new AccountLibraryStore().Load().Where(a=>!string.IsNullOrWhiteSpace(a.Proxy)))proxies.Add(Tuple.Create(a.Name,a.Proxy));}catch{}
                if(proxies.Count>0){var choices=Theme.MakeCombo(new[]{"Взять сохранённый прокси…"}.Concat(proxies.Select(p=>p.Item1+" · "+TelegramStore.ProxyLabel(p.Item2))).ToArray());choices.Width=410;choices.SelectedIndexChanged+=(s,e)=>{if(choices.SelectedIndex>0)proxy.Text=proxies[choices.SelectedIndex-1].Item2;};f.AddControl(choices);}
                if(f.ShowDialog(this)!=DialogResult.OK)return;
                store.Change(d=>{var a=d.accounts.First(x=>x.id==account.id);a.name=name.Text.Trim();a.proxy=proxy.Text.Trim();a.country=country.Text.Trim();a.note=note.Text.Trim();a.status="unchecked";});RefreshData();
            }}catch(Exception e){Failure(e);}
        }
        void RemoveSelected() {
            if(busy)return;try{var current=grid.CurrentRow?.Tag as TelegramAccount;if(current==null)return;var list=new[]{current};if(MessageBox.Show(this,"Удалить этот аккаунт только из Telegram-раздела? Сессии в Telegram не завершаются, исходники сохранятся.","Telegram",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
                var ids=new HashSet<string>(list.Select(a=>a.id));store.Change(d=>d.accounts.RemoveAll(a=>ids.Contains(a.id)));RefreshData();}catch(Exception e){Failure(e);}
        }
        void Settings() {
            try{var data=store.Load();using(var f=new TelegramFields("Настройки Telegram","API ID / API Hash нужны для .session без парного JSON. Получите их для своего приложения на my.telegram.org. Значения из JSON сохраняются отдельно для каждого аккаунта.")){
                var id=f.Add("API ID",data.api_id==0?"":data.api_id.ToString());var hash=f.Add("API Hash",data.api_hash,true);
                f.AddControl(Theme.MakeButton("Подготовить Telegram",action:async()=>await Prepare()));
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
                using(var process=Process.Start(info)){preparation=process;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await Task.Run(()=>process.WaitForExit());await output;await error;if(process.ExitCode!=0)throw new IOException("Подготовка не завершена. Агенту: запустите tools/telegram/setup.ps1 в PowerShell и проверьте установку Python 3.11 x64.");}
                status.Text="Telegram подготовлен";
            }catch(Exception e){Failure(e);}finally{preparation=null;busy=false;}
        }
        public async Task StopBeforeClose(){try{if(preparation!=null&&!preparation.HasExited){using(var kill=Process.Start(new ProcessStartInfo("taskkill.exe","/PID "+preparation.Id+" /T /F"){UseShellExecute=false,CreateNoWindow=true}))await Task.Run(()=>kill.WaitForExit());}}catch{}await runner.Stop();while(busy)await Task.Delay(50);}
    }
    internal sealed class TelegramFields : Form {
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
