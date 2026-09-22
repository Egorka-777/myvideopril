using System;
using System.Runtime.InteropServices;

namespace VideoBatch {
    public static class HttpWorkerSettings {
        public const int DefaultWorkers = 3;
        public const int MaxCustomWorkers = 10;
        public const long AutoMinAvailableMemoryMb = 1024;
        public static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(90);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct MemoryStatusEx {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        public static int ResolveWorkerCount(Preferences prefs) {
            string preset = (prefs?.HttpWorkerPreset ?? "normal").Trim().ToLowerInvariant();
            switch (preset) {
                case "safe": return 1;
                case "normal": return DefaultWorkers;
                case "fast": return 5;
                case "custom":
                    int custom = prefs?.HttpWorkerCustomCount ?? DefaultWorkers;
                    return Math.Max(1, Math.Min(MaxCustomWorkers, custom <= 0 ? DefaultWorkers : custom));
                case "auto": return DefaultWorkers;
                default:
                    if (prefs != null && prefs.MaxParallelUploads > 0)
                        return Math.Max(1, Math.Min(MaxCustomWorkers, prefs.MaxParallelUploads));
                    return DefaultWorkers;
            }
        }

        public static bool IsAutoMode(Preferences prefs) {
            return string.Equals(prefs?.HttpWorkerPreset, "auto", StringComparison.OrdinalIgnoreCase);
        }

        public static string PresetLabel(Preferences prefs) {
            string preset = (prefs?.HttpWorkerPreset ?? "normal").Trim().ToLowerInvariant();
            switch (preset) {
                case "safe": return "Безопасный (1)";
                case "fast": return "Быстрый (5)";
                case "custom": return "Пользовательский (" + ResolveWorkerCount(prefs) + ")";
                case "auto": return "Auto (до 3, по памяти)";
                default: return "Обычный (3)";
            }
        }

        public static string ResolvePublishMode(Preferences prefs) {
            string mode = (prefs?.YouTubeHttpPublishMode ?? "scheduled").Trim().ToLowerInvariant();
            if (mode == "immediate" || mode == "private") return mode;
            return "scheduled";
        }

        public static string ResolvePublishMode(Preferences prefs, string kind) {
            if (!string.Equals((kind ?? "").Trim(), "long", StringComparison.OrdinalIgnoreCase)) return ResolvePublishMode(prefs);
            string mode = (prefs?.YouTubeHttpLongPublishMode ?? "immediate").Trim().ToLowerInvariant();
            return mode == "scheduled" || mode == "private" ? mode : "immediate";
        }

        public static long GetAvailableMemoryMb() {
            try {
                var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (!GlobalMemoryStatusEx(ref status)) return long.MaxValue;
                return (long)(status.ullAvailPhys / (1024UL * 1024UL));
            } catch { return long.MaxValue; }
        }

        public static bool CanStartAnotherWorker() {
            return GetAvailableMemoryMb() >= AutoMinAvailableMemoryMb;
        }
    }
}
