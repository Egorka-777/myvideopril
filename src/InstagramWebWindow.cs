using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VideoBatch {
    internal sealed class InstagramWebWindow : Form {
        readonly string profileId,upstream;
        readonly WebView2 view;
        readonly Label hint;
        TelegramWebProxy proxy;
        bool closing,closed;
        internal static bool TrustedOrigin(string source) => Uri.TryCreate(source,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.Port==443&&(u.Host=="instagram.com"||u.Host.EndsWith(".instagram.com",StringComparison.OrdinalIgnoreCase)||u.Host=="threads.com"||u.Host.EndsWith(".threads.com",StringComparison.OrdinalIgnoreCase)||u.Host=="threads.net"||u.Host.EndsWith(".threads.net",StringComparison.OrdinalIgnoreCase));
        public InstagramWebWindow(string profileId,string name,string upstream) {
            if(!Regex.IsMatch(profileId??"",@"^\d+$"))throw new ArgumentException("Некорректный Dolphin ID");
            if(string.IsNullOrWhiteSpace(upstream))throw new ArgumentException("Профилю нужен прокси");
            this.profileId=profileId;this.upstream=upstream;
            Text="Instagram / Threads · "+name;ClientSize=new Size(430,850);MinimumSize=new Size(390,620);StartPosition=FormStartPosition.CenterParent;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;
            var sites=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42,Padding=new Padding(5),WrapContents=false};
            sites.Controls.Add(Theme.MakeButton("Instagram",ghost:true,action:()=>view.CoreWebView2?.Navigate("https://www.instagram.com/")));
            sites.Controls.Add(Theme.MakeButton("Threads",ghost:true,action:()=>view.CoreWebView2?.Navigate("https://www.threads.com/")));
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42,Padding=new Padding(5),WrapContents=false};
            bar.Controls.Add(Theme.MakeButton("Назад",ghost:true,action:()=>{if(view.CoreWebView2?.CanGoBack==true)view.CoreWebView2.GoBack();}));
            bar.Controls.Add(Theme.MakeButton("Обновить",ghost:true,action:()=>view.CoreWebView2?.Reload()));
            hint=new Label{Dock=DockStyle.Bottom,Height=56,Padding=new Padding(6),Text="Подключение через прокси профиля…",ForeColor=Theme.TextSecondary};
            view=new WebView2{Dock=DockStyle.Fill,DefaultBackgroundColor=Theme.Background};Controls.Add(view);Controls.Add(bar);Controls.Add(sites);Controls.Add(hint);
            Shown+=async(s,e)=>await Initialize();
            FormClosing+=async(s,e)=>{if(closed)return;e.Cancel=true;if(closing)return;closing=true;view.Dispose();if(proxy!=null)await proxy.Stop();closed=true;Close();};
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
                string agent=new Regex(@"\([^)]*\)").Replace(core.Settings.UserAgent,"(Linux; Android 13)",1);
                agent=Regex.Replace(agent,@"\s+Edg(?:e|A)?/[\d.]+","");
                core.Settings.UserAgent=agent.Replace(" Safari/"," Mobile Safari/");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride","{\"width\":390,\"height\":844,\"deviceScaleFactor\":1,\"mobile\":true}");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled","{\"enabled\":true,\"maxTouchPoints\":5}");
                core.NavigationStarting+=(s,e)=>{if(!TrustedOrigin(e.Uri)){e.Cancel=true;hint.Text="В этом окне открываются только Instagram и Threads.";}};
                core.NewWindowRequested+=(s,e)=>{e.Handled=true;if(TrustedOrigin(e.Uri))core.Navigate(e.Uri);};
                core.ServerCertificateErrorDetected+=(s,e)=>{e.Action=CoreWebView2ServerCertificateErrorAction.Cancel;hint.Text="Ошибка сертификата. Соединение отклонено.";};
                core.NavigationCompleted+=(s,e)=>{hint.Text=e.IsSuccess?"Instagram / Threads. Войдите вручную; для Threads используйте вход через Instagram. Обе сессии сохраняются в этом профиле.":"Страница не загрузилась: "+e.WebErrorStatus;AppDiagnostics.Write("Instagram",profileId,"","navigation",e.IsSuccess?"Страница загружена":e.WebErrorStatus.ToString(),e.IsSuccess?"info":"error");};
                core.Navigate("https://www.instagram.com/");
            }catch(Exception e){if(closing)return;hint.Text=e is InvalidOperationException||e is IOException?e.Message:"Не удалось открыть Instagram: "+e.GetType().Name;AppDiagnostics.Write("Instagram",profileId,"","web_error",e.GetType().Name,"error");view.Dispose();if(proxy!=null)await proxy.Stop();}
        }
    }
}
