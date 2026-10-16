using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class DolphinBrowserProfile {
        public string Id = "";
        public string Name = "";
        public string FolderName = "";
        public List<string> Tags = new List<string>();
        public string SuggestedMarket = "";
    }

    public sealed class DolphinProfileImportResult {
        public int Fetched;
        public int AddedYouTube;
        public int UpdatedYouTube;
        public int SkippedExisting;
        public List<string> Warnings = new List<string>();
    }

    public static class DolphinProfileCatalog {
        const int PageLimit = 50;

        public static async Task<List<DolphinBrowserProfile>> FetchAll(string token, HttpMessageHandler handler = null, CancellationToken cancel = default(CancellationToken)) {
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Сначала укажите API-токен Dolphin в настройках.");
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<DolphinBrowserProfile>();
            using (var client = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })) {
                client.Timeout = TimeSpan.FromSeconds(45);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                async Task<Dictionary<string, object>> get(string path) {
                    using (var response = await client.GetAsync("https://dolphin-anty-api.com" + path, cancel).ConfigureAwait(false)) {
                        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Dolphin: HTTP " + (int)response.StatusCode);
                        return Object(json.DeserializeObject(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
                            ?? throw new InvalidOperationException("Dolphin вернул неожиданный ответ.");
                    }
                }
                try {
                    for (int page = 1; page <= 200; page++) {
                        cancel.ThrowIfCancellationRequested();
                        var body = await get("/folders?limit=100&page=" + page).ConfigureAwait(false);
                        if (!TryRows(body, out object[] rows)) break;
                        foreach (var value in rows) {
                            var row = Object(value);
                            if (row == null) continue;
                            string id = Str(row, "id");
                            string name = Str(row, "name");
                            if (id != "" && name != "") folders[id] = name;
                        }
                        if (!HasNextPage(body, page)) break;
                    }
                } catch (OperationCanceledException) { throw; }
                catch { /* folders optional */ }

                for (int page = 1; page <= 500; page++) {
                    cancel.ThrowIfCancellationRequested();
                    var body = await get("/browser_profiles?limit=" + PageLimit + "&page=" + page).ConfigureAwait(false);
                    if (!TryRows(body, out object[] rows)) break;
                    foreach (var value in rows) {
                        var row = Object(value);
                        if (row == null) continue;
                        string id = Str(row, "id");
                        if (id == "" || !Regex.IsMatch(id, @"^\d+$")) continue;
                        var profile = new DolphinBrowserProfile {
                            Id = id,
                            Name = Str(row, "name"),
                            Tags = ReadTags(row),
                            FolderName = FolderName(row, folders)
                        };
                        profile.SuggestedMarket = DetectMarket(profile.Name, profile.FolderName, profile.Tags);
                        list.Add(profile);
                    }
                    if (!HasNextPage(body, page)) break;
                    if (page == 500) throw new InvalidOperationException("Слишком много профилей Dolphin; импорт остановлен.");
                }
            }
            return list.GroupBy(p => p.Id).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static DolphinProfileImportResult Import(Preferences settings, IEnumerable<DolphinBrowserProfile> profiles, string defaultMarket = "RU") {
            settings = settings ?? new Preferences();
            if (settings.YouTubeChannels == null) settings.YouTubeChannels = new List<YouTubeChannel>();
            var result = new DolphinProfileImportResult();
            defaultMarket = NormMarket(defaultMarket);
            foreach (var profile in profiles ?? Enumerable.Empty<DolphinBrowserProfile>()) {
                result.Fetched++;
                string pid = (profile.Id ?? "").Trim();
                if (pid == "") continue;
                string market = NormMarket(string.IsNullOrWhiteSpace(profile.SuggestedMarket) ? defaultMarket : profile.SuggestedMarket);
                var existing = settings.YouTubeChannels.FirstOrDefault(c => c != null
                    && string.Equals((c.ProfileId ?? "").Trim(), pid, StringComparison.OrdinalIgnoreCase)
                    && NormMarket(c.Market) == market);
                string name = string.IsNullOrWhiteSpace(profile.Name) ? "Dolphin " + pid : profile.Name.Trim();
                if (existing != null) {
                    if (string.IsNullOrWhiteSpace(existing.Name) || existing.Name.StartsWith("Профиль ", StringComparison.Ordinal))
                        existing.Name = name;
                    result.SkippedExisting++;
                    continue;
                }
                settings.YouTubeChannels.Add(new YouTubeChannel {
                    Enabled = false,
                    Name = name,
                    ProfileId = pid,
                    Market = market,
                    Kind = "shorts",
                    Status = "Импорт Dolphin"
                });
                result.AddedYouTube++;
            }
            TikTokProfileSync.SyncFromYouTube(settings, "RU");
            TikTokProfileSync.SyncFromYouTube(settings, "EN");
            return result;
        }

        public static string DetectMarket(string name, string folder, IEnumerable<string> tags) {
            string blob = string.Join(" ", new[] { name, folder }.Concat(tags ?? Enumerable.Empty<string>())).ToLowerInvariant();
            if (blob == "") return "";
            if (Regex.IsMatch(blob, @"\ben\b|english|англ|pocket\s*option|usa|us\b|uk\b|global"))
                return "EN";
            if (Regex.IsMatch(blob, @"\bru\b|rus|russian|рус|бинод|binod|рф\b|ru-"))
                return "RU";
            return "";
        }

        static string NormMarket(string m) => (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU";

        static string FolderName(Dictionary<string, object> row, Dictionary<string, string> folders) {
            string folderId = Str(row, "folderId");
            if (folderId == "") folderId = Str(row, "folder_id");
            if (folderId != "" && folders.TryGetValue(folderId, out string name)) return name;
            var folder = Object(row.TryGetValue("folder", out var f) ? f : null);
            if (folder != null) return Str(folder, "name");
            return "";
        }

        static List<string> ReadTags(Dictionary<string, object> row) {
            var tags = new List<string>();
            if (row.TryGetValue("tags", out var raw) && raw is object[] array)
                foreach (var item in array) {
                    var text = Convert.ToString(item, CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(text)) tags.Add(text);
                }
            return tags;
        }

        static bool TryRows(Dictionary<string, object> body, out object[] rows) {
            rows = null;
            if (body != null && body.TryGetValue("data", out var data) && data is object[] array) {
                rows = array;
                return true;
            }
            return false;
        }

        static bool HasNextPage(Dictionary<string, object> body, int page) {
            if (body == null) return false;
            if (int.TryParse(Str(body, "last_page"), out int last) && last > page) return true;
            if (int.TryParse(Str(body, "current_page"), out int current) && int.TryParse(Str(body, "last_page"), out last) && current < last) return true;
            return !string.IsNullOrWhiteSpace(Str(body, "next_page_url"));
        }

        static Dictionary<string, object> Object(object value) => value as Dictionary<string, object>;
        static string Str(Dictionary<string, object> value, string key) => value != null && value.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
    }

    public static class ProfileProxyLoad {
        public const int MaxYouTubePerRoute = 2;
        public const int MaxTikTokPerRoute = 2;

        public static string RouteKey(UnifiedProfile profile, Func<string, string> proxyForProfile) {
            if (profile == null) return "";
            string url = proxyForProfile?.Invoke(profile.ProfileId ?? "") ?? "";
            if (!string.IsNullOrWhiteSpace(url)) return "proxy:" + TelegramHealth.Key(url);
            if (!string.IsNullOrWhiteSpace(profile.ExpectedIp) && profile.ExpectedIp != "—") return "ip:" + profile.ExpectedIp.Trim();
            return "profile:" + (profile.ProfileId ?? "");
        }

        public static string FormatLoad(int youtube, int tiktok) {
            return "YT " + youtube + "/" + MaxYouTubePerRoute + " · TT " + tiktok + "/" + MaxTikTokPerRoute;
        }

        public static bool IsFull(int youtube, int tiktok) => youtube >= MaxYouTubePerRoute || tiktok >= MaxTikTokPerRoute;
    }
}
