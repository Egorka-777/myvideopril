using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    public static class DolphinProfileCatalogTests {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

        sealed class MetadataFixture : System.Net.Http.HttpMessageHandler {
            public List<string> Paths = new List<string>();
            protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancel) {
                string path = request.RequestUri.PathAndQuery; Paths.Add(path);
                Check(request.Method == System.Net.Http.HttpMethod.Get && request.RequestUri.Host == "dolphin-anty-api.com", "read-only Dolphin metadata");
                string body;
                if (path.StartsWith("/folders?", StringComparison.Ordinal)) body = "{\"data\":[{\"id\":119194,\"name\":\"Youtube RU\"},{\"id\":421922,\"name\":\"Youtube EN\"}]}";
                else if (path.StartsWith("/browser_profiles?", StringComparison.Ordinal)) body = "{\"data\":[{\"id\":101,\"name\":\"Misleading EN name\",\"folder\":119194,\"tags\":[]},{\"id\":102,\"name\":\"Misleading RU name\",\"folder\":421922,\"tags\":[]},{\"id\":103,\"name\":\"RU name only\",\"tags\":[]}],\"last_page\":1}";
                else if (path == "/browser_profiles/101" || path == "/browser_profiles/102") {
                    bool first = path.EndsWith("101", StringComparison.Ordinal);
                    body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { data = new { proxy = new { id = first ? 201 : 202, type = "socks5", host = "shared.test", port = "50101", login = first ? "login@one" : "login@two", password = "secret:/@" } } });
                } else throw new Exception("Unexpected Dolphin path");
                return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(body) });
            }
        }

        static void MetadataTests() {
            using (var handler = new MetadataFixture()) {
                var profiles = DolphinProfileCatalog.FetchAll("fixture-token", handler).GetAwaiter().GetResult();
                Check(profiles.Single(p => p.Id == "101").FolderName == "Youtube RU" && profiles.Single(p => p.Id == "101").SuggestedMarket == "RU", "numeric folder maps to RU metadata");
                Check(profiles.Single(p => p.Id == "102").SuggestedMarket == "EN", "numeric folder maps to EN metadata");
                Check(profiles.Single(p => p.Id == "103").SuggestedMarket == "", "name-only market remains unknown");
            }
            using (var handler = new MetadataFixture()) {
                var routes = TelegramProxyCatalog.FetchDolphin("fixture-token", new[] { new KeyValuePair<string,string>("101", "First"), new KeyValuePair<string,string>("102", "Second") }, handler, includeCatalog:false).GetAwaiter().GetResult();
                Check(routes.Warnings.Count == 0 && routes.Proxies.Count == 2, "assigned routes fetched separately");
                var first = routes.Proxies[0]; var second = routes.Proxies[1];
                Check(first.url != second.url && TelegramProxyCatalog.Login(first.url) == "login@one" && TelegramProxyCatalog.Password(first.url) == "secret:/@", "credentials retained in route, not collapsed by host");
                Check(TelegramProxyCatalog.AssignedLabel(first) != TelegramProxyCatalog.AssignedLabel(second), "shared host distinct proxy IDs visible");
                Check(TelegramProxyCatalog.AssignedLabel(first).Contains("#201") && TelegramProxyCatalog.AssignedLabel(first).Contains("shared.test:50101"), "safe endpoint and assignment ID");
                first.dolphin_proxy_id = ""; second.dolphin_proxy_id = "";
                Check(TelegramProxyCatalog.AssignedLabel(first) != TelegramProxyCatalog.AssignedLabel(second), "opaque login fingerprint without proxy ID");
                Check(!TelegramProxyCatalog.AssignedLabel(first).Contains("login@one") && !TelegramProxyCatalog.AssignedLabel(first).Contains("secret"), "no displayed proxy credentials");
                Check(handler.Paths.SequenceEqual(new[] { "/browser_profiles/101", "/browser_profiles/102" }), "no proxy catalog or browser launch");
            }
        }

        public static bool RunSelfTests() {
            MetadataTests();
            Check(DolphinProfileCatalog.DetectMarket("RU channel", "Russian", new[] { "yt" }) == "RU", "folder RU");
            Check(DolphinProfileCatalog.DetectMarket("Pocket Option EN", "", new List<string>()) == "", "profile name is not market metadata");
            Check(DolphinProfileCatalog.DetectMarket("x", "Global pocket option", null) == "", "business/topic words are not language metadata");
            Check(DolphinProfileCatalog.DetectMarket("x", "RU", new[] { "EN" }) == "", "conflicting metadata remains unknown");
            Check(DolphinProfileCatalog.DetectMarket("misc", "English profiles", null) == "EN", "folder english");
            Check(DolphinProfileCatalog.DetectMarket("misc", "other", new[] { "ru-tag" }) == "RU", "tag RU");
            Check(DolphinProfileCatalog.DetectMarket("x", "y", null) == "", "unknown market");

            var settings = new Preferences {
                YouTubeChannels = new List<YouTubeChannel> {
                    new YouTubeChannel { ProfileId = "100", Name = "Old", Market = "RU" }
                }
            };
            var imported = DolphinProfileCatalog.Import(settings, new[] {
                new DolphinBrowserProfile { Id = "100", Name = "Renamed", SuggestedMarket = "RU" },
                new DolphinBrowserProfile { Id = "200", Name = "New EN", SuggestedMarket = "EN" }
            });
            Check(imported.AddedYouTube == 1 && imported.SkippedExisting == 1, "import dedupe");
            Check(settings.YouTubeChannels.Count == 2, "two channels");
            Check(settings.YouTubeChannels.Any(c => c.ProfileId == "200" && c.Market == "EN"), "EN row");

            string key = ProfileProxyLoad.RouteKey(
                new UnifiedProfile { ProfileId = "1", ExpectedIp = "1.2.3.4", YouTubeAccounts = { new YouTubeChannel() } },
                _ => "http://user:pass@1.2.3.4:8080");
            Check(key.StartsWith("proxy:", StringComparison.Ordinal), "route prefers proxy url");
            Check(ProfileProxyLoad.FormatLoad(2, 1) == "YT 2/2 · TT 1/2", "load label");
            Check(ProfileProxyLoad.IsFull(2, 0), "full when YT max");
            return true;
        }
    }
}
