using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    public sealed class AccountSourceLink {
        public string Platform = "", SourceId = "", ProfileId = "", Market = "RU", Kind = "", Name = "", Url = "", ExpectedIp = "";
        public AccountSourceLink Copy() { return (AccountSourceLink)MemberwiseClone(); }
        public string Key => Platform + "|" + SourceId;
    }
    public sealed class AccountSyncResult {
        public int Added, Linked, Unresolved;
        public bool Changed;
    }
    public static class AccountLibrarySync {
        static string Market(string value) { return string.Equals(value, "EN", StringComparison.OrdinalIgnoreCase) ? "EN" : "RU"; }
        static bool Equal(string a, string b) { return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal); }
        public static List<AccountSourceLink> Sources(Preferences settings) {
            var result = new List<AccountSourceLink>();
            if (settings == null) return result;
            foreach (var ch in settings.YouTubeChannels ?? new List<YouTubeChannel>()) if (ch != null)
                result.Add(new AccountSourceLink { Platform = "YouTube", SourceId = ch.ChannelId ?? "", ProfileId = ch.ProfileId ?? "", Market = Market(ch.Market), Kind = ch.Kind ?? "", Name = ch.Name ?? "", Url = HttpUrl(ch.ChannelUrl), ExpectedIp = ch.ExpectedIp ?? "" });
            foreach (var a in settings.TikTokAccounts ?? new List<TikTokAccount>()) if (a != null)
                result.Add(new AccountSourceLink { Platform = "TikTok", SourceId = a.AccountId ?? "", ProfileId = a.ProfileId ?? "", Market = Market(a.Market), Name = a.Name ?? "", ExpectedIp = a.ExpectedIp ?? "" });
            return result;
        }
        static string HttpUrl(string value) {
            string url = (value ?? "").Trim();
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? url : "";
        }
        static string UrlKey(string value) {
            if (!Uri.TryCreate(HttpUrl(value), UriKind.Absolute, out var u)) return "";
            string host = u.Host.ToLowerInvariant(); if (host.StartsWith("www.")) host = host.Substring(4);
            string path = u.AbsolutePath.TrimEnd('/');
            if (host == "youtube.com") foreach (string tab in new[] { "/videos", "/shorts", "/featured", "/streams", "/about" })
                if (path.EndsWith(tab, StringComparison.OrdinalIgnoreCase)) { path = path.Substring(0, path.Length - tab.Length); break; }
            // A channel ID can be case-sensitive. Do not equate distinct IDs by lowercasing paths.
            return host + path;
        }
        public static AccountSyncResult Sync(Preferences settings, AccountLibraryStore store, Action<Preferences> save = null) {
            if (settings == null) return new AccountSyncResult();
            // Refuse a damaged vault before changing even source identifiers.
            store.Load();
            var restore = new List<Action>();
            foreach (var ch in settings.YouTubeChannels ?? new List<YouTubeChannel>()) if (ch != null && string.IsNullOrWhiteSpace(ch.ChannelId)) {
                string old = ch.ChannelId; restore.Add(() => ch.ChannelId = old); ch.ChannelId = Guid.NewGuid().ToString("N");
            }
            foreach (var a in settings.TikTokAccounts ?? new List<TikTokAccount>()) if (a != null && string.IsNullOrWhiteSpace(a.AccountId)) {
                string old = a.AccountId; restore.Add(() => a.AccountId = old); a.AccountId = Guid.NewGuid().ToString("N");
            }
            if (restore.Count > 0) {
                try { (save ?? Store.Save)(settings); } catch { foreach (var action in restore) action(); throw; }
            }
            var sources = Sources(settings);
            return store.Synchronize(data => {
                var report = new AccountSyncResult();
                foreach (var source in sources) {
                    var exact = data.Accounts.Where(a => (a.Sources ?? new List<AccountSourceLink>()).Any(s => s.Key == source.Key)).ToList();
                    if (exact.Count > 1) { report.Unresolved++; continue; }
                    var record = exact.SingleOrDefault();
                    if (record == null && source.Url != "") {
                        string key = UrlKey(source.Url);
                        var matches = data.Accounts.Where(a => a.Platform == source.Platform && UrlKey(a.Url) == key).ToList();
                        if (matches.Count > 1) {
                            var byMarket = matches.Where(a => a.Language == source.Market).ToList();
                            if (byMarket.Count == 1) record = byMarket[0]; else { report.Unresolved++; continue; }
                        } else record = matches.SingleOrDefault();
                    }
                    if (record == null) {
                        record = new AccountRecord { Name = source.Name == "" ? source.Platform + " · " + (source.ProfileId == "" ? source.SourceId : source.ProfileId) : source.Name,
                            Platform = source.Platform, Language = source.Market, Url = source.Url, ImportedName = source.Name, ImportedUrl = source.Url, ImportedSourceKey = source.Key };
                        data.Accounts.Add(record); report.Added++; report.Changed = true;
                    }
                    if (record.Sources == null) record.Sources = new List<AccountSourceLink>();
                    if (record.ImportedSourceKey == "") { record.ImportedSourceKey = source.Key; report.Changed = true; }
                    var link = record.Sources.FirstOrDefault(s => s.Key == source.Key);
                    if (link == null) { record.Sources.Add(source.Copy()); report.Linked++; report.Changed = true; }
                    else {
                        // Only imported values follow source changes. Personal edits take precedence.
                        if (record.ImportedSourceKey == source.Key && (record.Name == "" || record.Name == record.ImportedName) && source.Name != "" && record.Name != source.Name) { record.Name = source.Name; report.Changed = true; }
                        if (record.ImportedSourceKey == source.Key && (record.Url == "" || record.Url == record.ImportedUrl) && source.Url != "" && record.Url != source.Url) { record.Url = source.Url; report.Changed = true; }
                        if (record.Sources.Count == 1 && record.Language == link.Market && record.Language != source.Market) { record.Language = source.Market; report.Changed = true; }
                        if (!SameLink(link, source)) { record.Sources[record.Sources.IndexOf(link)] = source.Copy(); report.Changed = true; }
                    }
                    if (record.ImportedSourceKey == source.Key && (record.ImportedName != source.Name || record.ImportedUrl != source.Url)) { record.ImportedName = source.Name; record.ImportedUrl = source.Url; report.Changed = true; }
                }
                return report;
            });
        }
        static bool SameLink(AccountSourceLink a, AccountSourceLink b) {
            return Equal(a.ProfileId, b.ProfileId) && Equal(a.Market, b.Market) && Equal(a.Kind, b.Kind) && Equal(a.Name, b.Name) && Equal(a.Url, b.Url) && Equal(a.ExpectedIp, b.ExpectedIp);
        }
        public static string[] Markets(AccountRecord record) {
            return (record.Sources?.Count > 0 ? record.Sources.Select(s => s.Market) : new[] { record.Language }).Distinct().OrderBy(m => m).ToArray();
        }
        public static string[] Missing(AccountRecord a) {
            var fields = new List<string>();
            if (string.IsNullOrWhiteSpace(a.Url)) fields.Add("ссылка на аккаунт");
            if (string.IsNullOrWhiteSpace(a.AccessNotes) && (string.IsNullOrWhiteSpace(a.Login) || string.IsNullOrWhiteSpace(a.Password))) fields.Add("данные для входа");
            if (string.IsNullOrWhiteSpace(a.Proxy)) fields.Add("прокси");
            if (string.IsNullOrWhiteSpace(a.Country)) fields.Add("страна");
            if (string.IsNullOrWhiteSpace(a.PurchaseUrl)) fields.Add("ссылка на покупку");
            return fields.ToArray();
        }
        public static string DescribeSources(AccountRecord record, Preferences settings) {
            var current = Sources(settings).GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First());
            return string.Join("\r\n", (record.Sources ?? new List<AccountSourceLink>()).Select(s => {
                bool present = current.TryGetValue(s.Key, out var active); var link = present ? active : s;
                return link.Platform + " · " + link.Market + (link.Kind == "" ? "" : " · " + (link.Kind == "shorts" ? "Shorts" : "Long"))
                    + (present ? " — в списке" : " — убран из списка") + (link.ProfileId == "" ? "" : "\r\nПрофиль браузера: " + link.ProfileId)
                    + (link.ExpectedIp == "" ? "" : "\r\nIP из настроек: " + link.ExpectedIp);
            }));
        }
    }
}
