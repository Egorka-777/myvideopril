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
                : "Два экрана: тяните границу, колёсико меняет масштаб половины.";
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
        bool baseVideoSelected, draggingBaseVideo, resizingBaseVideo;
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
            if (background == null) return;
            var box = AssemblyVideoPlacement.Bounds(template);
            var target = new RectangleF(frame.X + box.X * frame.Width / template.Width, frame.Y + box.Y * frame.Height / template.Height,
                box.Width * frame.Width / template.Width, box.Height * frame.Height / template.Height);
            graphics.DrawImage(background, target);
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
                double maximum = Math.Min((1 - template.BaseVideoX) / baseVideoOldWidth, (1 - template.BaseVideoY) / baseVideoOldHeight);
                double minimum = Math.Max(.02 / baseVideoOldWidth, .02 / baseVideoOldHeight);
                factor = Math.Max(minimum, Math.Min(maximum, factor));
                template.BaseVideoWidth = baseVideoOldWidth * factor; template.BaseVideoHeight = baseVideoOldHeight * factor;
            } else {
                template.BaseVideoX = Math.Max(0, Math.Min(1 - template.BaseVideoWidth, baseVideoOldX + dx));
                template.BaseVideoY = Math.Max(0, Math.Min(1 - template.BaseVideoHeight, baseVideoOldY + dy));
            }
            BaseVideoPlacementChanged?.Invoke(); Invalidate(); return true;
        }
    }
}
