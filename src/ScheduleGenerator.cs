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

    /// <summary>Publishing slots: per-profile legacy or global cross-channel queue (Shorts 10–30 min, Long 1–5 min, no night pause).</summary>
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

        static bool BatchNeedsRecalculation(IList<PreparedProfileBatch> batches, Preferences prefs) {
            if (batches == null) return false;
            DateTime floor = ScheduleFloor(prefs);
            foreach (var batch in batches) {
                if (batch?.Items == null) continue;
                foreach (var it in batch.Items) {
                    if (!TryParseSlot(it.ScheduleDate, it.ScheduleTime, out var at)) return true;
                    if (at < floor) return true;
                    if (at <= DateTime.Now.AddMinutes(YouTubeApiMinLeadMinutes)) return true;
                }
            }
            return false;
        }

        /// <summary>Recalculate entire queue when any slot is too soon for YouTube HTTP (+lead buffer).</summary>
        public static bool EnsureValidYouTubeSchedule(IList<PreparedProfileBatch> batches, Preferences prefs) {
            if (batches == null || batches.Count == 0) return false;
            if (!BatchNeedsRecalculation(batches, prefs)) return false;
            AssignCrossBatchSchedule(batches, prefs, forceFromFloor: true);
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

        /// <summary>Global queue: channel1 all videos, then channel2… Gap from previous slot; Shorts 10–30 min, Long 1–5 min.</summary>
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
                int minM = Math.Max(1, prefs?.YouTubeScheduleMinMinutes ?? 10);
                int maxM = Math.Max(minM, prefs?.YouTubeScheduleMaxMinutes ?? 60);
                return Rng.Next(minM, maxM + 1);
            }
            int lmin = Math.Max(1, prefs?.YouTubeScheduleLongMinMinutes ?? 1);
            int lmax = Math.Max(lmin, prefs?.YouTubeScheduleLongMaxMinutes ?? 5);
            return Rng.Next(lmin, lmax + 1);
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
                time = at.ToString("HH:mm"),
                MinutesAfterPrevious = prev == DateTime.MinValue ? 0 : (int)Math.Round((at - prev).TotalMinutes)
            };
        }

        /// <summary>Across channels: first video +1–10 min from previous channel; within channel use Shorts/Long gaps.</summary>
        public static void AssignCrossBatchSchedule(IList<PreparedProfileBatch> batches, Preferences prefs = null, bool forceFromFloor = false) {
            if (batches == null || batches.Count == 0) return;

            DateTime floor = ScheduleFloor(prefs);
            DateTime cursor = forceFromFloor ? floor : LoadNextSlot(GlobalStatePath());
            if (cursor < floor) cursor = floor;
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeScheduleFirstPublish)
                && DateTime.TryParse(prefs.YouTubeScheduleFirstPublish, null, System.Globalization.DateTimeStyles.RoundtripKind, out var firstOverride)
                && firstOverride > DateTime.Now)
                cursor = firstOverride;

            DateTime prev = DateTime.MinValue;
            foreach (var batch in batches) {
                if (batch?.Items == null) continue;
                int vi = 0;
                foreach (var it in batch.Items) {
                    if (prev != DateTime.MinValue) {
                        int gap;
                        if (vi == 0)
                            gap = Rng.Next(1, 11);
                        else if (batch.Items.Count > 1)
                            gap = Rng.Next(30, 61);
                        else
                            gap = RandomGapForKind(batch.Channel?.Kind ?? "shorts", prefs);
                        cursor = prev.AddMinutes(gap);
                    }
                    var slot = MakeSlot(cursor, prev);
                    it.ScheduleDate = slot.date;
                    it.ScheduleTime = slot.time;
                    prev = cursor;
                    vi++;
                }
            }
            if (prev != DateTime.MinValue) SaveNextSlot(GlobalStatePath(), prev);
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
                return true;
            } finally {
                try { File.Delete(temp); } catch { }
                try { File.Delete(temp + "2"); } catch { }
                try { File.Delete(temp + "g"); } catch { }
            }
        }
    }
}
