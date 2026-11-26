using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    /// <summary>Прогрев YouTube: заголовок из имени файла, опционально лёгкий перефраз через OpenRouter с сохранением #хэштегов.</summary>
    public static class YouTubeWarmupTitles {
        static readonly Regex TagPattern = new Regex(@"(?:^|\s)(#[\p{L}\p{N}_]+)", RegexOptions.Compiled);
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static async Task<IList<string>> RewriteAsync(IList<string> titles, Preferences settings, CancellationToken ct) {
            if (titles == null || titles.Count == 0) return titles ?? new List<string>();
            string key = WindowsSupport.Unprotect(settings.ProtectedOpenRouterKey);
            if (string.IsNullOrWhiteSpace(key)) throw new Exception("Укажите ключ OpenRouter в «Настройки → нейросеть и озвучка».");
            string model = string.IsNullOrWhiteSpace(settings.OpenRouterModel) ? "openai/gpt-4o-mini" : settings.OpenRouterModel.Trim();
            string proxy = WindowsSupport.Unprotect(settings.ProtectedWarmupHttpProxy);
            var items = titles.Select((t, i) => new { id = i, title = (t ?? "").Trim(), tags = string.Join(" ", ExtractHashtags(t)) }).ToArray();
            string instruction = "Перепиши заголовки YouTube другими словами (лёгкий перефраз, не плагиат), сохрани смысл и тон. "
                + "Все хэштеги (#...) из поля tags верни в начале заголовка без изменений, затем перефразированный текст. "
                + "Без кавычек. Не длиннее 95 символов. Верни JSON {titles:[{id,text}]} строго по одному id.";
            var schema = new {
                type = "object",
                properties = new { titles = new { type = "array", items = new { type = "object", properties = new { id = new { type = "integer" }, text = new { type = "string" } }, required = new[] { "id", "text" }, additionalProperties = false } } },
                required = new[] { "titles" },
                additionalProperties = false
            };
            var body = new {
                model,
                messages = new[] { new { role = "system", content = instruction }, new { role = "user", content = Json().Serialize(items) } },
                response_format = new { type = "json_schema", json_schema = new { name = "warmup_titles", strict = true, schema = schema } }
            };
            using (var client = OutboundHttp.Create(proxy, TimeSpan.FromMinutes(3), false))
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")) {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                request.Headers.TryAddWithoutValidation("X-Title", "VideoBatch");
                request.Content = new StringContent(Json().Serialize(body), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false)) {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (!response.IsSuccessStatusCode) throw new Exception("OpenRouter: " + (int)response.StatusCode + " · " + raw);
                    return Parse(raw, titles);
                }
            }
        }

        static IList<string> Parse(string raw, IList<string> fallback) {
            try {
                var root = Json().Deserialize<Dictionary<string, object>>(raw);
                var choices = root?["choices"] as System.Collections.ArrayList;
                var first = choices?[0] as Dictionary<string, object>;
                var message = first?["message"] as Dictionary<string, object>;
                string content = Convert.ToString(message?["content"]) ?? "";
                var payload = Json().Deserialize<Dictionary<string, object>>(content);
                var list = payload?["titles"] as System.Collections.ArrayList;
                if (list == null) return fallback;
                var map = new Dictionary<int, string>();
                foreach (var entry in list) {
                    if (!(entry is Dictionary<string, object> row)) continue;
                    map[Convert.ToInt32(row["id"])] = MergeHashtags(Convert.ToString(fallback[Convert.ToInt32(row["id"])]), Convert.ToString(row["text"]));
                }
                var result = new List<string>(fallback.Count);
                for (int i = 0; i < fallback.Count; i++)
                    result.Add(map.TryGetValue(i, out string t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : (fallback[i] ?? "").Trim());
                return result;
            } catch {
                return fallback;
            }
        }

        internal static IList<string> ExtractHashtags(string title) {
            if (string.IsNullOrWhiteSpace(title)) return new List<string>();
            return TagPattern.Matches(title).Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string MergeHashtags(string original, string rewritten) {
            var tags = ExtractHashtags(original);
            string body = (rewritten ?? "").Trim();
            foreach (var tag in tags) body = Regex.Replace(body, Regex.Escape(tag), "", RegexOptions.IgnoreCase).Trim();
            body = Regex.Replace(body, @"\s+", " ").Trim();
            if (tags.Count == 0) return body;
            return string.Join(" ", tags) + (body.Length > 0 ? " " + body : "");
        }

        public static List<string> TitlesFromFiles(IEnumerable<string> paths) {
            return (paths ?? Enumerable.Empty<string>()).Select(VideoBatchOutputPaths.SourceTitle).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }
    }
}