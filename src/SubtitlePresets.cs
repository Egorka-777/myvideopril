using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace VideoBatch {
    public sealed class SubtitleWord { public string Text; public double Start, End; }
    public sealed class SubtitlePreset {
        public string Id, Name;
        public WarmupSubtitleStyle Style = new WarmupSubtitleStyle();
        public override string ToString() => Name;
    }
    public sealed class SubtitlePresetFile { public List<SubtitlePreset> Presets = new List<SubtitlePreset>(); }

    public static class SubtitlePresets {
        public static readonly string[] Fonts = { "Segoe UI", "Arial", "Tahoma", "Impact", "Arial Black", "Trebuchet MS" };
        public static List<SubtitlePreset> BuiltIn() {
            var result = new List<SubtitlePreset>();
            Action<string, string, WarmupSubtitleStyle> add = (id, name, style) => {
                style.PresetId = id; style.Normalize();
                result.Add(new SubtitlePreset { Id = id, Name = name, Style = style });
            };
            add("classic", "Классика", new WarmupSubtitleStyle());
            add("bold-green", "Зелёный акцент", new WarmupSubtitleStyle { FontFamily = "Impact", FontSize = 78, Uppercase = true, Plate = "none", OutlineWidth = 5, Highlight = "active" });
            add("bold-yellow", "Жёлтый акцент", new WarmupSubtitleStyle { FontFamily = "Arial Black", FontSize = 68, Uppercase = true, Plate = "none", OutlineWidth = 4, Highlight = "active", AccentColor = "#FFE84A" });
            add("soft-plate", "Мягкая плашка", new WarmupSubtitleStyle { FontSize = 58, Plate = "rounded", PlateColor = "#10131A", Opacity = 88 });
            add("karaoke", "Караоке", new WarmupSubtitleStyle { FontSize = 64, Uppercase = true, Plate = "none", OutlineWidth = 4, Highlight = "progressive", AccentColor = "#FFE84A", WordsPerCaption = 4 });
            add("word-pop", "По одному слову", new WarmupSubtitleStyle { FontFamily = "Impact", FontSize = 86, Uppercase = true, Plate = "none", OutlineWidth = 5, WordsPerCaption = 1, Motion = "pop" });
            add("minimal", "Минимализм", new WarmupSubtitleStyle { FontSize = 44, Bold = false, Plate = "none", OutlineWidth = 1, WordsPerCaption = 5 });
            return result;
        }
        public static List<SubtitlePreset> Load(string path = null) {
            var result = BuiltIn(); path = path ?? Path.Combine(Store.Root, "subtitle-presets.xml");
            if (!File.Exists(path)) return result;
            using (var reader = File.OpenRead(path)) {
                var saved = (SubtitlePresetFile)new XmlSerializer(typeof(SubtitlePresetFile)).Deserialize(reader);
                foreach (var preset in saved?.Presets ?? new List<SubtitlePreset>()) {
                    if (preset == null || preset.Style == null || !IsCustom(preset.Id) || string.IsNullOrWhiteSpace(preset.Name)) continue;
                    preset.Style.Normalize(); preset.Style.PresetId = preset.Id;
                    result.Add(preset);
                }
            }
            return result;
        }
        public static bool IsCustom(string id) => (id ?? "").StartsWith("custom-", StringComparison.Ordinal);
        public static SubtitlePreset Save(string name, WarmupSubtitleStyle style, string path = null) {
            name = (name ?? "").Trim();
            if (name.Length < 1 || name.Length > 40) throw new Exception("Название стиля: от 1 до 40 символов.");
            path = path ?? Path.Combine(Store.Root, "subtitle-presets.xml");
            var presets = Load(path).Where(p => IsCustom(p.Id)).ToList();
            var preset = new SubtitlePreset { Id = "custom-" + Guid.NewGuid().ToString("N"), Name = name, Style = style.Copy() };
            preset.Style.PresetId = preset.Id; preset.Style.Normalize(); presets.Add(preset);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var writer = File.Create(temp)) new XmlSerializer(typeof(SubtitlePresetFile)).Serialize(writer, new SubtitlePresetFile { Presets = presets });
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            return preset;
        }
        public static List<WarmupCaption> ForStyle(IEnumerable<WarmupCaption> source, WarmupSubtitleStyle style) {
            var cues = (source ?? Enumerable.Empty<WarmupCaption>()).Where(c => c != null).ToList();
            if (cues.Count == 0 || cues.Any(c => c.Words == null || c.Words.Count == 0)) return cues;
            var words = cues.SelectMany(c => c.Words).ToList();
            return WarmupSubtitles.Cues(new WarmupAlignment {
                characters = words.Select(w => w.Text + " ").ToArray(),
                character_start_times_seconds = words.Select(w => w.Start).ToArray(),
                character_end_times_seconds = words.Select(w => w.End).ToArray()
            }, Math.Max(cues.Max(c => c.End), words.Max(w => w.End)), style.WordsPerCaption);
        }
        public static WarmupCaption FrameAt(WarmupCaption cue, WarmupSubtitleStyle style, double time) {
            int active = -1;
            if (cue.Words != null) for (int i = 0; i < cue.Words.Count; i++) if (cue.Words[i].Start <= time) active = i;
            double elapsed = Math.Max(0, time - cue.Start);
            float scale = style.Motion != "pop" ? 1 : elapsed < .05 ? .88f : elapsed < .10 ? .97f : 1;
            return new WarmupCaption { Text = cue.Text, Start = cue.Start, End = cue.End, Words = cue.Words, ActiveWord = active, TextScale = scale };
        }
        public static List<WarmupCaption> Frames(IEnumerable<WarmupCaption> source, WarmupSubtitleStyle style) {
            var result = new List<WarmupCaption>();
            foreach (var cue in ForStyle(source, style)) {
                var cuts = new List<double> { cue.Start, cue.End };
                if (style.Highlight != "none" && cue.Words != null) cuts.AddRange(cue.Words.Select(w => w.Start));
                if (style.Motion == "pop") { cuts.Add(cue.Start + .05); cuts.Add(cue.Start + .10); }
                cuts = cuts.Where(t => t >= cue.Start && t <= cue.End).Distinct().OrderBy(t => t).ToList();
                for (int i = 0; i + 1 < cuts.Count; i++) {
                    if (cuts[i + 1] <= cuts[i]) continue;
                    var frame = FrameAt(cue, style, cuts[i] + .0001); frame.Start = cuts[i]; frame.End = cuts[i + 1]; result.Add(frame);
                }
            }
            return result;
        }
    }

    // Same glyph paths, outline, wrapping and word states in the editor and PNG overlays.
    public static class SubtitleTypography {
        sealed class WordBox { public string Text; public RectangleF Box; public int Index; }
        public static bool UsesTypography(WarmupSubtitleStyle style) => style.OutlineWidth > 0 || style.Highlight != "none" || style.Motion != "none" || !style.Bold || style.FontFamily == "Impact" || style.FontFamily == "Arial Black";
        static List<WordBox> Layout(string text, WarmupSubtitleStyle style, int width, int height, out RectangleF plate) {
            style = style.Copy(); style.Normalize();
            text = style.Uppercase ? (text ?? "").ToUpper(CultureInfo.GetCultureInfo("ru-RU")) : text ?? "";
            var words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<WordBox>();
            using (var image = new Bitmap(1, 1)) using (var g = Graphics.FromImage(image))
            using (var font = new Font(style.FontFamily, style.FontSize, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone()) {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                float maxWidth = width * .84f, space = g.MeasureString(" ", font, int.MaxValue, format).Width;
                float lineHeight = font.GetHeight(g) * 1.10f, x = 0, y = 0;
                var lines = new List<List<WordBox>> { new List<WordBox>() };
                for (int i = 0; i < words.Length; i++) {
                    float wordWidth = Math.Min(maxWidth, g.MeasureString(words[i], font, int.MaxValue, format).Width);
                    if (x > 0 && x + wordWidth > maxWidth) { x = 0; y += lineHeight; lines.Add(new List<WordBox>()); }
                    var item = new WordBox { Text = words[i], Box = new RectangleF(x, y, wordWidth, lineHeight), Index = i };
                    lines.Last().Add(item); result.Add(item); x += wordWidth + space;
                }
                float totalHeight = Math.Max(lineHeight, y + lineHeight);
                float top = Math.Max(24, Math.Min(height - totalHeight - 24, height * style.Position / 100f - totalHeight / 2));
                foreach (var line in lines) {
                    float span = line.Count == 0 ? 0 : line.Last().Box.Right;
                    foreach (var item in line) item.Box = new RectangleF((width - span) / 2 + item.Box.X, top + item.Box.Y, item.Box.Width, item.Box.Height);
                }
                float left = result.Count == 0 ? width / 2 : result.Min(w => w.Box.Left);
                float right = result.Count == 0 ? left : result.Max(w => w.Box.Right);
                plate = new RectangleF(left - 22, top - 12, right - left + 44, totalHeight + 24);
            }
            return result;
        }
        public static RectangleF Bounds(string text, WarmupSubtitleStyle style, int width, int height) { Layout(text, style, width, height, out RectangleF plate); return plate; }
        public static Bitmap Paint(WarmupCaption cue, WarmupSubtitleStyle style, int width, int height) {
            style = style.Copy(); style.Normalize();
            var words = Layout(cue.Text, style, width, height, out RectangleF plate);
            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var font = new Font(style.FontFamily, style.FontSize, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var family = new FontFamily(font.FontFamily.Name))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone()) {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = cue.TextScale <= 0 ? 1 : cue.TextScale;
                g.TranslateTransform(plate.Left + plate.Width / 2, plate.Top + plate.Height / 2);
                g.ScaleTransform(scale, scale); g.TranslateTransform(-(plate.Left + plate.Width / 2), -(plate.Top + plate.Height / 2));
                if (style.Plate != "none") using (var brush = new SolidBrush(Color.FromArgb(255 * style.Opacity / 100, ColorTranslator.FromHtml(style.PlateColor)))) {
                    if (style.Plate == "solid") g.FillRectangle(brush, plate);
                    else using (var path = Rounded(plate)) g.FillPath(brush, path);
                }
                foreach (var word in words) using (var path = new GraphicsPath()) {
                    var face = style.Bold && family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
                    path.AddString(word.Text, family, (int)face, style.FontSize, new PointF(word.Box.X, word.Box.Y), format);
                    using (var shadow = (GraphicsPath)path.Clone()) using (var shift = new Matrix()) using (var brush = new SolidBrush(Color.FromArgb(160, 0, 0, 0))) {
                        shift.Translate(2, 3); shadow.Transform(shift); g.FillPath(brush, shadow);
                    }
                    if (style.OutlineWidth > 0) using (var pen = new Pen(ColorTranslator.FromHtml(style.OutlineColor), style.OutlineWidth * 2) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                    bool accent = style.Highlight == "active" ? word.Index == cue.ActiveWord : style.Highlight == "progressive" && word.Index <= cue.ActiveWord;
                    using (var brush = new SolidBrush(ColorTranslator.FromHtml(accent ? style.AccentColor : style.TextColor))) g.FillPath(brush, path);
                }
            }
            return bitmap;
        }
        static GraphicsPath Rounded(RectangleF r) {
            var path = new GraphicsPath(); float d = Math.Min(32, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
    }
}
