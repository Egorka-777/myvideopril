using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VideoBatch {
    internal sealed class TelegramWebProxy {
        Process process;
        StreamWriter input;
        public int Port{get;private set;}
        public string Secret{get;}=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
        public async Task Start(TelegramStore store,string proxy){
            string python=Path.Combine(store.Root,"venv","Scripts","python.exe");
            if(!File.Exists(python))throw new InvalidOperationException("Подготовьте Telegram в настройках приложения");
            var start=new ProcessStartInfo(python,Core.Quote(Path.Combine(TelegramRunner.Scripts(),"web_proxy.py"))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            process=Process.Start(start);input=new StreamWriter(process.StandardInput.BaseStream,new UTF8Encoding(false)){AutoFlush=true};
            var json=new JavaScriptSerializer();await input.WriteLineAsync(json.Serialize(new{proxy,secret=Secret}));
            var errors=process.StandardError.ReadToEndAsync();
            var line=process.StandardOutput.ReadLineAsync();if(await Task.WhenAny(line,Task.Delay(15000))!=line)throw new IOException("Локальный маршрут прокси не запустился вовремя");
            var result=json.Deserialize<Dictionary<string,object>>(await line??"{}");
            if(!result.TryGetValue("port",out var port)||Convert.ToInt32(port)<1)throw new IOException("Не удалось запустить локальный маршрут прокси");Port=Convert.ToInt32(port);
        }
        public async Task Stop(){var p=process;if(p==null)return;process=null;try{if(input!=null)await input.WriteLineAsync("cancel");if(!await Task.Run(()=>p.WaitForExit(3000)))p.Kill();}catch{}finally{input=null;p.Dispose();}}
        internal static void BindAuthentication(CoreWebView2 core,int port,string secret){
            core.BasicAuthenticationRequested+=(s,e)=>{if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)&&uri.Host=="127.0.0.1"&&uri.Port==port){e.Response.UserName="local";e.Response.Password=secret;}else e.Cancel=true;};
        }
    }
    internal sealed class TelegramWebWindow:Form {
        readonly TelegramStore store;
        readonly TelegramAccount account;
        readonly Func<string,Task> authorize;
        readonly WebView2 view;
        readonly Label hint;
        readonly Button login;
        TelegramWebProxy proxy;
        bool closed,stopping,signing;
        // Verified against Telegram Web A's AuthQrCode.tsx. Read only the login QR,
        // never a QR embedded in a chat or a page with different origin.
        internal const string LoginRectScript=@"(()=>{const e=document.querySelector('#auth-qr-form .qr-container');if(!e||!e.getClientRects().length)return null;const r=e.getBoundingClientRect();if(r.width<80||r.height<80||r.x<0||r.y<0)return null;const top=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);if(!top||!top.closest('#auth-qr-form'))return null;return {x:r.x,y:r.y,width:r.width,height:r.height,viewportWidth:innerWidth,viewportHeight:innerHeight};})()";
        internal static bool TrustedOrigin(string source)=>Uri.TryCreate(source,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Host=="web.telegram.org"&&uri.Port==443;
        internal static string DecodeQr(Bitmap image){
            var reader=new ZXing.BarcodeReader{AutoRotate=true,Options=new ZXing.Common.DecodingOptions{TryHarder=true,PossibleFormats=new[]{ZXing.BarcodeFormat.QR_CODE}}};
            var result=reader.Decode(image);string text=result?.Text??"";
            var match=Regex.Match(text,@"^tg://login\?token=([A-Za-z0-9_-]{16,1024})$");
            if(!match.Success)throw new InvalidOperationException("QR-код входа не прочитан. Дождитесь обновления QR и нажмите ещё раз");return match.Groups[1].Value;
        }
        public TelegramWebWindow(TelegramStore store,TelegramAccount account,Func<string,Task> authorize){
            this.store=store;this.account=account;this.authorize=authorize;
            Text="Telegram · "+account.name;ClientSize=new Size(1080,760);MinimumSize=new Size(800,620);BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;StartPosition=FormStartPosition.CenterParent;
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=54,Padding=new Padding(10,7,0,0),WrapContents=false};
            login=Theme.MakeButton("Войти этим аккаунтом",accent:true,action:async()=>await SignIn());login.Enabled=false;bar.Controls.Add(login);
            bar.Controls.Add(Theme.MakeButton("Обновить",ghost:true,action:()=>view?.CoreWebView2?.Reload()));
            hint=new Label{Text="Загрузка Telegram Web…",Width=650,Height=38,ForeColor=Theme.TextSecondary};bar.Controls.Add(hint);
            view=new WebView2{Dock=DockStyle.Fill,DefaultBackgroundColor=Theme.Background};Controls.Add(view);Controls.Add(bar);
            Shown+=async(s,e)=>await Initialize();
            FormClosing+=async(s,e)=>{if(!closed){e.Cancel=true;await Shutdown();}};
        }
        async Task Initialize(){
            try{
                if(closed)return;store.Prepare();string root=Path.Combine(store.Root,"web",account.id);Directory.CreateDirectory(root);
                string args="--disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp";
                if(!string.IsNullOrWhiteSpace(account.proxy)){
                    proxy=new TelegramWebProxy();await proxy.Start(store,account.proxy);
                    args+=" --proxy-server=http://127.0.0.1:"+proxy.Port+" --proxy-bypass-list=<-loopback>";
                }
                if(string.IsNullOrWhiteSpace(account.proxy))args+=" --no-proxy-server";
                var environment=await CoreWebView2Environment.CreateAsync(null,root,new CoreWebView2EnvironmentOptions(args));
                if(closed){if(proxy!=null)await proxy.Stop();return;}
                await view.EnsureCoreWebView2Async(environment);
                if(proxy!=null)TelegramWebProxy.BindAuthentication(view.CoreWebView2,proxy.Port,proxy.Secret);
                var settings=view.CoreWebView2.Settings;settings.IsPasswordAutosaveEnabled=false;settings.IsGeneralAutofillEnabled=false;settings.AreDevToolsEnabled=false;
                view.CoreWebView2.NavigationStarting+=(s,e)=>{if(!TrustedOrigin(e.Uri)){e.Cancel=true;hint.Text="В этом окне открывается только официальный Telegram Web";}};
                view.CoreWebView2.NewWindowRequested+=(s,e)=>e.Handled=true;
                view.CoreWebView2.DownloadStarting+=(s,e)=>e.Cancel=true;
                view.CoreWebView2.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
                view.CoreWebView2.NavigationCompleted+=(s,e)=>{login.Enabled=e.IsSuccess;if(!e.IsSuccess)hint.Text="Страница не загрузилась: "+e.WebErrorStatus+". Проверьте прокси";};
                hint.Text="Первый вход: дождитесь QR → «Войти этим аккаунтом». 2FA вводится в веб-окне.";
                view.CoreWebView2.Navigate("https://web.telegram.org/a/");
            }catch(WebView2RuntimeNotFoundException){hint.Text="Нужен Microsoft Edge WebView2 Runtime. Инструкция установки — у агента.";}
            catch(Exception){hint.Text="Не удалось открыть Telegram Web. Проверьте подготовку Telegram и файлы WebView2 из обновления.";if(proxy!=null)await proxy.Stop();}
        }
        async Task SignIn(){
            if(signing)return;signing=true;login.Enabled=false;
            try{
                if(view.CoreWebView2==null||!TrustedOrigin(view.CoreWebView2.Source))throw new InvalidOperationException("Дождитесь официальной страницы Telegram Web");
                string rect=await view.CoreWebView2.ExecuteScriptAsync(LoginRectScript);
                if(rect=="null")throw new InvalidOperationException("QR входа не найден. Возможно, вы уже вошли; иначе выберите вход по QR на странице");
                var r=new JavaScriptSerializer().Deserialize<Dictionary<string,double>>(rect);
                using(var png=new MemoryStream()){
                    await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,png);png.Position=0;
                    using(var bitmap=new Bitmap(png)){
                        double sx=bitmap.Width/r["viewportWidth"],sy=bitmap.Height/r["viewportHeight"];
                        var bounds=Rectangle.Intersect(new Rectangle(0,0,bitmap.Width,bitmap.Height),new Rectangle((int)(r["x"]*sx),(int)(r["y"]*sy),(int)Math.Ceiling(r["width"]*sx),(int)Math.Ceiling(r["height"]*sy)));
                        if(bounds.Width<80||bounds.Height<80)throw new InvalidOperationException("QR входа не виден полностью");
                        string token;using(var qr=bitmap.Clone(bounds,bitmap.PixelFormat))token=DecodeQr(qr);
                        // Re-check login screen and origin immediately before authorization.
                        if(!TrustedOrigin(view.CoreWebView2.Source)||await view.CoreWebView2.ExecuteScriptAsync(LoginRectScript)=="null")throw new InvalidOperationException("Страница изменилась; повторите вход");
                        hint.Text="Подтверждаем новую веб-сессию выбранного аккаунта…";await authorize(token);
                    }
                }
                hint.Text="Вход подтверждён. Дождитесь загрузки; пароль 2FA при необходимости введите на странице.";
            }catch(InvalidOperationException e){hint.Text=e.Message;}
            catch{hint.Text="Вход не подтверждён. Проверьте журнал Telegram; истёкший QR обновите и повторите.";}
            finally{signing=false;if(!closed)login.Enabled=true;}
        }
        public async Task Shutdown(){
            if(closed)return;if(stopping){while(!closed)await Task.Delay(50);return;}stopping=true;
            try{view.Dispose();if(proxy!=null)await proxy.Stop();}finally{closed=true;if(!IsDisposed)Close();}
        }
    }
}
