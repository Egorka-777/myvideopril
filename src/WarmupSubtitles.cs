using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VideoBatch {
    public sealed class WarmupSubtitleStyle {
        public bool Enabled = true;
        public string FontFamily = "Segoe UI", TextColor = "#FFFFFF", PlateColor = "#171717", Plate = "rounded";
        public int FontSize = 56, Position = 72, WordsPerCaption = 3, Opacity = 90;
        public WarmupSubtitleStyle Copy() => (WarmupSubtitleStyle)MemberwiseClone();
        public void Normalize() {
            if (FontFamily != "Segoe UI" && FontFamily != "Arial" && FontFamily != "Tahoma") FontFamily = "Segoe UI";
            if (Plate != "rounded" && Plate != "solid" && Plate != "none") Plate = "rounded";
            FontSize = Math.Max(28, Math.Min(88, FontSize)); Position = Math.Max(10, Math.Min(90, Position));
            WordsPerCaption = Math.Max(1, Math.Min(6, WordsPerCaption)); Opacity = Math.Max(0, Math.Min(100, Opacity));
            TextColor = Hex(TextColor, "#FFFFFF"); PlateColor = Hex(PlateColor, "#171717");
        }
        static string Hex(string value, string fallback) => System.Text.RegularExpressions.Regex.IsMatch(value ?? "", "^#[0-9a-fA-F]{6}$") ? value.ToUpperInvariant() : fallback;
    }
    public sealed class WarmupAlignment {
        public string[] characters { get; set; }
        public double[] character_start_times_seconds { get; set; }
        public double[] character_end_times_seconds { get; set; }
    }
    public sealed class WarmupCaption {
        public string Text, ImagePath;
        public double Start, End;
    }
    public static class WarmupSubtitles {
        // Build complete words using the provider's character alignment. No estimated reading speed.
        public static List<WarmupCaption> Cues(WarmupAlignment alignment, double duration, int wordsPerCaption) {
            if (alignment == null || alignment.characters == null || alignment.character_start_times_seconds == null || alignment.character_end_times_seconds == null || alignment.characters.Length != alignment.character_start_times_seconds.Length || alignment.characters.Length != alignment.character_end_times_seconds.Length)
                throw new Exception("ElevenLabs не вернул таймкоды субтитров. Повторите запрос или отключите субтитры.");
            var words = new List<WarmupCaption>(); var word = new StringBuilder(); double start = 0, end = 0, lastStart = -1;
            Action finish = () => { if (word.Length > 0) { words.Add(new WarmupCaption { Text = word.ToString(), Start = start, End = end }); word.Clear(); } };
            for (int i = 0; i < alignment.characters.Length; i++) {
                double a = alignment.character_start_times_seconds[i], b = alignment.character_end_times_seconds[i];
                if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b) || a < 0 || b < a || a < lastStart || b > duration + .15)
                    throw new Exception("ElevenLabs вернул некорректные таймкоды субтитров.");
                lastStart = a;
                string chars = alignment.characters[i] ?? "";
                foreach (char c in chars) {
                    if (char.IsWhiteSpace(c)) finish();
                    else { if (word.Length == 0) start = Math.Min(a, duration); end = Math.Min(b, duration); word.Append(c); }
                }
            }
            finish();
            var cues = new List<WarmupCaption>(); wordsPerCaption = Math.Max(1, Math.Min(6, wordsPerCaption));
            for (int i = 0; i < words.Count;) {
                var part = new List<WarmupCaption>();
                do { var next = words[i++]; part.Add(next); if (".!?…".Contains(next.Text.Last())) break; } while (i < words.Count && part.Count < wordsPerCaption);
                double cueEnd = part.Last().End;
                if (i < words.Count) cueEnd = Math.Min(words[i].Start, cueEnd + .12);
                cueEnd = Math.Min(duration, cueEnd);
                if (cueEnd > part[0].Start) cues.Add(new WarmupCaption { Text = string.Join(" ", part.Select(p => p.Text)), Start = part[0].Start, End = cueEnd });
            }
            if (cues.Count == 0) throw new Exception("В озвучке нет пригодных таймкодов субтитров.");
            return cues;
        }
        // Shared renderer: the preview and exported captions use the same font, wrapping and plate geometry.
        public static Bitmap Paint(string text, WarmupSubtitleStyle style) {
            style = style.Copy(); style.Normalize();
            var bitmap = new Bitmap(1080, 1920, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap)) using (var font = new Font(style.FontFamily, style.FontSize, FontStyle.Bold, GraphicsUnit.Pixel)) using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var measured = g.MeasureString(text, font, new SizeF(900, 700), format);
                float width = Math.Min(960, measured.Width + 52), height = Math.Min(760, measured.Height + 34);
                float y = Math.Max(24, Math.Min(1920 - height - 24, 1920 * style.Position / 100f - height / 2));
                var plate = new RectangleF((1080 - width) / 2, y, width, height);
                if (style.Plate != "none") using (var brush = new SolidBrush(Color.FromArgb((int)(255 * style.Opacity / 100f), ColorTranslator.FromHtml(style.PlateColor)))) {
                    if (style.Plate == "solid") g.FillRectangle(brush, plate);
                    else using (var path = Rounded(plate, 24)) g.FillPath(brush, path);
                }
                var box = new RectangleF(plate.X + 26, plate.Y + 17, plate.Width - 52, plate.Height - 34);
                using (var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.DrawString(text, font, shadow, new RectangleF(box.X + 2, box.Y + 2, box.Width, box.Height), format);
                using (var brush = new SolidBrush(ColorTranslator.FromHtml(style.TextColor))) g.DrawString(text, font, brush, box, format);
            }
            return bitmap;
        }
        static GraphicsPath Rounded(RectangleF r, float radius) {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height)); var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        public static List<WarmupCaption> Rasterize(List<WarmupCaption> cues, WarmupSubtitleStyle style, string folder) {
            for (int i = 0; i < cues.Count; i++) {
                cues[i].ImagePath = Path.Combine(folder, "caption-" + i + ".png");
                using (var image = Paint(cues[i].Text, style)) image.Save(cues[i].ImagePath, ImageFormat.Png);
            }
            return cues;
        }
    }
}
