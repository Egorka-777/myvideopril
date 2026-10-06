using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace VideoBatch {
    public static class AssemblyRaster {
        public static Bitmap Create(AssemblyTemplate t, AssemblyLayerPlan plan) {
            var bitmap = new Bitmap(t.Width, t.Height, PixelFormat.Format32bppArgb);
            try {
                using (var g = Graphics.FromImage(bitmap)) {
                    g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    var l = plan.Layer;
                    var box = new RectangleF((float)(l.X * t.Width), (float)(l.Y * t.Height), (float)(l.Width * t.Width), (float)(l.Height * t.Height));
                    if (Path.GetExtension(plan.Asset.Path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) {
                        string text = File.ReadAllText(plan.Asset.Path, Encoding.UTF8).Trim();
                        if (text.Length == 0 || text.Length > 4000) throw new Exception("TXT должен содержать от 1 до 4000 символов: " + plan.Asset.Path);
                        using (var path = Rounded(box, (float)l.Radius))
                        using (var brush = new SolidBrush(Color.FromArgb((int)(l.Opacity * 255), Color.Black))) g.FillPath(brush, path);
                        float pad = (float)l.Padding;
                        var inner = new RectangleF(box.X + pad, box.Y + pad, box.Width - 2 * pad, box.Height - 2 * pad);
                        if (inner.Width < 8 || inner.Height < 8) throw new Exception("Плашка слишком мала для отступов текста.");
                        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.None }) {
                            float size = (float)l.FontSize;
                            for (;;) {
                                using (var font = new Font(l.Font, size, FontStyle.Bold, GraphicsUnit.Pixel)) {
                                    int chars, lines;
                                    var measured = g.MeasureString(text, font, new SizeF(inner.Width, inner.Height), format, out chars, out lines);
                                    if (chars >= text.Length && measured.Height <= inner.Height + .1) {
                                        using (var brush = new SolidBrush(Color.FromArgb((int)(l.Opacity * 255), Color.White))) g.DrawString(text, font, brush, inner, format);
                                        break;
                                    }
                                }
                                size -= 1; if (size < 12) throw new Exception("Текст не помещается. Увеличьте плашку или сократите текст: " + Path.GetFileName(plan.Asset.Path));
                            }
                        }
                    } else {
                        using (var image = Image.FromFile(plan.Asset.Path)) {
                            float ratio = Math.Min(box.Width / image.Width, box.Height / image.Height);
                            var target = new Rectangle((int)Math.Round(box.X + (box.Width - image.Width * ratio) / 2), (int)Math.Round(box.Y + (box.Height - image.Height * ratio) / 2), Math.Max(1, (int)Math.Round(image.Width * ratio)), Math.Max(1, (int)Math.Round(image.Height * ratio)));
                            using (var attrs = new ImageAttributes()) {
                                var matrix = new ColorMatrix(); matrix.Matrix33 = (float)l.Opacity; attrs.SetColorMatrix(matrix);
                                g.DrawImage(image, target, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
                            }
                        }
                    }
                }
                return bitmap;
            } catch { bitmap.Dispose(); throw; }
        }
        public static void Save(AssemblyTemplate t, AssemblyLayerPlan plan, string path) {
            using (var bitmap = Create(t, plan)) bitmap.Save(path, ImageFormat.Png);
        }
        static GraphicsPath Rounded(RectangleF box, float radius) {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
            if (d <= 0) { path.AddRectangle(box); return path; }
            path.AddArc(box.X, box.Y, d, d, 180, 90); path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90); path.AddArc(box.X, box.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
    }
}
