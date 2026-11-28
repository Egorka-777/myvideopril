using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Threading;
using System.Text.RegularExpressions;

namespace VideoBatch {
    // Observations only: cookies remain in the existing isolated WebView2 user-data folder.
    internal sealed class MetaProfileObservation {
        public string Id {get;set;}="";
        public string Name {get;set;}="";
        public bool? InstagramCookie {get;set;}
        public DateTime CheckedUtc {get;set;}
        public DateTime? CookieExpiresUtc {get;set;}
    }
    internal static class MetaProfileState {
        internal static string PathName => Path.Combine(Store.Root,"instagram","profiles.json");
        internal static List<MetaProfileObservation> Load(string path=null) {
            path=path??PathName;
            if(!File.Exists(path))return new List<MetaProfileObservation>();
            return new JavaScriptSerializer().Deserialize<List<MetaProfileObservation>>(File.ReadAllText(path))
                ??throw new InvalidDataException("Не удалось прочитать состояния Instagram.");
        }
        internal static void Save(MetaProfileObservation observation,string path=null) {
            path=path??PathName;
            var rows=Load(path);rows.RemoveAll(p=>p.Id==observation.Id);rows.Add(observation);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp=path+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(rows));
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        internal static string InstagramStatus(MetaProfileObservation p,DateTime now) {
            if(p?.InstagramCookie==null)return "Вход неизвестен";
            string time=" · "+p.CheckedUtc.ToLocalTime().ToString("dd.MM HH:mm");
            if(p.InstagramCookie==false)return "Cookie входа не найдена"+time;
            if(p.CookieExpiresUtc.HasValue&&p.CookieExpiresUtc.Value<=now)return "Срок cookie истёк; войдите вручную"+time;
            return "Cookie сессии обнаружена"+time+"; вход онлайн не проверен";
        }
        internal static List<DolphinBrowserProfile> LocalProfiles(Preferences settings,IEnumerable<MetaProfileObservation> observations) {
            return ProfileCatalog.Build(settings).Where(p=>!string.IsNullOrWhiteSpace(p.ProfileId))
                .Select(p=>new DolphinBrowserProfile{Id=p.ProfileId,Name=p.DisplayName})
                .Concat(observations.Select(p=>new DolphinBrowserProfile{Id=p.Id,Name=p.Name}))
                .GroupBy(p=>p.Id).Select(g=>g.First()).ToList();
        }
        internal static DolphinBrowserProfile Choose(Control owner,IEnumerable<DolphinBrowserProfile> profiles) {
            var choices=profiles.OrderBy(p=>p.Name).ToList();
            if(choices.Count==0){MessageBox.Show(owner,"Нет профилей. Нажмите «Обновить из Dolphin».","Добавить аккаунт");return null;}
            using(var dialog=new Form{Text="Добавить аккаунт · Dolphin",Name="MetaDolphinPicker",ClientSize=new Size(620,170),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false}) {
                Theme.StyleModal(dialog);
                var text=new Label{Text="Выберите профиль → войдите вручную в мобильном окне.\nИспользуется только назначенный профилю прокси; сессия изолирована по Dolphin ID.",Dock=DockStyle.Top,Height=54,ForeColor=Theme.TextSecondary};
                var combo=Theme.MakeCombo(choices.Select(p=>p.Name+" · Dolphin "+p.Id).ToArray());combo.Name="MetaDolphinProfile";combo.Dock=DockStyle.Top;combo.SelectedIndex=0;
                var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=50,FlowDirection=FlowDirection.RightToLeft};
                var ok=Theme.MakeButton("Открыть вход",accent:true);ok.DialogResult=DialogResult.OK;
                var cancel=Theme.MakeButton("Отмена",ghost:true);cancel.DialogResult=DialogResult.Cancel;
                buttons.Controls.Add(ok);buttons.Controls.Add(cancel);dialog.Controls.Add(combo);dialog.Controls.Add(text);dialog.Controls.Add(buttons);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                return dialog.ShowDialog(owner)==DialogResult.OK?choices[combo.SelectedIndex]:null;
            }
        }
    }

    internal sealed class DashboardCheckResult {
        internal bool? Authorized;
        internal string Detail="",AccountName="";
        internal bool IsError;
        internal string Evidence="online",Source="online";
        internal DateTime? ExpiresUtc;
    }
    internal sealed class DashboardSessionObservation {
        public string ProfileId {get;set;}="";
        public string Service {get;set;}="";
        public string Result {get;set;}="unknown";
        public string Detail {get;set;}="";
        public string AccountName {get;set;}="";
        public DateTime CheckedUtc {get;set;}
        public string Evidence {get;set;}="saved";
        public string Source {get;set;}="legacy";
        public DateTime? ExpiresUtc {get;set;}
        public string LastAttemptDetail {get;set;}="";
        public DateTime? LastAttemptUtc {get;set;}
    }
    internal static class DashboardSessionState {
        static readonly object sync = new object();
        internal static event Action Changed;
        internal static void Bind(Control control, Action refresh) {
            Action changed = () => {
                if(control.IsDisposed || !control.IsHandleCreated)return;
                control.BeginInvoke((Action)(() => { if(!control.IsDisposed)refresh(); }));
            };
            Changed += changed;
            control.Disposed += (s,e) => Changed -= changed;
        }
        internal static void Record(string profileId,string service,DashboardCheckResult result,string path=null) {
            Save(new DashboardSessionObservation{ProfileId=profileId,Service=service,
                Result=result.Authorized==true?(result.Evidence=="saved"?"saved":"verified"):result.IsError?"error":result.Authorized==false?"not_logged_in":"unknown",
                Evidence=result.Evidence,Source=result.Source,ExpiresUtc=result.ExpiresUtc,
                AccountName=result.AccountName,Detail=result.Detail,CheckedUtc=DateTime.UtcNow},path);
        }
        internal static void RecordCookieSession(string profileId,string service,bool present,string accountName=null,DateTime? expiresUtc=null) {
            if(string.IsNullOrWhiteSpace(profileId)||string.IsNullOrWhiteSpace(service))return;
            Record(profileId,service,new DashboardCheckResult{
                Authorized=present?(bool?)true:null,Evidence="saved",Source="webview-cookie",ExpiresUtc=expiresUtc,
                AccountName=accountName??"",Detail=present?"Сохранена cookie сессии "+service+"; вход онлайн не проверен.":"Cookie сессии не найдена; выход онлайн не подтверждён."
            });
        }
        internal static string PathName=>Path.Combine(Store.Root,"dashboard-sessions.json");
        internal static List<DashboardSessionObservation> Load(string path=null) {
            lock(sync) {
                path=path??PathName;if(!File.Exists(path))return new List<DashboardSessionObservation>();
                return new JavaScriptSerializer().Deserialize<List<DashboardSessionObservation>>(File.ReadAllText(path))??throw new InvalidDataException("Не удалось прочитать результаты проверки входа.");
            }
        }
        internal static bool Connected(DashboardSessionObservation item,DateTime now) =>
            (item.Result=="verified"||item.Result=="saved")&&(!item.ExpiresUtc.HasValue||item.ExpiresUtc.Value>now);
        internal static bool IsConnected(string profileId,string service=null,string path=null) {
            if(string.IsNullOrWhiteSpace(profileId))return false;
            return Load(path).Any(x=>x.ProfileId==profileId&&(service==null||x.Service==service)&&Connected(x,DateTime.UtcNow));
        }
        internal static bool IsVerified(string profileId,string service=null,string path=null) {
            if(string.IsNullOrWhiteSpace(profileId))return false;
            return Load(path).Any(x=>x.ProfileId==profileId&&(service==null||x.Service==service)&&x.Result=="verified"&&x.Evidence=="online"&&Connected(x,DateTime.UtcNow));
        }
        internal static string Summary(string profileId,string service,string path=null) {
            return Summary(Load(path).FirstOrDefault(x=>x.ProfileId==profileId&&x.Service==service));
        }
        internal static string Summary(DashboardSessionObservation item) {
            if(item==null)return "Сохранённый вход неизвестен; онлайн не проверен.";
            string label=Connected(item,DateTime.UtcNow)
                ?item.Evidence=="saved"?"Сохранённая сессия; онлайн не проверена":"Вход подтверждён онлайн"
                :item.ExpiresUtc.HasValue&&item.ExpiresUtc.Value<=DateTime.UtcNow?"Срок сохранённой cookie истёк; вход неизвестен"
                :item.Result=="not_logged_in"?"Выход подтверждён онлайн":"Состояние входа неизвестно";
            return label+" · "+item.CheckedUtc.ToLocalTime().ToString("dd.MM HH:mm")+"\n"+item.Detail
                +(item.LastAttemptUtc.HasValue?"\nПоследняя проверка: "+item.LastAttemptUtc.Value.ToLocalTime().ToString("dd.MM HH:mm")+" · "+item.LastAttemptDetail:"");
        }
        internal static void Save(DashboardSessionObservation item,string path=null) {
            bool notify=path==null;
            lock(sync) {
                path=path??PathName;var rows=Load(path);
                var previous=rows.FirstOrDefault(x=>x.ProfileId==item.ProfileId&&x.Service==item.Service);
                if(previous!=null&&item.Source=="webview-cookie"&&item.CheckedUtc<previous.CheckedUtc)return;
                if(previous!=null&&(previous.Result=="verified"||previous.Result=="saved")&&
                    (item.Result=="unknown"||item.Result=="error"||(item.Result=="not_logged_in"&&item.Source!=previous.Source)||
                     (previous.Evidence=="online"&&item.Result=="saved"))) {
                    previous.LastAttemptUtc=item.CheckedUtc;previous.LastAttemptDetail=item.Detail;item=previous;
                }
                rows.RemoveAll(x=>x.ProfileId==item.ProfileId&&x.Service==item.Service);rows.Add(item);
                Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";
                File.WriteAllText(temp,new JavaScriptSerializer().Serialize(rows));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);            }
            if(notify)Changed?.Invoke();
        }
    }

    internal sealed class SocialDashboard : UserControl {
        readonly Preferences settings;
        readonly DataGridView grid;
        readonly Label status,summary;
        internal event Action<string,string,string> ConnectRequested;
        sealed class NetworkCell {
            internal string Mark,Detail;internal bool Present,Verified;internal Color Color;
        }
        sealed class ProfileRow {
            internal string Id,Name,Proxy;
            internal NetworkCell[] Networks;
        }
        readonly Button refresh,stop;
        readonly ComboBox group;
        readonly List<Button> checkButtons=new List<Button>();
        CancellationTokenSource checkCancellation;
        List<DashboardSessionObservation> sessionObservations=new List<DashboardSessionObservation>();
        List<MetaProfileObservation> lastMeta=new List<MetaProfileObservation>();
        TelegramData lastTelegram=new TelegramData();
        Func<DolphinBrowserProfile,string,CancellationToken,Task<DashboardCheckResult>> checkSession;
        // The caller must return non-secret result/detail only, never cookie/token values or raw responses.
        internal Func<DolphinBrowserProfile,string,CancellationToken,Task<DashboardCheckResult>> CheckSession {
            get=>checkSession;
            set {checkSession=value;UpdateCheckControls();}
        }
        internal static string ProfileGroup(DolphinBrowserProfile profile) {
            string metadata=string.Join(" ",new[]{profile.FolderName??""}.Concat(profile.Tags??new List<string>()));
            bool ru=Regex.IsMatch(metadata,@"(?i)(?<![\p{L}\p{N}])(?:RU|RUS|RUSSIAN|РУ|РУС|РУССКИЙ|РУССКИЕ)(?![\p{L}\p{N}])");
            bool en=Regex.IsMatch(metadata,@"(?i)(?<![\p{L}\p{N}])(?:EN|ENG|ENGLISH|АНГЛ|АНГЛИЙСКИЙ|АНГЛИЙСКИЕ)(?![\p{L}\p{N}])");
            return ru==en?"Без группы":ru?"RU":"EN";
        }
        List<DolphinBrowserProfile> profiles=new List<DolphinBrowserProfile>();
        List<TelegramProxy> proxies=new List<TelegramProxy>();
        bool loaded,busy,completeRoutes;
        internal SocialDashboard(Preferences settings) {
            this.settings=settings;Dock=DockStyle.Fill;BackColor=Theme.Background;
            DashboardSessionState.Bind(this,RefreshLocal);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
            summary=new Label{Name="DashboardSummary",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Font=Theme.FontCardTitle,ForeColor=Theme.TextPrimary};
            var bar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
            group=Theme.MakeCombo(new[]{"Все группы","RU","EN","Без группы"});group.Name="DashboardGroup";group.Width=130;bar.Controls.Add(group);
            group.SelectedIndexChanged+=(s,e)=>{Render(lastMeta,lastTelegram);};
            refresh=Theme.MakeButton("Обновить профили / прокси",ghost:true,action:async()=>await Fetch());bar.Controls.Add(refresh);
            var presets=Theme.MakeButton("Пресеты",accent:true,action:()=>ProfilePresetDialog.ShowLibrary(FindForm()));presets.Name="DashboardPresets";bar.Controls.Add(presets);
            bar.Controls.Add(new Label{AutoSize=true,Text="✓ сессия сохранена / вход проверен   − выход подтверждён   ? неизвестно",ForeColor=Theme.TextSecondary,Margin=new Padding(12,10,0,0)});
            var checks=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
            checks.Controls.Add(new Label{Text="Проверить группу:",AutoSize=true,ForeColor=Theme.TextSecondary,Margin=new Padding(0,10,8,0)});
            foreach(string service in Networks){string selectedService=service;var button=Theme.MakeButton(service,ghost:true,action:async()=>await CheckGroup(selectedService));button.Name="CheckGroup"+service;button.Enabled=false;checkButtons.Add(button);checks.Controls.Add(button);}
            stop=Theme.MakeButton("Стоп",ghost:true,action:()=>checkCancellation?.Cancel());stop.Name="StopDashboardCheck";stop.Enabled=false;checks.Controls.Add(stop);
            grid=new DataGridView{Name="SocialDashboardGrid",Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None};Theme.StyleGrid(grid);grid.DefaultCellStyle.WrapMode=DataGridViewTriState.False;
            grid.RowTemplate.Height=28;grid.ColumnHeadersHeight=32;grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            foreach(var col in new[]{"Профиль","Прокси","YouTube","TikTok","Instagram","Threads","Telegram","Что осталось"})grid.Columns.Add(col,col);
            grid.Columns[0].FillWeight=150;grid.Columns[1].FillWeight=125;grid.Columns[7].FillWeight=210;
            for(int i=2;i<=6;i++){grid.Columns[i].FillWeight=60;grid.Columns[i].MinimumWidth=65;grid.Columns[i].DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;grid.Columns[i].HeaderCell.Style.Alignment=DataGridViewContentAlignment.MiddleCenter;}
            grid.CellClick+=(s,e)=>{if(e.RowIndex>=0&&e.ColumnIndex>=0)OpenCell(e.RowIndex,e.ColumnIndex);};
            status=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Theme.TextSecondary,Text="Выберите группу → сервис для проверки · Клик по отметке: детали и подключение. Проверки пока не подключены."};
            layout.Controls.Add(summary,0,0);layout.Controls.Add(bar,0,1);layout.Controls.Add(checks,0,2);layout.Controls.Add(grid,0,3);layout.Controls.Add(status,0,4);Controls.Add(layout);
        }
        internal void RefreshLocal() {
            try{sessionObservations=DashboardSessionState.Load();Render(MetaProfileState.Load(),new TelegramStore().Load());}
            catch(Exception e){status.Text="Не удалось загрузить дашборд: "+e.Message;AppDiagnostics.Write("Dashboard","","","load_error",e.GetType().Name,"error");}
        }
        List<DolphinBrowserProfile> SelectedProfiles() {
            string selected=Convert.ToString(group.SelectedItem);
            return (loaded?profiles:profiles.Concat(MetaProfileState.LocalProfiles(settings,lastMeta))).GroupBy(p=>p.Id).Select(g=>g.Last())
                .Where(p=>selected=="Все группы"||string.IsNullOrEmpty(selected)||ProfileGroup(p)==selected).OrderBy(p=>p.Name).ToList();
        }
        void UpdateCheckControls() {
            bool checking=checkCancellation!=null;
            foreach(var button in checkButtons)button.Enabled=checkSession!=null&&!busy&&!checking&&SelectedProfiles().Count>0;
            if(stop!=null)stop.Enabled=checking;
            if(group!=null)group.Enabled=!busy&&!checking;
            if(refresh!=null)refresh.Enabled=!busy&&!checking;
        }
        async Task CheckGroup(string service) {
            if(checkSession==null||busy||checkCancellation!=null)return;
            var selected=SelectedProfiles();if(selected.Count==0)return;
            var checker=checkSession;checkCancellation=new CancellationTokenSource();var cancel=checkCancellation.Token;UpdateCheckControls();
            int completed=0;
            try {
                int next=0;
                async Task CheckNext() {
                  while(next<selected.Count) {
                    cancel.ThrowIfCancellationRequested();
                    var profile=selected[next++];
                    status.Text=service+" · Готово "+completed+" / "+selected.Count+" · Проверяется: "+profile.Name+" · до 2 одновременно";
                    DashboardCheckResult result;
                    try{result=await checker(profile,service,cancel);}
                    catch(OperationCanceledException){throw;}
                    catch(Exception error){result=new DashboardCheckResult{IsError=true,Detail="Проверка завершилась ошибкой: "+error.GetType().Name};AppDiagnostics.Write("Dashboard",profile.Id,"","session_check_error",error.GetType().Name,"error");}
                    if(result==null)result=new DashboardCheckResult{Detail="Сервис не вернул результат проверки."};
                    DashboardSessionState.Record(profile.Id,service,result);
                    sessionObservations=DashboardSessionState.Load();completed++;
                    if(IsDisposed)return;Render(lastMeta,lastTelegram);
                  }
                }
                await Task.WhenAll(CheckNext(),CheckNext());
                status.Text=service+" · Проверено "+completed+" / "+selected.Count+". Результаты сохранены.";
            } catch(OperationCanceledException){if(!IsDisposed)status.Text=service+" · Остановлено · "+completed+" / "+selected.Count+". Готовые результаты сохранены.";}
            catch(Exception error){if(!IsDisposed)status.Text="Проверка не завершена: "+error.Message;AppDiagnostics.Write("Dashboard","","","check_group_error",error.GetType().Name,"error");}
            finally{checkCancellation.Dispose();checkCancellation=null;if(!IsDisposed)UpdateCheckControls();}
        }
        protected override void Dispose(bool disposing){if(disposing)checkCancellation?.Cancel();base.Dispose(disposing);}
        async Task Fetch() {
            if(busy||checkCancellation!=null)return;busy=true;UpdateCheckControls();status.Text="Получаю профили и назначенные прокси Dolphin…";
            try {
                string token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                var fetched=await DolphinProfileCatalog.FetchAll(token);
                var routes=await TelegramProxyCatalog.FetchDolphin(token,fetched.Select(p=>new KeyValuePair<string,string>(p.Id,p.Name)),includeCatalog:false);
                if(IsDisposed)return;
                profiles=fetched;proxies=routes.Proxies;loaded=true;completeRoutes=routes.Warnings.Count==0;
                Render(MetaProfileState.Load(),new TelegramStore().Load());
                status.Text=routes.Warnings.Count>0?"Данные неполные: "+string.Join("; ",routes.Warnings):"Профили и назначения прокси обновлены · Проверка входа не выполнялась.";
            }catch(Exception e){if(!IsDisposed)status.Text="Dolphin не обновлён: "+e.Message;AppDiagnostics.Write("Dashboard","","","refresh_error",e.GetType().Name,"error");}
            finally{busy=false;if(!IsDisposed)UpdateCheckControls();}
        }
        static readonly string[] Networks={"YouTube","TikTok","Instagram","Threads","Telegram"};
        static NetworkCell Account(bool present,string detail,bool verified=false,bool absenceUnknown=true) {
            return new NetworkCell{Present=present,Verified=verified,Mark=verified?"✓":present||absenceUnknown?"?":"−",Color=verified?Theme.Success:present||absenceUnknown?Theme.TextMuted:Theme.Error,Detail=detail};
        }
        void ApplyObservation(NetworkCell cell,string profileId,string service) {
            var observation=sessionObservations.FirstOrDefault(x=>x.ProfileId==profileId&&x.Service==service);if(observation==null)return;
            cell.Verified=DashboardSessionState.Connected(observation,DateTime.UtcNow);
            if(cell.Verified)cell.Present=true;
            cell.Mark=cell.Verified?"✓":observation.Result=="not_logged_in"?"−":"?";
            cell.Color=cell.Verified?Theme.Success:observation.Result=="not_logged_in"?Theme.Error:Theme.TextMuted;
            cell.Detail=DashboardSessionState.Summary(observation)+"\n"+observation.AccountName+"\n\n"+cell.Detail;
        }
        static bool TelegramVerified(TelegramAccount account) {
            return account.status=="ready"&&DateTime.TryParse(account.last_success_utc,null,System.Globalization.DateTimeStyles.RoundtripKind,out var success)&&DateTime.UtcNow-success.ToUniversalTime()<=TimeSpan.FromMinutes(15)
                &&(!DateTime.TryParse(account.wait_until,null,System.Globalization.DateTimeStyles.RoundtripKind,out var until)||until.ToUniversalTime()<=DateTime.UtcNow);
        }
        void Render(List<MetaProfileObservation> observations,TelegramData telegram) {
            lastMeta=observations;lastTelegram=telegram;
            grid.Rows.Clear();
            var all=SelectedProfiles();
            var known=ProfileCatalog.Build(settings);
            int full=0,presentCount=0,verifiedCount=0;
            foreach(var p in all) {
                var account=known.FirstOrDefault(x=>x.ProfileId==p.Id);
                var meta=observations.FirstOrDefault(x=>x.Id==p.Id);
                string key="dolphin:profile:"+p.Id;
                var proxy=proxies.FirstOrDefault(x=>x.source_id==key||(x.source_ids??new List<string>()).Contains(key));
                // Only an explicit profile link is ownership evidence; matching a shared endpoint is not.
                var tgProxyIds=new HashSet<string>(telegram.proxies.Where(x=>x.source_id==key||(x.source_ids??new List<string>()).Contains(key)).Select(x=>x.id));
                var tg=telegram.accounts.Where(a=>tgProxyIds.Contains(a.proxy_id)).ToList();
                bool ig=meta?.InstagramCookie==true;
                var cells=new[]{
                    Account(account?.YouTubeAccounts.Count>0,account?.YouTubeAccounts.Count>0?string.Join("\n",account.YouTubeAccounts.Select(c=>c.Name))+"\nДобавлен, вход не проверен":"Аккаунт YouTube не добавлен"),
                    Account(account?.TikTokAccounts.Count>0,account?.TikTokAccounts.Count>0?string.Join("\n",account.TikTokAccounts.Select(c=>c.Name))+"\nДобавлен, вход не проверен":"Аккаунт TikTok не добавлен"),
                    Account(ig,ig?"Добавлен, вход не проверен\n"+MetaProfileState.InstagramStatus(meta,DateTime.UtcNow):"Нет известного аккаунта Instagram\n"+MetaProfileState.InstagramStatus(meta,DateTime.UtcNow)),
                    Account(false,"Вход Threads неизвестен: отдельной проверки ещё не было. Общий браузер с Instagram не подтверждает вход.",absenceUnknown:true),
                    Account(tg.Count>0,tg.Count==0?"Нет аккаунта Telegram с явной привязкой к этому профилю":string.Join("\n",tg.Select(a=>a.name+" · "+TelegramStore.Status(a.status)+" · "+(a.checked_at??"")))+"\nПривязка через запись прокси Dolphin.",tg.Any(TelegramVerified))
                };
                for(int c=0;c<cells.Length;c++)ApplyObservation(cells[c],p.Id,Networks[c]);
                bool complete=cells.All(c=>c.Verified)&&proxy!=null;if(complete)full++;
                presentCount+=cells.Count(c=>c.Present);verifiedCount+=cells.Count(c=>c.Verified);
                var remains=new List<string>();if(proxy==null)remains.Add(loaded&&completeRoutes?"Прокси":"Прокси ?");remains.AddRange(Networks.Where((n,i)=>!cells[i].Verified).Select(n=>n+(cells[Array.IndexOf(Networks,n)].Mark=="?"?" ?":"")));
                string remaining=remains.Count>0?string.Join(", ",remains):cells.All(c=>c.Verified)?"Все сессии подключены":"Проверить вход";
                string proxyLabel=proxy!=null?TelegramProxyCatalog.AssignedLabel(proxy):loaded&&completeRoutes?"Не назначен":"?";
                int irow=grid.Rows.Add(p.Name,proxyLabel,cells[0].Mark,cells[1].Mark,cells[2].Mark,cells[3].Mark,cells[4].Mark,remaining);
                var row=grid.Rows[irow];row.Height=28;row.Tag=new ProfileRow{Id=p.Id,Name=p.Name,Proxy=proxyLabel,Networks=cells};
                row.Cells[0].ToolTipText=p.Name;row.Cells[1].ToolTipText=proxy==null?"Назначенный прокси не получен. Обновите профили / прокси.":"Назначенный прокси: "+proxyLabel+". Доступность не проверялась.";
                row.Cells[7].ToolTipText="Не добавлено: "+(remains.Count==0?"ничего":string.Join(", ",remains))+"\nПодключено сессий: "+cells.Count(c=>c.Verified)+" / 5";
                Theme.MarkConnected(row.Cells[0].Style,cells.Any(c=>c.Verified));
                for(int c=0;c<cells.Length;c++){Theme.MarkConnected(row.Cells[c+2].Style,cells[c].Verified);row.Cells[c+2].Style.ForeColor=cells[c].Color;row.Cells[c+2].Style.SelectionForeColor=cells[c].Color;row.Cells[c+2].ToolTipText=cells[c].Detail;}
            }
            summary.Text="Профилей: "+all.Count+"    Подключены: "+full+"    Требуют внимания: "+(all.Count-full)+"    Записей: "+presentCount+" / "+(all.Count*5)+"    Сессий подключено: "+verifiedCount;
            grid.ClearSelection();UpdateCheckControls();
        }
        void OpenCell(int rowIndex,int columnIndex) {
            var row=grid.Rows[rowIndex].Tag as ProfileRow;if(row==null)return;
            string network=columnIndex>=2&&columnIndex<=6?Networks[columnIndex-2]:null;
            string detail=network==null?"Прокси: "+row.Proxy+"\n\n"+string.Join("\n\n",Networks.Select((n,i)=>n+"\n"+row.Networks[i].Detail)):row.Networks[columnIndex-2].Detail;
            using(var dialog=new Form{Text=row.Name+(network==null?"":" · "+network),ClientSize=new Size(540,380),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false}) {
                Theme.StyleModal(dialog);
                var body=new TextBox{Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,Text=detail.Replace("\n",Environment.NewLine),BackColor=Theme.Background,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.None};
                var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,FlowDirection=FlowDirection.RightToLeft};
                var close=Theme.MakeButton("Закрыть",ghost:true);close.DialogResult=DialogResult.Cancel;actions.Controls.Add(close);
                if(network!=null){var open=Theme.MakeButton("Открыть / подключить",accent:true,action:()=>{dialog.Close();ConnectRequested?.Invoke(row.Id,row.Name,network);});actions.Controls.Add(open);}
                dialog.Controls.Add(body);dialog.Controls.Add(actions);dialog.CancelButton=close;dialog.ShowDialog(this);
            }
        }
    }
}
