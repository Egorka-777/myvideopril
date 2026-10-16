using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class WarmupPreviewControl : Control {
        string overlay = "none";
        Image background;

        public WarmupPreviewControl() {
            DoubleBuffered = true;
            BackColor = Theme.Background;
            MinimumSize = new Size(200, 320);
        }

        public void SetOverlay(string mode) {
            overlay = (mode ?? "none").Trim().ToLowerInvariant();
            Invalidate();
        }

        public void SetBackgroundImage(Image img) {
            background?.Dispose();
            background = img;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            e.Graphics.Clear(Theme.Background);
            float pad = 12;
            float w = Width - pad * 2;
            float h = Height - pad * 2;
            float scale = Math.Min(w / 1080f, h / 1920f);
            var frame = new RectangleF(pad + (w - 1080 * scale) / 2, pad + (h - 1920 * scale) / 2, 1080 * scale, 1920 * scale);
            e.Graphics.FillRectangle(Brushes.Black, frame);
            if (background != null)
                e.Graphics.DrawImage(background, frame);
            else using (var b = new SolidBrush(Theme.ColorFromHex("#1a2744")))
                e.Graphics.FillRectangle(b, frame);
            if (overlay == "tiktok") DrawTikTokChrome(e.Graphics, frame);
            else if (overlay == "shorts") DrawShortsChrome(e.Graphics, frame);
            using (var pen = new Pen(Theme.Accent, 2)) e.Graphics.DrawRectangle(pen, frame.X, frame.Y, frame.Width, frame.Height);
        }

        static void DrawTikTokChrome(Graphics g, RectangleF f) {
            using (var dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) {
                g.FillRectangle(dim, f.X + f.Width * .78f, f.Y, f.Width * .22f, f.Height);
                g.FillRectangle(dim, f.X, f.Y + f.Height * .72f, f.Width, f.Height * .28f);
            }
            using (var font = new Font("Segoe UI", Math.Max(7f, f.Width * .028f)))
            using (var brush = new SolidBrush(Color.White)) {
                g.DrawString("@канал", font, brush, f.X + f.Width * .04f, f.Y + f.Height * .74f);
                g.DrawString("Подпись · safe zone", font, brush, f.X + f.Width * .04f, f.Y + f.Height * .78f);
            }
            float rx = f.X + f.Width * .86f;
            for (int i = 0; i < 4; i++)
                g.FillEllipse(Brushes.White, rx, f.Y + f.Height * (.22f + i * .12f), f.Width * .08f, f.Width * .08f);
        }

        static void DrawShortsChrome(Graphics g, RectangleF f) {
            using (var dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillRectangle(dim, f.X, f.Y + f.Height * .82f, f.Width, f.Height * .18f);
            using (var font = new Font("Segoe UI", Math.Max(7f, f.Width * .026f)))
            using (var brush = new SolidBrush(Color.White))
                g.DrawString("YouTube Shorts · нижняя зона UI", font, brush, f.X + f.Width * .05f, f.Y + f.Height * .85f);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) background?.Dispose();
            base.Dispose(disposing);
        }
    }
}
