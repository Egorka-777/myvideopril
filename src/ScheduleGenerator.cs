using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VideoBatch {
    public sealed class ScheduleSlot {
        public string date;
        public string time;
        public DateTime At;
        public int MinutesAfterPrevious;
    }

    /// <summary>Время ПК для планирования; перед HTTP-запросом переводится в Unix timestamp.</summary>
    public static class ScheduleGenerator {
        /// <summary>YouTube API rejects schedules sooner than this.</summary>
        public const int YouTubeApiMinLeadMinutes = 15;
        /// <summary>Preflight/UI minimum before upload may start.</summary>
        public const int PreflightMinLeadMinutes = 20;
        public const int DefaultLeadMinutes = 30;

        static readonly Random Rng = new Random();

        public static int ResolveLeadMinutes(Preferences prefs) {
            int lead = prefs?.YouTubeScheduleLeadMinutes ?? DefaultLeadMinutes;
            return Math.Max(PreflightMinLeadMinutes, lead);
        }

        public static DateTime ScheduleFloor(Preferences prefs) {
            return DateTime.Now.AddMinutes(ResolveLeadMinutes(prefs));
        }

        public static bool TryParseSlot(string date, string time, out DateTime at) {
            at = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time)) return false;
            return DateTime.TryParse(date.Trim() + " " + time.Trim(), out at);
        }

        public static DateTime ParseSlot(string date, string time) {
            if (!TryParseSlot(date, time, out var at))
                throw new Exception("Некорректное расписание: «" + date + " " + time + "».");
            return at;
        }

        static bool BatchNeedsRecalculation(IList<PreparedProfileBatch> batches, Preferences prefs, bool allScheduled) {
            if (batches == null) return false;
            DateTime floor = ScheduleFloor(prefs);
            foreach (var batch in batches) {
                if (batch?.Items == null) continue;
                foreach (var it in batch.Items) {
                    if (!allScheduled && HttpWorkerSettings.ResolvePublishMode(prefs, it.Kind ?? batch.Channel?.Kind) != "scheduled") continue;
                    if (!TryParseSlot(it.ScheduleDate, it.ScheduleTime, out var at)) return true;
                    // Позволяем пройти предпросмотр: его несколько секунд не требуют
                    // нового расписания, если до публикации ещё есть безопасный запас.
                    if (at < floor.AddMinutes(-2)) return true;
                    if (at <= DateTime.Now.AddMinutes(YouTubeApiMinLeadMinutes)) return true;
                }
            }
            return false;
        }

        /// <summary>Recalculate entire queue when any slot is too soon for YouTube HTTP (+lead buffer).</summary>
        public static bool EnsureValidYouTubeSchedule(IList<PreparedProfileBatch> batches, Preferences prefs, bool allScheduled=false) {
            if (batches == null || batches.Count == 0) return false;
            if (!BatchNeedsRecalculation(batches, prefs, allScheduled)) return false;
            AssignCrossBatchSchedule(batches, prefs, forceFromFloor: true, allScheduled: allScheduled);
            return true;
        }

        public static List<(string date, string time)> GenerateForProfileBatches(IList<int> itemCountsPerProfile, string statePath, Preferences prefs = null) {
            var slots = new List<(string, string)>();
            if (itemCountsPerProfile == null || itemCountsPerProfile.Count == 0) return slots;
            int total = itemCountsPerProfile.Sum();
            var detailed = GenerateDetailed(total, statePath, prefs);
            return detailed.Select(s => (s.date, s.time)).ToList();
        }

        public static List<ScheduleSlot> GenerateDetailed(int count, string statePath, Preferences prefs = null) {
            var result = new List<ScheduleSlot>();
            if (count <= 0) return result;
            string mode = (prefs?.YouTubeScheduleMode ?? "random").Trim().ToLowerInvariant();
            if (mode == "period") result = GeneratePeriodMode(count, prefs, statePath);
            else result = GenerateRandomIntervalMode(count, prefs, statePath, "shorts");
            if (result.Count > 0) SaveNextSlot(statePath, result.Last().At);
            return result;
        }

        /// <summary>Legacy global queue, separate from the HTTP batch scheduler.</summary>
        public static List<ScheduleSlot> GenerateGlobalQueue(IList<string> kinds, string statePath, Preferences prefs = null) {
            var result = new List<ScheduleSlot>();
            if (kinds == null || kinds.Count == 0) return result;

            DateTime cursor = LoadNextSlot(statePath);
            DateTime floor = ScheduleFloor(prefs);
            if (cursor < floor) cursor = floor;
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeScheduleFirstPublish)
                && DateTime.TryParse(prefs.YouTubeScheduleFirstPublish, null, System.Globalization.DateTimeStyles.RoundtripKind, out var firstOverride)
                && firstOverride > DateTime.Now)
                cursor = firstOverride;

            DateTime prev = DateTime.MinValue;
            for (int i = 0; i < kinds.Count; i++) {
                if (i > 0) {
                    int gap = RandomGapForKind(kinds[i], prefs);
                    cursor = prev.AddMinutes(gap);
                }
                var slot = MakeSlot(cursor, prev);
                result.Add(slot);
                prev = cursor;
            }
            if (result.Count > 0) SaveNextSlot(statePath, result.Last().At);
            return result;
        }

        public static int RandomGapForKind(string kind, Preferences prefs) {
            bool shorts = !string.Equals((kind ?? "").Trim(), "long", StringComparison.OrdinalIgnoreCase);
            if (shorts) {
                int minM = Math.Max(1, prefs?.YouTubeScheduleMinMinutes ?? 15);
                int maxM = Math.Max(minM, prefs?.YouTubeScheduleMaxMinutes ?? 20);
                return Rng.Next(minM, maxM + 1);
            }
            int lmin = Math.Max(1, prefs?.YouTubeScheduleLongMinMinutes ?? 1);
            int lmax = Math.Max(lmin, prefs?.YouTubeScheduleLongMaxMinutes ?? 5);
            return Rng.Next(lmin, lmax + 1);
        }

        static int RandomGapSecondsForKind(string kind, Preferences prefs) {
            bool shorts = !string.Equals((kind ?? "").Trim(), "long", StringComparison.OrdinalIgnoreCase);
            int min = Math.Max(1, shorts ? prefs?.YouTubeScheduleMinMinutes ?? 15 : prefs?.YouTubeScheduleLongMinMinutes ?? 1);
            int max = Math.Max(min, shorts ? prefs?.YouTubeScheduleMaxMinutes ?? 20 : prefs?.YouTubeScheduleLongMaxMinutes ?? 5);
            return Rng.Next(min * 60, max * 60 + 1);
        }

        static List<ScheduleSlot> GenerateRandomIntervalMode(int count, Preferences prefs, string statePath, string kind) {
            DateTime next = LoadNextSlot(statePath);
            DateTime minimum = ScheduleFloor(prefs);
            if (next < minimum) next = minimum;
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeScheduleFirstPublish)
                && DateTime.TryParse(prefs.YouTubeScheduleFirstPublish, null, System.Globalization.DateTimeStyles.RoundtripKind, out var firstOverride)
                && firstOverride > DateTime.Now)
                next = firstOverride;

            var slots = new List<ScheduleSlot>();
            DateTime prev = DateTime.MinValue;
            for (int i = 0; i < count; i++) {
                if (i > 0) next = prev.AddMinutes(RandomGapForKind(kind, prefs));
                var slot = MakeSlot(next, prev);
                slots.Add(slot);
                prev = next;
            }
            return slots;
        }

        static List<ScheduleSlot> GeneratePeriodMode(int count, Preferences prefs, string statePath) {
            DateTime start = ScheduleFloor(prefs);
            DateTime end = start.AddHours(24);
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeSchedulePeriodStart)
                && DateTime.TryParse(prefs.YouTubeSchedulePeriodStart, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ps))
                start = ps;
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeSchedulePeriodEnd)
                && DateTime.TryParse(prefs.YouTubeSchedulePeriodEnd, null, System.Globalization.DateTimeStyles.RoundtripKind, out var pe))
                end = pe;
            if (end <= start) end = start.AddHours(24);
            int minGap = Math.Max(1, prefs?.YouTubeSchedulePeriodMinGapMinutes ?? 10);
            double spanMinutes = (end - start).TotalMinutes;
            if (spanMinutes < minGap * count) throw new Exception("Период слишком короткий для " + count + " роликов с минимальным интервалом " + minGap + " мин.");

            var offsets = new List<double>();
            double cursor = 0;
            for (int i = 0; i < count; i++) {
                if (i == 0) cursor = Rng.NextDouble() * Math.Min(minGap, spanMinutes / (count + 1));
                else {
                    double remaining = spanMinutes - cursor;
                    int left = count - i;
                    double minStep = minGap;
                    double maxStep = Math.Max(minStep, remaining / left - minGap * 0.5);
                    if (maxStep < minStep) maxStep = minStep;
                    cursor += minStep + Rng.NextDouble() * (maxStep - minStep);
                }
                offsets.Add(Math.Min(cursor, spanMinutes - 1));
            }
            offsets = offsets.OrderBy(x => x).ToList();
            for (int i = 1; i < offsets.Count; i++) {
                if (offsets[i] - offsets[i - 1] < minGap) offsets[i] = offsets[i - 1] + minGap + Rng.NextDouble() * minGap;
            }
            if (offsets.Last() >= spanMinutes) {
                double scale = (spanMinutes - minGap) / offsets.Last();
                for (int i = 0; i < offsets.Count; i++) offsets[i] *= scale;
            }

            var slots = new List<ScheduleSlot>();
            DateTime prev = DateTime.MinValue;
            foreach (var off in offsets) {
                var at = start.AddMinutes(off);
                slots.Add(MakeSlot(at, prev));
                prev = at;
            }
            return slots;
        }

        public static ScheduleSlot MakeSlot(DateTime at, DateTime prev) {
            return new ScheduleSlot {
                At = at,
                date = at.ToString("yyyy-MM-dd"),
                time = at.ToString("HH:mm:ss"),
                MinutesAfterPrevious = prev == DateTime.MinValue ? 0 : (int)Math.Round((at - prev).TotalMinutes)
            };
        }

        /// <summary>Общее расписание запланированных роликов (режим network — сеть аккаунтов на период ПК).</summary>
        public static void AssignCrossBatchSchedule(IList<PreparedProfileBatch> batches, Preferences prefs = null, bool forceFromFloor = false, bool allScheduled = false) {
            if (batches == null || batches.Count == 0) return;
            string mode = (prefs?.YouTubeScheduleMode ?? "network").Trim().ToLowerInvariant();
            if (mode == "network") {
                AssignNetworkSchedule(batches, prefs, allScheduled);
                return;
            }

            DateTime floor = ScheduleFloor(prefs);
            DateTime cursor = floor;
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeScheduleFirstPublish)
                && DateTime.TryParse(prefs.YouTubeScheduleFirstPublish, null, System.Globalization.DateTimeStyles.RoundtripKind, out var firstOverride)
                && firstOverride > DateTime.Now)
                cursor = firstOverride;

            DateTime prev = DateTime.MinValue;
            foreach (var batch in batches) {
                if (batch?.Items == null) continue;
                foreach (var it in batch.Items) {
                    string kind=it.Kind ?? batch.Channel?.Kind ?? "shorts";
                    if (!allScheduled && HttpWorkerSettings.ResolvePublishMode(prefs, kind) != "scheduled") {
                        it.ScheduleDate="";it.ScheduleTime="";
                        continue;
                    }
                    if (prev != DateTime.MinValue) cursor = prev.AddSeconds(RandomGapSecondsForKind(kind, prefs));
                    var slot = MakeSlot(cursor, prev);
                    it.ScheduleDate = slot.date;
                    it.ScheduleTime = slot.time;
                    prev = cursor;
                }
            }
        }

        /// <summary>Интервал между публикациями для N аккаунтов × 10 роликов (таблица 6–10 акк.).</summary>
        public static (int minMinutes, int maxMinutes) ResolveNetworkGapBounds(int accountCount, int totalVideos, Preferences prefs) {
            int hardMin = Math.Max(1, prefs?.YouTubeScheduleNetworkMinMinutes ?? 3);
            int hardMax = Math.Max(hardMin, prefs?.YouTubeScheduleNetworkMaxMinutes ?? 15);
            int[] tableMin = { 0, 0, 0, 0, 0, 0, 5, 4, 4, 3, 3 };
            int[] tableMax = { 0, 0, 0, 0, 0, 0, 12, 10, 9, 8, 7 };
            int tMin = hardMin, tMax = hardMax;
            if (accountCount >= 6 && accountCount <= 10) {
                tMin = tableMin[accountCount];
                tMax = tableMax[accountCount];
            }
            int period = Math.Max(60, prefs?.YouTubeScheduleNetworkPeriodMinutes ?? 480);
            double avg = totalVideos > 1 ? (double)period / (totalVideos - 1) : period;
            int fromAvgMin = Math.Max(1, (int)Math.Floor(avg * 0.7));
            int fromAvgMax = Math.Max(fromAvgMin, (int)Math.Ceiling(avg * 1.3));
            int min = Math.Max(hardMin, Math.Min(tMin, fromAvgMin));
            int max = Math.Min(hardMax, Math.Max(tMax, fromAvgMax));
            if (max < min) max = min;
            return (min, max);
        }

        static void ShuffleList<T>(IList<T> list) {
            for (int i = list.Count - 1; i > 0; i--) {
                int j = Rng.Next(i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        /// <summary>Очередь: раунд 1 — по одному ролику с каждого аккаунта (перемешано), раунд 2…</summary>
        public static List<(PreparedUploadItem item, string profileId)> BuildNetworkInterleavedQueue(
            IList<PreparedProfileBatch> batches, Preferences prefs, bool allScheduled) {
            var profiles = new List<(string profileId, List<PreparedUploadItem> items)>();
            foreach (var batch in batches) {
                if (batch?.Items == null || batch.Items.Count == 0) continue;
                var scheduled = new List<PreparedUploadItem>();
                foreach (var it in batch.Items) {
                    string kind = it.Kind ?? batch.Channel?.Kind ?? "shorts";
                    if (!allScheduled && HttpWorkerSettings.ResolvePublishMode(prefs, kind) != "scheduled") {
                        it.ScheduleDate = "";
                        it.ScheduleTime = "";
                        continue;
                    }
                    scheduled.Add(it);
                }
                if (scheduled.Count > 0)
                    profiles.Add(((batch.ProfileId ?? "").Trim(), scheduled));
            }
            if (profiles.Count == 0) return new List<(PreparedUploadItem, string)>();

            ShuffleList(profiles);
            int maxRound = profiles.Max(p => p.items.Count);
            var queue = new List<(PreparedUploadItem item, string profileId)>();
            for (int round = 0; round < maxRound; round++) {
                var roundProfiles = profiles.Where(p => round < p.items.Count).ToList();
                if (roundProfiles.Count == 0) continue;
                ShuffleList(roundProfiles);
                if (queue.Count > 0 && roundProfiles.Count > 1) {
                    string lastPid = queue[queue.Count - 1].profileId;
                    if (string.Equals(roundProfiles[0].profileId, lastPid, StringComparison.OrdinalIgnoreCase)) {
                        int swap = 1 + Rng.Next(roundProfiles.Count - 1);
                        var tmp = roundProfiles[0];
                        roundProfiles[0] = roundProfiles[swap];
                        roundProfiles[swap] = tmp;
                    }
                }
                foreach (var p in roundProfiles)
                    queue.Add((p.items[round], p.profileId));
            }
            return queue;
        }

        /// <summary>Распределить все отложенные публикации на период активности (время ПК), без двух подряд с одного аккаунта.</summary>
        public static void AssignNetworkSchedule(IList<PreparedProfileBatch> batches, Preferences prefs = null, bool allScheduled = false) {
            var queue = BuildNetworkInterleavedQueue(batches, prefs, allScheduled);
            if (queue.Count == 0) return;

            DateTime floor = ScheduleFloor(prefs);
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeScheduleFirstPublish)
                && DateTime.TryParse(prefs.YouTubeScheduleFirstPublish, null, System.Globalization.DateTimeStyles.RoundtripKind, out var firstOverride)
                && firstOverride > DateTime.Now)
                floor = firstOverride;

            if (queue.Count == 1) {
                var slot = MakeSlot(floor, DateTime.MinValue);
                queue[0].item.ScheduleDate = slot.date;
                queue[0].item.ScheduleTime = slot.time;
                return;
            }

            int accountCount = queue.Select(q => q.profileId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int periodMinutes = Math.Max(60, prefs?.YouTubeScheduleNetworkPeriodMinutes ?? 480);
            double avgMinutes = (double)periodMinutes / (queue.Count - 1);
            var bounds = ResolveNetworkGapBounds(accountCount, queue.Count, prefs);

            DateTime cursor = floor;
            DateTime prev = DateTime.MinValue;
            for (int i = 0; i < queue.Count; i++) {
                if (i > 0) {
                    double jitter = avgMinutes * (0.7 + Rng.NextDouble() * 0.6);
                    int gapSec = (int)Math.Round(Math.Max(bounds.minMinutes * 60.0, Math.Min(bounds.maxMinutes * 60.0, jitter * 60.0)));
                    cursor = prev.AddSeconds(gapSec);
                }
                var slot = MakeSlot(cursor, prev);
                queue[i].item.ScheduleDate = slot.date;
                queue[i].item.ScheduleTime = slot.time;
                prev = cursor;
            }
        }

        /// <summary>Все запланированные ролики в хронологическом порядке (для предпросмотра).</summary>
        public static List<(PreparedUploadItem item, PreparedProfileBatch batch)> OrderScheduledItems(IList<PreparedProfileBatch> batches) {
            var list = new List<(PreparedUploadItem item, PreparedProfileBatch batch, DateTime at)>();
            foreach (var batch in batches ?? Array.Empty<PreparedProfileBatch>()) {
                if (batch?.Items == null) continue;
                foreach (var it in batch.Items) {
                    if (!TryParseSlot(it.ScheduleDate, it.ScheduleTime, out var at)) continue;
                    list.Add((it, batch, at));
                }
            }
            list.Sort((a, b) => a.at.CompareTo(b.at));
            return list.Select(x => (x.item, x.batch)).ToList();
        }

        public static bool NetworkQueueHasConsecutiveSameProfile(IList<(PreparedUploadItem item, string profileId)> queue) {
            if (queue == null || queue.Count < 2) return false;
            for (int i = 1; i < queue.Count; i++)
                if (string.Equals(queue[i].profileId, queue[i - 1].profileId, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static string ProfileStatePath(string profileId) {
            string safe = string.IsNullOrWhiteSpace(profileId) ? "default" : profileId.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(Store.Root, "youtube-schedule-" + safe + ".json");
        }

        public static string GlobalStatePath() {
            return Path.Combine(Store.Root, "youtube-schedule-global.json");
        }

        static DateTime LoadNextSlot(string statePath) {
            try {
                string json = File.ReadAllText(statePath);
                int i = json.IndexOf("\"next\"", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) {
                    int q1 = json.IndexOf('"', i + 6);
                    int q2 = json.IndexOf('"', q1 + 1);
                    if (q1 >= 0 && q2 > q1 && DateTime.TryParse(json.Substring(q1 + 1, q2 - q1 - 1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        return parsed;
                }
            } catch { }
            return DateTime.MinValue;
        }

        static void SaveNextSlot(string statePath, DateTime next) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? Store.Root);
                string temp = statePath + ".tmp";
                File.WriteAllText(temp, "{\"next\":\"" + next.ToString("o") + "\"}", Encoding.UTF8);
                if (File.Exists(statePath)) File.Replace(temp, statePath, null);
                else File.Move(temp, statePath);
            } catch { }
        }

        public static bool RunSelfTests() {
            var prefs = new Preferences { YouTubeScheduleMode = "random", YouTubeScheduleMinMinutes = 10, YouTubeScheduleMaxMinutes = 60 };
            string temp = Path.Combine(Path.GetTempPath(), "vb-sched-" + Guid.NewGuid().ToString("N") + ".json");
            try {
                var a = GenerateDetailed(5, temp, prefs);
                if (a.Count != 5) return false;
                for (int i = 1; i < a.Count; i++)
                    if (a[i].At <= a[i - 1].At) return false;
                var kinds = new List<string> { "shorts", "shorts", "long", "long" };
                var g = GenerateGlobalQueue(kinds, temp + "g", prefs);
                if (g.Count != 4) return false;
                for (int i = 1; i < g.Count; i++)
                    if (g[i].At <= g[i - 1].At) return false;
                prefs.YouTubeScheduleMode = "period";
                prefs.YouTubeSchedulePeriodStart = DateTime.Now.AddDays(1).ToString("o");
                prefs.YouTubeSchedulePeriodEnd = DateTime.Now.AddDays(2).ToString("o");
                var b = GenerateDetailed(3, temp + "2", prefs);
                if (b.Count != 3) return false;
                for (int i = 1; i < b.Count; i++)
                    if (b[i].At <= b[i - 1].At) return false;
                prefs.YouTubeScheduleLeadMinutes = 30;
                var cross = new List<PreparedProfileBatch>();
                for (int i = 0; i < 3; i++) {
                    var at = DateTime.Now.AddMinutes(5);
                    cross.Add(new PreparedProfileBatch {
                        Items = new List<PreparedUploadItem> {
                            new PreparedUploadItem { ScheduleDate = at.ToString("yyyy-MM-dd"), ScheduleTime = at.ToString("HH:mm") }
                        }
                    });
                }
                if (!EnsureValidYouTubeSchedule(cross, prefs)) return false;
                if (cross[0].Items[0].ScheduleDate == null) return false;
                if (!TryParseSlot(cross[0].Items[0].ScheduleDate, cross[0].Items[0].ScheduleTime, out var firstAt)) return false;
                if (firstAt < DateTime.Now.AddMinutes(ResolveLeadMinutes(prefs) - 1)) return false;
                var networkPrefs = new Preferences {
                    YouTubeScheduleMode = "network",
                    YouTubeScheduleNetworkPeriodMinutes = 480,
                    YouTubeScheduleNetworkMinMinutes = 3,
                    YouTubeScheduleNetworkMaxMinutes = 15,
                    YouTubeScheduleLeadMinutes = 30,
                    YouTubeHttpPublishMode = "scheduled",
                    YouTubeHttpLongPublishMode = "scheduled"
                };
                var network = new List<PreparedProfileBatch>();
                for (int i = 0; i < 6; i++) {
                    network.Add(new PreparedProfileBatch {
                        ProfileId = "pid-" + i,
                        Channel = new YouTubeChannel { Kind = "shorts", ProfileId = "pid-" + i },
                        Items = Enumerable.Range(0, 10).Select(_ => new PreparedUploadItem { Kind = "shorts" }).ToList()
                    });
                }
                var interleaved = BuildNetworkInterleavedQueue(network, networkPrefs, false);
                if (interleaved.Count != 60) return false;
                if (NetworkQueueHasConsecutiveSameProfile(interleaved)) return false;
                AssignCrossBatchSchedule(network, networkPrefs);
                var ordered = OrderScheduledItems(network);
                if (ordered.Count != 60) return false;
                DateTime first = ParseSlot(ordered[0].item.ScheduleDate, ordered[0].item.ScheduleTime);
                DateTime last = ParseSlot(ordered[ordered.Count - 1].item.ScheduleDate, ordered[ordered.Count - 1].item.ScheduleTime);
                double spanHours = (last - first).TotalHours;
                if (spanHours < 6.5 || spanHours > 9.5) return false;
                var bounds = ResolveNetworkGapBounds(6, 60, networkPrefs);
                for (int i = 1; i < ordered.Count; i++) {
                    var slot = ParseSlot(ordered[i].item.ScheduleDate, ordered[i].item.ScheduleTime);
                    var prev = ParseSlot(ordered[i - 1].item.ScheduleDate, ordered[i - 1].item.ScheduleTime);
                    if (slot <= prev) return false;
                    double gapMin = (slot - prev).TotalMinutes;
                    if (gapMin < bounds.minMinutes - 0.5 || gapMin > bounds.maxMinutes + 0.5) return false;
                }
                var tenAccounts = new List<PreparedProfileBatch>();
                for (int i = 0; i < 10; i++) {
                    tenAccounts.Add(new PreparedProfileBatch {
                        ProfileId = "t-" + i,
                        Channel = new YouTubeChannel { ProfileId = "t-" + i, Kind = "shorts" },
                        Items = Enumerable.Range(0, 10).Select(_ => new PreparedUploadItem { Kind = "shorts" }).ToList()
                    });
                }
                AssignCrossBatchSchedule(tenAccounts, networkPrefs);
                var tenBounds = ResolveNetworkGapBounds(10, 100, networkPrefs);
                if (tenBounds.minMinutes != 3 || tenBounds.maxMinutes != 7) return false;
                return true;
            } finally {
                try { File.Delete(temp); } catch { }
                try { File.Delete(temp + "2"); } catch { }
                try { File.Delete(temp + "g"); } catch { }
            }
        }
    }
}
