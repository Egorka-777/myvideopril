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
        public static string Creative => Path.Combine(AssemblyRoot, "Креативы");

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

        static readonly Regex DayFolderPattern = new Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

        public static string DailyFolderName(DateTime? when = null) => (when ?? DateTime.Now).ToString("yyyy-MM-dd");

        public static bool IsDayFolder(string name) => !string.IsNullOrWhiteSpace(name) && DayFolderPattern.IsMatch(name.Trim());

        /// <summary>Убирает подпапку «yyyy-MM-dd», если путь уже указывает на дневную сессию.</summary>
        public static string SectionRoot(string path) {
            path = PathUtil.Normalize(path ?? "");
            if (string.IsNullOrEmpty(path)) return path;
            string leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!IsDayFolder(leaf)) return path;
            string parent = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(parent) ? path : parent;
        }

        /// <summary>Подраздел + сегодняшняя дата; создаёт папку при необходимости.</summary>
        public static string DailySessionFolder(string sectionRoot, DateTime? when = null) {
            sectionRoot = SectionRoot(sectionRoot);
            if (string.IsNullOrEmpty(sectionRoot)) sectionRoot = Processing;
            string day = Path.Combine(sectionRoot, DailyFolderName(when));
            Directory.CreateDirectory(day);
            return day;
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
            return AssemblyProfile.ShortLabel(AssemblyProfile.Normalize(scope), t);
        }

        public static string SourceTitle(string path) {
            string title=Path.GetFileNameWithoutExtension(path??"");
            var old=Regex.Match(title,@"^Ролик .+? · \d+ · \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2} · (.+) · [0-9a-f]{8}$",RegexOptions.IgnoreCase);
            if(old.Success)title=old.Groups[1].Value;
            title=Regex.Replace(title,@"_v\d+_[0-9a-f]{8}$","",RegexOptions.IgnoreCase);
            return SanitizeFileStem(title);
        }
        public static string NamedOutput(string folder,string title,string extension) {
            folder = PathUtil.Normalize(folder ?? "");
            string stem=SanitizeFileStem(title);
            // Leave room for «.videobatch-reports\…mp4.assembly.xml.{guid}.tmp» beside the MP4 (Win32 ~259).
            const int windowsMaxPath = 259;
            const int reportAndTempReserve = 77;
            int limit=Math.Max(16,Math.Min(96,windowsMaxPath-folder.Length-reportAndTempReserve));
            if(stem.Length>limit)stem=stem.Substring(0,limit).TrimEnd(' ','.');
            for(int i=1;;i++) {
                string path=Path.Combine(folder,stem+" "+i+"."+extension.TrimStart('.'));
                if(!File.Exists(path)&&!File.Exists(Path.Combine(folder,".processing_"+Path.GetFileName(path))))return path;
            }
        }
        public static string AssemblyVideoFileName(AssemblyTemplate t, int index, string titleHint = null) {
            return Path.GetFileName(NamedOutput(t.Output,string.IsNullOrWhiteSpace(titleHint)?"Ролик":titleHint,"mp4"));
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
