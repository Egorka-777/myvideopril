using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class TelegramProxy {
        public string id {get;set;}=Guid.NewGuid().ToString("N");
        public string name {get;set;}="";
        public string url {get;set;}="";
        public string source {get;set;}="Вручную";
        public string source_id {get;set;}="";
        public string country {get;set;}="";
        public List<string> source_ids {get;set;}=new List<string>();
        public override string ToString()=>string.IsNullOrWhiteSpace(name)?TelegramStore.ProxyLabel(url):name+" · "+TelegramStore.ProxyLabel(url);
    }
    public sealed class TelegramProxySync {
        public List<TelegramProxy> Proxies=new List<TelegramProxy>();
        public List<string> Warnings=new List<string>();
    }
    public static class TelegramProxyCatalog {
        public static string Normalize(string value) {
            string raw=(value??"").Trim();
            if(raw=="")throw new ArgumentException("Вставьте адрес прокси");
            if(!raw.Contains("://")) {
                var parts=raw.Split(new[]{':'},4);
                if(parts.Length==4)raw="socks5://"+Uri.EscapeDataString(parts[2])+":"+Uri.EscapeDataString(parts[3])+"@"+parts[0]+":"+parts[1];
                else raw="socks5://"+raw;
            }
            if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri)||!new[]{"socks5","socks4","http"}.Contains(uri.Scheme)||string.IsNullOrWhiteSpace(uri.Host)||uri.Port<1||uri.Port>65535||uri.Query!=""||uri.Fragment!=""||(uri.AbsolutePath!=""&&uri.AbsolutePath!="/"))
                throw new ArgumentException("Прокси: socks5://логин:пароль@host:port или http://host:port");
            string host=uri.Host.Trim('[',']');if(host.Contains(":"))host="["+host+"]";
            string auth=uri.GetComponents(UriComponents.UserInfo,UriFormat.UriEscaped);
            return uri.Scheme+"://"+(auth==""?"":auth+"@")+host+":"+uri.Port;
        }
        public static TelegramProxy Add(TelegramData data,string raw,string name="",string source="Вручную",string sourceId="",string country="") {
            string url=Normalize(raw);
            var item=sourceId!=""?data.proxies.FirstOrDefault(p=>p.source_id==sourceId||(p.source_ids!=null&&p.source_ids.Contains(sourceId))):null;
            item=item??data.proxies.FirstOrDefault(p=>p.url==url);
            if(item==null){item=new TelegramProxy();data.proxies.Add(item);}
            bool changed=item.url!=url;
            item.url=url;item.name=string.IsNullOrWhiteSpace(name)?TelegramStore.ProxyLabel(url):name;item.source=source;item.country=country;
            if(item.source_ids==null)item.source_ids=new List<string>();
            if(sourceId!=""){if(item.source_id=="")item.source_id=sourceId;if(!item.source_ids.Contains(sourceId))item.source_ids.Add(sourceId);}
            // Update only accounts explicitly bound to this entry; never reassign at random.
            foreach(var account in data.accounts.Where(a=>a.proxy_id==item.id)){account.proxy=url;if(changed)account.status="unchecked";}
            return item;
        }
        public static void Assign(TelegramData data,IEnumerable<string> accountIds,string proxyId) {
            var ids=new HashSet<string>(accountIds);var proxy=data.proxies.FirstOrDefault(p=>p.id==proxyId);
            if(proxyId!=""&&proxy==null)throw new ArgumentException("Выбранный прокси уже удалён");
            foreach(var account in data.accounts.Where(a=>ids.Contains(a.id))){account.proxy_id=proxyId;account.proxy=proxy?.url??"";account.status="unchecked";}
        }
        public static void Remove(TelegramData data,string id) {
            // Keep assigned connection snapshots; removing a catalog row must not switch to direct.
            data.proxies.RemoveAll(p=>p.id==id);
            foreach(var account in data.accounts.Where(a=>a.proxy_id==id))account.proxy_id="";
            if(data.default_proxy_id==id)data.default_proxy_id="";
        }
        internal static Dictionary<string,object> Object(object value)=>value as Dictionary<string,object>;
        internal static string Str(Dictionary<string,object> value,string key)=>value!=null&&value.TryGetValue(key,out var v)?Convert.ToString(v,CultureInfo.InvariantCulture)??"":"";
        internal static TelegramProxy Parse(Dictionary<string,object> row,string sourceId,string label) {
            string type=Str(row,"type").ToLowerInvariant(),host=Str(row,"host"),port=Str(row,"port");
            if(!new[]{"http","socks4","socks5"}.Contains(type)||host==""||!int.TryParse(port,out var n)||n<1||n>65535)throw new ArgumentException("Dolphin вернул неполный или неподдерживаемый прокси");
            string user=Str(row,"login"),password=Str(row,"password"),auth=user!=""||password!=""?Uri.EscapeDataString(user)+":"+Uri.EscapeDataString(password)+"@":"";
            host=host.Trim('[',']');if(host.Contains(":"))host="["+host+"]";
            string name=Str(row,"name");var last=Object(row.TryGetValue("lastCheck",out var l)?l:null);
            return new TelegramProxy{url=Normalize(type+"://"+auth+host+":"+port),name=label!=""?label:name,source="Dolphin",source_id=sourceId,country=Str(last,"country")};
        }
        public static async Task<TelegramProxySync> FetchDolphin(string token,IEnumerable<KeyValuePair<string,string>> profiles,HttpMessageHandler handler=null,CancellationToken cancel=default(CancellationToken)) {
            if(string.IsNullOrWhiteSpace(token))throw new InvalidOperationException("В настройках приложения не сохранён токен Dolphin. Добавьте его там или вставьте прокси вручную.");
            var result=new TelegramProxySync();
            using(var client=new HttpClient(handler??new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false})) {
                client.Timeout=TimeSpan.FromSeconds(20);client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
                var json=new JavaScriptSerializer{MaxJsonLength=8*1024*1024};
                async Task<Dictionary<string,object>> get(string path) {
                    using(var response=await client.GetAsync("https://dolphin-anty-api.com"+path,cancel).ConfigureAwait(false)) {
                        if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Dolphin: HTTP "+(int)response.StatusCode);
                        var value=json.DeserializeObject(await response.Content.ReadAsStringAsync().ConfigureAwait(false));return Object(value)??throw new InvalidOperationException("Dolphin вернул неожиданный формат ответа");
                    }
                }
                try {
                    for(int page=1;page<=100;page++) {
                        var body=await get("/proxy?limit=100&page="+page).ConfigureAwait(false);
                        if(!(body.TryGetValue("data",out var rows)&&rows is object[] array))throw new InvalidOperationException("Dolphin не вернул список прокси");
                        foreach(var value in array) {
                            var row=Object(value);if(row==null)continue;
                            try{result.Proxies.Add(Parse(row,"dolphin:proxy:"+Str(row,"id"),""));}
                            catch(ArgumentException){result.Warnings.Add("Один прокси Dolphin пропущен: неполные данные или неподдерживаемый протокол");}
                        }
                        int last=int.TryParse(Str(body,"last_page"),out var lastPage)?lastPage:page;
                        if(last<=page&&Str(body,"next_page_url")=="")break;
                        if(int.TryParse(Str(body,"current_page"),out var current)&&current!=page)throw new InvalidOperationException("Dolphin не подтвердил страницу прокси; список может быть неполным");
                        if(page==100)throw new InvalidOperationException("Слишком много страниц прокси; список неполный");
                    }
                }catch(OperationCanceledException){throw;}catch(Exception e)when(e is InvalidOperationException||e is HttpRequestException){result.Warnings.Add(e is InvalidOperationException?e.Message:"Не удалось получить общий список прокси Dolphin");}
                // These are only the YT/TikTok profiles already configured in this application.
                foreach(var profile in profiles.GroupBy(p=>p.Key).Select(g=>g.First())) {
                    cancel.ThrowIfCancellationRequested();
                    if(!System.Text.RegularExpressions.Regex.IsMatch(profile.Key,@"^\d+$")){result.Warnings.Add("Профиль Dolphin с нечисловым ID пропущен");continue;}
                    try {
                        var body=await get("/browser_profiles/"+Uri.EscapeDataString(profile.Key)).ConfigureAwait(false);
                        var data=Object(body.TryGetValue("data",out var d)?d:null)??body;
                        var row=Object(data.TryGetValue("proxy",out var p)?p:null);
                        if(row!=null&&Str(row,"host")!="")result.Proxies.Add(Parse(row,"dolphin:profile:"+profile.Key,profile.Value));
                    }catch(OperationCanceledException){throw;}catch(Exception e)when(e is InvalidOperationException||e is HttpRequestException||e is ArgumentException){result.Warnings.Add("Не получен прокси одного профиля Dolphin");}
                }
            }
            return result;
        }
    }
}
