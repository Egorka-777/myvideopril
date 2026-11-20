using System;
using System.Linq;

namespace VideoBatch {
    public sealed class SubtitleRandomSettings {
        public bool Enabled;
        public int SizeMin = 44, SizeMax = 72, PositionMin = 65, PositionMax = 80;
        public int OutlineMin = 2, OutlineMax = 5, OpacityMin = 80, OpacityMax = 95, WordsMin = 2, WordsMax = 5;
        public int ThicknessMin = 0, ThicknessMax = 3, PaddingXMin = 22, PaddingXMax = 40, PaddingYMin = 12, PaddingYMax = 24;
        public string[] Fonts = SubtitlePresets.Fonts.ToArray();
        public string[] Colors = { "#FFFFFF", "#FFE84A", "#32F56B", "#65D9FF" };
        public string[] Motions = { "none", "pop", "fade", "rise", "word-accent" };
        public string[] Plates = { "none", "rounded", "solid" };
        public string[] Highlights = { "none", "active", "progressive" };
        public SubtitleRandomSettings Copy() {
            var copy = (SubtitleRandomSettings)MemberwiseClone();
            copy.Fonts = Fonts?.ToArray(); copy.Colors = Colors?.ToArray(); copy.Motions = Motions?.ToArray();
            copy.Plates = Plates?.ToArray(); copy.Highlights = Highlights?.ToArray();
            return copy;
        }
        static int Between(Random random, int min, int max, int lower, int upper) {
            int a = Math.Max(lower, Math.Min(upper, Math.Min(min, max)));
            int b = Math.Max(lower, Math.Min(upper, Math.Max(min, max)));
            return random.Next(a, b + 1);
        }
        public static WarmupSubtitleStyle ForVideo(WarmupSubtitleStyle source, Random random) {
            var style = source.Copy(); var options = style.RandomStyle;
            if (!style.Enabled || !options.Enabled) return style;
            if (options.Fonts == null || options.Fonts.Length == 0 || options.Colors == null || options.Colors.Length == 0 || options.Motions == null || options.Motions.Length == 0 || options.Plates == null || options.Plates.Length == 0 || options.Highlights == null || options.Highlights.Length == 0)
                throw new Exception("Случайные субтитры: выберите хотя бы один вариант в каждом списке.");
            style.FontSize = Between(random, options.SizeMin, options.SizeMax, 28, 150);
            style.TextThickness = Between(random, options.ThicknessMin, options.ThicknessMax, 0, 10);
            style.PlatePaddingX = Between(random, options.PaddingXMin, options.PaddingXMax, 0, 100);
            style.PlatePaddingY = Between(random, options.PaddingYMin, options.PaddingYMax, 0, 100);
            style.Position = Between(random, options.PositionMin, options.PositionMax, 10, 90);
            style.OutlineWidth = Between(random, options.OutlineMin, options.OutlineMax, 0, 10);
            style.Opacity = Between(random, options.OpacityMin, options.OpacityMax, 0, 100);
            style.WordsPerCaption = Between(random, options.WordsMin, options.WordsMax, 1, 6);
            style.FontFamily = options.Fonts[random.Next(options.Fonts.Length)];
            style.TextColor = options.Colors[random.Next(options.Colors.Length)];
            style.AccentColor = options.Colors[random.Next(options.Colors.Length)];
            style.Motion = options.Motions[random.Next(options.Motions.Length)];
            style.Plate = options.Plates[random.Next(options.Plates.Length)];
            style.Highlight = options.Highlights[random.Next(options.Highlights.Length)];
            style.RandomStyle.Enabled = false; style.Normalize();
            return style;
        }
    }
}
