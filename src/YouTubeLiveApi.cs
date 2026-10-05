using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class LiveJson {
        public static Dictionary<string, object> Object(string text) => new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
        public static string Encode(object value) => new JavaScriptSerializer().Serialize(value);
        public static Dictionary<string, object> Obj(object value) => value as Dictionary<string, object> ?? new Dictionary<string, object>();
        public static object Get(object value, string key) { object result; return Obj(value).TryGetValue(key, out result) ? result : null; }
        public static string Text(object value, string key) => Convert.ToString(Get(value, key)) ?? "";
        public static object[] Items(object value) => Get(value, "items") as object[] ?? new object[0];
        public static string Escape(string value) => Uri.EscapeDataString(value ?? "");
    }
    public sealed class LiveApiException : Exception {
        public int StatusCode { get; }
        public string Reason { get; }
        public LiveApiException(int code, string reason) : base("YouTube API: HTTP " + code + " · " + reason) { StatusCode = code; Reason = reason; }
    }
    public interface IYouTubeLiveApi : IDisposable {
        Task VerifyChannel(string remoteId, string channelUrl, CancellationToken ct);
        Task<string> CreateBroadcast(LiveOptions options, string marker, CancellationToken ct);
        Task<Dictionary<string, object>> CreateStream(LiveOptions options, string marker, CancellationToken ct);
        Task Configure(string broadcastId, string streamId, LiveOptions options, CancellationToken ct);
        Task<string> BroadcastState(string id, CancellationToken ct);
        Task<string> StreamState(string id, CancellationToken ct);
        Task Complete(LiveJournalEntry journal, CancellationToken ct);
    }
    public sealed class YouTubeLiveApi : IYouTubeLiveApi {
        const string ApiRoot = "https://www.googleapis.com/youtube/v3/";
        readonly HttpClient http;
        readonly LiveStore store;
        readonly LiveAccount account;
        readonly SemaphoreSlim tokenGate = new SemaphoreSlim(1, 1);
        string accessToken;
        DateTime expires;
        public YouTubeLiveApi(LiveStore store, LiveAccount account, HttpMessageHandler handler = null) {
            this.store = store; this.account = account;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = handler == null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(35);
        }
        static LiveApiException Error(HttpResponseMessage response, string text) {
            string reason = "requestFailed";
            try {
                var root = LiveJson.Object(text); var error = LiveJson.Get(root, "error");
                var errors = LiveJson.Get(error, "errors") as object[];
                reason = errors != null && errors.Length > 0 ? LiveJson.Text(errors[0], "reason") : Convert.ToString(error);
                if (string.IsNullOrWhiteSpace(reason) || reason.Length > 100 || reason.Contains(" ")) reason = "requestFailed";
            } catch (ArgumentException) { }
            return new LiveApiException((int)response.StatusCode, reason);
        }
        async Task<string> Token(CancellationToken ct) {
            await tokenGate.WaitAsync(ct).ConfigureAwait(false);
            try {
                if (!string.IsNullOrEmpty(accessToken) && expires > DateTime.UtcNow.AddSeconds(60)) return accessToken;
                string refresh = WindowsSupport.Unprotect(account.ProtectedRefreshToken);
                if (string.IsNullOrEmpty(refresh)) throw new InvalidOperationException("Подключите канал к YouTube API повторно.");
                var fields = new Dictionary<string, string> { ["client_id"] = store.Config.ClientId, ["refresh_token"] = refresh, ["grant_type"] = "refresh_token" };
                string secret = WindowsSupport.Unprotect(store.Config.ProtectedClientSecret);
                if (!string.IsNullOrEmpty(secret)) fields["client_secret"] = secret;
                using (var body = new FormUrlEncodedContent(fields)) using (var response = await http.PostAsync("https://oauth2.googleapis.com/token", body, ct).ConfigureAwait(false)) {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw Error(response, text);
                    var result = LiveJson.Object(text); accessToken = LiveJson.Text(result, "access_token");
                    if (string.IsNullOrEmpty(accessToken)) throw new InvalidOperationException("OAuth не вернул access_token.");
                    expires = DateTime.UtcNow.AddSeconds(Convert.ToDouble(LiveJson.Get(result, "expires_in") ?? 300));
                    return accessToken;
                }
            } finally { tokenGate.Release(); }
        }
        async Task<Dictionary<string, object>> Request(HttpMethod method, string resource, object body, CancellationToken ct, byte[] media = null, string mime = null) {
            for (int attempt = 0; ; attempt++) {
                string token = await Token(ct).ConfigureAwait(false);
                string url = resource.StartsWith("https://", StringComparison.Ordinal) ? resource : ApiRoot + resource;
                using (var request = new HttpRequestMessage(method, url)) {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (media != null) { request.Content = new ByteArrayContent(media); request.Content.Headers.ContentType = new MediaTypeHeaderValue(mime); }
                    else if (body != null) request.Content = new StringContent(LiveJson.Encode(body), Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request, ct).ConfigureAwait(false)) {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (response.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>() : LiveJson.Object(text);
                        // Only reads are retried. A failed POST response may already have created a broadcast.
                        if (method == HttpMethod.Get && attempt < 2 && ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)) {
                            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct).ConfigureAwait(false); continue;
                        }
                        throw Error(response, text);
                    }
                }
            }
        }
        public async Task VerifyChannel(string remoteId, string channelUrl, CancellationToken ct) {
            var mine = LiveJson.Items(await Request(HttpMethod.Get, "channels?part=id&mine=true", null, ct).ConfigureAwait(false));
            if (mine.Length != 1 || LiveJson.Text(mine[0], "id") != remoteId) throw new InvalidOperationException("Подключён другой YouTube-канал. Переподключите нужный канал.");
            if (string.IsNullOrWhiteSpace(channelUrl)) return;
            var match = System.Text.RegularExpressions.Regex.Match(channelUrl, @"/channel/(UC[A-Za-z0-9_-]{22})(?:/|\?|$)");
            if (match.Success && match.Groups[1].Value != remoteId) throw new InvalidOperationException("Ссылка канала не совпадает с подключённым YouTube-аккаунтом.");
            var handle = System.Text.RegularExpressions.Regex.Match(channelUrl, @"youtube\.com/(@[^/?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (handle.Success) {
                var found = LiveJson.Items(await Request(HttpMethod.Get, "channels?part=id&forHandle=" + LiveJson.Escape(Uri.UnescapeDataString(handle.Groups[1].Value)), null, ct).ConfigureAwait(false));
                if (found.Length != 1 || LiveJson.Text(found[0], "id") != remoteId) throw new InvalidOperationException("@имя в ссылке не совпадает с подключённым каналом.");
            }
        }
        public async Task<string> CreateBroadcast(LiveOptions options, string marker, CancellationToken ct) {
            var result = await Request(HttpMethod.Post, "liveBroadcasts?part=snippet,status,contentDetails", new {
                snippet = new { title = options.Title, description = (options.Description ?? "") + "\n" + marker, scheduledStartTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                status = new { privacyStatus = options.Privacy, selfDeclaredMadeForKids = options.MadeForKids },
                contentDetails = new { enableAutoStart = true, enableAutoStop = true, monitorStream = new { enableMonitorStream = false } }
            }, ct).ConfigureAwait(false);
            string id = LiveJson.Text(result, "id");
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("YouTube не вернул ID созданного эфира.");
            return id;
        }
        public Task<Dictionary<string, object>> CreateStream(LiveOptions options, string marker, CancellationToken ct) => Request(HttpMethod.Post, "liveStreams?part=snippet,cdn,contentDetails", new {
            snippet = new { title = options.Title, description = marker }, cdn = new { ingestionType = "rtmp", resolution = options.Height + "p", frameRate = "30fps" }, contentDetails = new { isReusable = false }
        }, ct);
        public async Task Configure(string broadcastId, string streamId, LiveOptions options, CancellationToken ct) {
            await Request(HttpMethod.Post, "liveBroadcasts/bind?part=id,contentDetails&id=" + LiveJson.Escape(broadcastId) + "&streamId=" + LiveJson.Escape(streamId), null, ct).ConfigureAwait(false);
            var items = LiveJson.Items(await Request(HttpMethod.Get, "videos?part=snippet&id=" + LiveJson.Escape(broadcastId), null, ct).ConfigureAwait(false));
            if (items.Length != 1) throw new InvalidOperationException("YouTube не вернул видео созданного эфира.");
            var original = LiveJson.Obj(LiveJson.Get(items[0], "snippet"));
            var snippet = new Dictionary<string, object>();
            foreach (string key in new[] { "title", "description", "categoryId", "defaultLanguage", "defaultAudioLanguage" }) if (original.ContainsKey(key)) snippet[key] = original[key];
            if (!snippet.ContainsKey("categoryId")) throw new InvalidOperationException("YouTube не вернул categoryId эфира.");
            snippet["title"] = options.Title; snippet["description"] = options.Description ?? ""; snippet["tags"] = options.Tags;
            await Request(HttpMethod.Put, "videos?part=snippet", new { id = broadcastId, snippet }, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(options.Thumbnail)) {
                string mime = Path.GetExtension(options.Thumbnail).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
                await Request(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/thumbnails/set?uploadType=media&videoId=" + LiveJson.Escape(broadcastId), null, ct, File.ReadAllBytes(options.Thumbnail), mime).ConfigureAwait(false);
            }
        }
        async Task<string> State(string resource, string id, string key, CancellationToken ct) {
            var items = LiveJson.Items(await Request(HttpMethod.Get, resource + "?part=status&id=" + LiveJson.Escape(id), null, ct).ConfigureAwait(false));
            return items.Length == 0 ? "missing" : LiveJson.Text(LiveJson.Get(items[0], "status"), key);
        }
        public Task<string> BroadcastState(string id, CancellationToken ct) => State("liveBroadcasts", id, "lifeCycleStatus", ct);
        public Task<string> StreamState(string id, CancellationToken ct) => State("liveStreams", id, "streamStatus", ct);
        async Task Discover(LiveJournalEntry entry, CancellationToken ct) {
            string marker = Marker(entry.OperationId);
            foreach (string resource in new[] { "liveBroadcasts", "liveStreams" }) {
                if (resource == "liveBroadcasts" ? !string.IsNullOrEmpty(entry.BroadcastId) : !string.IsNullOrEmpty(entry.StreamId)) continue;
                string page = ""; bool done = false;
                for (int i = 0; i < 20; i++) {
                    string query = resource + "?part=id,snippet&mine=true&maxResults=50" + (resource == "liveBroadcasts" ? "&broadcastType=all" : "") + "&pageToken=" + LiveJson.Escape(page);
                    var result = await Request(HttpMethod.Get, query, null, ct).ConfigureAwait(false);
                    foreach (var item in LiveJson.Items(result)) if (LiveJson.Text(LiveJson.Get(item, "snippet"), "description").Contains(marker)) {
                        if (resource == "liveBroadcasts") entry.BroadcastId = LiveJson.Text(item, "id"); else entry.StreamId = LiveJson.Text(item, "id");
                        store.Journal(entry);
                    }
                    page = LiveJson.Text(result, "nextPageToken"); if (string.IsNullOrEmpty(page)) { done = true; break; }
                }
                if (!done) throw new InvalidOperationException("Не удалось полностью проверить оставшиеся эфиры. Проверьте YouTube Studio и повторите завершение.");
            }
        }
        public static string Marker(string operationId) => "[VideoBatch:" + operationId + "]";
        public async Task Complete(LiveJournalEntry entry, CancellationToken ct) {
            await VerifyChannel(entry.RemoteId, "", ct).ConfigureAwait(false);
            await Discover(entry, ct).ConfigureAwait(false);
            if ((entry.BroadcastAttempted && string.IsNullOrEmpty(entry.BroadcastId)) || (entry.StreamAttempted && string.IsNullOrEmpty(entry.StreamId)))
                throw new InvalidOperationException("Ответ на создание потерян, а созданный ресурс пока не найден. Повторите завершение позже и проверьте YouTube Studio; повторный запуск заблокирован.");
            if (!string.IsNullOrEmpty(entry.BroadcastId)) {
                string state = await BroadcastState(entry.BroadcastId, ct).ConfigureAwait(false);
                if (state != "complete" && state != "missing" && state != "revoked") {
                    if (state == "live" || state == "testing" || state == "liveStarting" || state == "testStarting") {
                        try { await Request(HttpMethod.Post, "liveBroadcasts/transition?part=status&broadcastStatus=complete&id=" + LiveJson.Escape(entry.BroadcastId), null, ct).ConfigureAwait(false); }
                        catch (LiveApiException) { if (await BroadcastState(entry.BroadcastId, ct).ConfigureAwait(false) != "complete") throw; }
                        for (int i = 0; i < 15; i++) {
                            state = await BroadcastState(entry.BroadcastId, ct).ConfigureAwait(false);
                            if (state == "complete" || state == "missing") break;
                            await Task.Delay(2000, ct).ConfigureAwait(false);
                        }
                        if (state != "complete" && state != "missing") throw new InvalidOperationException("YouTube ещё не подтвердил завершение эфира.");
                    } else {
                        await Request(HttpMethod.Delete, "liveBroadcasts?id=" + LiveJson.Escape(entry.BroadcastId), null, ct).ConfigureAwait(false);
                    }
                }
            }
            if (!string.IsNullOrEmpty(entry.StreamId)) {
                string streamState = await StreamState(entry.StreamId, ct).ConfigureAwait(false);
                for (int i = 0; i < 15 && streamState == "active"; i++) {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    streamState = await StreamState(entry.StreamId, ct).ConfigureAwait(false);
                }
                if (streamState == "active") throw new InvalidOperationException("YouTube ещё принимает видеопоток. Повторите завершение позже.");
                if (streamState != "missing") await Request(HttpMethod.Delete, "liveStreams?id=" + LiveJson.Escape(entry.StreamId), null, ct).ConfigureAwait(false);
            }
        }
        public void Dispose() { http.Dispose(); tokenGate.Dispose(); }
    }

    public static class YouTubeLiveOAuth {
        static string RandomUrl(int size) { using (var random = RandomNumberGenerator.Create()) { byte[] bytes = new byte[size]; random.GetBytes(bytes); return Url(bytes); } }
        static string Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public static async Task<LiveAccount> Connect(LiveStore store, string localId, CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(store.Config.ClientId)) throw new InvalidOperationException("Сначала импортируйте OAuth JSON для Desktop app.");
            string verifier = RandomUrl(32), state = RandomUrl(32), challenge;
            using (var hash = SHA256.Create()) challenge = Url(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            // TcpListener binds a random loopback port without HttpListener URL ACL/elevation requirements.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                using (deadline.Token.Register(() => listener.Stop())) try {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    string redirect = "http://127.0.0.1:" + port + "/";
                    string auth = "https://accounts.google.com/o/oauth2/v2/auth?client_id=" + LiveJson.Escape(store.Config.ClientId)
                        + "&redirect_uri=" + LiveJson.Escape(redirect) + "&response_type=code&scope=" + LiveJson.Escape("https://www.googleapis.com/auth/youtube.force-ssl")
                        + "&access_type=offline&prompt=consent%20select_account&state=" + state + "&code_challenge=" + challenge + "&code_challenge_method=S256";
                    Process.Start(new ProcessStartInfo(auth) { UseShellExecute = true });
                    string code = null;
                    while (code == null) using (var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false)) using (deadline.Token.Register(() => client.Close())) using (var stream = client.GetStream()) {
                        var line = new StringBuilder(); byte[] one = new byte[1];
                        while (line.Length < 8192 && await stream.ReadAsync(one, 0, 1, deadline.Token).ConfigureAwait(false) > 0) { if (one[0] == 10) break; line.Append((char)one[0]); }
                        var parts = line.ToString().Split(' ');
                        var request = parts.Length >= 2 && parts[0] == "GET" ? new Uri(redirect.TrimEnd('/') + parts[1]) : null;
                        var values = request == null ? null : System.Web.HttpUtility.ParseQueryString(request.Query);
                        bool valid = values != null && values["state"] == state && (!string.IsNullOrEmpty(values["code"]) || !string.IsNullOrEmpty(values["error"]));
                        string message = valid ? "You can return to VideoBatch." : "Invalid OAuth callback.";
                        byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 " + (valid ? "200 OK" : "400 Bad Request") + "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + Encoding.UTF8.GetByteCount(message) + "\r\nConnection: close\r\n\r\n" + message);
                        await stream.WriteAsync(response, 0, response.Length, deadline.Token).ConfigureAwait(false);
                        if (!valid) continue;
                        if (!string.IsNullOrEmpty(values["error"])) throw new InvalidOperationException("Доступ к YouTube не предоставлен.");
                        code = values["code"];
                    }
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) }) {
                        var fields = new Dictionary<string, string> { ["client_id"] = store.Config.ClientId, ["code"] = code, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier };
                        string secret = WindowsSupport.Unprotect(store.Config.ProtectedClientSecret);
                        if (!string.IsNullOrEmpty(secret)) fields["client_secret"] = secret;
                        using (var body = new FormUrlEncodedContent(fields)) using (var result = await http.PostAsync("https://oauth2.googleapis.com/token", body, deadline.Token).ConfigureAwait(false)) {
                            if (!result.IsSuccessStatusCode) throw new InvalidOperationException("OAuth: HTTP " + (int)result.StatusCode + ". Проверьте Desktop client и тестовых пользователей.");
                            var tokens = LiveJson.Object(await result.Content.ReadAsStringAsync().ConfigureAwait(false));
                            string refresh = LiveJson.Text(tokens, "refresh_token"), token = LiveJson.Text(tokens, "access_token");
                            if (string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(token)) throw new InvalidOperationException("Google не вернул offline-доступ. Повторите подключение.");
                            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=id,snippet&mine=true")) {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                                using (var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false)) {
                                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Не удалось получить подключённый канал: HTTP " + (int)response.StatusCode);
                                    var channels = LiveJson.Items(LiveJson.Object(await response.Content.ReadAsStringAsync().ConfigureAwait(false)));
                                    if (channels.Length != 1) throw new InvalidOperationException("Выберите один YouTube-канал при авторизации.");
                                    return new LiveAccount { LocalId = localId, RemoteId = LiveJson.Text(channels[0], "id"), Name = LiveJson.Text(LiveJson.Get(channels[0], "snippet"), "title"), ProtectedRefreshToken = WindowsSupport.Protect(refresh) };
                                }
                            }
                        }
                    }
                } catch (Exception) when (deadline.IsCancellationRequested) { throw new OperationCanceledException("Подключение отменено или истекло время ожидания.", deadline.Token); }
                finally { listener.Stop(); }
            }
        }
    }
}
