using System;
using System.Drawing;

namespace VideoBatch {
    // Placement applies to the already frame-filling base video, before any objects.
    // Split panes keep their own existing layout and zoom controls.
    public static class AssemblyVideoPlacement {
        public static Rectangle Bounds(AssemblyTemplate t) {
            if (t.SplitLayout != "none") return new Rectangle(0, 0, t.Width, t.Height);
            int width = Math.Min(t.Width, Math.Max(2, (int)Math.Round(t.BaseVideoWidth * t.Width / 2) * 2));
            int height = Math.Min(t.Height, Math.Max(2, (int)Math.Round(t.BaseVideoHeight * t.Height / 2) * 2));
            int x = Math.Max(0, Math.Min(t.Width - width, (int)Math.Round(t.BaseVideoX * t.Width / 2) * 2));
            int y = Math.Max(0, Math.Min(t.Height - height, (int)Math.Round(t.BaseVideoY * t.Height / 2) * 2));
            return new Rectangle(x, y, width, height);
        }
        public static string Filter(AssemblyTemplate t) {
            var box = Bounds(t);
            if (box == new Rectangle(0, 0, t.Width, t.Height)) return "";
            return ",scale=" + box.Width + ":" + box.Height + ",setsar=1,pad=" + t.Width + ":" + t.Height + ":" + box.X + ":" + box.Y + ":color=black";
        }
    }
}
