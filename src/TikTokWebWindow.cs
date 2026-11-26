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
    internal sealed class TikTokWebWindow : Form {
        readonly string profileId,upstream;
        readonly WebView2 view;
        readonly Label hint;
        TelegramWebProxy proxy;
        bool closing,closed,mobileReady;
        bool? sessionPresent;
        internal const string WebVideoNavigationScript=@"document.addEventListener('click',event=>{const anchor=event.target.closest&&event.target.closest('a[href]');if(!anchor||event.ctrlKey||event.metaKey||event.shiftKey||event.altKey)return;let url;try{url=new URL(anchor.href,location.href)}catch{return}if(url.protocol!=='https:'||url.hostname!=='www.tiktok.com'||!/^\/@[^/]+\/video\/\d+\/?$/.test(url.pathname))return;event.preventDefault();event.stopImmediatePropagation();location.assign(url.href);},true);";
        internal static bool TrustedOrigin(string source)=>Uri.TryCreate(source,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.Port==443&&(u.Host=="tiktok.com"||u.Host.EndsWith(".tiktok.com",StringComparison.OrdinalIgnoreCase));
        public TikTokWebWindow(string profileId,string name,string upstream){
            if(!Regex.IsMatch(profileId??"",@"^\d+$"))throw new ArgumentException("Некорректный Dolphin ID");
            if(string.IsNullOrWhiteSpace(upstream))throw new ArgumentException("Профилю нужен прокси");
            if(!openProfiles.TryAdd(profileId,0))throw new InvalidOperationException("Профиль TikTok уже открыт или проверяется.");
            Disposed+=(s,e)=>{byte removed;openProfiles.TryRemove(profileId,out removed);};
            this.profileId=profileId;this.upstream=upstream;
            Text="TikTok · "+name;ClientSize=new Size(391,Math.Min(849,Screen.FromPoint(Cursor.Position).WorkingArea.Height-80));MinimumSize=new Size(390,620);StartPosition=FormStartPosition.CenterParent;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;
            var title=new Label{Dock=DockStyle.Top,Height=48,Padding=new Padding(12,8,12,0),Text=name+" · "+profileId,Font=Theme.FontCardTitle,AutoEllipsis=true};
            var tabs=new TableLayoutPanel{Name="TikTokSiteTabs",Dock=DockStyle.Top,Height=52,Padding=new Padding(8,5,8,5),ColumnCount=1,RowCount=1,BackColor=Theme.TopBar};tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));tabs.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var home=Theme.MakeButton("TikTok",accent:true,action:()=>view.CoreWebView2?.Navigate("https://www.tiktok.com/"));home.Name="TikTokTab";home.Dock=DockStyle.Fill;home.AutoSize=false;home.MinimumSize=Size.Empty;home.Margin=Padding.Empty;tabs.Controls.Add(home,0,0);
            hint=new Label{Dock=DockStyle.Bottom,Height=66,Padding=new Padding(10,6,10,6),Text="Подключение через прокси профиля…",ForeColor=Theme.TextSecondary};
            var device=new Panel{Dock=DockStyle.Fill,Padding=new Padding(8),BackColor=Theme.Card};view=new WebView2{Dock=DockStyle.Fill,DefaultBackgroundColor=Theme.Background};device.Controls.Add(view);
            Controls.Add(device);Controls.Add(tabs);Controls.Add(title);Controls.Add(hint);
            view.SizeChanged+=async(s,e)=>{if(!mobileReady||closing)return;try{await ResizeMobileViewport();}catch(Exception error){if(!closing){hint.Text="Не удалось изменить размер мобильной страницы.";AppDiagnostics.Write("TikTok",profileId,"","viewport_error",error.GetType().Name,"error");}}};
            BrowserSessionProbe.Observe(view,profileId,text=>{if(!closing)hint.Text=text;});
            Shown+=async(s,e)=>await Initialize();
            FormClosing+=async(s,e)=>{if(closed)return;e.Cancel=true;if(closing)return;closing=true;try{if(!view.IsDisposed&&view.CoreWebView2!=null)await ReportSession(false);}catch(Exception error){AppDiagnostics.Write("TikTok",profileId,"","session_check_error",error.GetType().Name,"error");}finally{view.Dispose();if(proxy!=null)await proxy.Stop();closed=true;Close();}};
        }
        internal Task RunWarmup(System.Threading.CancellationToken ct,Action<string> report)=>SocialFeedWarmup.RunAsync(view,"TikTok",ct,report);
        Task ResizeMobileViewport(){int width=view.ClientSize.Width,height=view.ClientSize.Height;if(width<=0||height<=0)return Task.CompletedTask;return view.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride","{\"width\":"+width+",\"height\":"+height+",\"screenWidth\":375,\"screenHeight\":667,\"deviceScaleFactor\":2,\"mobile\":true}");}
        async Task ReportSession(bool updateHint){
            var cookies=await view.CoreWebView2.CookieManager.GetCookiesAsync("https://www.tiktok.com/");
            var auth=cookies.Where(c=>(c.Name=="sessionid"||c.Name=="sessionid_ss")&&!string.IsNullOrEmpty(c.Value)&&(c.IsSession||c.Expires.ToUniversalTime()>DateTime.UtcNow)).ToList();
            bool present=auth.Count>0;
            DateTime? expires=auth.Count==0||auth.Any(c=>c.IsSession)?(DateTime?)null:auth.Max(c=>c.Expires.ToUniversalTime());
            if(sessionPresent!=present){
                sessionPresent=present;
                AppDiagnostics.Write("TikTok",profileId,"","session",present?"В профиле есть cookie сессии TikTok":"В профиле нет cookie сессии TikTok");
            }
            if(present) DashboardSessionState.RecordCookieSession(profileId,"TikTok",true,expiresUtc:expires);
            else if(sessionPresent==false) DashboardSessionState.RecordCookieSession(profileId,"TikTok",false);
            if(updateHint&&!closing)hint.Text=present?"Сессия TikTok сохранена. Профиль: "+profileId+". Наличие cookie не заменяет проверку входа на сайте.":"Войдите в TikTok. Данные сайта сохраняются отдельно в профиле "+profileId+"; закрытие окна не выполняет выход.";
        }
        internal static bool IsProfileOpen(string id)=>openProfiles.ContainsKey(id);
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> openProfiles=new System.Collections.Concurrent.ConcurrentDictionary<string,byte>();
        internal async Task<DashboardCheckResult> CheckBackground(System.Threading.CancellationToken ct){
            var handle=Handle;var childHandle=view.Handle;
            try{await Initialize();ct.ThrowIfCancellationRequested();return await BrowserSessionProbe.ReadAsync(view,"TikTok",ct);}
            finally{closing=true;view.Dispose();if(proxy!=null)await proxy.Stop();closed=true;Dispose();}
        }
        async Task Initialize(){try{
            proxy=new TelegramWebProxy();await proxy.Start(new TelegramStore(),upstream,ev=>{if(Convert.ToString(ev["status"])=="failed")AppDiagnostics.Write("TikTok",profileId,"","proxy_route","Не удалось открыть маршрут через прокси профиля","error");},service:"tiktok");if(closing)return;
            string root=Path.Combine(Store.Root,"tiktok","web",profileId);Directory.CreateDirectory(root);
            string args="--disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp --proxy-server=http://127.0.0.1:"+proxy.Port+" --proxy-bypass-list=<-loopback>";
            var env=await CoreWebView2Environment.CreateAsync(null,root,new CoreWebView2EnvironmentOptions(args));if(closing)return;await view.EnsureCoreWebView2Async(env);if(closing)return;
            var core=view.CoreWebView2;TelegramWebProxy.BindAuthentication(core,proxy.Port,proxy.Secret);core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            core.Settings.UserAgent=InstagramWebWindow.MobileUserAgent;await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{userAgent=InstagramWebWindow.MobileUserAgent,platform="iPhone"}));await ResizeMobileViewport();await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled","{\"enabled\":true,\"maxTouchPoints\":5}");mobileReady=true;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebVideoNavigationScript);
            core.NavigationStarting+=(s,e)=>{if(!TrustedOrigin(e.Uri)){e.Cancel=true;hint.Text="В этом окне открывается только официальный TikTok.";if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var blocked))AppDiagnostics.Write("TikTok",profileId,"","web_navigation_blocked",blocked.Scheme+"://"+blocked.Host+blocked.AbsolutePath,"warning");}};
            core.NewWindowRequested+=(s,e)=>{e.Handled=true;if(TrustedOrigin(e.Uri))core.Navigate(e.Uri);};core.ServerCertificateErrorDetected+=(s,e)=>{e.Action=CoreWebView2ServerCertificateErrorAction.Cancel;hint.Text="Ошибка сертификата. Соединение отклонено.";};
            core.NavigationCompleted+=async(s,e)=>{if(closing)return;hint.Text=e.IsSuccess?"Страница загружена. Проверяю сохранённую сессию…":"Страница не загрузилась: "+e.WebErrorStatus;AppDiagnostics.Write("TikTok",profileId,"","web_navigation",e.IsSuccess?"Страница загружена":e.WebErrorStatus.ToString(),e.IsSuccess?"info":"error");if(e.IsSuccess)try{await ReportSession(true);}catch(Exception error){if(!closing)hint.Text="Не удалось проверить сохранённую сессию.";AppDiagnostics.Write("TikTok",profileId,"","session_check_error",error.GetType().Name,"error");}};
            await ReportSession(true);core.Navigate("https://www.tiktok.com/");
        }catch(Exception error){if(closing)return;hint.Text=error is InvalidOperationException||error is IOException?error.Message:"Не удалось открыть TikTok: "+error.GetType().Name;AppDiagnostics.Write("TikTok",profileId,"","web_error",error.GetType().Name+" · HRESULT 0x"+error.HResult.ToString("X8"),"error");view.Dispose();if(proxy!=null)await proxy.Stop();}}
    }
}
