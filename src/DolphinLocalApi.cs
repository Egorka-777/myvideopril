using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class DolphinLocalApi {
        public const string Build = "2026-10-29-dolphin-auth-v2";

        public static async Task<int> ResolvePortAsync(string token, int preferredPort, CancellationToken cancel = default(CancellationToken), Action<string> report = null) {
            if (string.IsNullOrWhiteSpace(token)) throw new Exception("Укажите API-токен Dolphin в настройках.");
            if (report == null) {
                string id = Guid.NewGuid().ToString("N").Substring(0, 8);
                report = line => AppDiagnostics.Write("Dolphin", "", id, "connection_probe", line);
            }
            report("Проверка API Dolphin · " + Build);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                try {
                    // Process/port enumeration must never run on the WinForms UI thread.
                    return await Task.Run(() => ResolvePortsAsync(token.Trim(), CandidatePorts(preferredPort),
                        new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }, deadline.Token, report), deadline.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) when (!cancel.IsCancellationRequested) {
                    throw new TimeoutException("Проверка API Dolphin превысила 20 секунд. Укажите API-порт из окна Health в Dolphin; подробности — в журнале приложения.");
                }
            }
        }

        // Tests exercise the real HTTP handling without Windows process discovery.
        internal static async Task<int> ResolvePortsAsync(string token, IEnumerable<int> ports, HttpMessageHandler handler, CancellationToken cancel, Action<string> report = null) {
            var replies = new List<string>();
            var json = new JavaScriptSerializer();
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2.5) }) {
                foreach (int port in ports) {
                    cancel.ThrowIfCancellationRequested();
                    string result;
                    try {
                        using (var body = new StringContent(json.Serialize(new { token }), Encoding.UTF8, "application/json"))
                        using (var response = await client.PostAsync("http://127.0.0.1:" + port + "/v1.0/auth/login-with-token", body, cancel).ConfigureAwait(false)) {
                            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            bool confirmed = false;
                            try {
                                var data = json.DeserializeObject((text ?? "").TrimStart('\uFEFF')) as Dictionary<string, object>;
                                object success;
                                confirmed = data != null && data.TryGetValue("success", out success) && success is bool && (bool)success;
                            } catch (ArgumentException) { } catch (InvalidOperationException) { }
                            result = "HTTP " + (int)response.StatusCode + (confirmed ? "; success=true" : "; вход не подтверждён");
                            report?.Invoke("POST 127.0.0.1:" + port + "/v1.0/auth/login-with-token → " + result);
                            if (response.IsSuccessStatusCode && confirmed) return port;
                        }
                    } catch (OperationCanceledException) when (!cancel.IsCancellationRequested) {
                        result = "таймаут";
                        report?.Invoke("Порт " + port + " → " + result);
                    } catch (HttpRequestException) {
                        result = "нет соединения";
                        report?.Invoke("Порт " + port + " → " + result);
                    }
                    // Internal Dolphin services may reject this route. Keep searching.
                    replies.Add(port + ": " + result);
                }
            }
            throw new Exception("Не удалось подтвердить вход в локальный API Dolphin. Ответы: " +
                string.Join("; ", replies.GetRange(0, Math.Min(replies.Count, 5))) +
                ". Проверьте API-порт в окне Health. Отказ одного из служебных портов не доказывает, что API-токен неверный; все попытки записаны в журнале.");
        }

        static IEnumerable<int> CandidatePorts(int preferred) {
            if (preferred < 1 || preferred > 65535) preferred = 3001;
            var seen = new HashSet<int>();
            seen.Add(preferred); yield return preferred;
            if (seen.Add(3001)) yield return 3001;
            // Lazy: a working configured/default port never runs netstat.
            foreach (int port in DiscoverDolphinListeningPorts()) if (seen.Add(port)) yield return port;
            for (int port = 3002; port <= 3020; port++) if (seen.Add(port)) yield return port;
        }

        static List<int> DiscoverDolphinListeningPorts() {
            var ports = new List<int>();
            try {
                var pids = new HashSet<int>();
                foreach (var proc in Process.GetProcessesByName("Dolphin Anty")) {
                    using (proc) { try { pids.Add(proc.Id); } catch (InvalidOperationException) { } }
                }
                if (pids.Count == 0) return ports;
                var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp") {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using (var netstat = Process.Start(psi)) {
                    if (netstat == null) return ports;
                    var output = netstat.StandardOutput.ReadToEndAsync();
                    if (!netstat.WaitForExit(2500)) { try { netstat.Kill(); } catch { } return ports; }
                    if (!output.Wait(500)) return ports;
                    foreach (string line in output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        int pid, port;
                        if (parts.Length < 5 || !int.TryParse(parts[parts.Length - 1], out pid) || !pids.Contains(pid)) continue;
                        int colon = parts[1].LastIndexOf(':');
                        if (colon >= 0 && int.TryParse(parts[1].Substring(colon + 1), out port) && port > 0 && port <= 65535 && !ports.Contains(port)) ports.Add(port);
                    }
                }
            } catch { /* Explicit/default ports still work if discovery is unavailable. */ }
            return ports;
        }
    }
}
