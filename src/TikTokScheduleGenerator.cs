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

    /// <summary>TikTok HTTP batch schedule: first slot ≥ now+15min, then +5..15 min random gaps.</summary>
    public static class TikTokScheduleGenerator {
        public const int MinLeadSeconds = 900;
        /// <summary>Extra lead so C# slots stay valid after UI delay and worker unix check.</summary>
        public const int ScheduleBufferSeconds = 60;
        public const int MinGapSeconds = 300;
        public const int MaxGapSeconds = 900;
        static readonly Random Rng = new Random();

        static long MinFirstUnixUtc() {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + MinLeadSeconds + ScheduleBufferSeconds;
        }

        static bool CachedSlotsValid(TikTokScheduleState loaded, int count) {
            if (loaded == null || loaded.UnixSlots == null || loaded.UnixSlots.Count != count) return false;
            long minFirst = MinFirstUnixUtc();
            if (loaded.UnixSlots[0] < minFirst) return false;
            var slots = loaded.UnixSlots.Select(x => DateTimeOffset.FromUnixTimeSeconds(x).LocalDateTime).ToList();
            return slots.Zip(slots.Skip(1), (a, b) => (b - a).TotalSeconds)
                .All(g => g >= MinGapSeconds && g <= MaxGapSeconds);
        }

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
                    if (CachedSlotsValid(loaded, count)) {
                        return loaded.UnixSlots
                            .Select(x => DateTimeOffset.FromUnixTimeSeconds(x).LocalDateTime)
                            .ToList();
                    }
                } catch { }
            }
            var result = new List<DateTime>();
            long firstUnix = MinFirstUnixUtc() + Rng.Next(0, 59);
            DateTime cursor = DateTimeOffset.FromUnixTimeSeconds(firstUnix).LocalDateTime;
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

        // Native web controls commonly expose minutes. Use whole-minute gaps;
        // HTTP slots remain randomized to the second.
        public static List<long> GenerateStudioUnixSlots(int count) {
            var slots=new List<long>();
            if(count<=0)return slots;
            long cursor=((MinFirstUnixUtc()+59)/60)*60;
            lock(Rng){
                cursor+=Rng.Next(0,2)*60;
                slots.Add(cursor);
                for(int i=1;i<count;i++){cursor+=Rng.Next(5,16)*60;slots.Add(cursor);}
            }
            return slots;
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
                var studio=GenerateStudioUnixSlots(20);
                if(studio.Count!=20||studio.Any(t=>t%60!=0))return false;
                for(int i=1;i<studio.Count;i++)if(studio[i]-studio[i-1]<300||studio[i]-studio[i-1]>900)return false;
                var slots = GenerateLocalSlots(20, tmp, forceRecalculate: true);
                if (slots.Count != 20) return false;
                if (slots[0] < DateTime.Now.AddSeconds(MinLeadSeconds + ScheduleBufferSeconds - 5)) return false;
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

