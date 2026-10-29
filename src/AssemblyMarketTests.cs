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
            Check(t.MarketRuInst.Scenes[0].Layers.Count==1,"Configured profiles do not inherit new primary objects");
            Check(t.MarketEn.Scenes[0].Layers.Count==1,"Migration seeds independent EN scene objects");
            return true;
        }

        static void SetEditorSettings(AssemblyTemplate t, int index) {
            t.Fps = 24 + index;
            t.MusicVolume = .1 + index * .1;
            t.Transition = index % 2 == 0 ? "cut" : "fade";
            t.TransitionDuration = .1 + index * .05;
            t.RenderMode = index % 2 == 0 ? "cpu" : "auto";
            t.RandomVideoStart = index % 2 == 0;
            t.LoopShortVideos = index % 2 != 0;
            t.AllowVideoReuse = index % 2 == 0;
            t.Videos = "videos-" + index;
            t.Music = "music-" + index;
            t.Scenes[0].Videos = "scene-video-" + index;
            t.Scenes[0].Music = "scene-music-" + index;
            t.Scenes[0].MusicVolume = .2 + index * .1;
            t.Scenes[0].Layers[0].Source = "scene-layer-" + index;
            t.GlobalLayers = new System.Collections.Generic.List<AssemblyLayer> { new AssemblyLayer { Source = "global-" + index } };
            t.ContinuousVideo = index % 2 != 0;
            t.SecondaryVideos = "secondary-" + index;
            t.SplitLayout = index % 2 == 0 ? "none" : "vertical";
            t.SplitSoftness = .05 + index * .01;
            t.Speech = new AssemblySpeechSettings { Text = "speech-" + index, VoiceId = "voice-id-" + index, Speed = .8 + index * .05 };
            t.Voiceover = "voice-" + index;
            t.VoiceoverVolume = .4 + index * .1;
            t.SubtitleStyle = new WarmupSubtitleStyle { Enabled = index % 2 != 0, FontSize = 40 + index, TextColor = "#123456" };
            t.Captions = new System.Collections.Generic.List<AssemblyCaption> { new AssemblyCaption { Text = "caption-" + index, Start = index, End = index + 1 } };
            t.OutputMetadata.Brands = new[] { "brand-" + index };
        }

        static void CheckEditorSettings(AssemblyTemplate t, int index) {
            Check(t.Fps == 24 + index && t.MusicVolume == .1 + index * .1, "FPS and music volume " + index);
            Check(t.Transition == (index % 2 == 0 ? "cut" : "fade") && t.TransitionDuration == .1 + index * .05, "Transitions " + index);
            Check(t.RenderMode == (index % 2 == 0 ? "cpu" : "auto"), "Render mode " + index);
            Check(t.RandomVideoStart == (index % 2 == 0) && t.LoopShortVideos == (index % 2 != 0) && t.AllowVideoReuse == (index % 2 == 0), "Reuse/start/loop " + index);
            Check(t.Videos == "videos-" + index && t.Music == "music-" + index, "Audio/video sources " + index);
            Check(t.Scenes[0].Videos == "scene-video-" + index && t.Scenes[0].Music == "scene-music-" + index && t.Scenes[0].MusicVolume == .2 + index * .1, "Scene sources/audio " + index);
            Check(t.Scenes[0].Layers[0].Source == "scene-layer-" + index && t.GlobalLayers.Single().Source == "global-" + index, "Scene/global objects " + index);
            Check(t.ContinuousVideo == (index % 2 != 0) && t.SecondaryVideos == "secondary-" + index, "Continuous and secondary sources " + index);
            Check(t.SplitLayout == (index % 2 == 0 ? "none" : "vertical") && t.SplitSoftness == .05 + index * .01, "Split settings " + index);
            Check(t.Voiceover == "voice-" + index && t.VoiceoverVolume == .4 + index * .1, "Voiceover " + index);
            Check(t.Speech.Text == "speech-" + index && t.Speech.VoiceId == "voice-id-" + index && t.Speech.Speed == .8 + index * .05, "Speech settings " + index);
            Check(t.SubtitleStyle.Enabled == (index % 2 != 0) && t.SubtitleStyle.FontSize == 40 + index && t.SubtitleStyle.TextColor == "#123456", "Subtitle style " + index);
            Check(t.Captions.Single().Text == "caption-" + index && t.Captions[0].Start == index && t.Captions[0].End == index + 1, "Caption timing " + index);
            Check(t.OutputMetadata.Brands.Single() == "brand-" + index, "Metadata " + index);
        }

        public static bool RunPersistenceIsolationTests() {
            string root = Path.Combine(Path.GetTempPath(), "assembly-profile-isolation-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(root);
                var t = AssemblyTemplate.Defaults();
                AssemblyMarketProfiles.EnsureSeeded(t);
                Check(AssemblyProfile.Visible.SequenceEqual(new[] { "RU", "EN", "RU-Inst", "EN-Inst", "TEST" }), "Visible IDs preserve persistence keys");
                Check(AssemblyProfile.Visible.Select(AssemblyProfile.ShortLabel).SequenceEqual(new[] { "RU YT", "EN YT", "RU INS", "EN INS", "ТЕСТ" }), "Visible profile labels");
                Check(AssemblyProfile.All.Length == 7, "Hidden TikTok persistence keys remain");
                for (int i = 0; i < AssemblyProfile.All.Length; i++) {
                    SetEditorSettings(t, i);
                    AssemblyMarketProfiles.Capture(t, AssemblyProfile.All[i]);
                    t.GlobalLayers[0].Source = "modified after capture";
                    t.Captions[0].Text = "modified after capture";
                    t.SubtitleStyle.FontSize = 88;
                    t.Speech.Text = "modified speech";
                    t.Scenes[0].Layers[0].Source = "modified after capture";
                    t.OutputMetadata.Brands[0] = "modified after capture";
                }
                string path = Path.Combine(root, "template.xml");
                AssemblyFiles.Save(path, t);
                t = AssemblyFiles.Load<AssemblyTemplate>(path);
                AssemblyMarketProfiles.EnsureSeeded(t);
                for (int i = 0; i < AssemblyProfile.All.Length; i++) {
                    AssemblyMarketProfiles.Apply(t, AssemblyProfile.All[i]);
                    CheckEditorSettings(t, i);
                    t.GlobalLayers[0].Source = "modified after apply";
                    t.Captions[0].Text = "modified after apply";
                    t.SubtitleStyle.FontSize = 88;
                    t.Speech.Text = "modified speech";
                    t.Scenes[0].Layers[0].Source = "modified after apply";
                    t.OutputMetadata.Brands[0] = "modified after apply";
                    t.Scenes[0].Layers.Add(new AssemblyLayer { Source = "profile-only" });
                    AssemblyMarketProfiles.SyncMissingLayersFromPrimary(t);
                    AssemblyMarketProfiles.Apply(t, AssemblyProfile.All[i]);
                    CheckEditorSettings(t, i);
                    Check(t.Scenes[0].Layers.Count == 1, "Applied scene layer lists are detached " + i);
                }
                AssemblyMarketProfiles.Apply(t, AssemblyProfile.Ru);
                t.Scenes[0].Layers.Add(new AssemblyLayer { Source = "RU only" });
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.Ru);
                AssemblyMarketProfiles.Apply(t, AssemblyProfile.En);
                Check(t.Scenes[0].Layers.Count == 1, "Capture never propagates scene objects to configured siblings");
                t.Scenes.Clear(); t.Videos = ""; t.Music = "";
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.En);
                AssemblyMarketProfiles.Apply(t, AssemblyProfile.Ru);
                AssemblyMarketProfiles.Apply(t, AssemblyProfile.En);
                Check(t.Scenes.Count == 0 && t.Videos == "" && t.Music == "", "Intentionally empty workspace remains isolated");
                return true;
            } finally {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        public static bool RunLegacyEditorMigrationTests() {
            var t = AssemblyTemplate.Defaults();
            AssemblyMarketProfiles.EnsureSeeded(t);
            SetEditorSettings(t, 2);
            t.MarketEn.EditorSettingsConfigured = false;
            AssemblyMarketProfiles.Apply(t, AssemblyProfile.En);
            Check(t.MarketEn.EditorSettingsConfigured && t.Fps == 26 && t.MusicVolume == .1 + 2 * .1, "Legacy slice inherits active editor values on first apply");
            Check(t.GlobalLayers[0].Source == "global-2" && t.SubtitleStyle.FontSize == 42, "Legacy global/subtitle settings retained");
            t.GlobalLayers[0].Source = "active edit";
            t.SubtitleStyle.FontSize = 80;
            t.Captions[0].Text = "active edit";
            SetEditorSettings(t, 3);
            AssemblyMarketProfiles.Apply(t, AssemblyProfile.En);
            Check(t.Fps == 26 && t.GlobalLayers[0].Source == "global-2" && t.SubtitleStyle.FontSize == 42 && t.Captions[0].Text == "caption-2", "Migrated editor values are detached and not inherited again");
            var legacy = AssemblyTemplate.Defaults();
            legacy.MarketRuTikTok.Configured = true;
            legacy.MarketRuTikTok.TitleBank.Add("hidden legacy title");
            legacy.MarketRuTikTok.Output = "hidden-output";
            AssemblyMarketProfiles.EnsureSeeded(legacy);
            Check(legacy.MarketRuTikTok.Output == "hidden-output" && legacy.MarketRuTikTok.TitleBank.Single() == "hidden legacy title", "Seeding retains configured hidden legacy data");
            return true;
        }

        public static bool RunSelfTests() {
            RunInitializationTests();
            RunPersistenceIsolationTests();
            RunLegacyEditorMigrationTests();
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
                Check(t.Scenes[0].Name == "EN scene", "EN scenes");
                Check(t.TitleBank.Single() == "En Title", "EN titles");

                t.Scenes[0].Name = "RU Inst scene";
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.RuInst);
                t.Scenes[0].Name = "RU main again";
                AssemblyMarketProfiles.Apply(t, AssemblyProfile.RuInst);
                Check(t.Scenes[0].Name == "RU Inst scene" && t.Width == 1080 && t.Height == 1920, "RU Inst isolated workspace + same format as RU");
                AssemblyMarketProfiles.Apply(t, AssemblyProfile.En);
                Check(t.Scenes[0].Name == "EN scene", "EN scene survives editing RU Inst");

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
