using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VideoBatch {
    public static class TelegramStatusTests {
        static void Check(bool value,string label){if(!value)throw new Exception(label);}
        public static bool RunSelfTests(){
            var txt=TelegramRecipients.Parse("@UserOne\nhttps://t.me/usertwo\nUSERONE\n000123\n123\nwrong!\n");
            Check(txt.Users.SequenceEqual(new[]{"UserOne","usertwo","123"})&&txt.Invalid==1&&txt.Duplicates==2,"TXT normalization/dedup/invalid");
            var csv=TelegramRecipients.Parse("name;username;user_id\n\"Name; surname\";userone;123\nOther;usertwo;456", ".csv");Check(csv.Users.SequenceEqual(new[]{"userone","usertwo"}),"Quoted CSV and username priority");
            var json=TelegramRecipients.Parse("{\"users\":[{\"user_id\":123},{\"username\":\"@usertwo\"},\"userone\"]}",".json");Check(json.Users.SequenceEqual(new[]{"123","usertwo","userone"}),"JSON recipients");
            bool rejected=false;try{TelegramRecipients.Parse("{\"api_id\":123,\"api_hash\":\"secret\"}",".json");}catch(IOException){rejected=true;}Check(rejected,"Session JSON cannot become invitation list");
            var a=new TelegramAccount{id=new string('a',64),status="network"};var d=new TelegramData();d.accounts.Add(a);
            TelegramHealth.ApplyAccount(d,new Dictionary<string,object>{{"id",a.id},{"status","ready"},{"text","OK"}});string success=a.last_success_utc;
            Check(TelegramHealth.AccountLabel(a).Contains("Работает"),"Only get_me confirmation enables green account");
            TelegramHealth.ApplyAccount(d,new Dictionary<string,object>{{"id",a.id},{"status","network"},{"text","Proxy refused"},{"code","RouteError"}});
            Check(a.last_error=="Proxy refused"&&a.last_success_utc==success&&a.last_attempt_utc!=""&&!TelegramHealth.AccountLabel(a).Contains("Работает"),"Failed attempt does not become successful check");
            a.status="ready";a.last_success_utc=DateTime.UtcNow.AddHours(-1).ToString("o");Check(TelegramHealth.AccountLabel(a).Contains("снова"),"Stale account result is not live status");
            a.wait_until=DateTime.UtcNow.AddMinutes(2).ToString("o");Check(TelegramHealth.AccountLabel(a).Contains("Пауза"),"Successful check cannot erase FloodWait");
            var proxy=TelegramProxyCatalog.Add(d,"socks5://u:p@localhost:1080");TelegramProxyCatalog.Assign(d,new[]{a.id},proxy.id);
            TelegramHealth.ApplyProxy(d,new Dictionary<string,object>{{"proxy_key",TelegramHealth.Key(proxy.url)},{"status","failed"},{"text","No tunnel"}});Check(proxy.status=="failed"&&a.proxy_status=="failed"&&a.status!="blocked","Proxy failure does not imply account ban");
            TelegramHealth.ApplyProxy(d,new Dictionary<string,object>{{"proxy_key",TelegramHealth.Key(proxy.url)},{"status","limited"},{"text","Web only"}});Check(TelegramHealth.ProxyLabel(a).Contains("Частичный доступ")&&proxy.status=="limited","Network health is separate from account authorization");
            a.status="blocked";a.last_error="Telegram ban";TelegramProxyCatalog.Assign(d,new[]{a.id},"");Check(a.status=="blocked"&&a.last_error=="Telegram ban"&&TelegramHealth.AccountLabel(a).Contains("Заблокирован"),"Proxy reassignment cannot erase a confirmed ban");
            Check(TelegramWebWindow.TrustedOrigin("https://web.telegram.org/a/")&&!TelegramWebWindow.TrustedOrigin("https://web.telegram.org.evil.test/a/")&&!TelegramWebWindow.TrustedOrigin("http://web.telegram.org/a/"),"Browser trust boundary");
            string token=new string('a',32);using(var qr=new ZXing.BarcodeWriter{Format=ZXing.BarcodeFormat.QR_CODE,Options=new ZXing.Common.EncodingOptions{Width=300,Height=300,Margin=4}}.Write("tg://login?token="+token))Check(TelegramWebWindow.DecodeQr(qr)==token,"Actual QR decoding, no clipboard injection");
            return true;
        }
        // Real WebView2 engine and Basic proxy authentication, with localhost only.
        // The local proxy never forwards a byte to Telegram or any remote server.
        public static bool RunBrowserTest(){
            Exception failure=null;var thread=new Thread(()=>{
                string root=Path.Combine(Path.GetTempPath(),"TelegramBrowserTest-"+Guid.NewGuid().ToString("N"));
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                var received=new TaskCompletionSource<bool>();var stop=new CancellationTokenSource();
                int port=((IPEndPoint)listener.LocalEndpoint).Port;
                var serving=Task.Run(async()=>{
                    try{while(!stop.IsCancellationRequested){var socket=await listener.AcceptTcpClientAsync();_ = Task.Run(async()=>{using(socket){var stream=socket.GetStream();var buffer=new byte[8192];int size=0;while(size<buffer.Length){int n=await stream.ReadAsync(buffer,size,buffer.Length-size);if(n==0)return;size+=n;if(Encoding.ASCII.GetString(buffer,0,size).Contains("\r\n\r\n"))break;}string request=Encoding.ASCII.GetString(buffer,0,size);bool auth=request.IndexOf("Proxy-Authorization: Basic "+Convert.ToBase64String(Encoding.ASCII.GetBytes("local:fixture-secret")),StringComparison.OrdinalIgnoreCase)>=0;string response=auth?"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n":"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"VideoBatch test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";byte[] bytes=Encoding.ASCII.GetBytes(response);await stream.WriteAsync(bytes,0,bytes.Length);if(auth)received.TrySetResult(true);}});}}catch(Exception e){if(!stop.IsCancellationRequested)received.TrySetException(e);}
                });
                try{
                    Application.EnableVisualStyles();using(var form=new Form{ClientSize=new Size(900,700)})using(var browser=new WebView2{Dock=DockStyle.Fill}){
                        form.Controls.Add(browser);form.Show();bool done=false;
                        form.Shown+=(s,e)=>{};
                        var task=Initialize();
                        async Task Initialize(){
                            try{
                                var env=await CoreWebView2Environment.CreateAsync(null,root,new CoreWebView2EnvironmentOptions("--proxy-server=http://127.0.0.1:"+port+" --proxy-bypass-list=<-loopback> --disable-quic --disable-background-networking"));
                                await browser.EnsureCoreWebView2Async(env);TelegramWebProxy.BindAuthentication(browser.CoreWebView2,port,"fixture-secret");
                                browser.CoreWebView2.Navigate("https://web.telegram.org/a/");
                                if(await Task.WhenAny(received.Task,Task.Delay(20000))!=received.Task)throw new Exception("WebView2 never authenticated to configured local proxy");await received.Task;
                                var loaded=new TaskCompletionSource<bool>();browser.CoreWebView2.NavigationCompleted+=(s,e)=>loaded.TrySetResult(e.IsSuccess);
                                string token=new string('b',32),image;
                                using(var qr=new ZXing.BarcodeWriter{Format=ZXing.BarcodeFormat.QR_CODE,Options=new ZXing.Common.EncodingOptions{Width=300,Height=300,Margin=4}}.Write("tg://login?token="+token))
                                using(var png=new MemoryStream()){qr.Save(png,System.Drawing.Imaging.ImageFormat.Png);image=Convert.ToBase64String(png.ToArray());}
                                browser.CoreWebView2.NavigateToString("<html><body><div id='auth-qr-form'><div class='qr-container' style='width:300px;height:300px;margin:40px'><img width='300' height='300' src='data:image/png;base64,"+image+"'></div></div></body></html>");
                                if(await Task.WhenAny(loaded.Task,Task.Delay(10000))!=loaded.Task)throw new Exception("WebView2 fixture rendering timeout");await Task.Delay(200);
                                string rect=await browser.CoreWebView2.ExecuteScriptAsync(TelegramWebWindow.LoginRectScript);Check(rect!="null"&&rect.Contains("viewportWidth"),"Login-screen-only DOM selector");
                                Check(await TelegramWebWindow.ReadLoginToken(browser.CoreWebView2)==token,"Rendered WebView2 QR capture/crop/decode");
                                Check(await browser.CoreWebView2.ExecuteScriptAsync(TelegramWebWindow.LoginStateScript)=="\"qr\"","QR page is not a signed-in account");
                                await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.qr-container').innerHTML=''");
                                Check(await browser.CoreWebView2.ExecuteScriptAsync(TelegramWebWindow.LoginStateScript)=="\"qr_loading\""&&await browser.CoreWebView2.ExecuteScriptAsync(TelegramWebWindow.LoginRectScript)=="null","Empty QR placeholder never triggers approval");
                                await browser.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML='<div id=Main>Account fixture</div>'");
                                Check(await browser.CoreWebView2.ExecuteScriptAsync(TelegramWebWindow.LoginStateScript)=="\"ready\"","Authorized Web A detection");
                                await browser.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML='<div id=auth-password-form>2FA fixture</div>'");
                                Check(await browser.CoreWebView2.ExecuteScriptAsync(TelegramWebWindow.LoginStateScript)=="\"password\"","2FA is distinguished from ready");
                                using(var png=new MemoryStream()){await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,png);Check(png.Length>1000,"Real WebView2 screenshot");}
                            }catch(Exception e){failure=e;}finally{done=true;form.Close();}
                        }
                        var deadline=DateTime.UtcNow.AddSeconds(60);while(!done&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(20);}if(!done)throw new Exception("WebView2 test deadline");
                    }
                }catch(Exception e){failure=e;}
                finally{stop.Cancel();listener.Stop();try{if(Directory.Exists(root))Directory.Delete(root,true);}catch{}}
            });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw new Exception("Telegram browser integration",failure);return true;
        }
        public static bool RunWebLoginFlowTest(string runtime,string sourceRoot){
            Exception failure=null;var thread=new Thread(()=>{
                Process fixture=null;Task<string> errors=null,trace=null;TelegramWebWindow window=null;
                X509Store trust=null;X509Certificate2 testRoot=null;bool addedRoot=false;
                try{
                    // Only in the disposable CI runner. Trust the fixture root for
                    // this test and remove it in finally; production TLS stays intact.
                    if(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true")throw new InvalidOperationException("Local TLS fixture runs only on CI");
                    string pem=File.ReadAllText(Path.Combine(sourceRoot,"tests","telegram","fixtures","web-test-root.pem"));
                    testRoot=new X509Certificate2(Convert.FromBase64String(pem.Replace("-----BEGIN CERTIFICATE-----","").Replace("-----END CERTIFICATE-----","")));
                    trust=new X509Store(StoreName.Root,StoreLocation.CurrentUser);trust.Open(OpenFlags.ReadWrite);
                    if(trust.Certificates.Find(X509FindType.FindByThumbprint,testRoot.Thumbprint,false).Count==0){trust.Add(testRoot);addedRoot=true;}
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    var job=Execute();var deadline=DateTime.UtcNow.AddSeconds(100);
                    while(!job.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
                    if(!job.IsCompleted)throw new Exception("Native Web login flow deadline");job.GetAwaiter().GetResult();
                    async Task Execute(){
                        string image;using(var qr=new ZXing.BarcodeWriter{Format=ZXing.BarcodeFormat.QR_CODE,Options=new ZXing.Common.EncodingOptions{Width=300,Height=300,Margin=4}}.Write("tg://login?token="+new string('b',32)))
                        using(var png=new MemoryStream()){qr.Save(png,System.Drawing.Imaging.ImageFormat.Png);image=Convert.ToBase64String(png.ToArray());}
                        var info=new ProcessStartInfo(Path.Combine(runtime,"venv","Scripts","python.exe"),Core.Quote(Path.Combine(sourceRoot,"tests","telegram","web_fixture.py"))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                        fixture=Process.Start(info);errors=fixture.StandardError.ReadToEndAsync();
                        var input=new StreamWriter(fixture.StandardInput.BaseStream,new UTF8Encoding(false)){AutoFlush=true};
                        await input.WriteLineAsync(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{image}));
                        var first=fixture.StandardOutput.ReadLineAsync();if(await Task.WhenAny(first,Task.Delay(10000))!=first)throw new Exception("TLS fixture startup");
                        var started=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(await first);
                        trace=fixture.StandardOutput.ReadToEndAsync();
                        int port=Convert.ToInt32(started["port"]);
                        foreach(bool failFirst in new[]{false,true}){
                            var store=new TelegramStore(runtime);int approvals=0;bool shownMain=false;
                            var account=new TelegramAccount{id=Guid.NewGuid().ToString("N"),name="Web fixture",proxy="socks5://fixture:local-test-password@127.0.0.1:"+port};
                            window=new TelegramWebWindow(store,account,async()=>{
                                approvals++;
                                string token=await window.CurrentLoginToken("");Check(token==new string('b',32),"Fresh rendered login QR only");
                                if(failFirst&&approvals==1)throw new IOException("Synthetic approval failure");
                                var field=typeof(TelegramWebWindow).GetField("view",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                                var native=(WebView2)field.GetValue(window);await native.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML='<div id=Main>Signed-in fixture</div>'");shownMain=true;
                            });
                            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                            var view=(WebView2)typeof(TelegramWebWindow).GetField("view",flags).GetValue(window);
                            window.Show();
                            async Task Wait(Func<bool> done,string label){var until=DateTime.UtcNow.AddSeconds(25);while(!done()&&DateTime.UtcNow<until)await Task.Delay(50);
                                if(!done()){
                                    Console.WriteLine("Web hint: "+((Label)typeof(TelegramWebWindow).GetField("hint",flags).GetValue(window)).Text);
                                    Console.WriteLine("Fixture DOM: "+await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({title:document.title,body:document.body.innerText.slice(0,160),workerError:document.body.dataset.workerError,websocketError:document.body.dataset.websocketError,images:document.querySelectorAll('.qr-container img').length})"));
                                    string reportPath=Path.Combine(runtime,"diagnostics","doctor-web-latest.json");if(File.Exists(reportPath))Console.WriteLine("Web report: "+File.ReadAllText(reportPath));
                                }Check(done(),label);}
                            if(failFirst){
                                await Wait(()=>approvals==1&&Convert.ToBoolean(typeof(TelegramWebWindow).GetField("loginFailed",flags).GetValue(window)),"Approval failure becomes actionable");
                                await (Task)typeof(TelegramWebWindow).GetMethod("Reload",flags).Invoke(window,new object[]{false});
                            }
                            await Wait(()=>shownMain,"Worker WSS -> rendered QR -> automatic account approval");
                            await Wait(()=>!Convert.ToBoolean(typeof(TelegramWebWindow).GetField("signing",flags).GetValue(window)),"Approval completed");
                            await (Task)typeof(TelegramWebWindow).GetMethod("PollLogin",flags).Invoke(window,null);
                            Check(approvals==(failFirst?2:1),"Exactly one approval per page, refresh permits a new attempt");
                            string report=File.ReadAllText(Path.Combine(runtime,"diagnostics","doctor-web-latest.json"));
                            Check(report.Contains("WebSessionConfirmed")&&report.Contains("zws2.web.telegram.org"),"Native worker's actual route recorded");
                            Check(!report.Contains("local-test-password")&&!report.Contains(new string('b',32)),"No proxy password or QR token in report");
                            await window.Shutdown();window.Dispose();window=null;
                        }
                        await input.WriteLineAsync("cancel");
                        if(!await Task.Run(()=>fixture.WaitForExit(5000)))fixture.Kill();await errors;
                    }
                }catch(Exception e){failure=e;Console.WriteLine("Web fixture failure: "+e);if(fixture!=null&&fixture.HasExited&&errors!=null&&errors.IsCompleted)Console.WriteLine("Fixture stderr: "+errors.GetAwaiter().GetResult());}
                finally{if(window!=null)window.Dispose();try{if(fixture!=null&&!fixture.HasExited){fixture.Kill();fixture.WaitForExit(3000);}if(failure!=null){if(trace!=null&&trace.IsCompleted)Console.WriteLine(trace.GetAwaiter().GetResult());if(errors!=null&&errors.IsCompleted)Console.WriteLine(errors.GetAwaiter().GetResult());}}catch{}fixture?.Dispose();if(addedRoot)trust.Remove(testRoot);trust?.Close();testRoot?.Dispose();}
            });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw new Exception("Native Telegram Web login flow",failure);return true;
        }
        public static bool RunShellLayoutTest(string output){
            Exception failure=null;var thread=new Thread(()=>{
                try{
                    Console.WriteLine("Shell layout: constructing");
                    using(var shell=new AppShell(new Preferences{YouTubeChannels=new System.Collections.Generic.List<YouTubeChannel>{new YouTubeChannel{Name="Sample RU",ProfileId="100001",Market="RU",Kind="shorts"}},TikTokAccounts=new System.Collections.Generic.List<TikTokAccount>{new TikTokAccount{Name="Sample EN",ProfileId="200001",Market="EN"}}})){
                        Console.WriteLine("Shell layout: showing");
                        shell.TopLevel=false;shell.FormBorderStyle=FormBorderStyle.None;
                        using(var host=new Form{ClientSize=new Size(900,700)}){
                        host.Controls.Add(shell);host.Show();shell.Show();Application.DoEvents();
                        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                        var type=typeof(AppShell);Directory.CreateDirectory(output);
                        foreach(var platform in new[]{AppPlatform.Telegram,AppPlatform.YouTube}){
                            Console.WriteLine("Shell layout: platform "+platform);
                            type.GetMethod("ApplyPlatform",flags).Invoke(shell,new object[]{platform,true});Application.DoEvents();
                            foreach(int width in new[]{1100,1280}){
                                Console.WriteLine("Shell layout: width "+width);
                                shell.ClientSize=new Size(width,820);shell.PerformLayout();Application.DoEvents();
                                Check(shell.ClientSize.Width==width&&shell.ClientSize.Height==820,"Requested shell render size is not silently clamped");
                                var bar=(Control)type.GetField("topBar",flags).GetValue(shell);
                                foreach(string field in new[]{"settingsBtn","platformBackBtn","menuToggleBtn","pageTitleLabel","tasksLabel"}){
                                    var c=(Control)type.GetField(field,flags).GetValue(shell);if(!c.Visible)continue;
                                    var bounds=bar.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                                    Check(bounds.Top>=0&&bounds.Bottom<=bar.ClientSize.Height&&bounds.Left>=0&&bounds.Right<=bar.ClientSize.Width,"Unclipped shell control: "+field+" @"+width);
                                }
                                Console.WriteLine("Shell layout: capture "+platform+" "+width);
                                var render=(Control)type.GetField("shellRoot",flags).GetValue(shell);
                                Check(render.Width==width&&render.Height==820,"Workspace layout matches requested viewport");
                                using(var bitmap=new Bitmap(render.Width,render.Height)){render.DrawToBitmap(bitmap,render.ClientRectangle);bitmap.Save(Path.Combine(output,"shell-"+platform+"-"+width+".png"));}
                            }
                        }
                        type.GetMethod("ApplyPlatform",flags).Invoke(shell,new object[]{AppPlatform.YouTube,true});
                        type.GetMethod("ShowSection",flags).Invoke(shell,new object[]{NavSection.Profiles});Application.DoEvents();
                        var profiles=(Control)type.GetField("profilesPanel",flags).GetValue(shell);
                        var grid=(DataGridView)typeof(ProfilesPanel).GetField("grid",flags).GetValue(profiles);
                        Check(grid.Rows.Count==2,"Profiles come from existing YouTube and TikTok accounts without re-import");
                        Check(grid.Height>200,"Profiles list remains visible between filters and actions");
                        var profileRender=(Control)type.GetField("shellRoot",flags).GetValue(shell);
                        using(var bitmap=new Bitmap(profileRender.Width,profileRender.Height)){profileRender.DrawToBitmap(bitmap,profileRender.ClientRectangle);bitmap.Save(Path.Combine(output,"shell-profiles.png"));}
                        shell.Close();host.Close();
                        }
                    }
                }catch(Exception e){failure=e;}
            });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw new Exception("Shared workspace layout",failure);return true;
        }

    }
}
