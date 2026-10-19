using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VideoBatch {
    public static class TelegramHealth {
        static string S(Dictionary<string,object> e,string k)=>e.TryGetValue(k,out var v)?Convert.ToString(v):"";
        public static string Key(string url){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes((url??"").Trim()))).Replace("-","").ToLowerInvariant();}
        public static void RouteChanged(TelegramAccount a){
            a.proxy_status="unchecked";a.proxy_detail="";a.proxy_checked_at="";
            // A different network route is not evidence of unblocking or restoring a revoked session.
            if(!new[]{"blocked","deactivated","unauthorized"}.Contains(a.status)){a.status="unchecked";a.last_error="";a.last_code="";}
        }
        public static string Dot(string state)=>state=="ready"?"●":state=="unchecked"||state=="direct"?"○":"●";
        public static string AccountLabel(TelegramAccount a){
            if(new[]{"ready","flood_wait","restricted"}.Contains(a.status)&&DateTime.TryParse(a.wait_until,null,System.Globalization.DateTimeStyles.RoundtripKind,out var until)&&until.ToUniversalTime()>DateTime.UtcNow)return "● Пауза Telegram";
            if(a.status=="ready"&&string.IsNullOrEmpty(a.last_success_utc))return "○ Проверить снова";
            if(a.status=="ready"&&DateTime.TryParse(a.last_success_utc,null,System.Globalization.DateTimeStyles.RoundtripKind,out var success)&&DateTime.UtcNow-success.ToUniversalTime()>TimeSpan.FromMinutes(15))return "○ Проверить снова";
            return Dot(a.status)+" "+TelegramStore.Status(a.status);
        }
        public static string ProxyLabel(TelegramAccount a)=>string.IsNullOrWhiteSpace(a.proxy)?"○ Без прокси":Dot(a.proxy_status)+" "+(a.proxy_status=="ready"?"Доступен":a.proxy_status=="failed"?"Ошибка сети":new[]{"limited","partial"}.Contains(a.proxy_status)?"Частичный доступ":"Не проверен");
        public static void ApplyAccount(TelegramData data,Dictionary<string,object> e){
            var a=data.accounts.FirstOrDefault(x=>x.id==S(e,"id"));if(a==null)return;
            a.status=S(e,"status");a.checked_at=DateTime.Now.ToString("dd.MM HH:mm");a.last_attempt_utc=DateTime.UtcNow.ToString("o");a.last_error=a.status=="ready"?"":S(e,"text");a.last_code=S(e,"code");
            if(a.status=="ready")a.last_success_utc=DateTime.UtcNow.ToString("o");
            if(e.ContainsKey("name"))a.name=S(e,"name");if(e.ContainsKey("phone"))a.phone=S(e,"phone");if(e.ContainsKey("username"))a.username=S(e,"username");if(e.ContainsKey("user_id"))a.user_id=Convert.ToInt64(e["user_id"]);
            if(e.TryGetValue("seconds",out var seconds)&&Convert.ToInt32(seconds)>0)a.wait_until=DateTime.UtcNow.AddSeconds(Convert.ToInt32(seconds)).ToString("o");
        }
        public static void ApplyProxy(TelegramData data,Dictionary<string,object> e){
            string key=S(e,"proxy_key"),state=S(e,"status"),detail=S(e,"text"),now=DateTime.UtcNow.ToString("o");int latency=e.TryGetValue("latency_ms",out var ms)?Convert.ToInt32(ms):0;
            foreach(var p in data.proxies.Where(p=>Key(p.url)==key)){p.status=state;p.detail=detail;p.checked_at=now;p.latency_ms=latency;}
            foreach(var a in data.accounts.Where(a=>Key(a.proxy)==key)){a.proxy_status=state;a.proxy_detail=detail;a.proxy_checked_at=now;}
        }
    }
}
