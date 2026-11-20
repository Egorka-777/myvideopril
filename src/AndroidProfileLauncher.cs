using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    internal static class AndroidProfileLauncher {
        static readonly HashSet<string> opening = new HashSet<string>(StringComparer.Ordinal);

        public static async Task Open(Preferences settings, string profileId, string displayName, string packageName, Action<string> log) {
            string id = (profileId ?? "").Trim();
            if (!Regex.IsMatch(id, @"^\d+$")) throw new InvalidOperationException("Нужен числовой Dolphin Profile ID.");
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
                string worker = Path.Combine(Path.GetDirectoryName(typeof(AndroidProfileLauncher).Assembly.Location), "tools", "android", "worker.py");
                if (!File.Exists(python)) throw new InvalidOperationException("Не найден установленный Python 3.11.");
                if (!File.Exists(worker)) throw new InvalidOperationException("Не найден tools/android/worker.py рядом с приложением.");
                var job = new {
                    action = "open", profile_id = id, name = displayName, package = packageName, proxy = proxy.url,
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
                    process.Start();
                    // Drain stderr without logging: native tools may echo credentials there.
                    var errors = DrainErrors(process.StandardError);
                    var json = new JavaScriptSerializer();
                    using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))) {
                        await input.WriteLineAsync(json.Serialize(job));
                    }
                    bool ready = false, failed = false, invalid = false;
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync()) != null) {
                        Dictionary<string, object> ev;
                        try { ev = json.Deserialize<Dictionary<string, object>>(line); }
                        catch (ArgumentException) { invalid = true; continue; }
                        if (ev == null || !ev.TryGetValue("event", out var kind) || !(kind is string)
                            || !ev.TryGetValue("text", out var text) || !(text is string)) { invalid = true; continue; }
                        // Only the worker's explicitly safe text field may reach the UI.
                        switch ((string)kind) {
                            case "status": log((string)text); break;
                            case "error": failed = true; log((string)text); break;
                            case "ready": ready = true; break;
                            default: invalid = true; break;
                        }
                    }
                    await Task.Run(() => process.WaitForExit());
                    await errors;
                    AppDiagnostics.Write("Android", id, "", "open_exit", "exit=" + process.ExitCode + "; ready=" + ready + "; error=" + failed + "; invalid=" + invalid);
                    if (process.ExitCode != 0 || failed || invalid || !ready)
                        throw new InvalidOperationException("Android не подтвердил открытие профиля. Проверьте сообщения Android; вход в аккаунт не подтверждён.");
                }
                log("Android открыт. Вход в аккаунт проверяется в приложении; публикация остаётся через Dolphin.");
            } finally {
                lock (opening) opening.Remove(id);
            }
        }

        static async Task DrainErrors(StreamReader reader) {
            var buffer = new char[2048];
            while (await reader.ReadAsync(buffer, 0, buffer.Length) != 0) { }
        }
    }
}
