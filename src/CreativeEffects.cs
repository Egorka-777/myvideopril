using System;
using System.Collections.Generic;
using System.Globalization;

namespace VideoBatch {
    [Serializable]
    public sealed class CreativeEffectsSettings {
        public string ColorPreset = "none";
        public double ColorStrength = 1;
        public string StylePreset = "none";
        public double StyleStrength = 1;

        public CreativeEffectsSettings Clone() { return (CreativeEffectsSettings)MemberwiseClone(); }
        public void Validate() {
            if (Array.IndexOf(CreativeEffects.ColorPresets, ColorPreset) < 0) throw new ArgumentException("Неизвестный цветовой фильтр: " + ColorPreset);
            if (Array.IndexOf(CreativeEffects.StylePresets, StylePreset) < 0) throw new ArgumentException("Неизвестная стилизация: " + StylePreset);
            Strength(ColorStrength); Strength(StyleStrength);
        }
        static void Strength(double value) {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException("strength", "Интенсивность эффекта должна быть от 0 до 1.");
        }
    }

    public static class CreativeEffects {
        public static string[] ColorPresets { get { return new[] { "none", "warm", "cool", "chocolate", "sepia", "mono" }; } }
        public static string[] StylePresets { get { return new[] { "none", "watercolor", "paint", "plastic", "soft" }; } }
        static string N(double value) { return value.ToString("0.########", CultureInfo.InvariantCulture); }

        /// <summary>Ordinary single-input filter chain, without stream labels. Empty means bypass.
        /// Apply to source before framing/overlays, for both intro and main; the freeze inherits it.
        /// Styles are FFmpeg approximations, not neural or Photoshop/CapCut filter replicas.</summary>
        public static string VideoFilter(CreativeEffectsSettings settings) {
            if (settings == null) return "";
            settings.Validate();
            var chain = new List<string>();
            double c = settings.ColorStrength, s = settings.StyleStrength;
            if (c > 0 && settings.ColorPreset != "none") {
                switch (settings.ColorPreset) {
                    case "warm": Matrix(chain, c, new[] { 1.09, .025, 0, 0, 1.015, 0, 0, 0, .88 }); break;
                    case "cool": Matrix(chain, c, new[] { .89, 0, 0, 0, 1.015, .015, 0, .02, 1.10 }); break;
                    case "chocolate":
                        Matrix(chain, c, new[] { 1.03, .045, .015, .025, .90, .015, .02, .025, .75 });
                        chain.Add("eq=contrast=" + N(1 + .12 * c) + ":brightness=" + N(-.025 * c) + ":saturation=" + N(1 - .18 * c)); break;
                    case "sepia": Matrix(chain, c, new[] { .393, .769, .189, .349, .686, .168, .272, .534, .131 }); break;
                    case "mono": Matrix(chain, c, new[] { .299, .587, .114, .299, .587, .114, .299, .587, .114 }); break;
                }
            }
            if (s > 0 && settings.StylePreset != "none") {
                switch (settings.StylePreset) {
                    case "watercolor":
                        chain.Add(Bilateral(4 * s, .14 * s));
                        chain.Add("eq=contrast=" + N(1 - .12 * s) + ":brightness=" + N(.025 * s) + ":saturation=" + N(1 + .12 * s)); break;
                    case "paint":
                        chain.Add(Bilateral(5 * s, .22 * s));
                        // Continuous mix of original channel and 24-level quantization; no split/blend labels.
                        string quantize = "val+" + N(s) + "*(round(val/11)*11-val)";
                        chain.Add("lutrgb=r='" + quantize + "':g='" + quantize + "':b='" + quantize + "'"); break;
                    case "plastic":
                        chain.Add(Bilateral(7 * s, .30 * s));
                        chain.Add("unsharp=lx=5:ly=5:la=" + N(.65 * s) + ":ca=0");
                        chain.Add("eq=contrast=" + N(1 + .12 * s) + ":saturation=" + N(1 + .08 * s)); break;
                    case "soft": chain.Add("gblur=sigma=" + N(2.4 * s) + ":steps=2"); break;
                }
            }
            return string.Join(",", chain);
        }
        static string Bilateral(double spatial, double range) {
            return "bilateral=sigmaS=" + N(spatial) + ":sigmaR=" + N(range) + ":planes=7";
        }
        static void Matrix(List<string> chain, double strength, double[] target) {
            string[] names = { "rr", "rg", "rb", "gr", "gg", "gb", "br", "bg", "bb" };
            var options = new string[9];
            for (int i = 0; i < 9; i++) {
                double identity = i % 4 == 0 ? 1 : 0;
                options[i] = names[i] + "=" + N(identity + strength * (target[i] - identity));
            }
            chain.Add("colorchannelmixer=" + string.Join(":", options));
        }
    }
}
