using System;
using System.IO;
using System.Linq;

namespace VideoBatch {
    public static class AssemblyMarketTests {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

        public static bool RunInitializationTests() {
            var t=AssemblyTemplate.Defaults();
            AssemblyMarketProfiles.EnsureSeeded(t);
            AssemblyMarketProfiles.EnsureSeeded(t);
            AssemblyMarketProfiles.SyncMissingLayersFromPrimary(t);
            Check(t.AssemblyMarketsSeeded&&t.MarketRu.Scenes.Count==4,"Repeated template initialization terminates and preserves scenes");
            t.MarketRu.Scenes[0].Layers.Add(new AssemblyLayer{Name="Another object",Source="test-images"});
            AssemblyMarketProfiles.SyncMissingLayersFromPrimary(t);
            AssemblyMarketProfiles.SyncMissingLayersFromPrimary(t);
            Check(t.MarketRuInst.Scenes[0].Layers.Count==2,"Objects propagate once to configured profiles");
            return true;
        }

        public static bool RunSelfTests() {
            string root = Path.Combine(Path.GetTempPath(), "assembly-market-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(root);
                string templatePath = Path.Combine(root, "assembly-template.xml");
                var t = AssemblyTemplate.Defaults();
                t.Materials = Path.Combine(root, "materials");
                AssemblyWorkspace.Prepare(t);
                t.Videos = "ru-videos";
                t.Music = "ru-music";
                t.Scenes[0].Name = "RU scene";
                t.Scenes[0].Videos = "ru-scene-vid";
                t.Scenes[0].Layers[0].Source = Path.Combine(t.Scenes[0].Folder, "ru-images");
                t.TitleBank = new System.Collections.Generic.List<string> { "RU title" };
                t.MarketRu.Configured = true;
                t.MarketEn.Configured = true;
                t.MarketEn.TitleBank = new System.Collections.Generic.List<string> { "EN title" };
                AssemblyFiles.Save(templatePath, t);

                t.Width = 1920; t.Height = 1080;
                AssemblyMarketProfiles.Capture(t, AssemblyHistoryScope.Ru);
                t.Width = 1080; t.Height = 1920;
                t.Videos = "en-videos";
                t.Music = "en-music";
                t.Scenes[0].Name = "EN scene";
                t.Scenes[0].Videos = "en-scene-vid";
                t.TitleBank = new System.Collections.Generic.List<string> { "EN title" };
                AssemblyMarketProfiles.Capture(t, AssemblyHistoryScope.En);
                t.HistoryScope = AssemblyHistoryScope.En;
                AssemblyFiles.Save(templatePath, t);

                AssemblyMarketProfiles.Apply(t, AssemblyHistoryScope.Ru);
                Check(t.Videos == "ru-videos" && t.Music == "ru-music", "RU videos/music");
                Check(t.Width == 1920 && t.Height == 1080, "RU saved frame size");
                Check(t.Scenes[0].Name == "RU scene" && t.Scenes[0].Videos == "ru-scene-vid", "RU scenes");
                Check(t.TitleBank.Count == 1 && t.TitleBank[0] == "Ru Title", "RU titles got: " + string.Join("|", t.TitleBank));

                AssemblyMarketProfiles.Apply(t, AssemblyHistoryScope.En);
                Check(t.Videos == "en-videos" && t.Music == "en-music", "EN videos/music");
                Check(t.Width == 1080 && t.Height == 1920, "EN saved frame size");

                t.Scenes[0].Name = "RU Inst scene";
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.RuInst);
                t.Scenes[0].Name = "RU main again";
                AssemblyMarketProfiles.Apply(t, AssemblyProfile.RuInst);
                Check(t.Scenes[0].Name == "RU Inst scene" && t.Width == 1080 && t.Height == 1920, "RU Inst isolated workspace + same format as RU");
                Check(t.Scenes[0].Name == "EN scene", "EN scenes");
                Check(t.TitleBank.Single() == "En Title", "EN titles");

                string legacyPath = Path.Combine(root, "assembly-template.xml.bak");
                var legacy = AssemblyTemplate.Defaults();
                legacy.Materials = t.Materials;
                legacy.HistoryScope = AssemblyHistoryScope.Ru;
                legacy.Videos = "legacy-ru-videos";
                legacy.Scenes[0].Name = "Legacy RU";
                legacy.Scenes[0].Videos = "legacy-scene-vid";
                legacy.Scenes[0].Layers[0].Source = "Сцены/legacy";
                legacy.TitleBank = new System.Collections.Generic.List<string> { "Бинодекс" };
                AssemblyWorkspace.Prepare(legacy);
                AssemblyFiles.Save(legacyPath, legacy);

                var fresh = AssemblyFiles.Load<AssemblyTemplate>(templatePath);
                fresh.MarketWorkspaceVersion = 0;
                fresh.MarketRu.Videos = "en-videos";
                fresh.MarketRu.Scenes = fresh.MarketEn.Scenes.Select(s => new AssemblyScene {
                    Folder = s.Folder, Name = s.Name, Videos = s.Videos, VideoMode = s.VideoMode, Duration = s.Duration,
                    Layers = s.Layers.Select(l => new AssemblyLayer { Name = l.Name, Source = l.Source, Enabled = l.Enabled, Unique = l.Unique, Fixed = l.Fixed,
                        PlacementConfigured = l.PlacementConfigured, ModeExplicit = l.ModeExplicit, FixedAssetHash = l.FixedAssetHash, ObjectId = l.ObjectId,
                        X = l.X, Y = l.Y, Width = l.Width, Height = l.Height, Opacity = l.Opacity, Start = l.Start, End = l.End, Font = l.Font, FontSize = l.FontSize, Radius = l.Radius, Padding = l.Padding }).ToList()
                }).ToList();
                fresh.MarketRu.WorkspaceConfigured = true;
                AssemblyMarketProfiles.EnsureWorkspaceMigrated(fresh, templatePath);
                Check(fresh.MarketRu.WorkspaceConfigured && fresh.MarketRu.Videos == "legacy-ru-videos",
                    "recover RU workspace from backup snapshot · wc=" + fresh.MarketRu.WorkspaceConfigured + " v=" + fresh.MarketRu.Videos + " scenes=" + fresh.MarketRu.Scenes.Count);

                return true;
            } finally {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
