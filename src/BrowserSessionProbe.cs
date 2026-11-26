using System;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.WinForms;

namespace VideoBatch {
    internal static class BrowserSessionProbe {
        internal static void Observe(WebView2 view,string profileId,Action<string> hint) {
            var timer=new System.Windows.Forms.Timer{Interval=2000};
            bool reading=false;string lastService=null;
            timer.Tick+=async(s,e)=>{
                if(reading||view.IsDisposed||view.CoreWebView2==null)return;
                reading=true;
                try {
                    string source=view.CoreWebView2.Source;
                    string service=ServiceFor(source);
                    if(service==null)return;
                    lastService=service;
                    var result=await ReadOnceAsync(view,service);
                    if(result.Authorized==true||result.Authorized==false)
                        DashboardSessionState.Record(profileId,service,result);
                    hint?.Invoke(service+": "+(result.Authorized==true?"вход подтверждён · "+result.AccountName:result.Authorized==false?"вход не выполнен":"состояние входа неизвестно"));
                } catch(Exception error) {
                    if(!view.IsDisposed&&lastService!=null) {
                        DashboardSessionState.Record(profileId,lastService,Unknown("Не удалось прочитать состояние страницы.",true));
                        AppDiagnostics.Write(lastService,profileId,"","session_check_error",error.GetType().Name,"error");
                    }
                } finally {reading=false;}
            };
            view.Disposed+=(s,e)=>timer.Dispose();timer.Start();
        }
        internal static string ServiceFor(string source) {
            if(!Uri.TryCreate(source,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Port!=443)return null;
            if(uri.Host=="accountscenter.instagram.com")return null;
            foreach(string service in new[]{"TikTok","Instagram","Threads"})if(SocialFeedWarmup.OwnOrigin(service,source))return service;
            return null;
        }
        internal static async Task<DashboardCheckResult> ReadOnceAsync(WebView2 view,string service) {
            if(view.IsDisposed||view.CoreWebView2==null||ServiceFor(view.CoreWebView2.Source)!=service)return Unknown("Страница выбранной соцсети не готова.");
            string source=view.CoreWebView2.Source;
            var result=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(await view.CoreWebView2.ExecuteScriptAsync(Script(service)));
            if(view.IsDisposed||view.CoreWebView2.Source!=source)return Unknown("Страница меняется; вход не подтверждён.");
            string state=Convert.ToString(result["state"]);
            if(state=="signed_out")return new DashboardCheckResult{Authorized=false,Detail="WebView2: отображается страница входа."};
            if(state=="signed_in")return new DashboardCheckResult{Authorized=true,AccountName=Convert.ToString(result["identity"]),Detail="WebView2: подтверждены собственный профиль и элементы авторизованного аккаунта."};
            return Unknown(state=="manual"?"Требуется ручная проверка; работа остановлена.":"Загрузка или состояние входа не определено.");
        }
        // Read rendered UI only: no private API calls, credentials, form submission or cookie inference.
        internal static string Script(string service) => @"(() => {
const service=" + new JavaScriptSerializer().Serialize(service) + @";
const visible=e=>!!e&&!!(e.offsetWidth||e.offsetHeight||e.getClientRects().length)&&getComputedStyle(e).visibility!=='hidden';
const nodes=s=>Array.from(document.querySelectorAll(s)).filter(visible);
const text=(document.body?.innerText||'').slice(0,60000);
const manual=/captcha|challenge|checkpoint|two_factor|consent/i.test(location.pathname)||nodes('iframe[src*=""captcha""],input[autocomplete=""one-time-code""]').length>0||/confirm it's you|verify it's you|подтвердите, что это вы|подтвердите свою личность|проверка безопасности/i.test(text);
if(manual)return {state:'manual'};
if(document.readyState==='loading')return {state:'pending'};
const password=nodes('input[type=""password""]').length>0;
const loginPath=/\/(accounts\/login|login|signup)(\/|$)/i.test(location.pathname);
const loginHeading=nodes('h1,h2,[role=""heading""]').some(e=>/^(log in|sign in|log in to tiktok|вход|войти|вход в tiktok)(\s|$)/i.test(e.innerText.trim()));
const loginControls=nodes('input,button,[role=""button""]').some(e=>/email|username|phone|continue with|почт|телефон|логин|продолжить/i.test([e.innerText,e.getAttribute('placeholder'),e.getAttribute('name')].join(' ')));
if(password||(loginPath&&nodes('form').length>0&&/log in|sign in|войти|вход/i.test(text))||(loginHeading&&loginControls))return {state:'signed_out'};
const label=e=>[e.getAttribute('aria-label'),e.getAttribute('title'),e.innerText,...Array.from(e.querySelectorAll('svg[aria-label],img[alt]')).map(n=>n.getAttribute('aria-label')||n.getAttribute('alt'))].filter(Boolean).join(' ').trim();
const own=nodes('a[href]').filter(e=>/^(profile|профиль|your profile|мой профиль)(\s|$)/i.test(label(e)));
const account=own.map(e=>{const u=new URL(e.href,location.href);return {path:u.pathname,host:u.hostname}}).find(u=>u.host===location.hostname&&(service==='Instagram'?/^\/[A-Za-z0-9._]+\/?$/.test(u.path):/^\/@[A-Za-z0-9._]+\/?$/.test(u.path)));
const privateNav=service==='Instagram'?nodes('a[href^=""/direct/inbox""]').length>0:service==='Threads'?nodes('a[href=""/activity""],a[href=""/activity/""]').length>0:nodes('[data-e2e=""profile-icon""]').length>0;
const accountControl=nodes('button,a,[role=""button""],[role=""menuitem""]').some(e=>/^(log out|logout|выйти|выход|edit profile|редактировать профиль)(\s|$)/i.test(label(e)));
if(account&&(privateNav||accountControl))return {state:'signed_in',identity:account.path.replace(/^\/@?/, '').replace(/\/$/,'')};
return {state:'pending'};
})()";

