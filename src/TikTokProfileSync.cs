using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    public sealed class TikTokSyncResult {
        public int Linked;
        public int Imported;
        public int RemovedEmpty;
    }

    /// <summary>Профили Dolphin общие с YouTube: подтягиваем Profile ID и IP.</summary>
    public static class TikTokProfileSync {
        static string NormMarket(string m) => (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU";

        static string NormName(string n) {
            n = (n ?? "").Trim().ToLowerInvariant();
            if (n.StartsWith("@")) n = n.Substring(1);
            return n;
        }

        static bool NamesMatch(string a, string b) {
            string x = NormName(a), y = NormName(b);
            if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y)) return false;
            return x == y || x.Contains(y) || y.Contains(x);
        }

        public static TikTokSyncResult SyncFromYouTube(Preferences settings, string market) {
            var result = new TikTokSyncResult();
            if (settings == null) return result;
            market = NormMarket(market);
            if (settings.TikTokAccounts == null) settings.TikTokAccounts = new List<TikTokAccount>();

            var yt = (settings.YouTubeChannels ?? new List<YouTubeChannel>())
                .Where(c => c != null && NormMarket(c.Market) == market && !string.IsNullOrWhiteSpace(c.ProfileId))
                .ToList();

            foreach (var c in yt) {
                string pid = (c.ProfileId ?? "").Trim();
                var byPid = settings.TikTokAccounts.FirstOrDefault(a =>
                    a != null && NormMarket(a.Market) == market
                    && string.Equals((a.ProfileId ?? "").Trim(), pid, StringComparison.OrdinalIgnoreCase));
                if (byPid != null) {
                    if (!string.IsNullOrWhiteSpace(c.ExpectedIp)) byPid.ExpectedIp = c.ExpectedIp.Trim();
                    if (string.IsNullOrWhiteSpace(byPid.Name) && !string.IsNullOrWhiteSpace(c.Name)) byPid.Name = c.Name.Trim();
                    result.Linked++;
                    continue;
                }

                var byName = settings.TikTokAccounts.FirstOrDefault(a =>
                    a != null && NormMarket(a.Market) == market
                    && string.IsNullOrWhiteSpace(a.ProfileId)
                    && NamesMatch(a.Name, c.Name));
                if (byName != null) {
                    byName.ProfileId = pid;
                    if (!string.IsNullOrWhiteSpace(c.ExpectedIp)) byName.ExpectedIp = c.ExpectedIp.Trim();
                    if (string.IsNullOrWhiteSpace(byName.Name)) byName.Name = c.Name.Trim();
                    if (string.IsNullOrWhiteSpace(byName.Status) || byName.Status.IndexOf("импорт", StringComparison.OrdinalIgnoreCase) >= 0)
                        byName.Status = "Готов";
                    result.Linked++;
                    continue;
                }

                settings.TikTokAccounts.Add(new TikTokAccount {
                    Enabled = true,
                    Name = string.IsNullOrWhiteSpace(c.Name) ? ("TT " + pid) : c.Name.Trim(),
                    ProfileId = pid,
                    ExpectedIp = c.ExpectedIp ?? "",
                    Market = market,
                    Status = "Импорт из YouTube"
                });
                result.Imported++;
            }

            // Пустые заготовки без ID и без видео — убираем, чтобы не ломали загрузку.
            int before = settings.TikTokAccounts.Count;
            settings.TikTokAccounts = settings.TikTokAccounts.Where(a => {
                if (a == null) return false;
                if (NormMarket(a.Market) != market) return true;
                bool hasPid = !string.IsNullOrWhiteSpace(a.ProfileId);
                bool hasVideo = a.Items != null && a.Items.Any(it => it != null && !string.IsNullOrWhiteSpace(it.Video));
                hasVideo = hasVideo || !string.IsNullOrWhiteSpace(a.Video);
                return hasPid || hasVideo;
            }).ToList();
            result.RemovedEmpty = before - settings.TikTokAccounts.Count;

            return result;
        }
    }
}
