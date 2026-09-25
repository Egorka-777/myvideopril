using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VideoBatch {
    [DataContract]
    sealed class TikTokScheduleState {
        [DataMember] public List<long> UnixSlots = new List<long>();
        [DataMember] public string CreatedAtUtc = "";
        [DataMember] public int VideoCount;
    }

    /// <summary>TikTok HTTP batch schedule: first slot ≥ now+15min, then +3..15 min random gaps.</summary>
    public static class TikTokScheduleGenerator {
        public const int MinLeadSeconds = 900;
        public const int MinGapSeconds = 180;
        public const int MaxGapSeconds = 900;
        static readonly Random Rng = new Random();

        public static string StatePath(string profileId, string market) {
            string safe = (profileId ?? "unknown").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(Store.Root, "tiktok-schedule-" + NormMarket(market) + "-" + safe + ".json");
        }

        static string NormMarket(string m) {
            m = (m ?? "RU").Trim().ToUpperInvariant();
            return m == "EN" ? "EN" : "RU";
        }

        public static List<DateTime> GenerateLocalSlots(int count, string statePath, bool forceRecalculate = false) {
            if (count <= 0) return new List<DateTime>();
            if (!forceRecalculate && File.Exists(statePath)) {
                try {
                    var loaded = LoadState(statePath);
                    if (loaded != null && loaded.UnixSlots != null && loaded.UnixSlots.Count == count) {
                        var slots = loaded.UnixSlots.Select(x => DateTimeOffset.FromUnixTimeSeconds(x).LocalDateTime).ToList();
                        if (slots[0] >= DateTime.Now.AddSeconds(MinLeadSeconds - 30)) return slots;
                    }
                } catch { }
            }
            var result = new List<DateTime>();
            DateTime cursor = DateTime.Now.AddSeconds(MinLeadSeconds + Rng.Next(0, 60));
            result.Add(cursor);
            for (int i = 1; i < count; i++) {
                cursor = cursor.AddSeconds(Rng.Next(MinGapSeconds, MaxGapSeconds + 1));
                result.Add(cursor);
            }
            SaveState(statePath, count, result);
            return result;
        }

        public static List<long> GenerateUnixSlots(int count, string statePath, bool forceRecalculate = false) {
            return GenerateLocalSlots(count, statePath, forceRecalculate)
                .Select(dt => new DateTimeOffset(dt).ToUnixTimeSeconds())
                .ToList();
        }

        static TikTokScheduleState LoadState(string path) {
            using (var fs = File.OpenRead(path))
                return (TikTokScheduleState)new DataContractJsonSerializer(typeof(TikTokScheduleState)).ReadObject(fs);
        }

        static void SaveState(string path, int count, List<DateTime> localSlots) {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Store.Root);
            var state = new TikTokScheduleState {
                VideoCount = count,
                CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                UnixSlots = localSlots.Select(dt => new DateTimeOffset(dt).ToUnixTimeSeconds()).ToList()
            };
            using (var fs = File.Create(path))
                new DataContractJsonSerializer(typeof(TikTokScheduleState)).WriteObject(fs, state);
        }

        public static bool RunSelfTests() {
            string tmp = Path.Combine(Path.GetTempPath(), "vb-tiktok-sched-" + Guid.NewGuid().ToString("N") + ".json");
            try {
                var slots = GenerateLocalSlots(20, tmp, forceRecalculate: true);
                if (slots.Count != 20) return false;
                if (slots[0] < DateTime.Now.AddSeconds(MinLeadSeconds - 5)) return false;
                for (int i = 1; i < slots.Count; i++) {
                    var gap = (slots[i] - slots[i - 1]).TotalSeconds;
                    if (gap < MinGapSeconds || gap > MaxGapSeconds) return false;
                    if (slots[i] <= slots[i - 1]) return false;
                }
                var unix = new DateTimeOffset(slots[0]).ToUnixTimeSeconds();
                var back = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                if (Math.Abs((back - slots[0]).TotalSeconds) > 2) return false;
                var reloaded = GenerateLocalSlots(20, tmp, forceRecalculate: false);
                if (reloaded.Count != 20 || reloaded[0] != slots[0]) return false;
                return true;
            } finally {
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