        internal static async Task<DashboardCheckResult> ReadAsync(WebView2 view, string service, CancellationToken ct) {
            if(view.IsDisposed||view.CoreWebView2==null)return Unknown("Браузерная сессия не инициализирована; проверьте журнал сервиса.",true);
            var json=new JavaScriptSerializer();
            DateTime deadline=DateTime.UtcNow.AddSeconds(35);
            while(DateTime.UtcNow<deadline) {
                ct.ThrowIfCancellationRequested();
                if(view.IsDisposed)return Unknown("Браузерная проверка прервана.",true);
                string source=view.CoreWebView2.Source;
                bool trusted=service=="TikTok"?TikTokWebWindow.TrustedOrigin(source):InstagramWebWindow.TrustedOrigin(source);
                if(trusted) {
                    var result=json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(await view.CoreWebView2.ExecuteScriptAsync(Script(service)));
                    string state=Convert.ToString(result["state"]);
                    if(state=="manual")return Unknown("Сайт требует ручную проверку, согласие или CAPTCHA. Автоматизация этого профиля остановлена.");
                    if(state=="signed_out")return new DashboardCheckResult{Authorized=false,Detail="Сайт показал форму/страницу входа в сохранённой WebView2-сессии. Сессия Dolphin отдельно не проверялась."};
                    if(state=="signed_in")return new DashboardCheckResult{Authorized=true,AccountName=Convert.ToString(result["identity"]),Detail="WebView2: сайт отобразил навигацию собственного профиля и элементы авторизованного аккаунта. Проверена локальная сессия приложения, не Dolphin."};
                }
                await Task.Delay(500,ct);
            }
            return Unknown("За 35 секунд сайт не показал однозначное состояние входа. Возможны сеть, загрузка или изменившийся интерфейс; выход не подтверждён.",true);
        }
        static DashboardCheckResult Unknown(string detail,bool error=false)=>new DashboardCheckResult{Detail=detail,IsError=error};
    }
}
