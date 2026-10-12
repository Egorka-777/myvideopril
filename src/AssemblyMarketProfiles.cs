using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoBatch {
    public class AssemblyMarketSettings {
        public bool Configured;
        public List<string> TitleBank = new List<string>();
        public string Output = "";
        public int Count = 10;
        public AssemblyOutputMetadata OutputMetadata;
    }

    public static class AssemblyMarketProfiles {
        public static string NormMarket(string market) {
            return (market ?? "").Trim().ToUpperInvariant() == "EN" ? AssemblyHistoryScope.En : AssemblyHistoryScope.Ru;
        }

        static string DefaultOutput(string scope) {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch");
            return scope == AssemblyHistoryScope.En ? Path.Combine(root, "Assembled-EN") : Path.Combine(root, "Assembled");
        }

        static AssemblyMarketSettings Slice(AssemblyTemplate t, string scope) {
            return scope == AssemblyHistoryScope.En ? t.MarketEn : t.MarketRu;
        }

        static AssemblyOutputMetadata CloneMetadata(AssemblyOutputMetadata source) {
            if (source == null) return AssemblyOutputMetadata.Default();
            return new AssemblyOutputMetadata {
                DeviceMetadata = source.DeviceMetadata,
                CaptureDate = source.CaptureDate,
                RandomizeCaptureTime = source.RandomizeCaptureTime,
                CaptureDaysBack = source.CaptureDaysBack,
                Software = source.Software,
                Brands = source.Brands == null ? null : (string[])source.Brands.Clone()
            };
        }

        public static void EnsureSeeded(AssemblyTemplate t) {
            if (t == null) return;
            if (t.MarketRu == null) t.MarketRu = new AssemblyMarketSettings();
            if (t.MarketEn == null) t.MarketEn = new AssemblyMarketSettings();
            if (t.AssemblyMarketsSeeded) return;
            Capture(t, AssemblyHistoryScope.Ru);
            t.MarketEn.TitleBank = t.MarketRu.TitleBank.ToList();
            t.MarketEn.Count = t.MarketRu.Count;
            t.MarketEn.OutputMetadata = CloneMetadata(t.MarketRu.OutputMetadata);
            t.MarketEn.Output = DefaultOutput(AssemblyHistoryScope.En);
            t.MarketEn.Configured = t.MarketRu.Configured;
            if (string.Equals(t.HistoryScope, AssemblyHistoryScope.En, StringComparison.OrdinalIgnoreCase)) {
                Capture(t, AssemblyHistoryScope.En);
            }
            t.AssemblyMarketsSeeded = true;
        }

        public static void Capture(AssemblyTemplate t, string scope) {
            if (t == null) return;
            scope = NormMarket(scope);
            var slice = Slice(t, scope);
            slice.TitleBank = AssemblyOutputTags.NormalizeBank(t.TitleBank);
            slice.Output = t.Output ?? "";
            slice.Count = t.Count;
            slice.OutputMetadata = CloneMetadata(t.OutputMetadata);
            slice.Configured = true;
        }

        public static void Apply(AssemblyTemplate t, string scope) {
            if (t == null) return;
            EnsureSeeded(t);
            scope = NormMarket(scope);
            var slice = Slice(t, scope);
            t.HistoryScope = scope;
            if (!slice.Configured) {
                slice.Output = DefaultOutput(scope);
                slice.Count = t.Count > 0 ? t.Count : 10;
                slice.OutputMetadata = AssemblyOutputMetadata.Default();
            }
            t.TitleBank = (slice.TitleBank ?? new List<string>()).ToList();
            t.Output = string.IsNullOrWhiteSpace(slice.Output) ? DefaultOutput(scope) : slice.Output;
            t.Count = slice.Count <= 0 ? 10 : slice.Count;
            t.OutputMetadata = CloneMetadata(slice.OutputMetadata);
        }
    }
}
