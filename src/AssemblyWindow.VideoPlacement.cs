using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        void SelectBaseVideo() {
            layerIndex = -1;
            LoadLayer(); RefreshChips();
            canvas.SelectBaseVideo();
            status.Text = template.SplitLayout == "none"
                ? "Основное видео: тяните рамку для перемещения, угол — для масштаба. Поля остаются чёрными."
                : "Тяните верх или низ в превью — кадр подстроится.";
        }
        void AddVideoPlacementControls(FlowLayoutPanel body) {
            body.Controls.Add(new Label { Text = "Размещение основного видео", AutoSize = true, Font = Theme.FontCardTitle, Margin = new Padding(0, 12, 0, 6) });
            var select = Button("Выбрать видео на холсте", () => { SelectBaseVideo(); body.FindForm().Close(); });
            select.Name = "SelectBaseVideo"; select.Width = 560; body.Controls.Add(select);
            body.Controls.Add(new Label { Text = "Перемещайте видео за рамку, меняйте размер за нижний правый угол. Объекты остаются на своих местах. При делении экрана действуют прежние настройки половин.", Width = 560, Height = 54, ForeColor = Theme.TextSecondary });
            var reset = Button("Видео на весь кадр · убрать поля", () => {
                template.BaseVideoX = template.BaseVideoY = 0;
                template.BaseVideoWidth = template.BaseVideoHeight = 1;
                Changed(false); Save();
            });
            reset.Name = "ResetBaseVideoPlacement"; reset.Width = 560; body.Controls.Add(reset);
        }
    }
    public sealed partial class AssemblyCanvas {
        string platformGuide = "Без разметки";
        public void SetPlatformGuide(string platform) { platformGuide = platform; Invalidate(); }
        void DrawPlatformGuide(Graphics g, RectangleF frame) {
            if (platformGuide == "Без разметки" || template.Width >= template.Height) return;
            var state = g.Save();
            g.TranslateTransform(frame.X, frame.Y); g.ScaleTransform(frame.Width / 1080, frame.Height / 1920);
            using (var shade = new SolidBrush(Color.FromArgb(65, 0, 0, 0))) {
                g.FillRectangle(shade, 0, 0, 1080, 170);
                g.FillRectangle(shade, 0, 1530, 1080, 390);
                g.FillRectangle(shade, 920, 650, 160, 880);
            }
            using (var ink = new SolidBrush(Color.FromArgb(185, Color.White)))
            using (var pen = new Pen(Color.FromArgb(165, Color.White), 4))
            using (var font = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel)) {
                bool instagram = platformGuide == "Instagram";
                g.DrawString(instagram ? "Reels                         ◯" : "Подписки     Для вас     ⌕", font, ink, 40, 65);
                string[] icons = { "♡", "◯", instagram ? "➤" : "↗", "···" };
                for (int i = 0; i < icons.Length; i++) { g.DrawEllipse(pen, 963, 770 + i * 175, 66, 66); g.DrawString(icons[i], font, ink, 974, 782 + i * 175); }
                g.DrawString("@username", font, ink, 42, 1550);
                g.DrawString("Описание видео…", font, ink, 42, 1620);
                g.DrawString("♫ Оригинальный звук", font, ink, 42, 1690);
                g.DrawString("⌂           ⌕           +           ♡           ◯", font, ink, 42, 1820);
                using (var safe = new Pen(Color.FromArgb(140, Theme.Accent), 3) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawRectangle(safe, 60, 190, 840, 1300);
            }
            g.Restore(state);
        }
        bool baseVideoSelected, draggingBaseVideo, resizingBaseVideo;
        double previewBakePrimaryPanX, previewBakePrimaryPanY, previewBakeSecondaryPanX, previewBakeSecondaryPanY;
        double previewBakePrimaryZoom = 1, previewBakeSecondaryZoom = 1;
        public event Action SplitMaskAdjusted;
        Point baseVideoOrigin;
        double baseVideoOldX, baseVideoOldY, baseVideoOldWidth, baseVideoOldHeight;
        public event Action BaseVideoSelected;
        public event Action BaseVideoPlacementChanged;
        public void SelectBaseVideo() { baseVideoSelected = true; selected = null; subtitleSelected = false; Invalidate(); }
        RectangleF BaseVideoBox {
            get {
                if (template == null) return RectangleF.Empty;
                var f = Frame; var box = AssemblyVideoPlacement.Bounds(template);
                return new RectangleF(f.X + box.X * f.Width / template.Width, f.Y + box.Y * f.Height / template.Height,
                    box.Width * f.Width / template.Width, box.Height * f.Height / template.Height);
            }
        }
        void DrawBaseVideo(Graphics graphics, RectangleF frame) {
            if (background == null || template == null) return;
            if (template.SplitLayout == "none") {
                var box = AssemblyVideoPlacement.Bounds(template);
                var target = new RectangleF(frame.X + box.X * frame.Width / template.Width, frame.Y + box.Y * frame.Height / template.Height,
                    box.Width * frame.Width / template.Width, box.Height * frame.Height / template.Height);
                graphics.DrawImage(background, target);
                return;
            }
            bool vertical = AssemblySplit.IsVertical(template.SplitLayout);
            AssemblySplit.PaneSizes(template, out int primarySpan, out int secondarySpan, out int splitLine, out _);
            float primaryRatio = primarySpan / (float)(vertical ? template.Height : template.Width);
            float line = vertical ? frame.Y + frame.Height * primaryRatio : frame.X + frame.Width * primaryRatio;
            var primaryPane = vertical
                ? new RectangleF(frame.X, frame.Y, frame.Width, line - frame.Y)
                : new RectangleF(frame.X, frame.Y, line - frame.X, frame.Height);
            var secondaryPane = vertical
                ? new RectangleF(frame.X, line, frame.Width, frame.Bottom - line)
                : new RectangleF(line, frame.Y, frame.Right - line, frame.Height);
            DrawSplitPaneVideo(graphics, primaryPane, false, primarySpan, splitLine);
            DrawSplitPaneVideo(graphics, secondaryPane, true, secondarySpan, splitLine);
        }
        void DrawSplitPaneVideo(Graphics graphics, RectangleF pane, bool secondary, int paneSpan, int splitLine) {
            double zoom = secondary ? template.SplitSecondaryZoom : template.SplitPrimaryZoom;
            double panX = (secondary ? template.SplitSecondaryPanX : template.SplitPrimaryPanX) - (secondary ? previewBakeSecondaryPanX : previewBakePrimaryPanX);
            double panY = (secondary ? template.SplitSecondaryPanY : template.SplitPrimaryPanY) - (secondary ? previewBakeSecondaryPanY : previewBakePrimaryPanY);
            bool vertical = AssemblySplit.IsVertical(template.SplitLayout);
            int cropW = vertical ? template.Width : paneSpan;
            int cropH = vertical ? paneSpan : template.Height;
            int px = AssemblySplit.PanOffset(cropW, zoom, panX);
            int py = AssemblySplit.PanOffset(cropH, zoom, panY);
            float scaleX = background.Width / (float)template.Width;
            float scaleY = background.Height / (float)template.Height;
            int srcX = vertical ? px : (secondary ? splitLine + px : px);
            int srcY = vertical ? (secondary ? splitLine + py : py) : py;
            int srcW = vertical ? template.Width : paneSpan;
            int srcH = vertical ? paneSpan : template.Height;
            var src = new RectangleF(srcX * scaleX, srcY * scaleY, srcW * scaleX, srcH * scaleY);
            float bakeZoom = (float)(secondary ? previewBakeSecondaryZoom : previewBakePrimaryZoom);
            float zoomFactor = bakeZoom > .001f ? (float)zoom / bakeZoom : 1f;
            var state = graphics.Save();
            graphics.SetClip(pane);
            if (Math.Abs(zoomFactor - 1f) > .01f) {
                float cx = pane.X + pane.Width / 2f, cy = pane.Y + pane.Height / 2f;
                graphics.TranslateTransform(cx, cy);
                graphics.ScaleTransform(zoomFactor, zoomFactor);
                graphics.TranslateTransform(-cx, -cy);
            }
            graphics.DrawImage(background, pane, src, GraphicsUnit.Pixel);
            graphics.Restore(state);
        }
        void DrawBaseVideoSelection(Graphics graphics) {
            if (!baseVideoSelected || template == null || template.SplitLayout != "none") return;
            var box = BaseVideoBox;
            using (var pen = new Pen(Theme.Accent, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            using (var brush = new SolidBrush(Theme.Accent)) graphics.FillRectangle(brush, box.Right - 7, box.Bottom - 7, 14, 14);
        }
        bool BeginBaseVideoDrag(MouseEventArgs e, bool requireBorder) {
            if (template == null || template.SplitLayout != "none") return false;
            var box = BaseVideoBox;
            var outer = box; outer.Inflate(8, 8);
            if (!outer.Contains(e.Location)) return false;
            var inner = box; inner.Inflate(-8, -8);
            bool handle = baseVideoSelected && Math.Abs(e.X - box.Right) <= 14 && Math.Abs(e.Y - box.Bottom) <= 14;
            if (requireBorder && (!baseVideoSelected || inner.Contains(e.Location)) && !handle) return false;
            SelectBaseVideo(); BaseVideoSelected?.Invoke();
            resizingBaseVideo = handle;
            draggingBaseVideo = true; Capture = true; baseVideoOrigin = e.Location;
            baseVideoOldX = template.BaseVideoX; baseVideoOldY = template.BaseVideoY;
            baseVideoOldWidth = template.BaseVideoWidth; baseVideoOldHeight = template.BaseVideoHeight;
            return true;
        }
        bool MoveBaseVideo(MouseEventArgs e) {
            if (!draggingBaseVideo || template == null) return false;
            var frame = Frame;
            double dx = (e.X - baseVideoOrigin.X) / frame.Width, dy = (e.Y - baseVideoOrigin.Y) / frame.Height;
            if (resizingBaseVideo) {
                double factor = Math.Abs(dx) >= Math.Abs(dy) ? 1 + dx / baseVideoOldWidth : 1 + dy / baseVideoOldHeight;
                double minimum = Math.Max(.02 / baseVideoOldWidth, .02 / baseVideoOldHeight);
                factor = Math.Max(minimum, factor);
                template.BaseVideoWidth = template.BaseVideoPreserveAspectRatio ? baseVideoOldWidth * factor : Math.Max(.02, baseVideoOldWidth + dx);
                template.BaseVideoHeight = template.BaseVideoPreserveAspectRatio ? baseVideoOldHeight * factor : Math.Max(.02, baseVideoOldHeight + dy);
            } else {
                template.BaseVideoX = baseVideoOldX + dx;
                template.BaseVideoY = baseVideoOldY + dy;
            }
            BaseVideoPlacementChanged?.Invoke(); Invalidate(); return true;
        }
    }
}
