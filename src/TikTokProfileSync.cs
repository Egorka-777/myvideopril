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

        public static TikTokSyncResult ImportAllFromYouTube(Preferences settings) {
            var result = new TikTokSyncResult();
            if (settings == null) return result;
            settings.TikTokSyncExcludedProfiles = new List<string>();
            foreach (var market in new[] { "RU", "EN" }) {
                var part = SyncFromYouTube(settings, market, true);
                result.Linked += part.Linked;
                result.Imported += part.Imported;
            }
            return result;
        }

        public static TikTokSyncResult SyncFromYouTube(Preferences settings, string market, bool copyDetails = false) {
            var result = new TikTokSyncResult();
            if (settings == null) return result;
            market = NormMarket(market);
            if (settings.TikTokAccounts == null) settings.TikTokAccounts = new List<TikTokAccount>();

            var yt = (settings.YouTubeChannels ?? new List<YouTubeChannel>())
                .Where(c => c != null && NormMarket(c.Market) == market)
                .ToList();

            foreach (var c in yt) {
                string pid = (c.ProfileId ?? "").Trim();
                string key = market + "|" + pid;
                if ((settings.TikTokSyncExcludedProfiles ?? new List<string>()).Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                var byPid = settings.TikTokAccounts.FirstOrDefault(a =>
                    a != null && NormMarket(a.Market) == market
                    && ((!string.IsNullOrWhiteSpace(c.ChannelId) && a.YouTubeSourceId == c.ChannelId)
                        || (string.IsNullOrWhiteSpace(a.YouTubeSourceId) && pid.Length > 0 && string.Equals((a.ProfileId ?? "").Trim(), pid, StringComparison.OrdinalIgnoreCase))));
                if (byPid != null) {
                    byPid.YouTubeSourceId = c.ChannelId;
                    byPid.ProfileId = pid;
                    if (copyDetails) { byPid.Name = c.Name; byPid.Enabled = c.Enabled; byPid.ExpectedIp = c.ExpectedIp ?? ""; }
                    if (!string.IsNullOrWhiteSpace(c.ExpectedIp)) byPid.ExpectedIp = c.ExpectedIp.Trim();
                    if (string.IsNullOrWhiteSpace(byPid.Name) && !string.IsNullOrWhiteSpace(c.Name)) byPid.Name = c.Name.Trim();
                    result.Linked++;
                    continue;
                }

                var byName = settings.TikTokAccounts.FirstOrDefault(a =>
                    a != null && NormMarket(a.Market) == market
                    && string.IsNullOrWhiteSpace(a.ProfileId)
                    && string.IsNullOrWhiteSpace(a.YouTubeSourceId)
                    && string.Equals(NormName(a.Name), NormName(c.Name), StringComparison.Ordinal));
                if (byName != null) {
                    byName.YouTubeSourceId = c.ChannelId;
                    byName.ProfileId = pid;
                    if (copyDetails) { byName.Name = c.Name; byName.Enabled = c.Enabled; byName.ExpectedIp = c.ExpectedIp ?? ""; }
                    if (!string.IsNullOrWhiteSpace(c.ExpectedIp)) byName.ExpectedIp = c.ExpectedIp.Trim();
                    if (string.IsNullOrWhiteSpace(byName.Name)) byName.Name = c.Name.Trim();
                    if (string.IsNullOrWhiteSpace(byName.Status) || byName.Status.IndexOf("импорт", StringComparison.OrdinalIgnoreCase) >= 0)
                        byName.Status = "Готов";
                    result.Linked++;
                    continue;
                }

                settings.TikTokAccounts.Add(new TikTokAccount {
                    YouTubeSourceId = c.ChannelId,
                    Enabled = c.Enabled,
                    Name = c.Name ?? "",
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
                return hasPid || hasVideo || !string.IsNullOrWhiteSpace(a.YouTubeSourceId) || yt.Any(c => string.Equals(c.Name, a.Name, StringComparison.Ordinal));
            }).ToList();
            result.RemovedEmpty = before - settings.TikTokAccounts.Count;

            return result;
        }
    }
}
