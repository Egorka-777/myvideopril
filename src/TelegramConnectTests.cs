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
    public static class TelegramConnectTests {
        static void Check(bool ok,string label){if(!ok)throw new Exception(label);}
        sealed class DolphinFixture:HttpMessageHandler {
            public List<string> Paths=new List<string>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel){
                string path=request.RequestUri.PathAndQuery;Paths.Add(path);
                Check(request.Method==HttpMethod.Get&&request.RequestUri.Host=="dolphin-anty-api.com","Read-only fixed Dolphin origin");
                Check(request.Headers.Authorization?.Parameter=="fixture-token","Dolphin auth header");
                object row=new{id=7,name="Saved",type="socks5",host="127.0.0.1",port=1080,login="u@ser",password="p:a/ss",lastCheck=new{country="DE"}};
                object body;
                if(path=="/proxy?limit=100&page=1")body=new{data=new[]{row},current_page=1,last_page=2,next_page_url="https://untrusted.invalid/steal"};
                else if(path=="/proxy?limit=100&page=2")body=new{data=new object[0],current_page=2,last_page=2,next_page_url=(string)null};
                else if(path=="/browser_profiles/123")body=new{data=new{proxy=row}};
                else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(body))});
            }
        }
        sealed class SlowDolphinFixture:HttpMessageHandler {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel){
                if(request.RequestUri.AbsolutePath=="/proxy")return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"data\":[{\"id\":7,\"type\":\"socks5\",\"host\":\"127.0.0.1\",\"port\":1080}],\"last_page\":1}")};
                await Task.Delay(10000,cancel);throw new Exception("Deadline was ignored");
            }
        }
        static void RunProfileSyncTests(){
            var settings=new Preferences();
            settings.YouTubeChannels=new List<YouTubeChannel>{new YouTubeChannel{ProfileId="123",Name="YouTube name"}};
            settings.TikTokAccounts=new List<TikTokAccount>{new TikTokAccount{ProfileId="123",Name="Shared TikTok"},new TikTokAccount{ProfileId="456",Name="TikTok name"}};
            var data=new TelegramData();
            var missing=TelegramProxyCatalog.ProfileChoices(data,settings);
            Check(missing.Count==2&&missing.All(p=>p.status=="missing"),"Profiles remain visible when proxies are not fetched; not treated as direct");
            string url="socks5://localhost:1080";
            var legacy=TelegramProxyCatalog.Add(data,url,"Old name","Dolphin","dolphin:proxy:7");
            TelegramProxyCatalog.Add(data,url,"Old name","Dolphin","dolphin:profile:123");
            data.accounts.Add(new TelegramAccount{id="bound",proxy_id=legacy.id,proxy=url});
            var first=TelegramProxyCatalog.SyncProfile(data,new TelegramProxy{source_id="dolphin:profile:123",name="YouTube name",url=url});
            var second=TelegramProxyCatalog.SyncProfile(data,new TelegramProxy{source_id="dolphin:profile:456",name="TikTok name",url=url});
            Check(first.id==legacy.id&&first.id!=second.id,"Migration preserves binding; profiles sharing an endpoint stay distinct");
            var manual=TelegramProxyCatalog.Add(data,url,"Manual");
            Check(manual.id!=first.id&&manual.id!=second.id&&first.name=="YouTube name","Manual proxy never overwrites a synced profile");
            var choices=TelegramProxyCatalog.ProfileChoices(data,settings);
            Check(choices.Count==2&&choices.Any(p=>p.name=="YouTube name")&&choices.Any(p=>p.name=="TikTok name"),"Shared YT/TikTok profile appears once under original name");
            settings.YouTubeChannels[0].Name="Renamed profile";
            Check(TelegramProxyCatalog.ProfileChoices(data,settings).Any(p=>p.id==first.id&&p.name=="Renamed profile"),"Profile rename immediately reflected");
            TelegramProxyCatalog.SyncProfile(data,new TelegramProxy{source_id="dolphin:profile:123",name="Renamed profile",url="socks5://localhost:1081"});
            Check(data.accounts[0].proxy.EndsWith(":1081")&&second.url==url,"Proxy change updates only the bound profile");
            settings.TikTokAccounts.RemoveAt(1);
            Check(TelegramProxyCatalog.ProfileChoices(data,settings).Count==1&&data.proxies.Contains(second),"Removed profile hidden without losing saved routes");
            var merged=new TelegramData();
            var old=TelegramProxyCatalog.Add(merged,url,"Old","Dolphin","dolphin:profile:123");
            TelegramProxyCatalog.Add(merged,url,"Old","Dolphin","dolphin:profile:456");
            merged.accounts.Add(new TelegramAccount{id="ambiguous",proxy_id=old.id,proxy=url});
            TelegramProxyCatalog.SyncProfile(merged,new TelegramProxy{source_id="dolphin:profile:456",name="TikTok name",url="socks5://localhost:1082"});
            Check(merged.accounts[0].proxy==url,"Ambiguous legacy assignments never switch to another profile");
        }
        public static bool RunSelfTests(){
            using(var onlyProfiles=new DolphinFixture()){
                var assigned=TelegramProxyCatalog.FetchDolphin("fixture-token",new[]{new KeyValuePair<string,string>("123","Profile")},onlyProfiles,includeCatalog:false).GetAwaiter().GetResult();
                Check(onlyProfiles.Paths.SequenceEqual(new[]{"/browser_profiles/123"})&&assigned.Proxies.Count==1,"Assigned proxy only: never requests full catalog");
            }
            RunProfileSyncTests();
            Check(InstagramWebWindow.TrustedOrigin("https://www.instagram.com/accounts/login/"),"Instagram login origin");
            foreach(string host in new[]{"www.threads.com","threads.com","www.threads.net"}) {
                Check(InstagramWebWindow.TrustedOrigin("https://"+host+"/login"),"Threads official origin");
                Check(!InstagramWebWindow.TrustedOrigin("https://"+host+".evil.test/")&&!InstagramWebWindow.TrustedOrigin("http://"+host+"/")&&!InstagramWebWindow.TrustedOrigin("https://"+host+":444/"),"Threads rejects foreign/insecure origins");
            }
            Check(!InstagramWebWindow.TrustedOrigin("https://instagram.com.evil.test/")&&!InstagramWebWindow.TrustedOrigin("http://instagram.com/"),"Instagram rejects foreign and insecure navigation");
            Check(TelegramProxyCatalog.Normalize("http://host.test:80")=="http://host.test:80","Explicit HTTP default port survives");
            Check(TelegramProxyCatalog.Normalize("localhost:1080:user:p@ss")=="socks5://user:p%40ss@localhost:1080","Legacy auth escaped");
            var ipv6=new Uri(TelegramProxyCatalog.Normalize("socks5://u:p%3Aa@[::1]:1080"));
            Check(System.Net.IPAddress.Parse(ipv6.Host.Trim('[',']')).Equals(System.Net.IPAddress.IPv6Loopback)&&ipv6.Port==1080,"IPv6 endpoint preserved");
            foreach(string value in new[]{"https://host:443","socks5://host:0","socks5://host:1080/path","socks5://host:1080?token=secret"}){bool rejected=false;try{TelegramProxyCatalog.Normalize(value);}catch(ArgumentException ex){rejected=!ex.Message.Contains("token=secret");}Check(rejected,"Invalid proxy rejected safely");}
            foreach(var name in new[]{"http://user:private-password@host.test:8080","socks5://user:private-password@host.test:1080","host.test:1080:user:private-password"}){
                var item=new TelegramProxy{name=name,url="socks5://user:private-password@host.test:1080"};
                Check(!item.ToString().Contains("private-password")&&!TelegramProxyCatalog.SafeName(name,item.url).Contains("private-password"),"Imported proxy labels never expose passwords");
            }
            var data=new TelegramData();data.accounts.Add(new TelegramAccount{id=new string('a',64),name="Keep name",note="Keep note"});
            var proxy=TelegramProxyCatalog.Add(data,"socks5://u:p@localhost:1080","Saved","Dolphin","dolphin:proxy:7");
            TelegramProxyCatalog.Assign(data,new[]{data.accounts[0].id},proxy.id);data.default_proxy_id=proxy.id;
            var same=TelegramProxyCatalog.Add(data,proxy.url,"Profile","Dolphin","dolphin:profile:123");
            Check(same.id==proxy.id&&data.proxies.Count==1,"Same endpoint aliases without duplicates");
            TelegramProxyCatalog.Add(data,"socks5://u:new@localhost:1081","Changed","Dolphin","dolphin:proxy:7");
            Check(data.accounts[0].proxy.EndsWith(":1081")&&data.accounts[0].note=="Keep note","Stable source updates exact assignment");
            Check(TelegramProxyCatalog.Assigned(data,same)==1&&TelegramProxyCatalog.Blocked(data,same)==0,"Assigned counters use real binding");
            data.accounts[0].status="network";Check(TelegramProxyCatalog.Blocked(data,same)==0,"Network error is not a ban counter");
            data.accounts[0].status="blocked";Check(TelegramProxyCatalog.Blocked(data,same)==1,"Only confirmed banned status counted");
            string snapshot=data.accounts[0].proxy;TelegramProxyCatalog.Remove(data,proxy.id);
            Check(data.accounts[0].proxy==snapshot&&data.accounts[0].proxy_id==""&&data.default_proxy_id=="","Remove does not fall back to direct");
            TelegramProxyCatalog.Assign(data,new[]{data.accounts[0].id},"");Check(data.accounts[0].proxy=="","Explicit direct assignment");
            var handler=new DolphinFixture();
            var result=Task.Run(()=>TelegramProxyCatalog.FetchDolphin("fixture-token",new[]{new KeyValuePair<string,string>("123","Channel"),new KeyValuePair<string,string>("124","Unavailable")},handler)).GetAwaiter().GetResult();
            Check(handler.Paths.SequenceEqual(new[]{"/proxy?limit=100&page=1","/proxy?limit=100&page=2","/browser_profiles/123","/browser_profiles/124"}),"Pagination never follows foreign next URL; no browser start");
            Check(result.Proxies.Count==2&&result.Warnings.Count==1,"Partial Dolphin results explicit");
            var watch=System.Diagnostics.Stopwatch.StartNew();
            var partial=Task.Run(()=>TelegramProxyCatalog.FetchDolphin("fixture-token",new[]{new KeyValuePair<string,string>("123","Slow profile")},new SlowDolphinFixture(),timeoutMs:50)).GetAwaiter().GetResult();
            Check(partial.Proxies.Count==1&&partial.Warnings.Count>0&&watch.ElapsedMilliseconds<2000,"Total Dolphin deadline retains already fetched proxies");
            var parsed=new Uri(result.Proxies[0].url);Check(Uri.UnescapeDataString(parsed.UserInfo)=="u@ser:p:a/ss","Dolphin credentials preserved");
            Check(!TelegramStore.ProxyLabel(result.Proxies[0].url).Contains("ss"),"No secret in proxy label");
            string root=Path.Combine(Path.GetTempPath(),"TelegramConnect-"+Guid.NewGuid().ToString("N"));
            try{
                var store=new TelegramStore(root);store.Change(d=>{var p=TelegramProxyCatalog.Add(d,"socks5://u:never-plaintext@localhost:1080");d.default_proxy_id=p.id;d.accounts.Add(data.accounts[0]);TelegramProxyCatalog.Assign(d,new[]{data.accounts[0].id},p.id);});
                var reopened=new TelegramStore(root).Load();Check(reopened.proxies.Count==1&&reopened.accounts[0].proxy_id==reopened.default_proxy_id,"Proxy/default DPAPI roundtrip");
                Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.Vault)).Contains("never-plaintext"),"Proxy secrets encrypted");
            }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
            return true;
        }
    }
}
