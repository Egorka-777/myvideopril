using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VideoBatch {
    internal sealed class InstagramWebWindow : Form {
        readonly string profileId,upstream,profileName;
        readonly WebView2 view;
        readonly Label hint;
        readonly Button instagramTab,threadsTab,metaTab;
        internal const string MetaUrl="https://accountscenter.instagram.com/";
        internal const string MobileUserAgent="Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1";
        string initialUrl="https://www.instagram.com/";
        internal void OpenNetwork(string network){initialUrl=network=="Threads"?"https://www.threads.com/":"https://www.instagram.com/";view.CoreWebView2?.Navigate(initialUrl);}
        void SelectSite(string url){view.CoreWebView2?.Navigate(url);}
        void RefreshSiteTabs(string url){
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri))return;
            var selected=uri.Host=="accountscenter.instagram.com"?metaTab:uri.Host.EndsWith("threads.com",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith("threads.net",StringComparison.OrdinalIgnoreCase)?threadsTab:instagramTab;
            foreach(var button in new[]{instagramTab,threadsTab,metaTab}){button.BackColor=button==selected?Theme.Accent:Theme.TopBar;button.ForeColor=button==selected?Color.White:Theme.TextSecondary;}
        }
        TelegramWebProxy proxy;
        bool closing,closed,mobileReady,backgroundChecking;
        Task initialization;
        Task EnsureInitialized() => initialization ?? (initialization = Initialize());
        bool? sessionPresent;
        internal static bool TrustedOrigin(string source) => Uri.TryCreate(source,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.Port==443&&(u.Host=="instagram.com"||u.Host.EndsWith(".instagram.com",StringComparison.OrdinalIgnoreCase)||u.Host=="threads.com"||u.Host.EndsWith(".threads.com",StringComparison.OrdinalIgnoreCase)||u.Host=="threads.net"||u.Host.EndsWith(".threads.net",StringComparison.OrdinalIgnoreCase));
        public InstagramWebWindow(string profileId,string name,string upstream) {
            if(!Regex.IsMatch(profileId??"",@"^\d+$"))throw new ArgumentException("Некорректный Dolphin ID");
            if(string.IsNullOrWhiteSpace(upstream))throw new ArgumentException("Профилю нужен прокси");
            if(!openProfiles.TryAdd(profileId,0))throw new InvalidOperationException("Профиль Instagram / Threads уже открыт или проверяется.");
            Disposed+=(s,e)=>{byte removed;openProfiles.TryRemove(profileId,out removed);};
            this.profileId=profileId;this.upstream=upstream;profileName=name;
            Text="Instagram / Threads / Meta · "+name;ClientSize=new Size(391,Math.Min(849,Screen.FromPoint(Cursor.Position).WorkingArea.Height-80));MinimumSize=new Size(390,620);StartPosition=FormStartPosition.CenterParent;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;
            var title=new Label{Dock=DockStyle.Top,Height=48,Padding=new Padding(12,8,12,0),Text=name+" · "+profileId,Font=Theme.FontCardTitle,AutoEllipsis=true};
            var sites=new TableLayoutPanel{Name="MetaSiteTabs",Dock=DockStyle.Top,Height=52,Padding=new Padding(8,5,8,5),ColumnCount=3,RowCount=1,BackColor=Theme.TopBar};
            for(int i=0;i<3;i++)sites.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));
            sites.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            instagramTab=Theme.MakeButton("Instagram",accent:true,action:()=>SelectSite("https://www.instagram.com/"));
            threadsTab=Theme.MakeButton("Threads",ghost:true,action:()=>SelectSite("https://www.threads.com/"));
            metaTab=Theme.MakeButton("Meta",ghost:true,action:()=>SelectSite(MetaUrl));
            var tabs=new[]{instagramTab,threadsTab,metaTab};
            for(int i=0;i<tabs.Length;i++){tabs[i].Name=new[]{"InstagramTab","ThreadsTab","MetaTab"}[i];tabs[i].Dock=DockStyle.Fill;tabs[i].AutoSize=false;tabs[i].MinimumSize=Size.Empty;tabs[i].Margin=new Padding(i==0?0:4,0,i==2?0:4,0);sites.Controls.Add(tabs[i],i,0);}
            hint=new Label{Dock=DockStyle.Bottom,Height=66,Padding=new Padding(10,6,10,6),Text="Подключение через прокси профиля…",ForeColor=Theme.TextSecondary};
            var device=new Panel{Dock=DockStyle.Fill,Padding=new Padding(8),BackColor=Theme.Card};
            view=new WebView2{Dock=DockStyle.Fill,DefaultBackgroundColor=Theme.Background};device.Controls.Add(view);
            Controls.Add(device);Controls.Add(sites);Controls.Add(title);Controls.Add(hint);
            view.SizeChanged+=async(s,e)=>{if(!mobileReady||closing)return;try{await ResizeMobileViewport();}catch(Exception error){if(!closing){hint.Text="Не удалось изменить размер мобильной страницы.";AppDiagnostics.Write("Instagram",profileId,"","viewport_error",error.GetType().Name,"error");}}};
            BrowserSessionProbe.Observe(view,profileId,text=>{if(!closing&&!backgroundChecking)hint.Text=text;});
            Shown+=async(s,e)=>await EnsureInitialized();
            FormClosing+=async(s,e)=>{
                if(closed)return;e.Cancel=true;if(closing)return;closing=true;
                try{if(!view.IsDisposed&&view.CoreWebView2!=null)await ReportSession(false);}
                catch(Exception error){AppDiagnostics.Write("Instagram",profileId,"","session_check_error",error.GetType().Name,"error");}
                finally{view.Dispose();if(proxy!=null)await proxy.Stop();closed=true;Close();}
            };
        }
        internal Task RunWarmup(string network,System.Threading.CancellationToken ct,Action<string> report)=>SocialFeedWarmup.RunAsync(view,network,ct,report);
        Task ResizeMobileViewport() {
            int width=view.ClientSize.Width,height=view.ClientSize.Height;
            if(width<=0||height<=0)return Task.CompletedTask;
            return view.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride","{\"width\":"+width+",\"height\":"+height+",\"screenWidth\":375,\"screenHeight\":667,\"deviceScaleFactor\":2,\"mobile\":true}");
        }
        async Task ReportSession(bool updateHint) {
            if(backgroundChecking)return;
            var cookies=await view.CoreWebView2.CookieManager.GetCookiesAsync("https://www.instagram.com/");
            var cookie=cookies.FirstOrDefault(c=>c.Name=="sessionid"&&!string.IsNullOrEmpty(c.Value)&&(c.IsSession||c.Expires.ToUniversalTime()>DateTime.UtcNow));
            bool present=cookie!=null;
            MetaProfileState.Save(new MetaProfileObservation{Id=profileId,Name=profileName,InstagramCookie=present,CheckedUtc=DateTime.UtcNow,CookieExpiresUtc=cookie==null||cookie.IsSession?(DateTime?)null:cookie.Expires.ToUniversalTime()});
            if(sessionPresent!=present){
                sessionPresent=present;
                AppDiagnostics.Write("Instagram",profileId,"","session",present?"В профиле есть cookie сессии Instagram":"В профиле нет cookie сессии Instagram");
                DashboardSessionState.RecordCookieSession(profileId,"Instagram",present,expiresUtc:cookie==null||cookie.IsSession?(DateTime?)null:cookie.Expires.ToUniversalTime());
            }
            if(updateHint&&!closing)hint.Text=present?"Cookie сессии Instagram обнаружена; действительность входа онлайн не проверена. Данные сохраняются в профиле "+profileId+". Вход Threads проверьте вручную во вкладке Threads.":"Войдите вручную. Данные сайтов сохраняются отдельно в профиле "+profileId+"; закрытие окна не выполняет выход.";
        }
        internal static bool IsProfileOpen(string id) => openProfiles.ContainsKey(id);
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> openProfiles=new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        internal async Task<DashboardCheckResult> CheckBackground(string network,System.Threading.CancellationToken ct) {
            backgroundChecking=true;OpenNetwork(network);
            var handle=Handle;var childHandle=view.Handle;
            try { await Initialize();ct.ThrowIfCancellationRequested();return await BrowserSessionProbe.ReadAsync(view,network,ct); }
            finally { closing=true;view.Dispose();if(proxy!=null)await proxy.Stop();closed=true;Dispose(); }
        }
        internal async Task<bool> ApplyProfileBackground(string expectedUsername,string displayName,string bio,string avatarPath,System.Threading.CancellationToken ct,System.Action<string> log) {
            backgroundChecking=true;
            try {
                Show();
                await EnsureInitialized();
                ct.ThrowIfCancellationRequested();
                return await InstagramWebProfileEditor.ApplyOnView(view,expectedUsername,displayName,bio,avatarPath,ct,text=>{
                    hint.Text=text;
                    log?.Invoke(text);
                });
            }
            finally { closing=true;view.Dispose();if(proxy!=null)await proxy.Stop();closed=true;Dispose(); }
        }
        async Task Initialize() {
            try{
                var store=new TelegramStore();proxy=new TelegramWebProxy();
                await proxy.Start(store,upstream,ev=>{
                    if(Convert.ToString(ev["status"])=="failed")AppDiagnostics.Write("Instagram",profileId,"","proxy_route","Не удалось открыть маршрут через прокси профиля","error");
                },service:"instagram");
                if(closing)return;
                string root=Path.Combine(Store.Root,"instagram","web",profileId);Directory.CreateDirectory(root);
                string args="--disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp --proxy-server=http://127.0.0.1:"+proxy.Port+" --proxy-bypass-list=<-loopback>";
                var env=await CoreWebView2Environment.CreateAsync(null,root,new CoreWebView2EnvironmentOptions(args));
                if(closing)return;
                await view.EnsureCoreWebView2Async(env);if(closing)return;
                var core=view.CoreWebView2;TelegramWebProxy.BindAuthentication(core,proxy.Port,proxy.Secret);
                core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
                core.Settings.UserAgent=MobileUserAgent;
                await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{userAgent=MobileUserAgent,platform="iPhone"}));
                await ResizeMobileViewport();
                await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled","{\"enabled\":true,\"maxTouchPoints\":5}");
                mobileReady=true;
                core.NavigationStarting+=(s,e)=>{if(!TrustedOrigin(e.Uri)){e.Cancel=true;hint.Text="В этом окне открываются Instagram, Threads и Центр аккаунтов Meta.";}};
                core.SourceChanged+=(s,e)=>RefreshSiteTabs(core.Source);
                core.NewWindowRequested+=(s,e)=>{e.Handled=true;if(TrustedOrigin(e.Uri))core.Navigate(e.Uri);};
                core.ServerCertificateErrorDetected+=(s,e)=>{e.Action=CoreWebView2ServerCertificateErrorAction.Cancel;hint.Text="Ошибка сертификата. Соединение отклонено.";};
                core.NavigationCompleted+=async(s,e)=>{
                    if(closing)return;
                    hint.Text=e.IsSuccess?"Страница загружена. Проверяю сохранённую сессию…":"Страница не загрузилась: "+e.WebErrorStatus;
                    AppDiagnostics.Write("Instagram",profileId,"","navigation",e.IsSuccess?"Страница загружена":e.WebErrorStatus.ToString(),e.IsSuccess?"info":"error");
                    if(e.IsSuccess)try{await ReportSession(true);}catch(Exception error){if(!closing)hint.Text="Не удалось проверить сохранённую сессию.";AppDiagnostics.Write("Instagram",profileId,"","session_check_error",error.GetType().Name,"error");}
                };
                await ReportSession(true);
                core.Navigate(initialUrl);
            }catch(Exception e){if(closing)return;hint.Text=e is InvalidOperationException||e is IOException?e.Message:"Не удалось открыть Instagram: "+e.GetType().Name;AppDiagnostics.Write("Instagram",profileId,"","web_error",e.GetType().Name+" · HRESULT 0x"+e.HResult.ToString("X8"),"error");try{view.Dispose();}catch(ObjectDisposedException){}if(proxy!=null)await proxy.Stop();proxy=null;byte removed;openProfiles.TryRemove(profileId,out removed);}
        }
    }
}
