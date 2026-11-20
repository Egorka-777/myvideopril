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
            add("clean-fade", "Плавное появление", new WarmupSubtitleStyle { FontSize = 58, Plate = "none", OutlineWidth = 2, Motion = "fade" });
            add("soft-rise", "Мягкий подъём", new WarmupSubtitleStyle { FontSize = 60, Plate = "none", OutlineWidth = 3, Motion = "rise" });
            add("word-accent", "Акцент на слове", new WarmupSubtitleStyle { FontSize = 64, Plate = "none", OutlineWidth = 3, Motion = "word-accent", AccentColor = "#FFE84A", WordsPerCaption = 4 });
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
            if (cues.Count == 0) return cues;
            if (cues.Any(c => c.Words == null || c.Words.Count == 0)) {
                var grouped = new List<WarmupCaption>();
                int count = Math.Max(1, Math.Min(6, style.WordsPerCaption));
                foreach (var cue in cues) {
                    if (cue.Words != null && cue.Words.Count > 0) { grouped.AddRange(ForStyle(new[] { cue }, style)); continue; }
                    var parts = (cue.Text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    // Group untimed edited text within its existing window, without
                    // claiming estimated word timing as provider alignment.
                    for (int i = 0; i < parts.Length; i += count) {
                        int end = Math.Min(parts.Length, i + count);
                        grouped.Add(new WarmupCaption { Text = string.Join(" ", parts.Skip(i).Take(end - i)),
                            Start = cue.Start + (cue.End - cue.Start) * i / parts.Length,
                            End = cue.Start + (cue.End - cue.Start) * end / parts.Length });
                    }
                }
                return grouped;
            }
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
            double edge = Math.Min(.18, (cue.End - cue.Start) / 2);
            float enter = edge <= 0 ? 1 : (float)Math.Min(1, elapsed / edge);
            float leave = edge <= 0 ? 1 : (float)Math.Max(0, Math.Min(1, (cue.End - time) / edge));
            float opacity = style.Motion == "fade" || style.Motion == "rise" ? Math.Min(enter, leave) : 1;
            float offset = style.Motion == "rise" ? style.FontSize * .24f * (1 - enter) * (1 - enter) : 0;
            float wordScale = 1;
            if (style.Motion == "word-accent" && active >= 0) {
                double age = Math.Max(0, time - cue.Words[active].Start);
                wordScale = 1 + .06f * (float)Math.Max(0, 1 - age / .16);
            }
            return new WarmupCaption { Text = cue.Text, Start = cue.Start, End = cue.End, Words = cue.Words, ActiveWord = active,
                TextScale = scale, FrameOpacity = opacity, OffsetY = offset, ActiveWordScale = wordScale };
        }
        public static List<WarmupCaption> Frames(IEnumerable<WarmupCaption> source, WarmupSubtitleStyle style) {
            var result = new List<WarmupCaption>();
            foreach (var cue in ForStyle(source, style)) {
                var cuts = new List<double> { cue.Start, cue.End };
                if ((style.Highlight != "none" || style.Motion == "word-accent") && cue.Words != null) cuts.AddRange(cue.Words.Select(w => w.Start));
                if (style.Motion == "pop") { cuts.Add(cue.Start + .05); cuts.Add(cue.Start + .10); }
                if (style.Motion == "fade" || style.Motion == "rise") {
                    double edge = Math.Min(.18, (cue.End - cue.Start) / 2);
                    for (double t = 0; t < edge; t += 1d / 30) { cuts.Add(cue.Start + t); cuts.Add(cue.End - edge + t); }
                    cuts.Add(cue.Start + edge); cuts.Add(cue.End - edge);
                }
                if (style.Motion == "word-accent" && cue.Words != null)
                    foreach (var word in cue.Words) for (int i = 1; i <= 5; i++) cuts.Add(word.Start + i / 30d);
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
        sealed class WordBox { public string Text; public RectangleF Box; public int Index; public float GlyphLeft, GlyphTop; }
        public static bool UsesTypography(WarmupSubtitleStyle style) => true;
        static List<WordBox> Layout(string text, WarmupSubtitleStyle style, int width, int height, out RectangleF plate, out float fontSize) {
            style = style.Copy(); style.Normalize();
            text = style.Uppercase ? (text ?? "").ToUpper(CultureInfo.GetCultureInfo("ru-RU")) : text ?? "";
            var words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            using (var image = new Bitmap(1, 1)) using (var g = Graphics.FromImage(image))
            using (var family = new FontFamily(style.FontFamily))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone()) {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                var face = style.Bold && family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
                fontSize = style.FontSize;
                List<WordBox> result;
                // Preserve the chosen size unless the complete text cannot fit in two lines.
                // Measure real outlines, not clamped word widths: even a single long word must fit.
                if (!TryLayout(words, style, width, height, fontSize, g, family, face, format, out result, out plate)) {
                    float low = 0, high = fontSize;
                    for (int i = 0; i < 20; i++) {
                        float candidate = (low + high) / 2;
                        if (TryLayout(words, style, width, height, candidate, g, family, face, format, out result, out plate)) low = candidate;
                        else high = candidate;
                    }
                    fontSize = low;
                    TryLayout(words, style, width, height, fontSize, g, family, face, format, out result, out plate);
                }
                return result;
            }
        }
        static bool TryLayout(string[] words, WarmupSubtitleStyle style, int width, int height, float size, Graphics g,
            FontFamily family, FontStyle face, StringFormat format, out List<WordBox> result, out RectangleF plate) {
            result = new List<WordBox>(); plate = RectangleF.Empty;
            using (var font = new Font(family, size, face, GraphicsUnit.Pixel)) {
                float edge = style.TextThickness + style.OutlineWidth;
                float padX = style.Plate == "none" ? 0 : style.PlatePaddingX;
                float padY = style.Plate == "none" ? 0 : style.PlatePaddingY;
                float maxWidth = Math.Min(width * .84f, width - 2 * (padX + edge + Math.Min(24, width * .1f))), space = g.MeasureString(" ", font, int.MaxValue, format).Width;
                float pitch = font.GetHeight(g) * style.LineSpacing / 100f, x = 0;
                float inkTop = float.MaxValue, inkBottom = float.MinValue;
                var lines = new List<List<WordBox>> { new List<WordBox>() };
                for (int i = 0; i < words.Length; i++) using (var path = new GraphicsPath()) {
                    path.AddString(words[i], family, (int)face, size, PointF.Empty, format);
                    var ink = path.GetBounds();
                    float wordWidth = ink.Width;
                    if (wordWidth > maxWidth) return false;
                    if (x > 0 && x + wordWidth > maxWidth) {
                        if (lines.Count == 2) return false;
                        x = 0; lines.Add(new List<WordBox>());
                    }
                    var item = new WordBox { Text = words[i], Index = i, GlyphLeft = ink.Left,
                        Box = new RectangleF(x, (lines.Count - 1) * pitch, wordWidth, ink.Height) };
                    lines.Last().Add(item); result.Add(item); x += wordWidth + space;
                    inkTop = Math.Min(inkTop, ink.Top); inkBottom = Math.Max(inkBottom, ink.Bottom);
                }
                // Anchor the plate and word animation to visible glyphs, not font-cell whitespace.
                float inkHeight = words.Length == 0 ? 0 : inkBottom - inkTop;
                float totalHeight = inkHeight + (lines.Count - 1) * pitch;
                float motionRoom = (style.Motion == "rise" ? size * .24f : 0) + (style.Motion == "word-accent" ? inkHeight * .03f : 0);
                float margin = Math.Min(24, height * .1f);
                if (totalHeight + motionRoom + 2 * (padY + edge) > height - margin * 2) return false;
                float top = Math.Max(margin + padY + edge, Math.Min(height - margin - padY - edge - totalHeight - motionRoom,
                    height * style.Position / 100f - totalHeight / 2));
                foreach (var line in lines) {
                    float span = line.Count == 0 ? 0 : line.Last().Box.Right;
                    foreach (var item in line) {
                        item.GlyphTop = words.Length == 0 ? 0 : inkTop;
                        item.Box = new RectangleF((width - span) / 2 + item.Box.X, top + item.Box.Y, item.Box.Width, inkHeight);
                    }
                }
                float left = result.Count == 0 ? width / 2 : result.Min(w => w.Box.Left);
                float right = result.Count == 0 ? left : result.Max(w => w.Box.Right);
                plate = new RectangleF(left - padX - edge, top - padY - edge, right - left + 2 * (padX + edge), totalHeight + 2 * (padY + edge));
                return true;
            }
        }
        public static RectangleF Bounds(string text, WarmupSubtitleStyle style, int width, int height) { Layout(text, style, width, height, out RectangleF plate, out float size); return plate; }
        public static Bitmap Paint(WarmupCaption cue, WarmupSubtitleStyle style, int width, int height) {
            style = style.Copy(); style.Normalize();
            var words = Layout(cue.Text, style, width, height, out RectangleF plate, out float fontSize);
            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var family = new FontFamily(style.FontFamily))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone()) {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(0, cue.OffsetY * fontSize / style.FontSize);
                Func<Color, Color> faded = color => Color.FromArgb((int)(color.A * cue.FrameOpacity), color.R, color.G, color.B);
                float scale = cue.TextScale <= 0 ? 1 : cue.TextScale;
                g.TranslateTransform(plate.Left + plate.Width / 2, plate.Top + plate.Height / 2);
                g.ScaleTransform(scale, scale); g.TranslateTransform(-(plate.Left + plate.Width / 2), -(plate.Top + plate.Height / 2));
                if (style.Plate != "none") using (var brush = new SolidBrush(faded(Color.FromArgb(255 * style.Opacity / 100, ColorTranslator.FromHtml(style.PlateColor))))) {
                    if (style.Plate == "solid") g.FillRectangle(brush, plate);
                    else using (var path = Rounded(plate)) g.FillPath(brush, path);
                }
                foreach (var word in words) using (var path = new GraphicsPath()) {
                    var face = style.Bold && family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
                    path.AddString(word.Text, family, (int)face, fontSize, new PointF(word.Box.X - word.GlyphLeft, word.Box.Y - word.GlyphTop), format);
                    if (style.TextThickness > 0) using (var original = (GraphicsPath)path.Clone())
                    using (var pen = new Pen(Color.Black, style.TextThickness * 2) { LineJoin = LineJoin.Round }) {
                        path.Widen(pen); path.AddPath(original, false); path.FillMode = FillMode.Winding;
                    }
                    if (word.Index == cue.ActiveWord && cue.ActiveWordScale != 1) using (var transform = new Matrix()) {
                        float cx = word.Box.Left + word.Box.Width / 2, cy = word.Box.Top + word.Box.Height / 2;
                        transform.Translate(cx, cy); transform.Scale(cue.ActiveWordScale, cue.ActiveWordScale); transform.Translate(-cx, -cy); path.Transform(transform);
                    }
                    using (var shadow = (GraphicsPath)path.Clone()) using (var shift = new Matrix()) using (var brush = new SolidBrush(faded(Color.FromArgb(160, 0, 0, 0)))) {
                        shift.Translate(2, 3); shadow.Transform(shift); g.FillPath(brush, shadow);
                    }
                    if (style.OutlineWidth > 0) using (var pen = new Pen(faded(ColorTranslator.FromHtml(style.OutlineColor)), style.OutlineWidth * 2) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                    bool accent = style.Motion == "word-accent" || style.Highlight == "active" ? word.Index == cue.ActiveWord : style.Highlight == "progressive" && word.Index <= cue.ActiveWord;
                    using (var brush = new SolidBrush(faded(ColorTranslator.FromHtml(accent ? style.AccentColor : style.TextColor)))) g.FillPath(brush, path);
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
