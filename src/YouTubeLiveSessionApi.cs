using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    // Adapter to the authenticated Studio browser. No Google OAuth, cookies or stream keys on disk.
    public sealed class YouTubeLiveSessionApi : IYouTubeLiveApi {
        readonly LiveAccount account;
        readonly Preferences preferences;
        readonly Func<Dictionary<string, object>, CancellationToken, Task<Dictionary<string, object>>> runner;
        string operation = Guid.NewGuid().ToString("N");
        public string ChannelName { get; private set; }
        public string ChannelId { get; private set; }
        public YouTubeLiveSessionApi(LiveAccount account, Preferences preferences,
            Func<Dictionary<string, object>, CancellationToken, Task<Dictionary<string, object>>> runner = null) {
            this.account = account; this.preferences = preferences; this.runner = runner ?? RunWorker;
        }
        public static void CheckFiles() {
            if (!File.Exists(DolphinRunner.Node) || !File.Exists(Path.Combine(DolphinRunner.Root, "worker-youtube-live-session.js"))
                || !Directory.Exists(Path.Combine(DolphinRunner.Root, "node_modules", "playwright-core")))
                throw new InvalidOperationException("Нужен полный runtime и новый worker-youtube-live-session.js рядом с EXE.");
        }
        public static void AssertProfileCanStop(int port, string profileId) {
            string key;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(port + ":" + profileId))).Replace("-", "").ToLowerInvariant();
            if (File.Exists(Path.Combine(Store.Root, "live-session", "leases", key + ".json")))
                throw new InvalidOperationException("Dolphin-профиль занят прямым эфиром. Сначала завершите эфир.");
        }
        async Task<Dictionary<string, object>> Call(string command, CancellationToken ct, string broadcast = "", LiveOptions options = null) {
            var job = new Dictionary<string, object> {
                ["command"] = command, ["operationId"] = operation, ["profileId"] = account.ProfileId,
                ["expectedChannelId"] = account.RemoteId, ["localPort"] = preferences?.DolphinPort ?? 3001,
                ["stateDirectory"] = Path.Combine(Store.Root, "live-session"), ["broadcastId"] = broadcast
            };
            if (options != null) job["options"] = new Dictionary<string, object> {
                ["title"] = options.Title, ["description"] = options.Description, ["thumbnail"] = options.Thumbnail,
                ["tags"] = options.Tags, ["privacy"] = options.Privacy, ["madeForKids"] = options.MadeForKids
            };
            return await runner(job, ct).ConfigureAwait(false);
        }
        public async Task VerifyChannel(string id, string url, CancellationToken ct) {
            var match = Regex.Match(url ?? "", @"/channel/(UC[A-Za-z0-9_-]+)");
            if (match.Success && !string.IsNullOrEmpty(id) && match.Groups[1].Value != id)
                throw new InvalidOperationException("Ссылка канала не совпадает с сохранённой сессией.");
            var result = await Call("verify", ct).ConfigureAwait(false);
            ChannelId = LiveJson.Text(result, "channelId"); ChannelName = LiveJson.Text(result, "channelName");
            if (!Regex.IsMatch(ChannelId ?? "", @"^UC[A-Za-z0-9_-]+$") || (!string.IsNullOrEmpty(id) && id != ChannelId)
                || (match.Success && match.Groups[1].Value != ChannelId)) throw new InvalidOperationException("В Dolphin открыт другой YouTube-канал.");
        }
        public async Task<string> CreateBroadcast(LiveOptions options, string marker, CancellationToken ct) {
            var match = Regex.Match(marker ?? "", @"\[VideoBatch:([a-f0-9]{32})\]");
            if (!match.Success) throw new InvalidOperationException("Отсутствует идентификатор операции эфира.");
            operation = match.Groups[1].Value;
            var result = await Call("create", ct, options: options).ConfigureAwait(false);
            string id = LiveJson.Text(result, "broadcastId");
            if (!Regex.IsMatch(id ?? "", @"^[A-Za-z0-9_-]{11}$")) throw new InvalidOperationException("Studio не подтвердил ID созданного эфира. Проверьте завершение.");
            return id;
        }
        public async Task<Dictionary<string, object>> CreateStream(LiveOptions options, string marker, CancellationToken ct) {
            var result = await Call("ingest", ct).ConfigureAwait(false);
            // Studio owns the binding. This internal handle is the broadcast ID, not a Data API liveStream resource.
            return new Dictionary<string, object> { ["id"] = LiveJson.Text(result, "broadcastId"), ["cdn"] = new Dictionary<string, object> {
                ["ingestionInfo"] = new Dictionary<string, object> { ["rtmpsIngestionAddress"] = LiveJson.Text(result, "address"), ["streamName"] = LiveJson.Text(result, "key") }
            } };
        }
        public async Task Configure(string broadcast, string stream, LiveOptions options, CancellationToken ct) {
            await Call("configure", ct, broadcast, options).ConfigureAwait(false);
        }
        public async Task<string> BroadcastState(string id, CancellationToken ct) {
            return LiveJson.Text(await Call("status", ct, id).ConfigureAwait(false), "broadcastState");
        }
        public async Task<string> StreamState(string id, CancellationToken ct) {
            return LiveJson.Text(await Call("stream-status", ct, id).ConfigureAwait(false), "streamState");
        }
        public async Task Complete(LiveJournalEntry entry, CancellationToken ct) {
            operation = entry.OperationId;
            if (!entry.BroadcastAttempted && !entry.StreamAttempted && string.IsNullOrEmpty(entry.BroadcastId)) return;
            await Call("complete", ct, entry.BroadcastId).ConfigureAwait(false);
        }
        async Task<Dictionary<string, object>> RunWorker(Dictionary<string, object> job, CancellationToken ct) {
            CheckFiles();
            string token = WindowsSupport.Unprotect(preferences?.ProtectedDolphinToken ?? "");
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Укажите API-токен Dolphin в настройках приложения.");
            var info = new ProcessStartInfo(DolphinRunner.Node, Core.Quote(Path.Combine(DolphinRunner.Root, "worker-youtube-live-session.js"))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = DolphinRunner.Root
            };
            info.EnvironmentVariables["VIDEOBATCH_DOLPHIN_TOKEN"] = token;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)) using (var process = new Process { StartInfo = info }) {
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                if (!process.Start()) throw new InvalidOperationException("Не удалось запустить управление эфиром через Dolphin.");
                YouTubeLiveProcessOwner owner;
                try { owner = new YouTubeLiveProcessOwner(process); }
                catch { if (!process.HasExited) process.Kill(); throw; }
                using (owner)
                using (timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } })) {
                    try {
                        var drain = process.StandardError.ReadToEndAsync();
                        string json = new JavaScriptSerializer().Serialize(job);
                        await process.StandardInput.WriteLineAsync(json).ConfigureAwait(false); process.StandardInput.Close();
                        Dictionary<string, object> reply = null;
                        string line;
                        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                            if (line.Length > 1024 * 1024) throw new InvalidOperationException("Некорректный ответ Studio worker.");
                            var value = LiveJson.Object(line);
                            if (LiveJson.Text(value, "type") == "result") reply = value;
                        }
                        await Task.Run(() => process.WaitForExit()).ConfigureAwait(false); await drain.ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested) throw new TimeoutException("Studio не подтвердил операцию за 5 минут. Проверьте завершение эфира.");
                        if (reply == null || process.ExitCode != 0 || !object.Equals(LiveJson.Get(reply, "ok"), true))
                            throw new InvalidOperationException(reply == null ? "Сессия Studio не подтвердила операцию. Повторный эфир не создаётся." : LiveJson.Text(reply, "error"));
                        return LiveJson.Get(reply, "data") as Dictionary<string, object> ?? new Dictionary<string, object>();
                    } finally { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
                }
            }
        }
        public void Dispose() { }
    }
}
