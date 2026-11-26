using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class AssemblySplitLayoutPicker : Control {
        public event Action<string> LayoutSelected;
        /// <summary>0 — верх/лево (основная пачка), 1 — низ/право.</summary>
        public event Action<int> HalfClicked;
        string layout = "none";
        int hover = -1;
        int hoverHalf = -1;
        static readonly string[] Keys = { "none", "vertical", "horizontal" };
        static readonly string[] Titles = { "Один кадр", "Сверху и снизу", "Слева и справа" };
        static readonly Font TitleFont = new Font("Segoe UI", 11f, FontStyle.Bold);

        public AssemblySplitLayoutPicker() {
            DoubleBuffered = true;
            Height = 168;
            MinimumSize = new Size(420, 168);
            Cursor = Cursors.Hand;
        }

        public string SplitLayout {
            get => layout;
            set { layout = string.IsNullOrWhiteSpace(value) ? "none" : value; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            int next = Hit(e.Location);
            int half = next >= 0 ? HalfAt(next, e.Location) : -1;
            if (next != hover || half != hoverHalf) { hover = next; hoverHalf = half; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) {
            base.OnMouseLeave(e);
            hover = -1; hoverHalf = -1; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            int cell = Hit(e.Location);
            if (cell < 0) return;
            string key = Keys[cell];
            if (key != layout) {
                layout = key;
                LayoutSelected?.Invoke(layout);
            }
            if (key == "none") HalfClicked?.Invoke(0);
            else HalfClicked?.Invoke(HalfAt(cell, e.Location));
            Invalidate();
        }

        int Hit(Point p) {
            for (int i = 0; i < 3; i++) {
                if (Cell(i).Contains(p)) return i;
            }
            return -1;
        }

        int HalfAt(int cell, Point p) {
            var r = Cell(cell);
            if (Keys[cell] == "vertical") return p.Y < r.Y + r.Height / 2 ? 0 : 1;
            if (Keys[cell] == "horizontal") return p.X < r.X + r.Width / 2 ? 0 : 1;
            return 0;
        }

        Rectangle Cell(int index) {
            int pad = 6, w = (Width - pad * 4) / 3;
            return new Rectangle(pad + index * (w + pad), 6, w, Height - 12);
        }

        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < 3; i++) {
                var cell = Cell(i);
                bool active = Keys[i] == layout;
                bool hot = i == hover;
                using (var back = new SolidBrush(active ? Color.FromArgb(42, Theme.Accent) : hot ? Theme.Card : Color.FromArgb(18, Theme.Card)))
                    e.Graphics.FillRectangle(back, cell);
                using (var border = new Pen(active ? Theme.Accent : Theme.Border, active ? 2.5f : 1f))
                    e.Graphics.DrawRectangle(border, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
                DrawIcon(e.Graphics, Keys[i], Rectangle.Inflate(cell, -12, -34), active && i == hover, hoverHalf);
                TextRenderer.DrawText(e.Graphics, Titles[i], TitleFont, new Rectangle(cell.X, cell.Bottom - 26, cell.Width, 24),
                    active ? Theme.Accent : Theme.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        void DrawIcon(Graphics g, string key, Rectangle area, bool hotActive, int half) {
            using (var frame = new Pen(Color.FromArgb(200, Theme.TextSecondary), 2f))
            using (var fill = new SolidBrush(Color.FromArgb(130, Theme.Accent)))
            using (var fillB = new SolidBrush(Color.FromArgb(85, Theme.TextMuted)))
            using (var hoverA = new SolidBrush(Color.FromArgb(200, Theme.Accent)))
            using (var hoverB = new SolidBrush(Color.FromArgb(160, Theme.Accent))) {
                g.DrawRectangle(frame, area.X, area.Y, area.Width - 1, area.Height - 1);
                if (key == "none") {
                    g.FillRectangle(hotActive && half == 0 ? hoverA : fill, area.X + 4, area.Y + 4, area.Width - 8, area.Height - 8);
                    return;
                }
                if (key == "vertical") {
                    int mid = area.Y + area.Height / 2;
                    g.FillRectangle(hotActive && half == 0 ? hoverA : fill, area.X + 4, area.Y + 4, area.Width - 8, mid - area.Y - 4);
                    g.FillRectangle(hotActive && half == 1 ? hoverB : fillB, area.X + 4, mid, area.Width - 8, area.Bottom - mid - 4);
                    g.DrawLine(frame, area.X, mid, area.Right, mid);
                    return;
                }
                int midX = area.X + area.Width / 2;
                g.FillRectangle(hotActive && half == 0 ? hoverA : fill, area.X + 4, area.Y + 4, midX - area.X - 4, area.Height - 8);
                g.FillRectangle(hotActive && half == 1 ? hoverB : fillB, midX, area.Y + 4, area.Right - midX - 4, area.Height - 8);
                g.DrawLine(frame, midX, area.Y, midX, area.Bottom);
            }
        }
    }
}