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

    /// <summary>Per-profile randomized publishing slots. Two modes: random interval or distribute in period.</summary>
    public static class ScheduleGenerator {
        static readonly Random Rng = new Random();

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
            else result = GenerateRandomIntervalMode(count, prefs, statePath);
            if (result.Count > 0) SaveNextSlot(statePath, result.Last().At);
            return result;
        }

        static List<ScheduleSlot> GenerateRandomIntervalMode(int count, Preferences prefs, string statePath) {
            int minM = Math.Max(1, prefs?.YouTubeScheduleMinMinutes ?? 10);
            int maxM = Math.Max(minM, prefs?.YouTubeScheduleMaxMinutes ?? 30);
            DateTime next = LoadNextSlot(statePath);
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeScheduleFirstPublish) && DateTime.TryParse(prefs.YouTubeScheduleFirstPublish, null, System.Globalization.DateTimeStyles.RoundtripKind, out var firstOverride) && firstOverride > DateTime.Now)
                next = firstOverride;
            DateTime minimum = DateTime.Now.AddHours(2);
            if (next < minimum) next = RoundUpToNextSlot(minimum);

            var slots = new List<ScheduleSlot>();
            DateTime prev = DateTime.MinValue;
            for (int i = 0; i < count; i++) {
                if (i > 0) {
                    int gap = Rng.Next(minM, maxM + 1);
                    next = prev.AddMinutes(gap);
                    next = EnsureBusinessHours(next);
                }
                var slot = MakeSlot(next, prev);
                slots.Add(slot);
                prev = next;
            }
            return slots;
        }

        static List<ScheduleSlot> GeneratePeriodMode(int count, Preferences prefs, string statePath) {
            DateTime start = DateTime.Now.AddHours(2);
            DateTime end = start.AddHours(24);
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeSchedulePeriodStart) && DateTime.TryParse(prefs.YouTubeSchedulePeriodStart, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ps))
                start = ps;
            if (!string.IsNullOrWhiteSpace(prefs?.YouTubeSchedulePeriodEnd) && DateTime.TryParse(prefs.YouTubeSchedulePeriodEnd, null, System.Globalization.DateTimeStyles.RoundtripKind, out var pe))
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
                at = EnsureBusinessHours(at);
                slots.Add(MakeSlot(at, prev));
                prev = at;
            }
            DateTime saved = LoadNextSlot(statePath);
            if (saved > slots.Last().At) {
                // Per-profile state means next batch continues after last saved moment
            }
            return slots;
        }

        static ScheduleSlot MakeSlot(DateTime at, DateTime prev) {
            return new ScheduleSlot {
                At = at,
                date = at.ToString("yyyy-MM-dd"),
                time = at.ToString("HH:mm"),
                MinutesAfterPrevious = prev == DateTime.MinValue ? 0 : (int)Math.Round((at - prev).TotalMinutes)
            };
        }

        static DateTime RoundUpToNextSlot(DateTime t) {
            return new DateTime(t.Year, t.Month, t.Day, 12, 0, 0).AddDays(t.Hour >= 12 ? 1 : 0);
        }

        static DateTime EnsureBusinessHours(DateTime t) {
            if (t.Hour >= 22) return new DateTime(t.Year, t.Month, t.Day, 9, 0, 0).AddDays(1);
            if (t.Hour < 7) return new DateTime(t.Year, t.Month, t.Day, 9, 0, 0);
            return t;
        }

        public static string ProfileStatePath(string profileId) {
            string safe = string.IsNullOrWhiteSpace(profileId) ? "default" : profileId.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(Store.Root, "youtube-schedule-" + safe + ".json");
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
            var prefs = new Preferences { YouTubeScheduleMode = "random", YouTubeScheduleMinMinutes = 10, YouTubeScheduleMaxMinutes = 30 };
            string temp = Path.Combine(Path.GetTempPath(), "vb-sched-" + Guid.NewGuid().ToString("N") + ".json");
            try {
                var a = GenerateDetailed(5, temp, prefs);
                if (a.Count != 5) return false;
                for (int i = 1; i < a.Count; i++)
                    if (a[i].At <= a[i - 1].At) return false;
                prefs.YouTubeScheduleMode = "period";
                prefs.YouTubeSchedulePeriodStart = DateTime.Now.AddDays(1).ToString("o");
                prefs.YouTubeSchedulePeriodEnd = DateTime.Now.AddDays(2).ToString("o");
                var b = GenerateDetailed(3, temp + "2", prefs);
                if (b.Count != 3) return false;
                for (int i = 1; i < b.Count; i++)
                    if (b[i].At <= b[i - 1].At) return false;
                return true;
            } finally {
                try { File.Delete(temp); } catch { }
                try { File.Delete(temp + "2"); } catch { }
            }
        }
    }
}
