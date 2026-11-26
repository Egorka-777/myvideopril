using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    internal static class AndroidProfileLauncher {
        static readonly HashSet<string> opening = new HashSet<string>(StringComparer.Ordinal);

        public static Task Open(Preferences settings, string profileId, string displayName, string packageName, Action<string> log) {
            return Execute(settings, profileId, displayName, packageName, log, CancellationToken.None, 0, 0);
        }

        public static Task Warmup(Preferences settings, string profileId, string displayName, int minutes, int likePercent, CancellationToken ct, Action<string> log) {
            if (minutes <= 0 || minutes > 1440 || likePercent < 0 || likePercent > 100) throw new ArgumentOutOfRangeException();
            return Execute(settings, profileId, displayName, "com.zhiliaoapp.musically", log, ct, minutes * 60, likePercent);
        }

        internal static HashSet<string> BoundProfileIds() {
            string path = Path.Combine(Path.GetDirectoryName(typeof(AndroidProfileLauncher).Assembly.Location), "tools", "android", "bindings.json");
            if (!File.Exists(path)) return new HashSet<string>();
            var bindings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            return new HashSet<string>(bindings == null ? Enumerable.Empty<string>() : bindings.Keys);
        }

        static async Task Execute(Preferences settings, string profileId, string displayName, string packageName, Action<string> log, CancellationToken ct, int durationSeconds, int likePercent) {
            ct.ThrowIfCancellationRequested();
            bool warmup = durationSeconds > 0;
            string id = (profileId ?? "").Trim();
            if (!Regex.IsMatch(id, @"^\d+$")) throw new InvalidOperationException("Нужен числовой Dolphin Profile ID.");
            string worker = Path.Combine(Path.GetDirectoryName(typeof(AndroidProfileLauncher).Assembly.Location), "tools", "android", "worker.py");
            string bindingsPath = Path.Combine(Path.GetDirectoryName(worker), "bindings.json");
            if (!File.Exists(worker)) throw new InvalidOperationException("Не найден tools/android/worker.py рядом с приложением.");
            if (!File.Exists(bindingsPath)) throw new InvalidOperationException("Не найден файл привязок Android tools/android/bindings.json.");
            var bindings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(bindingsPath));
            if (bindings == null || !bindings.ContainsKey(id))
                throw new InvalidOperationException("Для этого Dolphin ID нет привязанного Android-устройства в bindings.json.");
            lock (opening) {
                if (!opening.Add(id)) throw new InvalidOperationException("Этот Android-профиль уже открывается. Дождитесь завершения.");
            }
            try {
                log("Получаю назначенный профилю прокси Dolphin для Android…");
                TelegramProxySync route;
                try {
                    route = await TelegramProxyCatalog.FetchDolphin(WindowsSupport.Unprotect(settings.ProtectedDolphinToken),
                        new[] { new KeyValuePair<string, string>(id, displayName) }, includeCatalog: false);
                } catch (Exception error) {
                    AppDiagnostics.Write("Android", id, "", "proxy_error", error.GetType().Name, "error");
                    throw new InvalidOperationException("Не удалось получить прокси Dolphin. Проверьте токен и доступность Dolphin.");
                }
                var proxy = route.Proxies.SingleOrDefault();
                if (proxy == null || string.IsNullOrWhiteSpace(proxy.url))
                    throw new InvalidOperationException("Назначенный профилю прокси Dolphin не получен. Android без прокси не запускается.");

                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string python = Path.Combine(local, "Programs", "Python", "Python311", "python.exe");
                if (!File.Exists(python)) throw new InvalidOperationException("Не найден установленный Python 3.11.");
                var job = new {
                    action = warmup ? "warmup" : "open", duration_seconds = durationSeconds, like_percent = likePercent,
                    profile_id = id, name = displayName, package = packageName, proxy = proxy.url,
                    root = Path.Combine(Store.Root, "android"), sdk = Path.Combine(local, "PsyDroid", "sdk"),
                    avd_home = Path.Combine(local, "PsyDroid", "data", "avd"), apk_root = Path.Combine(local, "PsyDroid")
                };
                var start = new ProcessStartInfo(python, Core.Quote(worker)) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(worker)
                };
                start.EnvironmentVariables["PYTHONUTF8"] = "1";
                using (var process = new Process { StartInfo = start }) {
                    ct.ThrowIfCancellationRequested();
                    process.Start();
                    // Drain stderr without logging: native tools may echo credentials there.
                    var errors = DrainErrors(process.StandardError);
                    var json = new JavaScriptSerializer();
                    using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true })
                    using (var finished = new CancellationTokenSource()) {
                    input.WriteLine(json.Serialize(job));
                    using (ct.Register(() => {
                        try { input.WriteLine("cancel"); } catch (IOException) { } catch (ObjectDisposedException) { }
                    })) {
                    var stopWatch = StopUnresponsiveWorker(process, ct, finished.Token);
                    bool ready = false, failed = false, invalid = false;
                    string failure = null, line;
                    while ((line = await process.StandardOutput.ReadLineAsync()) != null) {
                        if (!HandleAndroidWorkerLine(line, json, id, warmup, log, ref ready, ref failed, ref invalid, ref failure)) invalid = true;
                        if (!warmup && ready) break;
                    }
                    if (!warmup && ready) {
                        finished.Cancel();
                        ContinueAndroidSessionMonitor(process, id, json);
                        AppDiagnostics.Write("Android", id, "", "open_exit", "ready; session monitor detached");
                        return;
                    }
                    await Task.Run(() => process.WaitForExit());
                    await errors;
                    finished.Cancel(); await stopWatch;
                    AppDiagnostics.Write("Android", id, "", warmup ? "warmup_exit" : "open_exit", "exit=" + process.ExitCode + "; ready=" + ready + "; error=" + failed + "; invalid=" + invalid);
                    ct.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0 || failed || invalid || !ready)
                        throw new InvalidOperationException(failure ?? "Android не подтвердил результат. Проверьте сообщения Android; успешный прогрев не подтверждён.");
                    }
                    }
                }
                if (!warmup) log("Android открыт. Вход в аккаунт проверяется в приложении; публикация остаётся через Dolphin.");
            } finally {
                lock (opening) opening.Remove(id);
            }
        }

        static async Task StopUnresponsiveWorker(Process process, CancellationToken ct, CancellationToken finished) {
            try {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, finished)) {
                    try { await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                if (!ct.IsCancellationRequested || finished.IsCancellationRequested) return;
                await Task.Delay(TimeSpan.FromSeconds(40), finished).ConfigureAwait(false);
                // Kill only the worker: the emulator and its VPN must remain visible and running.
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            } catch (OperationCanceledException) { }
        }

        static async Task DrainErrors(StreamReader reader) {
            var buffer = new char[2048];
            while (await reader.ReadAsync(buffer, 0, buffer.Length) != 0) { }
        }

        static bool HandleAndroidWorkerLine(string line, JavaScriptSerializer json, string profileId, bool warmup, Action<string> log,
            ref bool ready, ref bool failed, ref bool invalid, ref string failure) {
            Dictionary<string, object> ev;
            try { ev = json.Deserialize<Dictionary<string, object>>(line); }
            catch (ArgumentException) { return false; }
            if (ev == null || !ev.TryGetValue("event", out var kind) || !(kind is string)) return false;
            string text = ev.TryGetValue("text", out var textObj) && textObj is string ? (string)textObj : "";
            switch ((string)kind) {
                case "status": log(text); break;
                case "error":
                    failed = true; failure = text; log(failure);
                    AppDiagnostics.Write("Android", profileId, "", warmup ? "warmup_worker_error" : "open_worker_error", text, "error");
                    break;
                case "stats": log(text); AppDiagnostics.Write("Android", profileId, "", "warmup_stats", text); break;
                case "completed": if (warmup) { ready = true; log(text); } else return false; break;
                case "ready": if (!warmup) { ready = true; log(text); } else return false; break;
                case "session": ApplyAndroidSessionEvent(ev, profileId); break;
                default: return false;
            }
            return true;
        }

        static void ApplyAndroidSessionEvent(Dictionary<string, object> ev, string profileId) {
            string sessionResult = Convert.ToString(ev.TryGetValue("result", out var sr) ? sr : "");
            bool? authorized = sessionResult == "verified" ? (bool?)true
                : sessionResult == "not_logged_in" ? (bool?)false : null;
            if (!authorized.HasValue) return;
            if (authorized == false && DashboardSessionState.IsVerified(profileId, "TikTok")) return;
            string reason = Convert.ToString(ev.TryGetValue("reason", out var rs) ? rs : "");
            string account = Convert.ToString(ev.TryGetValue("account_name", out var an) ? an : "");
            if (authorized == true) AndroidTikTokSessionStore.SaveVerified(profileId, account);
            DashboardSessionState.Record(profileId, "TikTok", new DashboardCheckResult {
                Authorized = authorized.Value,
                AccountName = account,
                Detail = string.IsNullOrWhiteSpace(reason) ? "Android TikTok." : "Android TikTok: " + reason
            });
        }

        static void ContinueAndroidSessionMonitor(Process process, string profileId, JavaScriptSerializer json) {
            Task.Run(async () => {
                try {
                    bool ready = true, failed = false, invalid = false;
                    string failure = null, line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                        if (!HandleAndroidWorkerLine(line, json, profileId, warmup: false, _ => { }, ref ready, ref failed, ref invalid, ref failure))
                            invalid = true;
                    }
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                } catch { }
            });
        }
    }
}
