using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace VideoBatch {
    /// <summary>Clean upload titles: remove leading junk only; preserve words and meaningful digits.</summary>
    public static class TitleCleaner {
        static readonly Regex LeadingIndex = new Regex(@"^\s*(?:\[\s*\d+\s*\]|\(\s*\d+\s*\)|\d+\s*[\._\-]\s*|\d+\.\s*|\d+\s+)", RegexOptions.Compiled);
        static readonly Regex ExtSuffix = new Regex(@"\.(mp4|mov|mkv|webm|m4v|avi|mts|m2ts|ts|wmv)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex MultiSpace = new Regex(@"\s+", RegexOptions.Compiled);
        static readonly Regex DotUnderscoreGap = new Regex(@"(?<=\S)[\._]+(?=\S)", RegexOptions.Compiled);

        public static string CleanForUpload(string title) {
            if (string.IsNullOrWhiteSpace(title)) return "";
            string t = title.Trim();
            t = ExtSuffix.Replace(t, "");
            while (true) {
                var m = LeadingIndex.Match(t);
                if (!m.Success) break;
                t = t.Substring(m.Length).Trim();
            }
            t = DotUnderscoreGap.Replace(t, " ");
            t = MultiSpace.Replace(t, " ").Trim();
            t = t.Trim(' ', '.', '_', '-');
            return t;
        }

        public static string CleanFromFileName(string filePath) {
            if (string.IsNullOrWhiteSpace(filePath)) return "";
            string name = Path.GetFileNameWithoutExtension(filePath);
            string cleaned = CleanForUpload(name);
            cleaned = cleaned.Replace(" . ", " | ").Replace(" · ", " | ");
            if (cleaned.Contains(" . ")) cleaned = cleaned.Replace(" . ", " | ");
            var parts = cleaned.Split(new[] { " . ", " · ", " | " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) cleaned = string.Join(" | ", parts.Select(p => p.Trim()).Where(p => p.Length > 0));
            return cleaned;
        }

        public static void ValidateLength(IWin32Window owner, string title) {
            if (string.IsNullOrWhiteSpace(title)) throw new Exception("Пустой заголовок.");
            if (title.Length > 100) throw new Exception("Заголовок длиннее 100 символов (" + title.Length + "). Сократите его вручную — автоматическая обрезка запрещена.");
            if (title.Contains("<") || title.Contains(">")) throw new Exception("Заголовок не должен содержать < или >.");
        }

        public static bool RunSelfTests() {
            string sample = "001. Трейдинг на Бинодекс . Бинарные опционы стратегия . BinoDex.mp4";
            string got = CleanForUpload(sample);
            if (!got.Contains("Трейдинг") || !got.Contains("BinoDex")) return false;
            if (got.StartsWith("001")) return false;
            if (CleanForUpload("12.34 стратегия 2024").IndexOf("2024") < 0) return false;
            if (CleanForUpload("[01] Тест").StartsWith("[01]")) return false;
            return true;
        }
    }
}
