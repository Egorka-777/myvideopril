using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VideoBatch {
    public static class YouTubeMetadata {
        public const string TagsFooterPrefix = "Тэги к видео:";

        public static readonly string[] AllRuTags = {
            "binodex", "бинодекс", "трейдинг", "binodex регистрация", "binodex обзор", "обучение трейдингу",
            "binodex бинарные опционы", "трейдинг для начинающих", "binodex трейдинг", "binodex отзывы",
            "трейдинг с нуля", "binodex обучение", "binodex платформа", "binodex сигналы", "binodex стратегия",
            "binodex торговля", "binodex брокер", "трейдинг обучение с нуля", "бинарные опциоы", "pocket option",
            "покет опшн", "ии бот для трейдинга", "бинарные опционы стратегия", "ии трейдинг",
            "binary options strategy", "binary options"
        };

        public static string DefaultTagsCsv(string market) {
            return string.Equals((market ?? "").Trim(), "EN", StringComparison.OrdinalIgnoreCase)
                ? YouTubeTagDefaults.En
                : string.Join(", ", AllRuTags);
        }

        public static IList<string> ParseTags(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            return raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().TrimStart('#'))
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IList<string> MasterTags(string market, string configuredCsv) {
            var parsed = ParseTags(configuredCsv);
            if (parsed.Count > 0) return parsed;
            return string.Equals((market ?? "").Trim(), "EN", StringComparison.OrdinalIgnoreCase)
                ? ParseTags(YouTubeTagDefaults.En)
                : AllRuTags.ToList();
        }

        public static string[] ShuffleTags(string profileId, IList<string> tags) {
            var list = (tags ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (list.Count <= 1) return list.ToArray();
            int seed = StableSeed(profileId);
            var rng = new Random(seed);
            for (int i = list.Count - 1; i > 0; i--) {
                int j = rng.Next(i + 1);
                string tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
            return list.ToArray();
        }

        static int StableSeed(string profileId) {
            unchecked {
                int hash = 17;
                string pid = (profileId ?? "").Trim();
                foreach (char c in pid) hash = hash * 31 + char.ToLowerInvariant(c);
                return hash;
            }
        }

        public static string JoinTags(IEnumerable<string> tags) {
            return string.Join(", ", tags ?? Enumerable.Empty<string>());
        }

        public static string StripLegacyTagFooter(string text) {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                lines.RemoveAt(lines.Count - 1);
            while (lines.Count > 0) {
                string last = (lines[lines.Count - 1] ?? "").Trim();
                if (last.StartsWith("#", StringComparison.Ordinal) || Regex.IsMatch(last, @"#BinoDex|#обучениетрейдингу|#pocketoption|#трейдинг|#aitrading", RegexOptions.IgnoreCase)) {
                    lines.RemoveAt(lines.Count - 1);
                    continue;
                }
                if (last.Contains("•") && Regex.IsMatch(last, @"Binodex|Бинодекс|трейдинг|ИИ", RegexOptions.IgnoreCase)) {
                    lines.RemoveAt(lines.Count - 1);
                    continue;
                }
                break;
            }
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                lines.RemoveAt(lines.Count - 1);
            return string.Join(Environment.NewLine, lines).Trim();
        }

        public static string AppendTagsFooter(string baseDescription, IEnumerable<string> shuffledTags) {
            string body = StripLegacyTagFooter(baseDescription);
            string footer = TagsFooterPrefix + " " + JoinTags(shuffledTags);
            if (string.IsNullOrWhiteSpace(body)) return footer;
            return body + Environment.NewLine + Environment.NewLine + footer;
        }

        public static string PickDescriptionBase(Preferences settings, YouTubeChannel channel, int itemIndex) {
            if (settings == null || channel == null) return "";
            string market = (channel.Market ?? "RU").Trim().ToUpperInvariant();
            if (market != "RU") return "";
            var bank = settings.YouTubeDescriptionBankRu ?? new List<string>();
            if (bank.Count == 0) bank = settings.TikTokFullCaptionBankRu ?? new List<string>();
            if (bank.Count == 0) return "";
            int cursor = settings.YouTubeDescriptionCursorRu;
            if (cursor < 0) cursor = 0;
            int idx = (cursor + Math.Max(0, itemIndex - 1)) % bank.Count;
            return (bank[idx] ?? "").Trim();
        }

        public static void AdvanceDescriptionCursor(Preferences settings, int uploadedCount) {
            if (settings == null || uploadedCount <= 0) return;
            var bank = settings.YouTubeDescriptionBankRu ?? new List<string>();
            if (bank.Count == 0) bank = settings.TikTokFullCaptionBankRu ?? new List<string>();
            if (bank.Count == 0) return;
            settings.YouTubeDescriptionCursorRu = (settings.YouTubeDescriptionCursorRu + uploadedCount) % bank.Count;
        }
    }
}
