using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoBatch {
    public class AssemblyMarketSettings {
        public bool Configured;
        public bool WorkspaceConfigured;
        public List<string> TitleBank = new List<string>();
        public string Output = "";
        public int Count = 10;
        public int FormatWidth;
        public int FormatHeight;
        public string Videos = "";
        public string Music = "";
        public List<AssemblyScene> Scenes = new List<AssemblyScene>();
        public AssemblyOutputMetadata OutputMetadata;
    }

    public static class AssemblyMarketProfiles {
        public static string NormMarket(string market) => AssemblyProfile.MainProfile(market);

        public static string DefaultOutput(string profile) {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch");
            profile = AssemblyProfile.Normalize(profile);
            if (profile == AssemblyProfile.En) return Path.Combine(root, "Assembled-EN");
            if (profile == AssemblyProfile.RuInst) return Path.Combine(root, "Assembled-RU-Inst");
            if (profile == AssemblyProfile.EnInst) return Path.Combine(root, "Assembled-EN-Inst");
            return Path.Combine(root, "Assembled");
        }

        static AssemblyMarketSettings Slice(AssemblyTemplate t, string profile) {
            profile = AssemblyProfile.Normalize(profile);
            if (profile == AssemblyProfile.En) return t.MarketEn ?? (t.MarketEn = new AssemblyMarketSettings());
            if (profile == AssemblyProfile.RuInst) return t.MarketRuInst ?? (t.MarketRuInst = new AssemblyMarketSettings());
            if (profile == AssemblyProfile.EnInst) return t.MarketEnInst ?? (t.MarketEnInst = new AssemblyMarketSettings());
            return t.MarketRu ?? (t.MarketRu = new AssemblyMarketSettings());
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

        static AssemblyLayer CloneLayer(AssemblyLayer l) {
            if (l == null) return new AssemblyLayer();
            return new AssemblyLayer {
                Name = l.Name,
                Source = l.Source,
                Enabled = l.Enabled,
                Unique = l.Unique,
                Fixed = l.Fixed,
                PlacementConfigured = l.PlacementConfigured,
                ModeExplicit = l.ModeExplicit,
                FixedAssetHash = l.FixedAssetHash,
                ObjectId = l.ObjectId,
                X = l.X,
                Y = l.Y,
                Width = l.Width,
                Height = l.Height,
                Opacity = l.Opacity,
                Start = l.Start,
                End = l.End,
                Font = l.Font,
                FontSize = l.FontSize,
                Radius = l.Radius,
                Padding = l.Padding
            };
        }

        static AssemblyScene CloneScene(AssemblyScene s) {
            if (s == null) return new AssemblyScene();
            return new AssemblyScene {
                Folder = s.Folder,
                Name = s.Name,
                Videos = s.Videos,
                VideoMode = s.VideoMode,
                Duration = s.Duration,
                Layers = s.Layers == null ? new List<AssemblyLayer>() : s.Layers.Select(CloneLayer).ToList()
            };
        }

        static List<AssemblyScene> CloneScenes(List<AssemblyScene> scenes) {
            if (scenes == null || scenes.Count == 0) return new List<AssemblyScene>();
            return scenes.Select(CloneScene).ToList();
        }

        static void CaptureWorkspace(AssemblyTemplate t, AssemblyMarketSettings slice) {
            slice.Videos = t.Videos ?? "";
            slice.Music = t.Music ?? "";
            slice.Scenes = CloneScenes(t.Scenes);
            slice.WorkspaceConfigured = slice.Scenes.Count > 0;
        }

        static void ApplyWorkspace(AssemblyTemplate t, AssemblyMarketSettings slice) {
            if (!slice.WorkspaceConfigured || slice.Scenes == null || slice.Scenes.Count == 0) return;
            t.Videos = slice.Videos ?? "";
            t.Music = slice.Music ?? "";
            t.Scenes = CloneScenes(slice.Scenes);
        }

        static void ImportWorkspaceFromSettings(AssemblyMarketSettings target, AssemblyMarketSettings source) {
            if (source == null || !source.WorkspaceConfigured) return;
            target.Videos = source.Videos ?? "";
            target.Music = source.Music ?? "";
            target.Scenes = CloneScenes(source.Scenes);
            target.WorkspaceConfigured = target.Scenes.Count > 0;
        }

        static bool SnapshotMatchesMarket(AssemblyTemplate snap, string scope) {
            if (snap == null) return false;
            if (NormMarket(snap.HistoryScope) == scope) return snap.Scenes != null && snap.Scenes.Count > 0;
            if (scope == AssemblyHistoryScope.Ru) {
                if (snap.Scenes != null && snap.Scenes.Any(s => (s.Name ?? "").Any(c => c >= '\u0400' && c <= '\u04FF'))) return true;
                if (snap.Scenes != null && snap.Scenes.Any(s => (s.Videos ?? "").Any(c => c >= '\u0400' && c <= '\u04FF'))) return true;
                var bank = snap.MarketRu?.TitleBank ?? snap.TitleBank;
                return bank != null && bank.Any(line => (line ?? "").Any(c => c >= '\u0400' && c <= '\u04FF'));
            }
            if (scope == AssemblyHistoryScope.En) {
                var bank = snap.MarketEn?.TitleBank ?? snap.TitleBank;
                return bank != null && bank.Any(line => (line ?? "").IndexOf("Pocket Option", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return false;
        }

        static void ImportWorkspaceSnapshot(AssemblyMarketSettings slice, AssemblyTemplate snap) {
            slice.Videos = snap.Videos ?? "";
            slice.Music = snap.Music ?? "";
            slice.Scenes = CloneScenes(snap.Scenes);
            slice.WorkspaceConfigured = slice.Scenes.Count > 0;
        }

        static void TryRecoverInactiveWorkspace(AssemblyTemplate t, string scope, string templatePath, bool force) {
            var slice = Slice(t, scope);
            if (!force && slice.WorkspaceConfigured) return;
            if (string.IsNullOrWhiteSpace(templatePath)) return;
            string dir = Path.GetDirectoryName(templatePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            var candidates = new List<string>();
            string bak = templatePath + ".bak";
            if (File.Exists(bak)) candidates.Add(bak);
            candidates.AddRange(Directory.GetFiles(dir, "assembly-template.xml.before-*.xml"));
            candidates.AddRange(Directory.GetFiles(dir, "assembly-template.xml.unreadable-*.xml"));
            foreach (string file in candidates.OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)) {
                try {
                    var snap = AssemblyFiles.Load<AssemblyTemplate>(file);
                    if (!SnapshotMatchesMarket(snap, scope)) continue;
                    ImportWorkspaceSnapshot(slice, snap);
                    return;
                } catch { }
            }
        }

        public static void EnsureWorkspaceMigrated(AssemblyTemplate t, string templatePath) {
            if (t == null) return;
            EnsureSeeded(t);
            if (t.MarketWorkspaceVersion >= 1) return;
            foreach (string scope in new[] { AssemblyHistoryScope.Ru, AssemblyHistoryScope.En }) {
                TryRecoverInactiveWorkspace(t, scope, templatePath, force: true);
            }
            Capture(t, AssemblyProfile.Normalize(t.HistoryScope));
            t.MarketWorkspaceVersion = 1;
        }

        public static void EnsureSeeded(AssemblyTemplate t) {
            if (t == null) return;
            if (t.MarketRu == null) t.MarketRu = new AssemblyMarketSettings();
            if (t.MarketEn == null) t.MarketEn = new AssemblyMarketSettings();
            if (t.MarketRuInst == null) t.MarketRuInst = new AssemblyMarketSettings();
            if (t.MarketEnInst == null) t.MarketEnInst = new AssemblyMarketSettings();
            if (t.AssemblyMarketsSeeded) return;
            Capture(t, AssemblyProfile.Ru);
            t.MarketEn.TitleBank = t.MarketRu.TitleBank.ToList();
            t.MarketEn.Count = t.MarketRu.Count;
            t.MarketEn.OutputMetadata = CloneMetadata(t.MarketRu.OutputMetadata);
            t.MarketEn.FormatWidth = t.MarketRu.FormatWidth;
            t.MarketEn.FormatHeight = t.MarketRu.FormatHeight;
            t.MarketEn.Output = DefaultOutput(AssemblyProfile.En);
            t.MarketEn.Configured = t.MarketRu.Configured;
            if (string.Equals(t.HistoryScope, AssemblyHistoryScope.En, StringComparison.OrdinalIgnoreCase)) {
                Capture(t, AssemblyProfile.En);
            }
            t.AssemblyMarketsSeeded = true;
        }

        public static void Capture(AssemblyTemplate t, string profile) {
            if (t == null) return;
            profile = AssemblyProfile.Normalize(profile);
            var slice = Slice(t, profile);
            slice.TitleBank = AssemblyOutputTags.NormalizeBank(t.TitleBank);
            slice.Output = t.Output ?? "";
            slice.Count = t.Count;
            slice.FormatWidth = t.Width;
            slice.FormatHeight = t.Height;
            slice.OutputMetadata = CloneMetadata(t.OutputMetadata);
            CaptureWorkspace(t, slice);
            slice.Configured = true;
        }

        public static void Apply(AssemblyTemplate t, string profile) {
            if (t == null) return;
            profile = AssemblyProfile.Normalize(profile);
            var slice = Slice(t, profile);
            var main = Slice(t, AssemblyProfile.MainProfile(profile));
            if (!slice.Configured) {
                slice.Output = DefaultOutput(profile);
                slice.Count = main.Configured && main.Count > 0 ? main.Count : (t.Count > 0 ? t.Count : 10);
                slice.OutputMetadata = CloneMetadata(main.Configured ? main.OutputMetadata : AssemblyOutputMetadata.Default());
                if (main.FormatWidth >= 180 && main.FormatHeight >= 180) {
                    slice.FormatWidth = main.FormatWidth;
                    slice.FormatHeight = main.FormatHeight;
                } else if (t.Width >= 180 && t.Height >= 180) {
                    slice.FormatWidth = t.Width;
                    slice.FormatHeight = t.Height;
                }
                if (AssemblyProfile.IsInst(profile) && !slice.WorkspaceConfigured && main.WorkspaceConfigured)
                    ImportWorkspaceFromSettings(slice, main);
                slice.TitleBank = (main.TitleBank ?? new List<string>()).ToList();
                slice.Configured = true;
            }
            t.HistoryScope = AssemblyProfile.LedgerScope(profile);
            t.TitleBank = (slice.TitleBank ?? new List<string>()).ToList();
            t.Output = string.IsNullOrWhiteSpace(slice.Output) ? DefaultOutput(profile) : slice.Output;
            t.Count = slice.Count <= 0 ? 10 : slice.Count;
            if (slice.FormatWidth >= 180 && slice.FormatHeight >= 180) {
                t.Width = slice.FormatWidth;
                t.Height = slice.FormatHeight;
            }
            t.OutputMetadata = CloneMetadata(slice.OutputMetadata);
            ApplyWorkspace(t, slice);
        }
    }
}
