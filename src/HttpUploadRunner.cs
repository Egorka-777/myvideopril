using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    [DataContract]
    public sealed class HttpUploadItemJob {
        [DataMember] public string localJobId, video, title, scheduleDate, scheduleTime;
        [DataMember] public long scheduledUnixSeconds;
    }

    [DataContract]
    public sealed class HttpUploadJob {
        [DataMember] public string profileId, expectedIp, expectedChannelId;
        [DataMember] public int localPort;
        [DataMember] public bool keepProfileOpen = true;
        [DataMember] public HttpUploadItemJob[] items;
    }

    public static class HttpUploadRunner {
        static readonly DataContractJsonSerializer JobSerializer = new DataContractJsonSerializer(typeof(HttpUploadJob));
        static readonly DataContractJsonSerializer MessageSerializer = new DataContractJsonSerializer(typeof(UploadMessage));

        public static string Worker => Path.Combine(DolphinRunner.Root, "worker-http.js");

        public static void CheckFiles() {
            if (!File.Exists(DolphinRunner.Node) || !File.Exists(Worker) || !Directory.Exists(Path.Combine(DolphinRunner.Root, "node_modules", "playwright-core")))
                throw new Exception("Архив распакован не полностью: не найден модуль быстрой HTTP-загрузки (worker-http.js).");
        }

        public static async Task<UploadRunResult> Run(HttpUploadJob job, string token, Action<UploadMessage> update, CancellationToken cancel) {
            CheckFiles();
            Directory.CreateDirectory(Path.Combine(Store.Root, "jobs"));
            string file = Path.Combine(Store.Root, "jobs", "http-" + Guid.NewGuid().ToString("N") + ".json");
            ProcessWrapper process = null;
            var result = new UploadRunResult();
            try {
                using (var memory = new MemoryStream()) {
                    JobSerializer.WriteObject(memory, job);
                    File.WriteAllBytes(file, memory.ToArray());
                }
                var info = new System.Diagnostics.ProcessStartInfo(DolphinRunner.Node, Core.Quote(Worker) + " " + Core.Quote(file)) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = DolphinRunner.Root
                };
                info.EnvironmentVariables["VIDEOBATCH_DOLPHIN_TOKEN"] = token ?? "";
                var proc = System.Diagnostics.Process.Start(info);
                if (proc == null) throw new Exception("Не удалось запустить HTTP-загрузчик.");
                process = new ProcessWrapper(proc);
                var stderr = process.Process.StandardError.ReadToEndAsync();
                using (cancel.Register(() => { try { if (process?.Process != null && !process.Process.HasExited) process.Process.Kill(); } catch { } })) {
                    string line;
                    while ((line = await process.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try {
                            using (var m = new MemoryStream(Encoding.UTF8.GetBytes(line))) {
                                var msg = (UploadMessage)MessageSerializer.ReadObject(m);
                                update?.Invoke(msg);
                                if (msg.ip != null) result.Ip = msg.ip;
                                if (msg.url != null) result.Url = msg.url;
                                if (msg.stage == "done") {
                                    result.Success = msg.success;
                                    if (!msg.success && string.IsNullOrWhiteSpace(result.Error)) result.Error = msg.text;
                                }
                                if (msg.stage == "error" || msg.stage == "manual_check") {
                                    result.Error = msg.error ?? msg.text;
                                    result.KeptOpen = msg.keptOpen;
                                    if (msg.stage == "manual_check") result.Success = false;
                                }
                            }
                        } catch {
                            update?.Invoke(new UploadMessage { stage = "log", text = line });
                        }
                    }
                    await Task.Run(() => process.Process.WaitForExit()).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    string err = await stderr.ConfigureAwait(false);
                    if (process.Process.ExitCode != 0 && !result.Success && string.IsNullOrWhiteSpace(result.Error))
                        result.Error = string.IsNullOrWhiteSpace(err) ? "HTTP-загрузчик завершился с ошибкой." : err.Trim();
                    if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
                        throw new UploadException("Загрузчик не подтвердил завершение. Проверьте профиль Dolphin перед повтором.", result.KeptOpen);
                    if (!result.Success)
                        throw new UploadException(string.IsNullOrWhiteSpace(result.Error) ? "Ошибка HTTP-загрузки." : result.Error, result.KeptOpen);
                    return result;
                }
            } finally {
                process?.Dispose();
                try { File.Delete(file); } catch { }
            }
        }

        sealed class ProcessWrapper : IDisposable {
            public System.Diagnostics.Process Process { get; }
            public ProcessWrapper(System.Diagnostics.Process p) { Process = p; }
            public void Dispose() {
                try { if (Process != null) { if (!Process.HasExited) Process.Kill(); Process.Dispose(); } } catch { }
            }
        }
    }
}
