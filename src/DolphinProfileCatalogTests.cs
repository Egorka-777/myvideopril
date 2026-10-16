using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    public static class DolphinProfileCatalogTests {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

        public static bool RunSelfTests() {
            Check(DolphinProfileCatalog.DetectMarket("RU channel", "Russian", new[] { "yt" }) == "RU", "folder RU");
            Check(DolphinProfileCatalog.DetectMarket("Pocket Option EN", "", new List<string>()) == "EN", "name EN");
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
