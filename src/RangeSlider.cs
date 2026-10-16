using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VideoBatch {
    /// <summary>One track with min/max thumbs (session length, watch time, etc.).</summary>
    public sealed class AssemblyRangeSlider : Control {
        readonly NumericUpDown min, max;
        readonly string caption, unit;
        readonly EventHandler minChanged, maxChanged, enabledChanged;
        bool dragMin;

        public AssemblyRangeSlider(string caption, NumericUpDown min, NumericUpDown max, string unit) {
            this.caption = caption; this.min = min; this.max = max; this.unit = unit;
            Name = "RangeSlider"; Height = 66; Margin = new Padding(0, 0, 0, 10); Cursor = Cursors.Hand;
            TabStop = true; DoubleBuffered = true; BackColor = Theme.Card; ForeColor = Theme.TextPrimary;
            AccessibleName = caption; AccessibleRole = AccessibleRole.Slider;
            minChanged = (s, e) => { if (min.Value > max.Value) max.Value = min.Value; Invalidate(); };
            maxChanged = (s, e) => { if (max.Value < min.Value) min.Value = max.Value; Invalidate(); };
            enabledChanged = (s, e) => Enabled = min.Enabled && max.Enabled;
            min.ValueChanged += minChanged; max.ValueChanged += maxChanged;
            min.EnabledChanged += enabledChanged; max.EnabledChanged += enabledChanged;
            Enabled = min.Enabled && max.Enabled;
            SetStyle(ControlStyles.Selectable, true);
        }

        string ValueLabel() {
            string u = unit == "" ? "" : " " + unit;
            return min.Value.ToString("0.##") + " – " + max.Value.ToString("0.##") + u;
        }

        float MinRatio() => (float)((min.Value - min.Minimum) / Math.Max(1, min.Maximum - min.Minimum));
        float MaxRatio() => (float)((max.Value - max.Minimum) / Math.Max(1, max.Maximum - max.Minimum));

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var text = new SolidBrush(Enabled ? Theme.TextPrimary : Theme.TextMuted)) {
                e.Graphics.DrawString(caption, Font, text, 12, 9);
                using (var format = new StringFormat { Alignment = StringAlignment.Far })
                    e.Graphics.DrawString(ValueLabel(), Font, text, new RectangleF(Width - 140, 9, 128, 24), format);
            }
            float left = 18, right = Width - 18, top = 46, w = right - left;
            float r0 = left + w * MinRatio(), r1 = left + w * MaxRatio();
            using (var track = new Pen(Theme.Border, 5)) { track.StartCap = track.EndCap = LineCap.Round; e.Graphics.DrawLine(track, left, top, right, top); }
            using (var fill = new Pen(Enabled ? Theme.Accent : Theme.TextMuted, 5)) {
                fill.StartCap = fill.EndCap = LineCap.Round;
                e.Graphics.DrawLine(fill, r0, top, r1, top);
            }
            using (var brush = new SolidBrush(Enabled ? Theme.TextPrimary : Theme.TextMuted)) {
                e.Graphics.FillEllipse(brush, r0 - 7, top - 7, 14, 14);
                e.Graphics.FillEllipse(brush, r1 - 7, top - 7, 14, 14);
            }
            if (Focused) using (var pen = new Pen(Theme.Accent)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }

        void SetAt(int x, bool isMin) {
            decimal ratio = (decimal)Math.Max(0, Math.Min(1, (x - 18.0) / Math.Max(1, Width - 36)));
            decimal v = Math.Round(min.Minimum + ratio * (min.Maximum - min.Minimum), min.DecimalPlaces);
            if (isMin) min.Value = Math.Min(v, max.Value); else max.Value = Math.Max(v, min.Value);
        }

        bool Near(float px, float cx) => Math.Abs(px - cx) <= 12;

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus(); Capture = true;
            float left = 18, w = Width - 36;
            float r0 = left + w * MinRatio(), r1 = left + w * MaxRatio();
            if (Near(e.X, r0) && Near(e.X, r1)) dragMin = Math.Abs(e.X - r0) <= Math.Abs(e.X - r1);
            else if (Near(e.X, r0)) dragMin = true;
            else if (Near(e.X, r1)) dragMin = false;
            else { dragMin = Math.Abs(e.X - r0) <= Math.Abs(e.X - r1); SetAt(e.X, dragMin); return; }
            SetAt(e.X, dragMin);
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            if (Capture) SetAt(e.X, dragMin);
        }

        protected override void OnMouseUp(MouseEventArgs e) {
            base.OnMouseUp(e);
            dragMin = false; Capture = false;
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                min.ValueChanged -= minChanged; max.ValueChanged -= maxChanged;
                min.EnabledChanged -= enabledChanged; max.EnabledChanged -= enabledChanged;
            }
            base.Dispose(disposing);
        }
    }
}
