using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    // One application-wide, non-overlapping read-only batch. No browser/WebView/Android launch.
    internal sealed class DashboardSessionRefresh : IDisposable {
        readonly Preferences settings;
        readonly DolphinCookieSessionProvider cookies;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly System.Windows.Forms.Timer timer;
        readonly Func<CancellationToken,Task<List<DolphinBrowserProfile>>> profiles;
        readonly string path;
        readonly Func<string> token;
        int running,disposed,released;
        internal DashboardSessionRefresh(Preferences settings,DolphinCookieSessionProvider provider=null,
            Func<CancellationToken,Task<List<DolphinBrowserProfile>>> profiles=null,string path=null,Func<string> token=null) {
            this.settings=settings;cookies=provider??new DolphinCookieSessionProvider();this.path=path;
            this.token=token??(()=>WindowsSupport.Unprotect(settings.ProtectedDolphinToken));
            this.profiles=profiles??(ct=>DolphinProfileCatalog.FetchAll(this.token(),cancel:ct));
            timer=new System.Windows.Forms.Timer{Interval=45000};
            timer.Tick+=async(s,e)=>await RefreshAsync();timer.Start();
        }
        internal async Task RefreshAsync() {
            if(Volatile.Read(ref disposed)!=0||Interlocked.CompareExchange(ref running,1,0)!=0)return;
            var ct=lifetime.Token;
            try {
                // Snapshot encrypted settings on the UI thread; network and JSON work never block it.
                string bearer=token();
                await Task.Run(async()=>{
                    if(path==null)foreach(var meta in MetaProfileState.Load().Where(m=>m.InstagramCookie==true)) {
                        ct.ThrowIfCancellationRequested();
                        DashboardSessionState.Save(new DashboardSessionObservation{ProfileId=meta.Id,Service="Instagram",Result="saved",
                            Evidence="saved",Source="webview-cookie",ExpiresUtc=meta.CookieExpiresUtc,CheckedUtc=meta.CheckedUtc,
                            Detail="Сохранена cookie сессии Instagram (WebView2); вход онлайн не проверен."});
                    }
                    if(string.IsNullOrWhiteSpace(bearer))return;
                    var rows=await profiles(ct).ConfigureAwait(false);
                    foreach(var profile in rows.GroupBy(p=>p.Id).Select(g=>g.First())) {
                        ct.ThrowIfCancellationRequested();
                        try {
                            var result=await cookies.ReadAsync(bearer,profile.Id,ct).ConfigureAwait(false);
                            foreach(var service in result) {
                                ct.ThrowIfCancellationRequested();
                                DashboardSessionState.Record(profile.Id,service.Key,service.Value,path);
                            }
                        } catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                        catch(Exception error) {
                            foreach(string service in DolphinCookieSessionProvider.Services)
                                DashboardSessionState.Record(profile.Id,service,new DashboardCheckResult{IsError=true,Evidence="saved",Source="dolphin-cookie",
                                    Detail="Dolphin cookie export: "+error.GetType().Name+"; вход неизвестен."},path);
                            AppDiagnostics.Write("Dashboard",profile.Id,"","session_refresh_error",error.GetType().Name,"error");
                        }
                    }
                },ct).ConfigureAwait(false);
            } catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
            catch(Exception error) {AppDiagnostics.Write("Dashboard","","","session_refresh_error",error.GetType().Name,"error");}
            finally {
                Interlocked.Exchange(ref running,0);
                if(Volatile.Read(ref disposed)!=0)Release();
            }
        }
        void Release(){if(Interlocked.Exchange(ref released,1)==0){cookies.Dispose();lifetime.Dispose();}}
        public void Dispose() {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            timer.Dispose();lifetime.Cancel();
            if(Volatile.Read(ref running)==0)Release();
        }
    }
}
