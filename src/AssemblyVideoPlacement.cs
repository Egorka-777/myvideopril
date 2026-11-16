using System;
using System.Drawing;

namespace VideoBatch {
    // Placement applies to the already frame-filling base video, before any objects.
    // Split panes keep their own existing layout and zoom controls.
    public static class AssemblyVideoPlacement {
        public static Rectangle Bounds(AssemblyTemplate t) {
            if (t.SplitLayout != "none") return new Rectangle(0, 0, t.Width, t.Height);
            int width = Math.Max(2, (int)Math.Round(t.BaseVideoWidth * t.Width / 2) * 2);
            int height = Math.Max(2, (int)Math.Round(t.BaseVideoHeight * t.Height / 2) * 2);
            int x = (int)Math.Round(t.BaseVideoX * t.Width / 2) * 2;
            int y = (int)Math.Round(t.BaseVideoY * t.Height / 2) * 2;
            return new Rectangle(x, y, width, height);
        }
        public static string Filter(AssemblyTemplate t, bool transparent = false) {
            var box = Bounds(t);
            if (box == new Rectangle(0, 0, t.Width, t.Height)) return "";
            int left = Math.Min(0, box.X), top = Math.Min(0, box.Y);
            int width = Math.Max(t.Width, box.Right) - left, height = Math.Max(t.Height, box.Bottom) - top;
            return ",scale=" + box.Width + ":" + box.Height + ",setsar=1" + (transparent ? ",format=yuva444p" : "")
                + ",pad=" + width + ":" + height + ":" + (box.X - left) + ":" + (box.Y - top) + ":color=" + (transparent ? "black@0" : "black")
                + ",crop=" + t.Width + ":" + t.Height + ":" + (-left) + ":" + (-top);
        }
    }
}
