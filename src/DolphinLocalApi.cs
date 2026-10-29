using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class DolphinLocalApi {
        enum LoginOutcome { Failed, Ok, Unauthorized }

        public static async Task<int> ResolvePortAsync(string token, int preferredPort, CancellationToken cancel = default(CancellationToken)) {
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Шаг 1: укажите API-токен Dolphin. После переноса на другой ПК токен нужно ввести заново в «1. Dolphin…».");
            if (preferredPort < 1 || preferredPort > 65535) preferredPort = 3001;

            int lastStatus = 0;
            bool sawUnauthorized = false;
            bool wrongServiceOnPreferred = false;
            foreach (int port in CandidatePorts(preferredPort)) {
                cancel.ThrowIfCancellationRequested();
                var outcome = await TryLoginAsync(token, port, cancel).ConfigureAwait(false);
                if (outcome == LoginOutcome.Ok) return port;
                if (outcome == LoginOutcome.Unauthorized) sawUnauthorized = true;
                else {
                    int status = await LastHttpStatus(token, port, cancel).ConfigureAwait(false);
                    if (status != 0 && lastStatus == 0) lastStatus = status;
                    if (port == preferredPort && status == 404) wrongServiceOnPreferred = true;
                }
            }

            if (sawUnauthorized)
                throw new Exception("API-токен Dolphin не принят локальным API. Создайте новый токен в личном кабинете (dolphin-anty.com → API), вставьте в «1. Dolphin…» или Настройки, оставьте Dolphin Anty запущенным под тем же аккаунтом.");
            if (wrongServiceOnPreferred)
                throw new Exception("Порт " + preferredPort + " занят не Dolphin (HTTP 404). Закройте другой сервис на этом порту или укажите порт из окна Health в Dolphin Anty.");
            throw new Exception("Dolphin Anty не отвечает на локальном API (HTTP " + (lastStatus == 0 ? "404" : lastStatus.ToString()) + "). Запустите Dolphin, в окне Health посмотрите порт и укажите его в «1. Dolphin…».");
        }

        static IEnumerable<int> CandidatePorts(int preferred) {
            var ports = new List<int>();
            void add(int p) {
                if (p < 1 || p > 65535) return;
                if (!ports.Contains(p)) ports.Add(p);
            }
            foreach (int p in DiscoverDolphinListeningPorts()) add(p);
            add(preferred);
            for (int p = 3001; p <= 3020; p++) add(p);
            return ports;
        }

        /// <summary>When 3001 is taken by another app, Dolphin binds a random high port.</summary>
        static List<int> DiscoverDolphinListeningPorts() {
            var ports = new List<int>();
            try {
                var pids = new HashSet<int>();
                foreach (var proc in Process.GetProcessesByName("Dolphin Anty")) {
                    try { pids.Add(proc.Id); } catch { }
                }
                if (pids.Count == 0) return ports;

                var psi = new ProcessStartInfo("netstat.exe", "-ano") {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var netstat = Process.Start(psi)) {
                    if (netstat == null) return ports;
                    string line;
                    while ((line = netstat.StandardOutput.ReadLine()) != null) {
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 4) continue;
                        if (!int.TryParse(parts[parts.Length - 1], out int pid) || !pids.Contains(pid)) continue;
                        string addr = parts[1];
                        int colon = addr.LastIndexOf(':');
                        if (colon < 0) continue;
                        string portText = addr.Substring(colon + 1);
                        if (portText.StartsWith("[", StringComparison.Ordinal)) continue;
                        if (int.TryParse(portText, out int port) && port > 0 && port <= 65535 && !ports.Contains(port))
                            ports.Add(port);
                    }
                    netstat.WaitForExit(8000);
                }
            } catch { }
            return ports;
        }

        static async Task<LoginOutcome> TryLoginAsync(string token, int port, CancellationToken cancel) {
            using (var handler = new HttpClientHandler { UseProxy = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2.5) }) {
                var body = new StringContent("{\"token\":\"" + JsonEscape(token) + "\"}", Encoding.UTF8, "application/json");
                HttpResponseMessage resp;
                try { resp = await client.PostAsync("http://127.0.0.1:" + port + "/v1.0/auth/login-with-token", body, cancel).ConfigureAwait(false); }
                catch { return LoginOutcome.Failed; }

                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return LoginOutcome.Unauthorized;
                if (!resp.IsSuccessStatusCode) return LoginOutcome.Failed;
                if (Regex.IsMatch(text ?? "", @"""success""\s*:\s*false", RegexOptions.IgnoreCase))
                    return LoginOutcome.Unauthorized;
                return LoginOutcome.Ok;
            }
        }

        static async Task<int> LastHttpStatus(string token, int port, CancellationToken cancel) {
            using (var handler = new HttpClientHandler { UseProxy = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) }) {
                var body = new StringContent("{\"token\":\"" + JsonEscape(token) + "\"}", Encoding.UTF8, "application/json");
                try {
                    var resp = await client.PostAsync("http://127.0.0.1:" + port + "/v1.0/auth/login-with-token", body, cancel).ConfigureAwait(false);
                    return (int)resp.StatusCode;
                } catch { return 0; }
            }
        }

        static string JsonEscape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }
    }
}
