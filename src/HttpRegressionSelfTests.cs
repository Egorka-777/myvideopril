using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class HttpRegressionSelfTests {
        public static bool RunAll() {
            if (!HttpUploadRunner.RunParallelSerializationSelfTest(10)) return false;
            if (!ScheduleGenerator.RunSelfTests()) return false;
            if (!RunLog160617Scenario()) return false;
            if (!TaskQueueWriter.RunConcurrentWriteSelfTest(40, 25)) return false;
            return RunWorkerPoolScenario();
        }

        public static bool RunLog160617Scenario() {
            var prefs = new Preferences { YouTubeScheduleLeadMinutes = 30 };
            var batches = BuildSixAccountBatchesTooSoon();
            if (!AnyScheduleTooSoon(batches, prefs)) return false;
            if (!ScheduleGenerator.EnsureValidYouTubeSchedule(batches, prefs)) return false;
            int lead = ScheduleGenerator.ResolveLeadMinutes(prefs);
            DateTime minAt = DateTime.Now.AddMinutes(lead - 1);
            DateTime apiMin = DateTime.Now.AddMinutes(ScheduleGenerator.YouTubeApiMinLeadMinutes);
            foreach (var batch in batches) {
                foreach (var it in batch.Items) {
                    if (!ScheduleGenerator.TryParseSlot(it.ScheduleDate, it.ScheduleTime, out var at)) return false;
                    if (at < minAt) return false;
                    if (at <= apiMin) return false;
                }
            }
            if (!HttpUploadRunner.RunParallelSerializationSelfTest(6)) return false;
            return true;
        }

        /// <summary>10 account batches, max 3 concurrent, unique job paths, queue writer, pool resilience.</summary>
        public static string LastPoolError = "";

        public static bool RunWorkerPoolScenario() {
            try {
            if (HttpWorkerSettings.ResolveWorkerCount(new Preferences()) != 3) return false;
            if (HttpWorkerSettings.ResolveWorkerCount(new Preferences { HttpWorkerPreset = "safe" }) != 1) return false;
            if (HttpWorkerSettings.ResolveWorkerCount(new Preferences { HttpWorkerPreset = "fast" }) != 5) return false;
            if (HttpWorkerSettings.ResolveWorkerCount(new Preferences { HttpWorkerPreset = "custom", HttpWorkerCustomCount = 7 }) != 7) return false;

            var batches = BuildTenAccountBatches();
            int maxConcurrent = 0;
            int peak = 0;
            var jobPaths = new ConcurrentBag<string>();
            var active = 0;
            using (var pool = new HttpWorkerPool()) {
                pool.RunAsync(batches, 3, false, async (batch, metrics, ct) => {
                    int now = Interlocked.Increment(ref active);
                    int observed;
                    do {
                        observed = maxConcurrent;
                        if (now <= observed) break;
                    } while (Interlocked.CompareExchange(ref maxConcurrent, now, observed) != observed);
                    if (now > peak) peak = now;
                    metrics.MarkStarted();
                    string runId = Guid.NewGuid().ToString("N");
                    string path = Path.Combine(Store.Root, "jobs", "http-" + runId + ".json");
                    jobPaths.Add(path);
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    metrics.OnStage("dolphin");
                    metrics.OnStage("upload");
                    metrics.Finish("success");
                    Interlocked.Decrement(ref active);
                }, CancellationToken.None).GetAwaiter().GetResult();
                if (pool.Metrics.Processed != 10) return false;
            }
            if (peak > 3 || maxConcurrent > 3) return false;
            if (jobPaths.Count != 10) return false;
            var unique = new HashSet<string>(jobPaths, StringComparer.OrdinalIgnoreCase);
            if (unique.Count != 10) return false;

            // One failing worker must not stop the pool
            int completed = 0;
            using (var pool2 = new HttpWorkerPool()) {
                pool2.RunAsync(batches, 3, false, async (batch, metrics, ct) => {
                    if (batch.ProfileId == "pid-3") throw new Exception("simulated worker failure");
                    Interlocked.Increment(ref completed);
                    metrics.Finish("success");
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
            if (completed != 9) return false;
            return true;
            } catch (Exception ex) {
                LastPoolError = ex.ToString();
                return false;
            }
        }

        static List<PreparedProfileBatch> BuildTenAccountBatches() {
            var list = new List<PreparedProfileBatch>();
            var at = DateTime.Now.AddHours(1);
            for (int i = 0; i < 10; i++) {
                list.Add(new PreparedProfileBatch {
                    ProfileId = "pid-" + (i + 1),
                    Channel = new YouTubeChannel {
                        Name = "Account " + (i + 1),
                        ProfileId = "pid-" + (i + 1),
                        ExpectedIp = "1.2.3." + (i + 1),
                        Kind = i % 2 == 0 ? "long" : "shorts"
                    },
                    Items = new List<PreparedUploadItem> {
                        new PreparedUploadItem {
                            UploadTitle = "Title " + (i + 1),
                            StagedVideo = "C:\\VideoBatch\\Upload\\pid-" + (i + 1) + "\\video.mp4",
                            ScheduleDate = at.ToString("yyyy-MM-dd"),
                            ScheduleTime = at.ToString("HH:mm"),
                            Source = new YouTubeItem { Thumbnail = "C:\\VideoBatch\\thumb-" + (i + 1) + ".jpg" }
                        }
                    }
                });
            }
            return list;
        }

        static List<PreparedProfileBatch> BuildSixAccountBatchesTooSoon() {
            var list = new List<PreparedProfileBatch>();
            string[] names = {
                "Трейдинг для избранных (MY2) @lotusninja666",
                "Lucky Tom | Smart Trading @filmrezzzka",
                "Блог Трейдера - Предпринимателя @traderprdp",
                "Trader Max | Обучение трейдингу (LT2) @tepluy_veter",
                "Школа Трейдинга (ТП2) @marinhoffer",
                "Торговец из Binodex (Бинодекс)"
            };
            var tooSoon = DateTime.Now.AddMinutes(5);
            for (int i = 0; i < names.Length; i++) {
                list.Add(new PreparedProfileBatch {
                    ProfileId = "pid-" + (i + 1),
                    Channel = new YouTubeChannel { Name = names[i], ProfileId = "pid-" + (i + 1), ExpectedIp = "1.2.3." + (i + 1) },
                    Items = new List<PreparedUploadItem> {
                        new PreparedUploadItem {
                            UploadTitle = "Title " + (i + 1),
                            StagedVideo = "C:\\VideoBatch\\Upload\\pid-" + (i + 1) + "\\video.mp4",
                            ScheduleDate = tooSoon.ToString("yyyy-MM-dd"),
                            ScheduleTime = tooSoon.ToString("HH:mm")
                        }
                    }
                });
            }
            return list;
        }

        static bool AnyScheduleTooSoon(IList<PreparedProfileBatch> batches, Preferences prefs) {
            DateTime floor = ScheduleGenerator.ScheduleFloor(prefs);
            foreach (var batch in batches) {
                foreach (var it in batch.Items ?? new List<PreparedUploadItem>()) {
                    if (!ScheduleGenerator.TryParseSlot(it.ScheduleDate, it.ScheduleTime, out var at)) continue;
                    if (at < floor) return true;
                }
            }
            return false;
        }
    }
}
