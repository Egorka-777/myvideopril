using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    // Cookie values and the bearer token are transient only. Return allowlisted names/expiry, never raw payloads.
    internal sealed class DolphinCookieSessionProvider : IDisposable {
        internal static readonly string[] Services={"YouTube","TikTok","Instagram","Threads"};
        readonly HttpClient client;
        internal DolphinCookieSessionProvider(HttpMessageHandler handler=null) {
            client=handler==null?new HttpClient():new HttpClient(handler,false);
            client.Timeout=TimeSpan.FromSeconds(25);
        }
        internal async Task<Dictionary<string,DashboardCheckResult>> ReadAsync(string token,string profileId,CancellationToken ct) {
            if(!long.TryParse(profileId,NumberStyles.None,CultureInfo.InvariantCulture,out var id)||id<=0)
                return Unknown("Некорректный Dolphin ID.",true);
            if(string.IsNullOrWhiteSpace(token))return Unknown("Dolphin token не настроен; вход неизвестен.",true);
            using(var request=new HttpRequestMessage(HttpMethod.Post,"https://darkwing.dolphin-anty-api.com/api/v1/cookies/export")) {
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                request.Content=new StringContent(new JavaScriptSerializer().Serialize(new{browserProfileId=id}),Encoding.UTF8,"application/json");
                using(var response=await client.SendAsync(request,ct).ConfigureAwait(false)) {
                    if(!response.IsSuccessStatusCode) {
                        int status=(int)response.StatusCode;
                        string detail=status==400?"Cloud sync выключен":status==402?"Тариф не поддерживает export":status==401||status==403?"Нет доступа к cookie export":"Cookie export недоступен";
                        return Unknown("Dolphin HTTP "+status+": "+detail+"; состояние входа неизвестно.",true);
                    }
                    string body=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return Parse(body,DateTime.UtcNow);
                }
            }
        }
        internal static Dictionary<string,DashboardCheckResult> Parse(string body,DateTime now) {
            var root=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(body);
            if(root==null||!root.TryGetValue("success",out var success)||!(success is bool)||!(bool)success||
                !root.TryGetValue("cookies",out var raw)||!(raw is IList))
                return Unknown("Dolphin не подтвердил cookie export; вход неизвестен.",true);
            var cookies=((IList)raw).OfType<Dictionary<string,object>>().ToList();
            var result=new Dictionary<string,DashboardCheckResult>();
            foreach(string service in Services) {
                var names=new HashSet<string>();DateTime? expires=null;bool sessionCookie=false;
                foreach(var cookie in cookies) {
                    string name=Text(cookie,"name"),domain=Text(cookie,"domain").TrimStart('.').ToLowerInvariant();
                    if(!AuthCookie(service,name,domain)||string.IsNullOrEmpty(Text(cookie,"value")))continue;
                    DateTime? expiry=null;
                    bool session=cookie.TryGetValue("session",out var sessionValue)&&sessionValue is bool&&(bool)sessionValue;
                    if(!session) {
                        if(!cookie.TryGetValue("expirationDate",out var expiration))continue;
                        if(!double.TryParse(Convert.ToString(expiration,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out var seconds)||double.IsNaN(seconds)||double.IsInfinity(seconds))continue;
                        if(seconds<=0)continue;
                        try{expiry=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(seconds);}catch(ArgumentOutOfRangeException){continue;}
                        if(expiry<=now)continue;
                    }
                    names.Add(name);
                    if(!expiry.HasValue)sessionCookie=true;
                    else if(!expires.HasValue||expiry.Value>expires.Value)expires=expiry;
                }
                bool present=names.Count>0;
                result[service]=new DashboardCheckResult{Authorized=present?(bool?)true:null,Evidence="saved",Source="dolphin-cookie",
                    ExpiresUtc=sessionCookie?null:expires,
                    Detail=present?"Dolphin: сохранены auth cookie ("+string.Join(", ",names.OrderBy(n=>n))+"); вход онлайн не проверен.":"Dolphin: действующие auth cookie не найдены; выход онлайн не подтверждён."};
            }
            return result;
        }
        static bool Domain(string host,string domain)=>host==domain||host.EndsWith("."+domain,StringComparison.Ordinal);
        static bool AuthCookie(string service,string name,string domain) {
            switch(service) {
                case "YouTube":return (Domain(domain,"google.com")||Domain(domain,"youtube.com"))&&
                    (name=="SID"||name=="__Secure-1PSID"||name=="__Secure-3PSID");
                case "TikTok":return Domain(domain,"tiktok.com")&&(name=="sessionid"||name=="sessionid_ss");
                case "Instagram":return Domain(domain,"instagram.com")&&name=="sessionid";
                case "Threads":return (Domain(domain,"threads.net")||Domain(domain,"threads.com"))&&name=="sessionid";
                default:return false;
            }
        }
        static string Text(Dictionary<string,object> row,string key)=>row.TryGetValue(key,out var value)?Convert.ToString(value,CultureInfo.InvariantCulture):"";
        static Dictionary<string,DashboardCheckResult> Unknown(string detail,bool error)=>Services.ToDictionary(s=>s,s=>new DashboardCheckResult{Evidence="saved",Source="dolphin-cookie",Detail=detail,IsError=error});
        public void Dispose()=>client.Dispose();
    }
}
