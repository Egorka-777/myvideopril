using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    [DataContract]
    public sealed class HttpTikTokItemJob {
        [DataMember] public string localJobId, video, caption, description, publishMode;
        [DataMember] public long scheduledUnixSeconds;
        [DataMember] public int packIndex;
    }

    [DataContract]
    public sealed class HttpTikTokUploadJob {
        [DataMember] public string profileId, expectedIp, market, runId;
        [DataMember] public int localPort;
        [DataMember] public bool keepProfileOpen = true, checkOnly;
        [DataMember] public HttpTikTokItemJob[] items;
    }

    public sealed class HttpTikTokUploadRunResult {
        public bool Success, KeptOpen, WorkerStarted, ManualCheck;
        public string Ip = "", Url = "", Error = "", LastStage = "", ProjectId = "";
        public string JobJsonPath = "", DiagnosticPath = "", RunId = "";
    }

    public static class HttpTikTokUploadRunner {
        public static string Worker => Path.Combine(DolphinRunner.Root, "worker-tiktok-http.js");

        public static void CheckFiles() {
            if (!File.Exists(DolphinRunner.Node) || !File.Exists(Worker) || !Directory.Exists(Path.Combine(DolphinRunner.Root, "node_modules", "playwright-core")))
                throw new Exception("Архив распакован не полностью: не найден модуль HTTP-загрузки TikTok (worker-tiktok-http.js).");
        }

        static byte[] SerializeJob(HttpTikTokUploadJob job) {
            var serializer = new DataContractJsonSerializer(typeof(HttpTikTokUploadJob));
            using (var memory = new MemoryStream()) {
                serializer.WriteObject(memory, job);
                return memory.ToArray();
            }
        }

        public static async Task<HttpTikTokUploadRunResult> Run(HttpTikTokUploadJob job, string token, Action<UploadMessage> update, CancellationToken cancel) {
            CheckFiles();
            if (string.IsNullOrWhiteSpace(job.runId)) job.runId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(Store.Root, "jobs"));
            string file = Path.Combine(Store.Root, "jobs", "tiktok-http-" + job.runId + ".json");
            var result = new HttpTikTokUploadRunResult { JobJsonPath = file, RunId = job.runId };
            var messageSerializer = new DataContractJsonSerializer(typeof(UploadMessage));
            System.Diagnostics.Process process = null;
            try {
                File.WriteAllBytes(file, SerializeJob(job));
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
                info.EnvironmentVariables["VIDEOBATCH_RUN_ID"] = job.runId ?? "";
                process = System.Diagnostics.Process.Start(info);
                if (process == null) throw new Exception("Не удалось запустить HTTP-загрузчик TikTok.");
                result.WorkerStarted = true;
                var stderr = process.StandardError.ReadToEndAsync();
                using (cancel.Register(() => { try { if (process != null && !process.HasExited) process.Kill(); } catch { } })) {
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try {
                            using (var m = new MemoryStream(Encoding.UTF8.GetBytes(line))) {
                                var msg = (UploadMessage)messageSerializer.ReadObject(m);
                                update?.Invoke(msg);
                                if (!string.IsNullOrWhiteSpace(msg.stage)) result.LastStage = msg.stage;
                                if (msg.ip != null) result.Ip = msg.ip;
                                if (msg.url != null) result.Url = msg.url;
                                if (msg.diagnosticFile != null) result.DiagnosticPath = msg.diagnosticFile;
                                if (msg.stage == "done") {
                                    result.Success = msg.success;
                                    if (!msg.success && string.IsNullOrWhiteSpace(result.Error)) result.Error = msg.text;
                                }
                                if (msg.stage == "error") {
                                    result.Error = msg.error ?? msg.text;
                                    result.KeptOpen = msg.keptOpen;
                                    result.Success = false;
                                }
                                if (msg.stage == "manual_check") {
                                    result.ManualCheck = true;
                                    result.Error = msg.error ?? msg.text;
                                    result.KeptOpen = msg.keptOpen;
                                    result.Success = false;
                                }
                            }
                        } catch {
                            update?.Invoke(new UploadMessage { stage = "log", text = line });
                        }
                    }
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    string err = await stderr.ConfigureAwait(false);
                    if (process.ExitCode == 2 && result.ManualCheck) {
                        throw new UploadException(string.IsNullOrWhiteSpace(result.Error) ? "Нужна ручная проверка TikTok HTTP." : result.Error, true);
                    }
                    if (process.ExitCode != 0 && !result.Success && string.IsNullOrWhiteSpace(result.Error))
                        result.Error = string.IsNullOrWhiteSpace(err) ? "HTTP TikTok worker завершился с ошибкой." : err.Trim();
                    if (!result.Success)
                        throw new UploadException(string.IsNullOrWhiteSpace(result.Error) ? "HTTP TikTok не подтвердил загрузку." : result.Error, result.KeptOpen);
                    return result;
                }
            } finally {
                try { if (process != null) { if (!process.HasExited) process.Kill(); process.Dispose(); } } catch { }
                try { File.Delete(file); } catch { }
            }
        }
    }
}
