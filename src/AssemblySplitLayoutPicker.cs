using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class AssemblySplitLayoutPicker : Control {
        public event Action<string> LayoutSelected;
        string layout = "none";
        int hover = -1;
        static readonly string[] Keys = { "none", "vertical", "horizontal" };
        static readonly string[] Titles = { "Один кадр", "Сверху и снизу", "Слева и справа" };

        public AssemblySplitLayoutPicker() {
            DoubleBuffered = true;
            Height = 72;
            Cursor = Cursors.Hand;
        }

        public string SplitLayout {
            get => layout;
            set { layout = string.IsNullOrWhiteSpace(value) ? "none" : value; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            int next = Hit(e.Location);
            if (next != hover) { hover = next; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) {
            base.OnMouseLeave(e);
            hover = -1; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            int hit = Hit(e.Location);
            if (hit < 0) return;
            layout = Keys[hit];
            LayoutSelected?.Invoke(layout);
            Invalidate();
        }

        int Hit(Point p) {
            for (int i = 0; i < 3; i++) {
                var r = Cell(i);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        Rectangle Cell(int index) {
            int pad = 4, w = (Width - pad * 4) / 3;
            return new Rectangle(pad + index * (w + pad), 4, w, Height - 8);
        }

        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < 3; i++) {
                var cell = Cell(i);
                bool active = Keys[i] == layout;
                bool hot = i == hover;
                using (var back = new SolidBrush(active ? Color.FromArgb(36, Theme.Accent) : hot ? Theme.Card : Color.FromArgb(18, Theme.Card)))
                    e.Graphics.FillRectangle(back, cell);
                using (var border = new Pen(active ? Theme.Accent : Theme.Border, active ? 2f : 1f))
                    e.Graphics.DrawRectangle(border, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
                DrawIcon(e.Graphics, Keys[i], Rectangle.Inflate(cell, -10, -18));
                TextRenderer.DrawText(e.Graphics, Titles[i], Theme.FontBody, new Rectangle(cell.X, cell.Bottom - 18, cell.Width, 16), active ? Theme.Accent : Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        static void DrawIcon(Graphics g, string key, Rectangle area) {
            using (var frame = new Pen(Color.FromArgb(180, Theme.TextSecondary), 1.5f))
            using (var fill = new SolidBrush(Color.FromArgb(120, Theme.Accent)))
            using (var fillB = new SolidBrush(Color.FromArgb(80, Theme.TextMuted))) {
                g.DrawRectangle(frame, area.X, area.Y, area.Width - 1, area.Height - 1);
                if (key == "none") {
                    g.FillRectangle(fill, area.X + 3, area.Y + 3, area.Width - 6, area.Height - 6);
                    return;
                }
                if (key == "vertical") {
                    int mid = area.Y + area.Height / 2;
                    g.FillRectangle(fill, area.X + 3, area.Y + 3, area.Width - 6, mid - area.Y - 3);
                    g.FillRectangle(fillB, area.X + 3, mid, area.Width - 6, area.Bottom - mid - 3);
                    g.DrawLine(frame, area.X, mid, area.Right, mid);
                    return;
                }
                int midX = area.X + area.Width / 2;
                g.FillRectangle(fill, area.X + 3, area.Y + 3, midX - area.X - 3, area.Height - 6);
                g.FillRectangle(fillB, midX, area.Y + 3, area.Right - midX - 3, area.Height - 6);
                g.DrawLine(frame, midX, area.Y, midX, area.Bottom);
            }
        }
    }
}
