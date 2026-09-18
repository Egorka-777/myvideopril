using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    public sealed class UnifiedProfile {
        public string ProfileId = "";
        public string DisplayName = "";
        public string PlatformsLabel = "";
        public string ChannelNames = "";
        public string Market = "";
        public string ContentKind = "";
        public string CountryCode = "—";
        public string ExpectedIp = "";
        public string VerifiedIp = "";
        public int VideoCount;
        public string Status = "Готов";
        public string LastOperation = "";
        public string ProxyCheckResult = "";
        public DateTime? LastProxyCheck;
        public YouTubeChannel PrimaryYouTube;
        public TikTokAccount PrimaryTikTok;
        public List<YouTubeChannel> YouTubeAccounts = new List<YouTubeChannel>();
        public List<TikTokAccount> TikTokAccounts = new List<TikTokAccount>();
    }

    public static class ProfileCatalog {
        public static List<UnifiedProfile> Build(Preferences settings) {
            settings = settings ?? new Preferences();
            var map = new Dictionary<string, UnifiedProfile>(StringComparer.OrdinalIgnoreCase);
            var orphans = new List<UnifiedProfile>();

            foreach (var ch in settings.YouTubeChannels ?? new List<YouTubeChannel>()) {
                if (ch == null) continue;
                string pid = (ch.ProfileId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(pid)) {
                    orphans.Add(MakeOrphanYouTube(ch));
                    continue;
                }
                var p = GetOrCreate(map, pid);
                p.YouTubeAccounts.Add(ch);
                if (p.PrimaryYouTube == null) p.PrimaryYouTube = ch;
                MergeYouTube(p, ch);
            }

            foreach (var acc in settings.TikTokAccounts ?? new List<TikTokAccount>()) {
                if (acc == null) continue;
                string pid = (acc.ProfileId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(pid)) {
                    orphans.Add(MakeOrphanTikTok(acc));
                    continue;
                }
                var p = GetOrCreate(map, pid);
                p.TikTokAccounts.Add(acc);
                if (p.PrimaryTikTok == null) p.PrimaryTikTok = acc;
                MergeTikTok(p, acc);
            }

            var list = map.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            list.AddRange(orphans.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase));
            return list;
        }

        static UnifiedProfile GetOrCreate(Dictionary<string, UnifiedProfile> map, string pid) {
            if (!map.TryGetValue(pid, out var p)) {
                p = new UnifiedProfile { ProfileId = pid, DisplayName = "Профиль " + pid };
                map[pid] = p;
            }
            return p;
        }

        static UnifiedProfile MakeOrphanYouTube(YouTubeChannel ch) {
            var p = new UnifiedProfile {
                ProfileId = "",
                DisplayName = string.IsNullOrWhiteSpace(ch.Name) ? "YouTube без Profile ID" : ch.Name,
                PrimaryYouTube = ch
            };
            p.YouTubeAccounts.Add(ch);
            MergeYouTube(p, ch);
            return p;
        }

        static UnifiedProfile MakeOrphanTikTok(TikTokAccount acc) {
            var p = new UnifiedProfile {
                ProfileId = "",
                DisplayName = string.IsNullOrWhiteSpace(acc.Name) ? "TikTok без Profile ID" : acc.Name,
                PrimaryTikTok = acc
            };
            p.TikTokAccounts.Add(acc);
            MergeTikTok(p, acc);
            return p;
        }

        static void MergeYouTube(UnifiedProfile p, YouTubeChannel ch) {
            if (string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.StartsWith("Профиль "))
                p.DisplayName = string.IsNullOrWhiteSpace(ch.Name) ? p.DisplayName : ch.Name;
            p.ExpectedIp = FirstNonEmpty(p.ExpectedIp, ch.ExpectedIp);
            p.Market = CombineMarkets(p.Market, ch.Market);
            p.ContentKind = CombineKind(p.ContentKind, NormKind(ch.Kind));
            p.VideoCount += CountYouTubeVideos(ch);
            p.Status = PickStatus(p.Status, ch.Status);
            p.LastOperation = FirstNonEmpty(p.LastOperation, ch.Status);
        }

        static void MergeTikTok(UnifiedProfile p, TikTokAccount acc) {
            if (p.DisplayName.StartsWith("Профиль ") && !string.IsNullOrWhiteSpace(acc.Name))
                p.DisplayName = acc.Name;
            p.ExpectedIp = FirstNonEmpty(p.ExpectedIp, acc.ExpectedIp);
            p.Market = CombineMarkets(p.Market, acc.Market);
            if (string.IsNullOrWhiteSpace(p.ContentKind) || p.ContentKind == "—") p.ContentKind = "TikTok";
            else if (p.ContentKind != "TikTok") p.ContentKind = "Mixed";
            p.VideoCount += acc.Items?.Count(i => i != null && !string.IsNullOrWhiteSpace(i.Video)) ?? 0;
            p.Status = PickStatus(p.Status, acc.Status);
            p.LastOperation = FirstNonEmpty(p.LastOperation, acc.Status);
        }

        public static void FinalizeLabels(UnifiedProfile p) {
            var platforms = new List<string>();
            if (p.YouTubeAccounts.Count > 0) platforms.Add("YouTube");
            if (p.TikTokAccounts.Count > 0) platforms.Add("TikTok");
            p.PlatformsLabel = platforms.Count == 0 ? "—" : string.Join(", ", platforms);

            var names = new List<string>();
            names.AddRange(p.YouTubeAccounts.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
            names.AddRange(p.TikTokAccounts.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
            p.ChannelNames = names.Count == 0 ? "—" : string.Join(" · ", names.Distinct(StringComparer.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(p.Market)) p.Market = "—";
            if (string.IsNullOrWhiteSpace(p.ContentKind)) p.ContentKind = "—";
            if (string.IsNullOrWhiteSpace(p.ExpectedIp)) p.ExpectedIp = "—";
            p.VerifiedIp = string.IsNullOrWhiteSpace(p.VerifiedIp) ? p.ExpectedIp : p.VerifiedIp;
            if (string.IsNullOrWhiteSpace(p.ProxyCheckResult))
                p.ProxyCheckResult = p.ExpectedIp == "—" ? "Не проверялся" : "Привязан";
        }

        static int CountYouTubeVideos(YouTubeChannel ch) {
            if (ch.Items != null && ch.Items.Count > 0)
                return ch.Items.Count(i => i != null && !string.IsNullOrWhiteSpace(i.Video));
            return string.IsNullOrWhiteSpace(ch.Video) ? 0 : 1;
        }

        static string NormKind(string k) {
            return (k ?? "").Trim().ToLowerInvariant() == "shorts" ? "Shorts" : "Long";
        }

        static string CombineMarkets(string a, string b) {
            a = (a ?? "").Trim().ToUpperInvariant();
            b = (b ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(b)) return string.IsNullOrWhiteSpace(a) ? "" : a;
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (a == b) return a;
            return "Mixed";
        }

        static string CombineKind(string a, string b) {
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (string.IsNullOrWhiteSpace(b)) return a;
            if (a == b) return a;
            return "Mixed";
        }

        static string PickStatus(string a, string b) {
            if ((b ?? "").IndexOf("ошиб", StringComparison.OrdinalIgnoreCase) >= 0) return b;
            if ((a ?? "").IndexOf("ошиб", StringComparison.OrdinalIgnoreCase) >= 0) return a;
            return FirstNonEmpty(b, a, "Готов");
        }

        static string FirstNonEmpty(params string[] values) {
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return "";
        }

        public static (int total, int uniqueProfileIds, int youtube, int tiktok) Counts(Preferences settings) {
            var all = Build(settings);
            foreach (var p in all) FinalizeLabels(p);
            int yt = (settings.YouTubeChannels ?? new List<YouTubeChannel>()).Count(c => c != null);
            int tk = (settings.TikTokAccounts ?? new List<TikTokAccount>()).Count(c => c != null);
            int unique = all.Count(p => !string.IsNullOrWhiteSpace(p.ProfileId));
            return (all.Count, unique, yt, tk);
        }
    }
}
