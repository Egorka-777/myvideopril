using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace VideoBatch {
    /// <summary>Human-readable result folders under «Видео\VideoBatch» and safe unique file names.</summary>
    public static class VideoBatchOutputPaths {
        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch");

        public static string Processing => Path.Combine(Root, "Обработка видео");
        public static string Highlights => Path.Combine(Root, "Нарезчик");
        public static string WarmupRoot => Path.Combine(Root, "Прогрев");
        public static string AssemblyRoot => Path.Combine(Root, "Сборка роликов");

        public static string WarmupMarket(string market) => Path.Combine(WarmupRoot, market == "EN" ? "EN" : "RU");

        public static string AssemblyFolder(string profile) =>
            Path.Combine(AssemblyRoot, AssemblyProfile.ShortLabel(AssemblyProfile.Normalize(profile)));

        public static string LegacyAssemblyProfile(string profile) {
            profile = AssemblyProfile.Normalize(profile);
            if (profile == AssemblyProfile.En) return Path.Combine(Root, "Assembled-EN");
            if (profile == AssemblyProfile.RuInst) return Path.Combine(Root, "Assembled-RU-Inst");
            if (profile == AssemblyProfile.EnInst) return Path.Combine(Root, "Assembled-EN-Inst");
            if (profile == AssemblyProfile.RuTikTok) return Path.Combine(Root, "Assembled-RU-TikTok");
            if (profile == AssemblyProfile.EnTikTok) return Path.Combine(Root, "Assembled-EN-TikTok");
            if (profile == AssemblyProfile.Test) return Path.Combine(Root, "Assembled-TEST");
            return Path.Combine(Root, "Assembled");
        }

        static readonly HashSet<string> LegacyAssemblySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static VideoBatchOutputPaths() {
            foreach (string profile in AssemblyProfile.All)
                LegacyAssemblySet.Add(LegacyAssemblyProfile(profile));
        }

        public static string SanitizeFileStem(string stem) {
            stem = (stem ?? "").Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) stem = stem.Replace(ch, ' ');
            stem = Regex.Replace(stem, @"\s+", " ").Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(stem) ? "файл" : stem;
        }

        public static string AssemblyChannelLabel(AssemblyTemplate t) {
            string scope = (t?.HistoryScope ?? "").Trim();
            if (scope.Length == 0 || scope.Equals(AssemblyHistoryScope.Shared, StringComparison.OrdinalIgnoreCase) || scope.Equals("Общий", StringComparison.OrdinalIgnoreCase))
                return "Общий";
            return AssemblyProfile.ShortLabel(AssemblyProfile.Normalize(scope));
        }

        public static string AssemblyVideoFileName(AssemblyTemplate t, int index, string titleHint = null) {
            string stem = "Ролик " + AssemblyChannelLabel(t) + " · " + (index + 1).ToString("000") + " · " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            if (!string.IsNullOrWhiteSpace(titleHint)) {
                string shortTitle = titleHint.Trim();
                if (shortTitle.Length > 36) shortTitle = shortTitle.Substring(0, 36).Trim() + "…";
                stem += " · " + shortTitle;
            }
            stem += " · " + Guid.NewGuid().ToString("N").Substring(0, 8);
            return SanitizeFileStem(stem) + ".mp4";
        }

        public static string WarmupVideoFileName(string market) {
            string stem = "Прогрев " + (market == "EN" ? "EN" : "RU") + " · " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + " · " + Guid.NewGuid().ToString("N").Substring(0, 8);
            return SanitizeFileStem(stem) + ".mp4";
        }

        public static string RemapAssemblyOutput(string current, string profile) {
            if (string.IsNullOrWhiteSpace(current)) return AssemblyFolder(profile);
            string norm = PathUtil.Normalize(current);
            if (LegacyAssemblySet.Contains(norm) || norm.Equals(LegacyAssemblyProfile(profile), StringComparison.OrdinalIgnoreCase))
                return AssemblyFolder(profile);
            return current;
        }

        public static string RemapProcessingOutput(string current) {
            if (string.IsNullOrWhiteSpace(current)) return Processing;
            string norm = PathUtil.Normalize(current);
            if (norm.Equals(PathUtil.Normalize(Root), StringComparison.OrdinalIgnoreCase)) return Processing;
            return current;
        }

        public static string RemapWarmupOutput(string current, string market) {
            string target = WarmupMarket(market);
            if (string.IsNullOrWhiteSpace(current)) return target;
            string norm = PathUtil.Normalize(current);
            string legacyMarket = Path.Combine(Root, "Warmup-" + market);
            string legacyFlat = WarmupRoot;
            if (norm.Equals(PathUtil.Normalize(legacyMarket), StringComparison.OrdinalIgnoreCase)
                || norm.Equals(PathUtil.Normalize(legacyFlat), StringComparison.OrdinalIgnoreCase))
                return target;
            return current;
        }

    }
}
