using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class DashboardSessionTests {
        static void Check(bool value,string message){if(!value)throw new Exception(message);}
        sealed class Fixture : HttpMessageHandler {
            internal int Calls;internal HttpStatusCode Status=HttpStatusCode.OK;
            internal string Body="{\"success\":true,\"cookies\":[]}";
            internal bool Block;
            internal TaskCompletionSource<bool> Entered=new TaskCompletionSource<bool>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
                Interlocked.Increment(ref Calls);
                Check(request.Method==HttpMethod.Post&&request.RequestUri.AbsoluteUri=="https://darkwing.dolphin-anty-api.com/api/v1/cookies/export","documented endpoint only");
                Check(request.Headers.Authorization.Scheme=="Bearer"&&request.Headers.Authorization.Parameter=="fixture-token","bearer header");
                var json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());
                Check(Convert.ToInt64(json["browserProfileId"])==12345&&json.Count==1,"numeric profile id payload");
                Entered.TrySetResult(true);
                if(Block)await Task.Delay(Timeout.Infinite,ct);
                return new HttpResponseMessage(Status){Content=new StringContent(Body)};
            }
        }
        static string Body(params object[] cookies)=>new JavaScriptSerializer().Serialize(new{success=true,cookies});
        static object Cookie(string name,string domain,double expires=4102444800,bool session=false)=>new{name,domain,value="fixture-secret-value",expirationDate=expires,session};
        public static bool RunSelfTests() {
            var context=SynchronizationContext.Current;
            try{DashboardRendering();}finally{SynchronizationContext.SetSynchronizationContext(context);}
            Task.Run(() => Run()).GetAwaiter().GetResult();return true;
        }
        static void DashboardRendering() {
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            using(var dashboard=new SocialDashboard(new Preferences())) {
                var type=typeof(SocialDashboard);
                type.GetField("loaded",flags).SetValue(dashboard,true);
                type.GetField("profiles",flags).SetValue(dashboard,new List<DolphinBrowserProfile>{new DolphinBrowserProfile{Id="12345",Name="Saved fixture"}});
                var saved=new DashboardSessionObservation{ProfileId="12345",Service="YouTube",Result="saved",Evidence="saved",CheckedUtc=DateTime.UtcNow,ExpiresUtc=DateTime.UtcNow.AddHours(1)};
                type.GetField("sessionObservations",flags).SetValue(dashboard,new List<DashboardSessionObservation>{saved});
                var render=type.GetMethod("Render",flags);
                render.Invoke(dashboard,new object[]{new List<MetaProfileObservation>(),new TelegramData()});
                var grid=(System.Windows.Forms.DataGridView)type.GetField("grid",flags).GetValue(dashboard);
                var cell=grid.Rows[0].Cells[2];
                Check(Convert.ToString(cell.Value)=="✓"&&cell.Style.ForeColor==Theme.Success,"saved dashboard cookie must render green");
                Check(cell.ToolTipText.Contains("онлайн не проверена"),"saved dashboard tooltip must not claim online verification");
                saved.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);
                render.Invoke(dashboard,new object[]{new List<MetaProfileObservation>(),new TelegramData()});
                cell=grid.Rows[0].Cells[2];
                Check(Convert.ToString(cell.Value)=="?"&&cell.Style.ForeColor!=Theme.Success&&cell.ToolTipText.Contains("истёк"),"expired dashboard cookie must not render green");
                saved.ExpiresUtc=null;saved.Result="verified";saved.Evidence="online";
                render.Invoke(dashboard,new object[]{new List<MetaProfileObservation>(),new TelegramData()});
                Check(grid.Rows[0].Cells[2].ToolTipText.Contains("подтверждён онлайн"),"dashboard online tooltip");
            }
        }
        static async Task Run() {
            var now=DateTime.UtcNow;
            string valid=Body(Cookie("SID",".google.com"),Cookie("sessionid_ss","www.tiktok.com"),Cookie("sessionid",".instagram.com"));
            using(var fixture=new Fixture{Body=valid})using(var provider=new DolphinCookieSessionProvider(fixture)) {
                var results=await provider.ReadAsync("fixture-token","12345",CancellationToken.None);
                Check(results["YouTube"].Authorized==true&&results["TikTok"].Authorized==true&&results["Instagram"].Authorized==true,"auth cookie presence");
                Check(results["Threads"].Authorized==null,"Instagram must not authenticate Threads");
                Check(results.Values.All(r=>r.Evidence=="saved"&&!r.Detail.Contains("fixture-secret")),"saved-only, sanitized results");
                foreach(var status in new[]{400,401,402,403,500}) {
                    fixture.Status=(HttpStatusCode)status;int before=fixture.Calls;
                    results=await provider.ReadAsync("fixture-token","12345",CancellationToken.None);
                    Check(results.Values.All(r=>r.Authorized==null&&r.IsError),"HTTP failures unknown, not logout");
                    Check(fixture.Calls==before+1,"no retries or browser fallback");
                }
                fixture.Status=HttpStatusCode.OK;fixture.Body="{\"success\":false}";
                Check((await provider.ReadAsync("fixture-token","12345",CancellationToken.None))["YouTube"].IsError,"invalid success payload");
            }
            var parsed=DolphinCookieSessionProvider.Parse(Body(Cookie("SID","evilgoogle.com"),Cookie("sessionid","instagram.com.evil.test"),Cookie("csrftoken","threads.net"),Cookie("sessionid","tiktok.com",1)),now);
            Check(parsed.Values.All(r=>r.Authorized==null),"wrong domain, arbitrary and expired cookies rejected");
            parsed=DolphinCookieSessionProvider.Parse(Body(Cookie("__Secure-3PSID","youtube.com",1,true),Cookie("sessionid","www.threads.com")),now);
            Check(parsed["YouTube"].Authorized==true&&!parsed["YouTube"].ExpiresUtc.HasValue&&parsed["Threads"].Authorized==true,"session cookie and own Threads domain");
            string folder=Path.Combine(Path.GetTempPath(),"DashboardSessionTests-"+Guid.NewGuid().ToString("N"));
            string path=Path.Combine(folder,"sessions.json");
            try {
                DashboardSessionState.Record("12345","YouTube",new DashboardCheckResult{Authorized=true,Evidence="saved",Source="dolphin-cookie",ExpiresUtc=now.AddHours(1)},path);
                Check(DashboardSessionState.IsConnected("12345","YouTube",path)&&!DashboardSessionState.IsConnected("12345","Threads",path),"service-scoped connected API");
                Check(DashboardSessionState.Summary("12345","YouTube",path).Contains("онлайн не проверена"),"saved tooltip");
                Check(!DashboardSessionState.IsVerified("12345","YouTube",path),"saved evidence is not online verified");
                DashboardSessionState.Record("12345","YouTube",new DashboardCheckResult{IsError=true,Detail="HTTP 402"},path);
                Check(DashboardSessionState.IsConnected("12345","YouTube",path)&&DashboardSessionState.Summary("12345","YouTube",path).Contains("HTTP 402"),"error preserves positive and reports last attempt");
                DashboardSessionState.Record("12345","YouTube",new DashboardCheckResult{Authorized=true,Evidence="saved",ExpiresUtc=now.AddSeconds(-1)},path);
                Check(!DashboardSessionState.IsConnected("12345","YouTube",path),"persisted positive expiry");
                DashboardSessionState.Record("12345","YouTube",new DashboardCheckResult{Authorized=true,Source="webview"},path);
                Check(DashboardSessionState.Summary("12345","YouTube",path).Contains("подтверждён онлайн"),"online tooltip");
                Check(DashboardSessionState.IsVerified("12345","YouTube",path),"online evidence is verified");
                DashboardSessionState.Record("12345","YouTube",new DashboardCheckResult{Authorized=true,Evidence="saved",Source="dolphin-cookie"},path);
                Check(DashboardSessionState.Summary("12345","YouTube",path).Contains("подтверждён онлайн"),"saved refresh must not relabel existing online evidence");
                DashboardSessionState.Record("12345","YouTube",new DashboardCheckResult{Authorized=false,Source="webview"},path);
                Check(!DashboardSessionState.IsConnected("12345","YouTube",path),"explicit online logout");
                File.WriteAllText(path,"[{\"ProfileId\":\"12345\",\"Service\":\"YouTube\",\"Result\":\"verified\"}]");
                Check(DashboardSessionState.IsConnected("12345","YouTube",path)&&DashboardSessionState.Summary("12345","YouTube",path).Contains("онлайн не проверена"),"legacy confirmed records treated as saved, not new online checks");
                using(var fixture=new Fixture{Body=valid})using(var provider=new DolphinCookieSessionProvider(fixture))
                using(var refresher=new DashboardSessionRefresh(new Preferences(),provider,ct=>Task.FromResult(new List<DolphinBrowserProfile>{new DolphinBrowserProfile{Id="12345"}}),path,()=>"fixture-token")) {
                    await refresher.RefreshAsync();Check(fixture.Calls==1,"one export for four services");
                }
                using(var fixture=new Fixture{Block=true})using(var provider=new DolphinCookieSessionProvider(fixture)) {
                    var refresher=new DashboardSessionRefresh(new Preferences(),provider,ct=>Task.FromResult(new List<DolphinBrowserProfile>{new DolphinBrowserProfile{Id="12345"}}),path,()=>"fixture-token");
                    var first=refresher.RefreshAsync();await fixture.Entered.Task;
                    await refresher.RefreshAsync();Check(fixture.Calls==1,"overlapping batches skipped");
                    refresher.Dispose();await first;Check(fixture.Calls==1,"dispose cancels HTTP without retries");
                }
                Check(!File.ReadAllText(path).Contains("fixture-secret-value"),"no persisted cookie values");
            } finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
        }
    }
}
