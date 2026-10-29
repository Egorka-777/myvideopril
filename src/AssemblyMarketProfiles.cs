using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoBatch {
    public class AssemblyMarketSettings {
        public bool Configured;
        public bool WorkspaceConfigured;
        public bool EditorSettingsConfigured;
        public int Fps = 30;
        public double MusicVolume = .8, TransitionDuration = .35;
        public string Transition = "fade", RenderMode = "auto";
        public bool RandomVideoStart, LoopShortVideos, AllowVideoReuse;
        public List<AssemblyLayer> GlobalLayers = new List<AssemblyLayer>();
        public bool ContinuousVideo;
        public AssemblySpeechSettings Speech = new AssemblySpeechSettings();
        public List<AssemblySpeechLine> SpeechLines = new List<AssemblySpeechLine>();
        public string SecondaryVideos = "", SplitLayout = "none", Voiceover = "";
        public double SplitSoftness = .08, VoiceoverVolume = 1;
        public WarmupSubtitleStyle SubtitleStyle = new WarmupSubtitleStyle { Enabled = false };
        public List<AssemblyCaption> Captions = new List<AssemblyCaption>();
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
            if (profile == AssemblyProfile.RuTikTok) return Path.Combine(root, "Assembled-RU-TikTok");
            if (profile == AssemblyProfile.EnTikTok) return Path.Combine(root, "Assembled-EN-TikTok");
            return Path.Combine(root, "Assembled");
        }

        static AssemblyMarketSettings Slice(AssemblyTemplate t, string profile) {
            profile = AssemblyProfile.Normalize(profile);
            if (profile == AssemblyProfile.En) return t.MarketEn ?? (t.MarketEn = new AssemblyMarketSettings());
            if (profile == AssemblyProfile.RuInst) return t.MarketRuInst ?? (t.MarketRuInst = new AssemblyMarketSettings());
            if (profile == AssemblyProfile.EnInst) return t.MarketEnInst ?? (t.MarketEnInst = new AssemblyMarketSettings());
            if (profile == AssemblyProfile.RuTikTok) return t.MarketRuTikTok ?? (t.MarketRuTikTok = new AssemblyMarketSettings());
            if (profile == AssemblyProfile.EnTikTok) return t.MarketEnTikTok ?? (t.MarketEnTikTok = new AssemblyMarketSettings());
            return t.MarketRu ?? (t.MarketRu = new AssemblyMarketSettings());
        }

        // Retained for existing callers: only initial migration may seed sibling workspaces.
        public static void SyncMissingLayersFromPrimary(AssemblyTemplate t) {
            if (t != null && !t.AssemblyMarketsSeeded) EnsureSeeded(t);
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
                Music = s.Music,
                MusicVolume = s.MusicVolume,
                Layers = CloneLayers(s.Layers)
            };
        }

        static List<AssemblyScene> CloneScenes(List<AssemblyScene> scenes) {
            if (scenes == null || scenes.Count == 0) return new List<AssemblyScene>();
            return scenes.Select(CloneScene).ToList();
        }

        static List<AssemblyLayer> CloneLayers(List<AssemblyLayer> layers) {
            return layers == null ? new List<AssemblyLayer>() : layers.Select(CloneLayer).ToList();
        }

        static List<AssemblyCaption> CloneCaptions(List<AssemblyCaption> captions) {
            return captions == null ? new List<AssemblyCaption>() : captions.Select(c => new AssemblyCaption {
                Text = c.Text, Start = c.Start, End = c.End
            }).ToList();
        }

        static AssemblySpeechSettings CloneSpeech(AssemblySpeechSettings speech) {
            if (speech == null) return new AssemblySpeechSettings();
            return new AssemblySpeechSettings {
                Text = speech.Text, VoiceId = speech.VoiceId, ModelId = speech.ModelId,
                Stability = speech.Stability, Similarity = speech.Similarity, Style = speech.Style,
                Speed = speech.Speed, SpeakerBoost = speech.SpeakerBoost
            };
        }

        static List<AssemblySpeechLine> CloneSpeechLines(List<AssemblySpeechLine> lines) {
            return lines == null ? new List<AssemblySpeechLine>() : lines.Select(l => new AssemblySpeechLine { Text = l.Text ?? "", AudioPath = l.AudioPath ?? "" }).ToList();
        }

        static void CaptureEditorSettings(AssemblyTemplate t, AssemblyMarketSettings slice) {
            slice.Fps = t.Fps;
            slice.MusicVolume = t.MusicVolume;
            slice.Transition = t.Transition;
            slice.TransitionDuration = t.TransitionDuration;
            slice.RenderMode = t.RenderMode;
            slice.RandomVideoStart = t.RandomVideoStart;
            slice.LoopShortVideos = t.LoopShortVideos;
            slice.AllowVideoReuse = t.AllowVideoReuse;
            slice.GlobalLayers = CloneLayers(t.GlobalLayers);
            slice.ContinuousVideo = t.ContinuousVideo;
            slice.Speech = CloneSpeech(t.Speech);
            slice.SpeechLines = CloneSpeechLines(t.SpeechLines);
            slice.SecondaryVideos = t.SecondaryVideos;
            slice.SplitLayout = t.SplitLayout;
            slice.SplitSoftness = t.SplitSoftness;
            slice.Voiceover = t.Voiceover;
            slice.VoiceoverVolume = t.VoiceoverVolume;
            slice.SubtitleStyle = t.SubtitleStyle == null ? new WarmupSubtitleStyle { Enabled = false } : t.SubtitleStyle.Copy();
            slice.Captions = CloneCaptions(t.Captions);
            slice.EditorSettingsConfigured = true;
        }

        static void ApplyEditorSettings(AssemblyTemplate t, AssemblyMarketSettings slice) {
            // Old XML slices did not contain these settings: preserve the active editor on first use.
            if (!slice.EditorSettingsConfigured) CaptureEditorSettings(t, slice);
            t.Fps = slice.Fps;
            t.MusicVolume = slice.MusicVolume;
            t.Transition = slice.Transition;
            t.TransitionDuration = slice.TransitionDuration;
            t.RenderMode = slice.RenderMode;
            t.RandomVideoStart = slice.RandomVideoStart;
            t.LoopShortVideos = slice.LoopShortVideos;
            t.AllowVideoReuse = slice.AllowVideoReuse;
            t.GlobalLayers = CloneLayers(slice.GlobalLayers);
            t.ContinuousVideo = slice.ContinuousVideo;
            t.Speech = CloneSpeech(slice.Speech);
            t.SpeechLines = CloneSpeechLines(slice.SpeechLines);
            t.SecondaryVideos = slice.SecondaryVideos;
            t.SplitLayout = slice.SplitLayout;
            t.SplitSoftness = slice.SplitSoftness;
            t.Voiceover = slice.Voiceover;
            t.VoiceoverVolume = slice.VoiceoverVolume;
            t.SubtitleStyle = slice.SubtitleStyle == null ? new WarmupSubtitleStyle { Enabled = false } : slice.SubtitleStyle.Copy();
            t.Captions = CloneCaptions(slice.Captions);
        }

        static void CaptureWorkspace(AssemblyTemplate t, AssemblyMarketSettings slice) {
            slice.Videos = t.Videos ?? "";
            slice.Music = t.Music ?? "";
            slice.Scenes = CloneScenes(t.Scenes);
            slice.WorkspaceConfigured = true;
        }

        static void ApplyWorkspace(AssemblyTemplate t, AssemblyMarketSettings slice) {
            if (!slice.WorkspaceConfigured) CaptureWorkspace(t, slice);
            t.Videos = slice.Videos ?? "";
            t.Music = slice.Music ?? "";
            t.Scenes = CloneScenes(slice.Scenes);
        }

        static void ImportWorkspaceFromSettings(AssemblyMarketSettings target, AssemblyMarketSettings source) {
            if (source == null || !source.WorkspaceConfigured) return;
            target.Videos = source.Videos ?? "";
            target.Music = source.Music ?? "";
            target.Scenes = CloneScenes(source.Scenes);
            target.WorkspaceConfigured = true;
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
            if (t.MarketRuTikTok == null) t.MarketRuTikTok = new AssemblyMarketSettings();
            if (t.MarketEnTikTok == null) t.MarketEnTikTok = new AssemblyMarketSettings();
            if (t.AssemblyMarketsSeeded) {
                foreach (string profile in AssemblyProfile.All) {
                    var slice = Slice(t, profile);
                    if (!slice.EditorSettingsConfigured) CaptureEditorSettings(t, slice);
                }
                return;
            }
            if (!t.MarketRu.Configured) Capture(t, AssemblyProfile.Ru);
            if (!t.MarketEn.Configured) {
                t.MarketEn.TitleBank = t.MarketRu.TitleBank.ToList();
                t.MarketEn.Count = t.MarketRu.Count;
                t.MarketEn.OutputMetadata = CloneMetadata(t.MarketRu.OutputMetadata);
                t.MarketEn.FormatWidth = t.MarketRu.FormatWidth;
                t.MarketEn.FormatHeight = t.MarketRu.FormatHeight;
                t.MarketEn.Output = DefaultOutput(AssemblyProfile.En);
                if (!t.MarketEn.WorkspaceConfigured) ImportWorkspaceFromSettings(t.MarketEn, t.MarketRu);
                t.MarketEn.Configured = true;
            }
            if (string.Equals(t.HistoryScope, AssemblyHistoryScope.En, StringComparison.OrdinalIgnoreCase)) {
                Capture(t, AssemblyProfile.En);
            }
            SeedChannelProfile(t, AssemblyProfile.RuInst, AssemblyProfile.Ru);
            SeedChannelProfile(t, AssemblyProfile.EnInst, AssemblyProfile.En);
            SeedChannelProfile(t, AssemblyProfile.RuTikTok, AssemblyProfile.Ru);
            SeedChannelProfile(t, AssemblyProfile.EnTikTok, AssemblyProfile.En);
            foreach (string profile in AssemblyProfile.All) {
                var slice = Slice(t, profile);
                if (!slice.EditorSettingsConfigured) CaptureEditorSettings(t, slice);
            }
            t.AssemblyMarketsSeeded = true;
        }

        static void SeedChannelProfile(AssemblyTemplate t, string channel, string main) {
            var slice = Slice(t, channel);
            var parent = Slice(t, main);
            if (slice.Configured) return;
            slice.Output = DefaultOutput(channel);
            slice.Count = parent.Count > 0 ? parent.Count : t.Count;
            slice.OutputMetadata = CloneMetadata(parent.OutputMetadata);
            slice.FormatWidth = parent.FormatWidth >= 180 ? parent.FormatWidth : t.Width;
            slice.FormatHeight = parent.FormatHeight >= 180 ? parent.FormatHeight : t.Height;
            slice.TitleBank = (parent.TitleBank ?? new List<string>()).ToList();
            if (parent.WorkspaceConfigured) ImportWorkspaceFromSettings(slice, parent);
            slice.Configured = true;
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
            CaptureEditorSettings(t, slice);
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
                if (AssemblyProfile.IsChannelProfile(profile) && !slice.WorkspaceConfigured && main.WorkspaceConfigured)
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
            ApplyEditorSettings(t, slice);
        }
    }
}
