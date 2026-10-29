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
            feather = Even((int)Math.Round(dimension * t.SplitSoftness / 2));
            if (feather > primarySpan - 2) feather = Math.Max(0, primarySpan - 2);
        }

        public static string Fill(int width, int height, int fps, double zoom) {
            zoom = Math.Max(.85, Math.Min(1.15, zoom <= 0 ? 1 : zoom));
            if (Math.Abs(zoom - 1) < .001)
                return "fps=" + fps + ",scale=" + width + ":" + height + ":force_original_aspect_ratio=increase,crop=" + width + ":" + height + ",setsar=1,format=yuv420p";
            int zw = Even((int)Math.Round(width * zoom));
            int zh = Even((int)Math.Round(height * zoom));
            return "fps=" + fps + ",scale=" + zw + ":" + zh + ":force_original_aspect_ratio=increase,crop=" + width + ":" + height + ",setsar=1,format=yuv420p";
        }

        public static string BlendWeight(bool vertical, int splitLine, int feather) {
            string coordinate = vertical ? "Y" : "X";
            if (feather <= 0) return "gte(" + coordinate + "," + splitLine + ")";
            return "clip((" + coordinate + "-" + Core.N(splitLine - feather / 2.0) + ")/" + feather + ",0,1)";
        }

        public static string PadOffset(bool vertical, bool secondary, int primarySpan, int fullW, int fullH) {
            if (vertical) return secondary ? "0:" + primarySpan : "0:0";
            return secondary ? primarySpan + ":0" : "0:0";
        }
    }
}
