using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    public sealed class TikTokSyncResult {
        public int Linked;
        public int Imported;
        public int RemovedEmpty;
    }

    /// <summary>Профили Dolphin общие с YouTube: Profile ID, IP и название (YouTube — источник).</summary>
    public static class TikTokProfileSync {
        static string NormMarket(string m) => (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU";

        static string NormName(string n) {
            n = (n ?? "").Trim().ToLowerInvariant();
            if (n.StartsWith("@")) n = n.Substring(1);
            return n;
        }

        static bool SameProfile(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        static YouTubeChannel LinkedChannel(Preferences settings, TikTokAccount account) {
            if (string.IsNullOrWhiteSpace(account.YouTubeSourceId)) return null;
            var matches = (settings.YouTubeChannels ?? new List<YouTubeChannel>())
                .Where(c => c != null && c.ChannelId == account.YouTubeSourceId).ToList();
            return matches.Count == 1 && SameProfile(matches[0].ProfileId, account.ProfileId) ? matches[0] : null;
        }

        static bool Compatible(string a, string b) => string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b;
        static string Fill(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

        static void MergeItem(List<TikTokItem> items, TikTokItem incoming) {
            if (incoming == null) return;
            // A file alone does not identify a publication: retain conflicting jobs/posts and metadata.
            var item = items.FirstOrDefault(i => i != null
                && ((!string.IsNullOrEmpty(i.Video) && i.Video == incoming.Video)
                    || (!string.IsNullOrEmpty(i.SharedShortsSourceId) && i.SharedShortsSourceId == incoming.SharedShortsSourceId)
                    || (!string.IsNullOrEmpty(i.HttpVideoId) && i.HttpVideoId == incoming.HttpVideoId)
                    || (!string.IsNullOrEmpty(i.LocalJobId) && i.LocalJobId == incoming.LocalJobId))
                && Compatible(i.Video, incoming.Video) && Compatible(i.Title, incoming.Title)
                && Compatible(i.Description, incoming.Description) && Compatible(i.Caption, incoming.Caption)
                && Compatible(i.HttpVideoId, incoming.HttpVideoId) && Compatible(i.LocalJobId, incoming.LocalJobId)
                && Compatible(i.SharedShortsSourceId, incoming.SharedShortsSourceId)
                && (i.ScheduledUnixSeconds == 0 || incoming.ScheduledUnixSeconds == 0 || i.ScheduledUnixSeconds == incoming.ScheduledUnixSeconds));
            if (item == null) { items.Add(incoming); return; }
            item.Video = Fill(item.Video, incoming.Video); item.Title = Fill(item.Title, incoming.Title);
            item.Description = Fill(item.Description, incoming.Description); item.Caption = Fill(item.Caption, incoming.Caption);
            item.HttpVideoId = Fill(item.HttpVideoId, incoming.HttpVideoId); item.LocalJobId = Fill(item.LocalJobId, incoming.LocalJobId);
            item.SharedShortsSourceId = Fill(item.SharedShortsSourceId, incoming.SharedShortsSourceId);
            item.Published |= incoming.Published; item.Scheduled |= incoming.Scheduled;
            item.HttpSubmitted |= incoming.HttpSubmitted; item.HttpNeedsReview |= incoming.HttpNeedsReview;
            item.ContentReview |= incoming.ContentReview;
            if (item.ScheduledUnixSeconds == 0) item.ScheduledUnixSeconds = incoming.ScheduledUnixSeconds;
        }

        static void PreserveLegacyMetadata(TikTokAccount account, List<TikTokItem> items) {
            if (string.IsNullOrEmpty(account.Video) && string.IsNullOrEmpty(account.Title)
                && string.IsNullOrEmpty(account.Description) && string.IsNullOrEmpty(account.Caption)) return;
            MergeItem(items, new TikTokItem { Video = account.Video, Title = account.Title,
                Description = account.Description, Caption = account.Caption });
        }

        static void NormalizeLinks(Preferences settings) {
            var retained = new List<TikTokAccount>();
            foreach (var account in settings.TikTokAccounts) {
                if (account == null) continue;
                var source = LinkedChannel(settings, account);
                if (source == null) { retained.Add(account); continue; }
                account.Market = NormMarket(source.Market);
                var target = retained.FirstOrDefault(a => a.YouTubeSourceId == account.YouTubeSourceId
                    && SameProfile(a.ProfileId, account.ProfileId) && LinkedChannel(settings, a) != null);
                if (target == null) { retained.Add(account); continue; }
                if (target.Items == null) target.Items = new List<TikTokItem>();
                // Legacy top-level video/captions are queue data too, not disposable duplicate-row fields.
                PreserveLegacyMetadata(target, target.Items);
                foreach (var item in account.Items ?? new List<TikTokItem>()) MergeItem(target.Items, item);
                PreserveLegacyMetadata(account, target.Items);
            }
            settings.TikTokAccounts = retained;
        }

        static void ApplyYouTubeLink(TikTokAccount account, YouTubeChannel c, bool copyDetails) {
            if (account == null || c == null) return;
            account.YouTubeSourceId = c.ChannelId;
            account.Market = NormMarket(c.Market);
            account.ProfileId = (c.ProfileId ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(c.ExpectedIp)) account.ExpectedIp = c.ExpectedIp.Trim();
            if (!string.IsNullOrWhiteSpace(c.Name)) account.Name = c.Name.Trim();
            if (copyDetails) account.Enabled = c.Enabled;
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
            NormalizeLinks(settings);

            var yt = (settings.YouTubeChannels ?? new List<YouTubeChannel>())
                .Where(c => c != null && NormMarket(c.Market) == market)
                .ToList();

            foreach (var c in yt) {
                string pid = (c.ProfileId ?? "").Trim();
                string key = market + "|" + pid;
                if ((settings.TikTokSyncExcludedProfiles ?? new List<string>()).Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                var byPid = settings.TikTokAccounts.FirstOrDefault(a =>
                    a != null && NormMarket(a.Market) == market
                    && ((!string.IsNullOrWhiteSpace(c.ChannelId) && a.YouTubeSourceId == c.ChannelId && SameProfile(a.ProfileId, pid))
                        || (string.IsNullOrWhiteSpace(a.YouTubeSourceId) && pid.Length > 0 && string.Equals((a.ProfileId ?? "").Trim(), pid, StringComparison.OrdinalIgnoreCase))));
                if (byPid != null) {
                    ApplyYouTubeLink(byPid, c, copyDetails);
                    result.Linked++;
                    continue;
                }

                var byName = settings.TikTokAccounts.FirstOrDefault(a =>
                    a != null && NormMarket(a.Market) == market
                    && string.IsNullOrWhiteSpace(a.ProfileId)
                    && string.IsNullOrWhiteSpace(a.YouTubeSourceId)
                    && string.Equals(NormName(a.Name), NormName(c.Name), StringComparison.Ordinal));
                if (byName != null) {
                    ApplyYouTubeLink(byName, c, copyDetails);
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

            // Manual/unresolved rows are not disposable and must not inherit a sibling channel's name.
            foreach (var account in settings.TikTokAccounts.Where(a => a != null && NormMarket(a.Market) == market)) {
                var src = LinkedChannel(settings, account);
                if (src == null) continue;
                ApplyYouTubeLink(account, src, false);
                SharedShortsQueue.SyncTikTok(settings, account);
            }

            return result;
        }
    }
}
