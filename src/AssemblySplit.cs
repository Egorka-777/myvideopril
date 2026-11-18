using System;

namespace VideoBatch {
    public static class AssemblySplit {
        public static bool IsVertical(string layout) => layout == "vertical" || layout == "top-bottom";
        public static int Even(int value) { return Math.Max(2, value / 2 * 2); }

        public static void PaneSizes(AssemblyTemplate t, out int primarySpan, out int secondarySpan, out int splitLine, out int feather) {
            bool vertical = IsVertical(t.SplitLayout);
            int dimension = vertical ? t.Height : t.Width;
            double ratio = Math.Max(.25, Math.Min(.75, t.SplitRatio <= 0 ? .5 : t.SplitRatio));
            primarySpan = Even(Math.Min(dimension - 2, (int)Math.Round(dimension * ratio)));
            secondarySpan = Even(dimension - primarySpan);
            splitLine = primarySpan;
            feather = t.SplitSoftness <= 0 ? 0 : Even((int)Math.Round(dimension * t.SplitSoftness / 2));
            feather = Math.Min(feather, Math.Max(0, Math.Min(primarySpan, secondarySpan) - 2));
        }

        public static string Fill(int width, int height, int fps, double zoom) {
            zoom = Math.Max(.85, Math.Min(1.15, zoom <= 0 ? 1 : zoom));
            if (Math.Abs(zoom - 1) < .001)
                return "fps=" + fps + ",scale=" + width + ":" + height + ":force_original_aspect_ratio=increase,crop=" + width + ":" + height + ",setsar=1,format=yuv420p";
            int zw = Even((int)Math.Round(width * zoom));
            int zh = Even((int)Math.Round(height * zoom));
            return "fps=" + fps + ",scale=" + zw + ":" + zh + ":force_original_aspect_ratio=increase,crop=" + width + ":" + height + ",setsar=1,format=yuv420p";
        }

        internal static string PaneFilter(AssemblyTemplate t, bool secondary, double zoom) {
            bool vertical = IsVertical(t.SplitLayout);
            PaneSizes(t, out int primarySpan, out int secondarySpan, out _, out int feather);
            int span = secondary ? secondarySpan : primarySpan;
            int width = vertical ? t.Width : span, height = vertical ? span : t.Height;
            // Keep the hard-seam scale and crop centre. Reveal source pixels beyond that
            // crop towards the seam, rather than rescaling an enlarged pane.
            int extension = feather <= 0 ? 0 : Even((feather + 3) / 2);
            int x = !vertical && secondary ? primarySpan - extension : 0;
            int y = vertical && secondary ? primarySpan - extension : 0;
            if (extension == 0)
                return Fill(width, height, t.Fps, zoom) + ",pad=" + t.Width + ":" + t.Height + ":" + x + ":" + y + ",format=yuv444p";
            zoom = Math.Max(.85, Math.Min(1.15, zoom <= 0 ? 1 : zoom));
            int zw = Even((int)Math.Round(width * zoom)), zh = Even((int)Math.Round(height * zoom));
            int cropW = width + (vertical ? 0 : extension), cropH = height + (vertical ? extension : 0);
            // At an exact-fit source edge no further pixels exist. Extend its edge,
            // not black, while leaving every pixel in the original pane in place.
            int border = extension + Math.Max(Math.Max(0, width - zw), Math.Max(0, height - zh));
            return "fps=" + t.Fps + ",scale=" + zw + ":" + zh + ":force_original_aspect_ratio=increase,format=yuv420p"
                + ",pad=iw+" + (2 * border) + ":ih+" + (2 * border) + ":" + border + ":" + border
                + ",fillborders=left=" + border + ":right=" + border + ":top=" + border + ":bottom=" + border + ":mode=smear"
                + ",crop=" + cropW + ":" + cropH + ":floor((iw-" + width + ")/4)*2-" + (!vertical && secondary ? extension : 0)
                + ":floor((ih-" + height + ")/4)*2-" + (vertical && secondary ? extension : 0)
                + ",setsar=1,pad=" + t.Width + ":" + t.Height + ":" + x + ":" + y + ",format=yuv444p";
        }

        public static string BlendWeight(bool vertical, int splitLine, int feather) {
            string coordinate = vertical ? "Y" : "X";
            if (feather <= 0) return "gte(" + coordinate + "," + splitLine + ")";
            return "clip((" + coordinate + "-" + Core.N(splitLine - feather / 2.0) + ")/" + feather + ",0,1)";
        }

        internal static string BlendFilter(bool vertical, int splitLine, int feather, int width, int height) {
            // Geometry is invariant: evaluate one mask, then use the native pixel mixer.
            string weight = "255*(" + BlendWeight(vertical, splitLine, feather) + ")";
            return "color=c=black:s=" + width + "x" + height + ",format=yuv444p,trim=end_frame=1,geq=lum='"
                + weight + "':cb='" + weight + "':cr='" + weight + "'[splitMask];"
                + "[splitA][splitB][splitMask]maskedmerge[split]";
        }

        public static string PadOffset(bool vertical, bool secondary, int primarySpan, int fullW, int fullH) {
            if (vertical) return secondary ? "0:" + primarySpan : "0:0";
            return secondary ? primarySpan + ":0" : "0:0";
        }
    }
}
