using System;
using System.Collections.Concurrent;
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
        [DataMember] public string thumbnail, contentKind, thumbnailStatus;
        [DataMember] public long scheduledUnixSeconds;
    }

    [DataContract]
    public sealed class HttpUploadJob {
        [DataMember] public string profileId, expectedIp, expectedChannelId, runId, publishMode;
        [DataMember] public int localPort;
        [DataMember] public bool keepProfileOpen = true;
        [DataMember] public HttpUploadItemJob[] items;
    }

    public sealed class HttpUploadRunResult {
        public bool Success, KeptOpen, WorkerStarted, ManualCheck;
        public bool ThumbnailWarning;
        public string Ip = "", Url = "", Error = "", VideoId = "", LastStage = "", ThumbnailStatus = "";
        public string JobJsonPath = "", DiagnosticPath = "", RunId = "";
        public TimeSpan WorkerExitWait = TimeSpan.Zero;
    }

    public static class HttpUploadRunner {
        public const int WorkerExitGraceMs = 2000;
        public static string Worker => Path.Combine(DolphinRunner.Root, "worker-http.js");

        public static void CheckFiles() {
            if (!File.Exists(DolphinRunner.Node) || !File.Exists(Worker) || !Directory.Exists(Path.Combine(DolphinRunner.Root, "node_modules", "playwright-core")))
                throw new Exception("Архив распакован не полностью: не найден модуль быстрой HTTP-загрузки (worker-http.js).");
        }

        static byte[] SerializeJob(HttpUploadJob job) {
            var serializer = new DataContractJsonSerializer(typeof(HttpUploadJob));
            using (var memory = new MemoryStream()) {
                serializer.WriteObject(memory, job);
                return memory.ToArray();
            }
        }

        public static bool RunParallelSerializationSelfTest(int jobs = 10) {
            if (jobs < 1) jobs = 10;
            var errors = new ConcurrentBag<string>();
            Parallel.For(0, jobs, i => {
                try {
                    var job = new HttpUploadJob {
                        profileId = "profile-" + i,
                        expectedIp = "1.2.3." + (i % 250),
                        localPort = 3001,
                        runId = Guid.NewGuid().ToString("N"),
                        publishMode = "scheduled",
                        items = new[] {
                            new HttpUploadItemJob {
                                localJobId = "job-" + i,
                                video = "C:\\fake\\video-" + i + ".mp4",
                                title = "Title " + i,
                                thumbnail = "C:\\fake\\thumb-" + i + ".jpg",
                                contentKind = i % 2 == 0 ? "long" : "shorts",
                                scheduleDate = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd"),
                                scheduleTime = DateTime.Now.AddHours(1).ToString("HH:mm"),
                                scheduledUnixSeconds = DateTimeOffset.Now.AddHours(1).ToUnixTimeSeconds()
                            }
                        }
                    };
                    byte[] bytes = SerializeJob(job);
                    var readSerializer = new DataContractJsonSerializer(typeof(HttpUploadJob));
                    using (var memory = new MemoryStream(bytes)) {
                        var read = (HttpUploadJob)readSerializer.ReadObject(memory);
                        if (read == null || read.items == null || read.items.Length != 1)
                            errors.Add("job " + i + ": deserialize mismatch");
                        if (read != null && read.items[0].thumbnail != job.items[0].thumbnail)
                            errors.Add("job " + i + ": thumbnail missing");
                    }
                } catch (Exception ex) {
                    errors.Add("job " + i + ": " + ex.Message);
                }
            });
            return errors.IsEmpty;
        }

        public static async Task<HttpUploadRunResult> Run(HttpUploadJob job, string token, Action<UploadMessage> update, CancellationToken cancel) {
            CheckFiles();
            if (string.IsNullOrWhiteSpace(job.runId)) job.runId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(Store.Root, "jobs"));
            string file = Path.Combine(Store.Root, "jobs", "http-" + job.runId + ".json");
            ProcessWrapper process = null;
            var result = new HttpUploadRunResult { JobJsonPath = file, RunId = job.runId };
            bool keepJobFile = false;
            var messageSerializer = new DataContractJsonSerializer(typeof(UploadMessage));
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
                var proc = System.Diagnostics.Process.Start(info);
                if (proc == null) throw new Exception("Не удалось запустить HTTP-загрузчик.");
                result.WorkerStarted = true;
                process = new ProcessWrapper(proc);
                var stderr = process.Process.StandardError.ReadToEndAsync();
                using (cancel.Register(() => process.RequestKill())) {
                    string line;
                    while ((line = await process.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try {
                            using (var m = new MemoryStream(Encoding.UTF8.GetBytes(line))) {
                                var msg = (UploadMessage)messageSerializer.ReadObject(m);
                                update?.Invoke(msg);
                                if (!string.IsNullOrWhiteSpace(msg.stage)) result.LastStage = msg.stage;
                                if (msg.ip != null) result.Ip = msg.ip;
                                if (msg.url != null) result.Url = msg.url;
                                if (msg.videoId != null) result.VideoId = msg.videoId;
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
                                    if (!string.IsNullOrWhiteSpace(msg.url)) result.Url = msg.url;
                                    if (!string.IsNullOrWhiteSpace(msg.videoId)) result.VideoId = msg.videoId;
                                }
                                if (msg.stage == "thumbnail_warning") result.ThumbnailWarning = true;
                            }
                        } catch {
                            update?.Invoke(new UploadMessage { stage = "log", text = line });
                        }
                    }
                    var exitSw = System.Diagnostics.Stopwatch.StartNew();
                    bool exited = await Task.Run(() => process.Process.WaitForExit(WorkerExitGraceMs)).ConfigureAwait(false);
                    exitSw.Stop();
                    result.WorkerExitWait = exitSw.Elapsed;
                    if (!exited && !process.Process.HasExited) process.RequestKill();
                    cancel.ThrowIfCancellationRequested();
                    string err = await stderr.ConfigureAwait(false);
                    if (process.Process.ExitCode != 0 && !result.Success && !result.ManualCheck && string.IsNullOrWhiteSpace(result.Error))
                        result.Error = string.IsNullOrWhiteSpace(err) ? "HTTP-загрузчик завершился с ошибкой." : err.Trim();
                    if (result.ManualCheck) {
                        keepJobFile = true;
                        return result;
                    }
                    if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
                        throw new UploadException("Загрузчик не подтвердил завершение. Проверьте профиль Dolphin перед повтором.", result.KeptOpen);
                    if (!result.Success)
                        throw new UploadException(string.IsNullOrWhiteSpace(result.Error) ? "Ошибка HTTP-загрузки." : result.Error, result.KeptOpen);
                    return result;
                }
            } catch {
                keepJobFile = true;
                throw;
            } finally {
                process?.Dispose(killIfRunning: false);
                if (!keepJobFile) {
                    try { File.Delete(file); } catch { }
                }
            }
        }

        sealed class ProcessWrapper : IDisposable {
            public System.Diagnostics.Process Process { get; }
            bool _killRequested;
            public ProcessWrapper(System.Diagnostics.Process p) { Process = p; }
            public void RequestKill() {
                _killRequested = true;
                try { if (Process != null && !Process.HasExited) Process.Kill(); } catch { }
            }
            public void Dispose(bool killIfRunning = true) {
                try {
                    if (Process == null) return;
                    if (killIfRunning && _killRequested && !Process.HasExited) Process.Kill();
                    Process.Dispose();
                } catch { }
            }
            public void Dispose() => Dispose(killIfRunning: true);
        }
    }
}
