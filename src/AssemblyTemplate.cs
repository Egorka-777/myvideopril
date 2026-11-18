using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace VideoBatch {
    public class AssemblyLayer {
        public string Name = "Изображение", Source = "";
        public bool AudioEnabled;
        public ProcessingParameter AudioPitch = new ProcessingParameter(-2, 2);
        public bool Enabled = true, Unique = false, Fixed = false, PlacementConfigured = false, ModeExplicit = false;
        public string FixedAssetHash = "", ObjectId = Guid.NewGuid().ToString("N");
        public bool InCombination = true;
        public bool UniformImageScale;
        public bool PreserveAspectRatio = true;
        public bool GreenScreen;
        public double X = .08, Y = .35, Width = .84, Height = .25, Opacity = 1;
        public double Start = 0, End = 0; // End=0 means until the end of this scene.
        public string AppearanceTransition = "cut";
        public double AppearanceDuration = .35;
        // Missing fields in existing XML retain the original continuous playback.
        public string PlaybackMode = "continuous"; // once | continuous | repeat
        public double RepeatPause = 1, PhotoDuration = 3;
        public double RepeatInterval;
        public string ExitTransition = "cut";
        public double ExitDuration = .35;
        /// <summary>all | primary | continuation — which video part shows this image.</summary>
        public string ImagePart = "all";
        public string Kind = "image", Text = "";
        public string TextColor = "#FFFFFF", TextOutlineColor = "#000000";
        public double TextOutlineWidth = 0, CornerRadius = 0;
        public bool TextBold, TextItalic, TextUppercase;
        public string TextPlate = "none", TextPlateColor = "#10131A";
        public int TextPlateOpacity = 80, TextLineSpacing = 100;
        [XmlIgnore] public bool IsText { get { return Kind == "text"; } }
        public string Font = "Arial";
        public double FontSize = 54, Radius = 28, Padding = 24;
    }
    public class AssemblySpeechSettings {
        public string Text = "", VoiceId = "", ModelId = "eleven_multilingual_v2";
        public double Stability = .5, Similarity = .75, Style = 0, Speed = 1;
        public bool SpeakerBoost = true;
    }
    public class AssemblySpeechLine {
        public string Text = "", AudioPath = "";
    }
    public class AssemblyCaption {
        public string Text = "";
        public double Start, End;
    }
    public class AssemblySceneVideoPack {
        public string Source = "";
        public bool Mix;
    }
    public class AssemblyScene {
        public string Folder = "";
        public string Name = "Сцена", Videos = "", VideoMode = ""; // Empty mode preserves legacy folder fallback.
        /// <summary>Intro clip: always first, plays the selected source in full before the transition.</summary>
        public bool IsPreview;
        public bool VideoInCombination = true;
        public bool DurationByVideo;
        /// <summary>Second video pack: fills the scene only after the first primary clip ends.</summary>
        public string ContinuationVideos = "";
        public bool MixVideos;
        public List<AssemblySceneVideoPack> VideoPacks = new List<AssemblySceneVideoPack>();
        public double Duration = 4;
        public string Music = "";
        // Empty mode keeps the old whole-roll voiceover; shared/own/none enable scene-level voiceover.
        public string Voiceover = "", VoiceMode = "";
        public double MusicVolume = 1;
        // Negative values preserve old projects: their former global levels remain the
        // initial per-scene levels until the scene is edited in the sound window.
        public double NativeAudioVolume = -1;
        public double BedMusicVolume = -1;
        public List<AssemblyLayer> Layers = new List<AssemblyLayer>();
        public override string ToString() { return Name + " · " + Duration.ToString("0.##") + " с"; }
    }
    public class AssemblyTemplate {
        public int Version = 1, PackStudioVersion = 0, ObjectStudioVersion = 0, SceneTimingVersion = 0;
        public AssemblyCombinationSettings Combination = new AssemblyCombinationSettings();
        public bool LoopShortVideos = false, AllowVideoReuse = false;
        public bool DurationByVideo;
        public string Id = Guid.NewGuid().ToString("N"), Name = "Моя студия";
        public string Materials = "", Videos = "Видео", Backgrounds = "", Music = "", Output = "";
        public bool BackgroundForeground;
        public int Width = 1080, Height = 1920, Fps = 30, Count = 10;
        public string Transition = "fade", RenderMode = "auto";
        public double TransitionDuration = .35, MusicVolume = .8;
        public bool RandomVideoStart = false;
        public string HistoryScope = AssemblyHistoryScope.Shared;
        public bool AssemblyMarketsSeeded;
        public int MarketWorkspaceVersion;
        public AssemblyMarketSettings MarketRu = new AssemblyMarketSettings(), MarketEn = new AssemblyMarketSettings();
        public AssemblyMarketSettings MarketRuInst = new AssemblyMarketSettings(), MarketEnInst = new AssemblyMarketSettings();
        public AssemblyMarketSettings MarketRuTikTok = new AssemblyMarketSettings(), MarketEnTikTok = new AssemblyMarketSettings();
        public AssemblyMarketSettings MarketTest = new AssemblyMarketSettings();
        public List<AssemblyCustomProfile> CustomProfiles = new List<AssemblyCustomProfile>();
        public List<string> TitleBank = new List<string>();
        public AssemblyOutputMetadata OutputMetadata = new AssemblyOutputMetadata();
        public ProcessingSettings OutputProcessing;
        public List<AssemblyScene> Scenes = new List<AssemblyScene>();
        public bool HasSceneVoiceovers { get { return Scenes != null && Scenes.Any(s => s.VoiceMode == "own" || s.VoiceMode == "shared" || s.VoiceMode == "none"); } }
        public bool UsesWholeRollVoiceover { get { return !HasSceneVoiceovers && (AssemblySpeechPlan.SharedVoiceOptions(this).Count > 0 || !string.IsNullOrWhiteSpace(Voiceover)); } }
        public double SceneNativeAudioVolume(AssemblyScene scene) {
            if (scene == null) return 0;
            return scene.NativeAudioVolume >= 0 ? scene.NativeAudioVolume
                : scene.IsPreview ? 1 : PrimaryNativeAudio ? PrimaryNativeVolume : 0;
        }
        public double SceneBedMusicVolume(AssemblyScene scene) {
            if (scene == null) return 0;
            return scene.BedMusicVolume >= 0 ? scene.BedMusicVolume : scene.IsPreview ? 0 : MusicVolume;
        }
        public List<AssemblyLayer> GlobalLayers = new List<AssemblyLayer>();
        public List<AssemblyInsertion> Insertions = new List<AssemblyInsertion>();
        public ProcessingParameter BackgroundAudioPitch = new ProcessingParameter(-2, 2);
        public string AudioProcessingSourceId = "";
        public bool ContinuousVideo = false;
        public double BaseVideoX = 0, BaseVideoY = 0, BaseVideoWidth = 1, BaseVideoHeight = 1;
        public bool BaseVideoPreserveAspectRatio = true;
        public AssemblySpeechSettings Speech = new AssemblySpeechSettings();
        public List<AssemblySpeechLine> SpeechLines = new List<AssemblySpeechLine>();
        public string SecondaryVideos = "", SplitLayout = "none", Voiceover = "";
        /// <summary>Обе половины экрана выбираются из общего набора видео (основная пачка + второе видео).</summary>
        public bool SplitSharedPool;
        /// <summary>Верх/низ (или лево/право) не меняются местами между связками.</summary>
        public bool SplitFixHalves = true;
        public string SplitAudioMode = "";
        public double SplitAudioVolume = 1;
        public string EffectiveSplitAudioMode => string.IsNullOrEmpty(SplitAudioMode) ? (!PrimaryNativeAudio && SecondaryNativeAudio ? "bottom" : "top") : SplitAudioMode;
        public bool SplitPrimaryAudio(AssemblyPlan plan) => (EffectiveSplitAudioMode == "bottom" || EffectiveSplitAudioMode == "alternate" && plan.SplitAudioBottom) == plan.SplitSwapped;
        public bool PrimaryNativeAudio = true, SecondaryNativeAudio = true;
        public double PrimaryNativeVolume = 1, SecondaryNativeVolume = 1;
        public bool NativeAudioLeveled;
        public List<AssemblyAudioLevel> NativeAudioLevels = new List<AssemblyAudioLevel>();
        public double VoicePitchSemitones;
        /// <summary>Доля первой половины (верх/слева), 0.25–0.75.</summary>
        public double SplitRatio = .5;
        public double SplitPrimaryZoom = 1, SplitSecondaryZoom = 1;
        public double SplitSoftness = .08, VoiceoverVolume = 1;
        public WarmupSubtitleStyle SubtitleStyle = new WarmupSubtitleStyle { Enabled = false };
        internal AssemblyTemplate WithVideoSubtitleStyle(Random random) {
            var copy = (AssemblyTemplate)MemberwiseClone();
            copy.SubtitleStyle = SubtitleRandomSettings.ForVideo(SubtitleStyle ?? new WarmupSubtitleStyle { Enabled = false }, random);
            return copy;
        }
        public List<AssemblyCaption> Captions = new List<AssemblyCaption>();
        public double TransitionSeconds { get { return SceneTimingVersion>0?Math.Round(TransitionDuration*Fps,MidpointRounding.AwayFromZero)/Fps:TransitionDuration; } }
        public double Duration { get { return Scenes.Sum(s => s.Duration)+(SceneTimingVersion>0 && Transition!="cut"?Math.Max(0,Scenes.Count-1)*TransitionSeconds:0); } }
        public string Resolve(string path) {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Materials, path));
            return PathUtil.Normalize(full);
        }
        public string ResolveAudio(string source) {
            if (string.IsNullOrWhiteSpace(source)) return "";
            string path = Resolve(source);
            if (File.Exists(path)) return path;
            return AssemblyFiles.Pool(path, AssemblyFiles.MusicExtensions).FirstOrDefault() ?? "";
        }
        public static AssemblyTemplate Defaults() {
            var t = new AssemblyTemplate { PackStudioVersion = 1, ObjectStudioVersion = 1, SceneTimingVersion=1,Transition="random", LoopShortVideos = true, AllowVideoReuse = true, Music = "Музыка" };
            for (int i = 1; i <= 4; i++) {
                string folder = Path.Combine("Сцены", "Сцена-" + i.ToString("00"));
                t.Scenes.Add(new AssemblyScene { Name = "Сцена " + i, Folder = folder, Duration = 5, Layers = new List<AssemblyLayer> {
                    new AssemblyLayer { Name = "Объект 1", Source = Path.Combine(folder, "Изображения"), Y = .35, Height = .3 }
                }});
            }
            return t;
        }
        public string BackgroundVideoSource(AssemblyScene scene) => ContinuousVideo || scene == null || scene.VideoMode != "own" ? Videos : scene.Videos;
        public bool HasBackgroundVideo(AssemblyScene scene) => AssemblyFiles.Pool(Resolve(BackgroundVideoSource(scene)),AssemblyFiles.VideoExtensions).Count>0;
        public string VideoSource(AssemblyScene scene) {
            string source = scene == null || scene.VideoMode == "shared" ? Videos
                : scene.VideoMode == "own" ? scene.Videos : string.IsNullOrWhiteSpace(scene.Videos) ? Videos : scene.Videos;
            if (AssemblyFiles.Pool(Resolve(source),AssemblyFiles.VideoExtensions).Count > 0) return source;
            return scene?.Layers.FirstOrDefault(l => l.Enabled && !l.IsText
                && AssemblyFiles.Pool(Resolve(l.Source),AssemblyFiles.VideoExtensions).Count > 0)?.Source ?? source;
        }
        public List<string> SplitVideoPaths() {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ContinuousVideo) {
                foreach (var p in AssemblyFiles.Pool(Resolve(Videos), AssemblyFiles.VideoExtensions)) paths.Add(p);
            } else foreach (var scene in Scenes) {
                foreach (var p in AssemblyFiles.Pool(Resolve(VideoSource(scene)), AssemblyFiles.VideoExtensions)) paths.Add(p);
            }
            foreach (var p in AssemblyFiles.Pool(Resolve(SecondaryVideos), AssemblyFiles.VideoExtensions)) paths.Add(p);
            return paths.ToList();
        }
        public int SplitPoolFileCount() {
            if (SplitLayout == "none") return 0;
            if (!SplitSharedPool) {
                int primary = ContinuousVideo
                    ? AssemblyFiles.Pool(Resolve(Videos), AssemblyFiles.VideoExtensions).Count
                    : Scenes.Sum(s => AssemblyFiles.Pool(Resolve(VideoSource(s)), AssemblyFiles.VideoExtensions).Count);
                return primary + AssemblyFiles.Pool(Resolve(SecondaryVideos), AssemblyFiles.VideoExtensions).Count;
            }
            return SplitVideoPaths().Count;
        }
        public string[] PictureExtensions { get { return PackStudioVersion > 0 ? AssemblyFiles.PictureExtensions : AssemblyFiles.ImageExtensions; } }
        public bool HasPreviewScene => Scenes != null && Scenes.Count > 0 && Scenes[0].IsPreview;
        public bool UsesVideoDuration(AssemblyScene scene) => scene.IsPreview || scene.DurationByVideo || DurationByVideo && scene == Scenes.FirstOrDefault(s => !s.IsPreview);
        public bool HasVideoDuration => Scenes.Any(UsesVideoDuration);
        public void MigrateVideoDuration() {
            if (!DurationByVideo) return;
            var main = Scenes.FirstOrDefault(s => !s.IsPreview);
            if (main != null) main.DurationByVideo = true;
            DurationByVideo = false;
        }
        public void Validate() {
            BackgroundAudioPitch.Validate(-12,12,"Высота голоса фонового видео");
            foreach(var layer in Scenes.SelectMany(s=>s.Layers))layer.AudioPitch.Validate(-12,12,"Высота голоса пачки");
            if (Combination == null) Combination = new AssemblyCombinationSettings();
            AssemblySpeechPlan.NormalizeSceneVoiceModes(this);
            if (Version != 1) throw new Exception("Неизвестная версия шаблона.");
            if (string.IsNullOrWhiteSpace(Id) || Id.Length > 100 || Id.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new Exception("Некорректный ID шаблона.");
            if (Scenes == null || Scenes.Count > 12) throw new Exception("В студии допустимо до 12 сцен.");
            foreach (var scene in Scenes) if (scene.IsPreview && Scenes.IndexOf(scene) != 0) throw new Exception("Сцена превью должна идти первой. Перемести её стрелками «←».");
            if (Scenes.Count(s => s.IsPreview) > 1) throw new Exception("Допустима только одна сцена превью.");
            if (Width < 180 || Width > 2160 || Height < 180 || Height > 3840 || Width % 2 != 0 || Height % 2 != 0) throw new Exception("Размер кадра должен быть чётным, от 180 до 2160 × 3840.");
            if (Fps < 15 || Fps > 60 || Count < 1) throw new Exception("FPS: 15–60. Количество роликов должно быть положительным.");
            Check(MusicVolume, 0, 2, "Громкость музыки"); Check(TransitionDuration, 0, 2, "Длительность перехода");
            if (RenderMode != "auto" && RenderMode != "cpu") throw new Exception("Выбери автоматическую сборку или сборку на процессоре.");
            TitleBank = AssemblyOutputTags.NormalizeBank(TitleBank);
            HistoryScope = AssemblyHistoryScope.Normalize(HistoryScope);
            if (!Transitions.Contains(Transition)) throw new Exception("Неизвестный переход.");
            if (SplitLayout != "none" && SplitLayout != "horizontal" && SplitLayout != "vertical") throw new Exception("Неизвестный шаблон двух видео.");
            Check(BaseVideoX, double.MinValue, double.MaxValue, "Видео · X"); Check(BaseVideoY, double.MinValue, double.MaxValue, "Видео · Y");
            Check(BaseVideoWidth, .02, double.MaxValue, "Видео · ширина"); Check(BaseVideoHeight, .02, double.MaxValue, "Видео · высота");
            Check(SplitAudioVolume, 0, 2, "Громкость звука двух экранов");
            if (SplitAudioMode != "" && SplitAudioMode != "top" && SplitAudioMode != "bottom" && SplitAudioMode != "alternate") throw new Exception("Неизвестный режим звука двух экранов.");
            Check(SplitSoftness, 0, .5, "Мягкость границы"); Check(SplitRatio, .25, .75, "Доля первой половины");
            Check(SplitPrimaryZoom, .85, 1.15, "Масштаб первой половины"); Check(SplitSecondaryZoom, .85, 1.15, "Масштаб второй половины");
            Check(VoiceoverVolume, 0, 2, "Громкость озвучки");
            Check(VoicePitchSemitones, -12, 12, "Тон голоса");
            Check(PrimaryNativeVolume, 0, 2, "Родной звук первой пачки");
            Check(SecondaryNativeVolume, 0, 2, "Родной звук второй пачки");
            if (GlobalLayers == null) GlobalLayers = new List<AssemblyLayer>();
            if (Captions == null) Captions = new List<AssemblyCaption>();
            if (SubtitleStyle == null) SubtitleStyle = new WarmupSubtitleStyle { Enabled = false };
            foreach (var caption in Captions) {
                Check(caption.Start, 0, 86400, "Начало субтитра"); Check(caption.End, 0, 86400, "Конец субтитра");
                if (caption.End <= caption.Start) throw new Exception("Конец субтитра должен быть позже начала.");
            }
            foreach (var l in GlobalLayers.Where(l => l.Enabled)) {
                if (!l.IsText && string.IsNullOrWhiteSpace(l.Source)) throw new Exception("Укажи изображения для слоя на весь ролик.");
                Check(l.X, double.MinValue, double.MaxValue, "X"); Check(l.Y, double.MinValue, double.MaxValue, "Y"); Check(l.Width, .02, double.MaxValue, "Ширина"); Check(l.Height, .02, double.MaxValue, "Высота");
                Check(l.Opacity, 0, 1, "Непрозрачность");
                ValidateAppearance(l);
                Check(l.Start, 0, 86400, "Появление"); Check(l.End, 0, 86400, "Исчезновение");
                if (l.End != 0 && l.End <= l.Start) throw new Exception("Время исчезновения должно быть позже появления.");
                if (l.Fixed && l.Unique) throw new Exception("Фиксированное изображение не может одновременно быть без повторов.");
            }
            foreach (var scene in Scenes) {
                if (scene.VideoMode != "" && scene.VideoMode != "shared" && scene.VideoMode != "own") throw new Exception("Неизвестный источник видео сцены.");
                if (scene.VoiceMode != "" && scene.VoiceMode != "shared" && scene.VoiceMode != "own" && scene.VoiceMode != "none") throw new Exception("Неизвестный режим озвучки сцены.");
                if (scene.VoiceMode == "own" && string.IsNullOrWhiteSpace(scene.Voiceover)) throw new Exception(scene.Name + ": добавьте свои файлы озвучки.");
                if (scene.VideoMode == "own" && string.IsNullOrWhiteSpace(scene.Videos)) throw new Exception("Укажи папку своих видео для сцены.");
                Check(scene.Duration, .5, 60, "Длительность сцены");
                Check(scene.NativeAudioVolume, -1, 2, "Звук видео сцены");
                Check(scene.BedMusicVolume, -1, 2, "Фоновая музыка сцены");
                Check(scene.MusicVolume, 0, 2, "Отдельное аудио сцены");
                if (!UsesVideoDuration(scene) && Transition != "cut" && TransitionDuration >= scene.Duration / 2) throw new Exception("Переход должен быть короче половины каждой сцены.");
                if (scene.Layers == null || scene.Layers.Count > 8) throw new Exception("Допустимо до 8 изображений в сцене.");
                foreach (var l in scene.Layers.Where(l => l.Enabled)) {
                    if (!l.IsText && string.IsNullOrWhiteSpace(l.Source)) throw new Exception(scene.Name + ": укажите источник для «" + l.Name + "».");
                    Check(l.X, double.MinValue, double.MaxValue, "X"); Check(l.Y, double.MinValue, double.MaxValue, "Y"); Check(l.Width, .02, double.MaxValue, "Ширина"); Check(l.Height, .02, double.MaxValue, "Высота");
                    ValidateAppearance(l);
                    Check(l.Opacity, 0, 1, "Непрозрачность"); Check(l.Start, 0, UsesVideoDuration(scene) ? 86400 : scene.Duration, "Появление");
                    Check(l.End, 0, UsesVideoDuration(scene) ? 86400 : scene.Duration, "Исчезновение");
                    if ((!UsesVideoDuration(scene) || l.End != 0) && l.Start >= (l.End == 0 ? scene.Duration : l.End)) throw new Exception("Время исчезновения должно быть позже появления.");
                    Check(l.FontSize, 12, 240, "Размер шрифта"); Check(l.Radius, 0, 200, "Скругление"); Check(l.Padding, 0, 150, "Отступ текста");
                    if (l.Fixed && l.Unique) throw new Exception("Фиксированное изображение не может одновременно быть без повторов.");
                }
            }
        }
        static void ValidateAppearance(AssemblyLayer layer) {
            Check(layer.CornerRadius, 0, double.MaxValue, "Скругление углов");
            if (layer.IsText) {
                Check(layer.FontSize, 12, 240, "Размер шрифта");
                Check(layer.TextOutlineWidth, 0, 10, "Обводка текста");
                Check(layer.TextPlateOpacity, 0, 100, "Плотность плашки");
                Check(layer.TextLineSpacing, 60, 150, "Межстрочный интервал");
                System.Drawing.ColorTranslator.FromHtml(layer.TextPlateColor);
                System.Drawing.ColorTranslator.FromHtml(layer.TextColor);
                System.Drawing.ColorTranslator.FromHtml(layer.TextOutlineColor);
            }
            if (!Transitions.Contains(layer.AppearanceTransition)) throw new Exception("Неизвестный переход появления изображения.");
            Check(layer.AppearanceDuration, .05, 2, "Длительность появления");
            if (layer.PlaybackMode != "once" && layer.PlaybackMode != "continuous" && layer.PlaybackMode != "repeat") throw new Exception("Неизвестный режим анимации объекта.");
            Check(layer.RepeatPause, 0, 86400, "Пауза повтора");
            Check(layer.RepeatInterval, 0, 86400, "Период запуска");
            Check(layer.PhotoDuration, .05, 86400, "Время показа фото");
            if (layer.ExitTransition != "cut" && layer.ExitTransition != "fade") throw new Exception("Неизвестный эффект исчезновения объекта.");
            Check(layer.ExitDuration, .05, 2, "Длительность исчезновения");
        }
        internal static void Check(double value, double min, double max, string label) {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max) throw new Exception(label + ": значение вне допустимых границ.");
        }
        public static readonly string[] Transitions = { "random", "cut", "fade", "fadeblack", "fadewhite", "slideleft", "slideright", "smoothleft", "hblur", "zoomin", "zoomsoft", "zoomout", "pull", "swipe" };
    }
    public static class AssemblyFiles {
        public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" };
        public static readonly string[] MusicExtensions = { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac" };
        public static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".mov", ".webm", ".mp4" };
        public static readonly string[] BackgroundExtensions = PictureExtensions.Concat(VideoExtensions).Distinct().ToArray();
        public static readonly string[] ImageExtensions = PictureExtensions.Concat(new[] { ".txt" }).ToArray();
        public static List<string> Pool(string path, string[] extensions) {
            var items = File.Exists(path) ? new[] { path } : Directory.Exists(path) ? Directory.GetFiles(path) : new string[0];
            return items.Select(PathUtil.Normalize).Where(p => extensions.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static string Hash(string path) {
            // Normalize text so that renaming a file or changing line endings does not reuse a headline.
            using (var sha = SHA256.Create()) {
                if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Trim())));
                using (var stream = File.OpenRead(path)) return Hex(sha.ComputeHash(stream));
            }
        }
        public static string HashText(string text) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        public static string SafeImportDestination(string folder, string sourcePath, int maxPathLen = 259) {
            folder = PathUtil.Normalize(folder);
            sourcePath = PathUtil.Normalize(sourcePath);
            string ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
            string stem = SanitizeImportStem(Path.GetFileNameWithoutExtension(sourcePath));
            string destination = FitImportPath(folder, stem, ext, "", maxPathLen);
            if (!File.Exists(destination)) return destination;
            for (int n = 2; n < 1000; n++) {
                destination = FitImportPath(folder, stem, ext, " (" + n + ")", maxPathLen);
                if (!File.Exists(destination)) return destination;
            }
            return FitImportPath(folder, "import-" + Guid.NewGuid().ToString("N").Substring(0, 12), ext, "", maxPathLen);
        }
        static string SanitizeImportStem(string stem) {
            stem = (stem ?? "").Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) stem = stem.Replace(ch, ' ');
            stem = Regex.Replace(stem, @"\s+", " ").Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(stem) ? "file" : stem;
        }
        static string FitImportPath(string folder, string stem, string ext, string suffix, int maxPathLen) {
            string name = stem + suffix + ext;
            string dest = Path.Combine(folder, name);
            while (dest.Length > maxPathLen && stem.Length > 8) {
                stem = stem.Substring(0, stem.Length - 4).TrimEnd('.', ' ');
                name = stem + suffix + ext;
                dest = Path.Combine(folder, name);
            }
            if (dest.Length > maxPathLen) {
                name = "import-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ext;
                dest = Path.Combine(folder, name);
            }
            return dest;
        }
        public static T Load<T>(string path) {
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }
        public static void Save<T>(string path, T value) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    new XmlSerializer(typeof(T)).Serialize(stream, value); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public static class AssemblyWorkspace {
        public static void ClearBackgroundVideos(AssemblyTemplate t) {
            t.Videos=Path.Combine(t.Materials,"Видео-"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(t.Videos);
            t.Backgrounds="";t.BackgroundForeground=false;t.SecondaryVideos="";t.SplitLayout="none";t.ContinuousVideo=false;
            foreach(var scene in t.Scenes) {
                scene.Videos="";scene.VideoMode="shared";scene.VideoPacks.Clear();scene.ContinuationVideos="";
            }
        }
        public static void Prepare(AssemblyTemplate t) {
            var oldBackgrounds=AssemblyFiles.Pool(t.Resolve(t.Backgrounds),AssemblyFiles.BackgroundExtensions);
            var backgroundVideos=oldBackgrounds.Where(p=>AssemblyFiles.VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
            if(backgroundVideos.Count>0) {
                var existing=new HashSet<string>(AssemblyFiles.Pool(t.Resolve(t.Videos),AssemblyFiles.VideoExtensions).Select(AssemblyFiles.Hash));
                var missing=backgroundVideos.Where(p=>existing.Add(AssemblyFiles.Hash(p))).ToArray();
                if(missing.Length>0)Import(t,ref t.Videos,"Фоновое-видео",missing,AssemblyFiles.VideoExtensions,false);
                var pictures=oldBackgrounds.Except(backgroundVideos).ToArray();
                t.Backgrounds="";
                if(pictures.Length>0)Import(t,ref t.Backgrounds,"Рамки",pictures,AssemblyFiles.BackgroundExtensions,false);
            }
            if (t.OutputMetadata == null) t.OutputMetadata = AssemblyOutputMetadata.Default();
            bool migrate = t.PackStudioVersion == 0, objects=t.ObjectStudioVersion==0;
            if (migrate) { t.PackStudioVersion = 1; t.LoopShortVideos = true; t.AllowVideoReuse = true; }
            Directory.CreateDirectory(t.Materials);
            for (int i = 0; i < t.Scenes.Count; i++) {
                var scene = t.Scenes[i];
                if (string.IsNullOrWhiteSpace(scene.Folder)) scene.Folder = NewFolder(t, i + 1);
                Directory.CreateDirectory(t.Resolve(scene.Folder));
                Directory.CreateDirectory(t.Resolve(Path.Combine(scene.Folder, "Видео")));
                if (scene.VideoMode == "") scene.VideoMode = !string.IsNullOrWhiteSpace(scene.Videos) && AssemblyFiles.Pool(t.Resolve(scene.Videos),AssemblyFiles.VideoExtensions).Count > 0 ? "own" : "shared";
                if (scene.Layers.Count == 0 && (migrate || objects)) scene.Layers.Add(new AssemblyLayer { Name = "Объект 1", Source = Path.Combine(scene.Folder, "Изображения") });
                for (int j = 0; j < scene.Layers.Count; j++) {
                    var l = scene.Layers[j];
                    if (migrate || objects) l.Name = "Объект " + (j + 1);
                    if (string.IsNullOrWhiteSpace(l.ObjectId)) l.ObjectId=Guid.NewGuid().ToString("N");
                    if (l.IsText) continue;
                    if (string.IsNullOrWhiteSpace(l.Source)) l.Source = Path.Combine(scene.Folder, j == 0 ? "Изображения" : "Пачка-" + (j + 1));
                    MakeFolder(t.Resolve(l.Source));
                    var files=AssemblyFiles.Pool(t.Resolve(l.Source),t.PictureExtensions);
                    if (files.Count>0) {
                        l.PlacementConfigured=true;
                        if (!l.ModeExplicit) {
                            l.Fixed=files.Select(AssemblyFiles.Hash).Distinct().Count()==1;
                            l.FixedAssetHash=l.Fixed?AssemblyFiles.Hash(files[0]):"";
                        }
                    }
                    if (objects) l.ModeExplicit=l.Fixed || l.Unique;
                }
            }
            if(t.SceneTimingVersion==0){if(t.Transition!="cut")t.Transition="random";t.SceneTimingVersion=1;}
            t.RandomVideoStart=false;
            t.ObjectStudioVersion=1;
            if (!AssemblyProfile.IsCustom(t.HistoryScope)) {
                if (string.IsNullOrWhiteSpace(t.Videos)) t.Videos = "Видео";
                if (string.IsNullOrWhiteSpace(t.Music)) t.Music = "Музыка";
            }
            if (!string.IsNullOrWhiteSpace(t.Videos)) MakeFolder(t.Resolve(t.Videos));
            if (!string.IsNullOrWhiteSpace(t.Music)) MakeFolder(t.Resolve(t.Music));
        }
        static void MakeFolder(string path) { if (!File.Exists(path)) Directory.CreateDirectory(path); }
        public static string NewFolder(AssemblyTemplate t, int index) {
            string path = Path.Combine("Сцены", "Сцена-" + index.ToString("00"));
            if (Directory.Exists(t.Resolve(path)) || t.Scenes.Any(s => s.Folder == path)) path += "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            return path;
        }
        public static AssemblyScene Insert(AssemblyTemplate t, int index) {
            if (t.Scenes.Count >= 12) throw new Exception("Можно добавить до 12 сцен.");
            var s = new AssemblyScene { Name = "Сцена " + (t.Scenes.Count + 1), Folder = NewFolder(t, t.Scenes.Count + 1), Duration = 5 };
            s.Layers.Add(new AssemblyLayer { Name = "Объект 1", Source = Path.Combine(s.Folder, "Изображения") });
            t.Scenes.Insert(Math.Max(0, Math.Min(index, t.Scenes.Count)), s); Prepare(t); return s;
        }
        public static int Import(AssemblyTemplate t, ref string source, string fallback, string[] files, string[] extensions, bool replace) {
            string oldSource = source, path = t.Resolve(source);
            bool newFolder = replace || string.IsNullOrWhiteSpace(source) || File.Exists(path);
            if (newFolder) path = t.Resolve(fallback + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(path); int added = 0;
            if (newFolder && !replace && File.Exists(t.Resolve(oldSource))) {
                string oldFile = t.Resolve(oldSource);
                string oldCopy = AssemblyFiles.SafeImportDestination(path, oldFile);
                if (!Path.GetFullPath(oldFile).Equals(Path.GetFullPath(oldCopy), StringComparison.OrdinalIgnoreCase)) File.Copy(oldFile, oldCopy);
            }
            try {
                foreach (string file in PathUtil.NormalizeAll(files).Where(PathUtil.Exists).Where(p => extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))) {
                    string destination = AssemblyFiles.SafeImportDestination(path, file);
                    if (Path.GetFullPath(file).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(destination) && AssemblyFiles.Hash(file) == AssemblyFiles.Hash(destination)) continue;
                    PathUtil.CopyFile(file, destination); added++;
                }
                if (newFolder && added > 0) source = path; // Keep the old pack intact on cancel/failure.
                return added;
            } catch { source = oldSource; throw; }
        }
    }
    public static class AssemblyTimeline {
        public static readonly string[] RandomTransitions={"fade","zoomsoft","zoomout","pull","swipe"};
        public static double MainStart(AssemblyTemplate t, AssemblyPlan p) => p.Scenes.Count > 1 && p.Scenes[0].Scene.IsPreview
            ? SceneSeconds(p.Scenes[0]) + (t.SceneTimingVersion > 0 && t.Transition != "cut" ? t.TransitionSeconds : 0) : 0;
        public static double SceneSeconds(AssemblyScenePlan s) => s.ContentSeconds>0?s.ContentSeconds:s.Scene.Duration;
        public static double ContentDuration(AssemblyTemplate t,AssemblyScenePlan s) => s.ContentSeconds>0?s.ContentSeconds:(t.SceneTimingVersion>0?s.Scene.Duration:s.RenderDuration);
        public static bool LoopInput(AssemblyTemplate t,AssemblyScenePlan s) {
            if (s.VideoSegments != null && s.VideoSegments.Count > 0) return false;
            if (t.UsesVideoDuration(s.Scene) && s.Layers.Any(l => l.Layer.Enabled && !l.Layer.IsText && l.Asset != null && AssemblyOverlayMedia.IsAnimated(l.Asset.Path)))
                return s.Video.Duration+.001<ContentDuration(t,s);
            if (t.SceneTimingVersion > 0 && s.ContentSeconds > 0 && s.Scene.VideoMode == "own") return false;
            return t.LoopShortVideos && !(t.SceneTimingVersion>0 && s.Scene.VideoMode=="own") && s.Video.Duration+.001<ContentDuration(t,s);
        }
        public static double SourceStart(AssemblyTemplate t,AssemblyScenePlan s) => t.SceneTimingVersion>0?0:s.VideoStart;
        public static string Transition(AssemblyTemplate t,AssemblyScenePlan s) => string.IsNullOrEmpty(s.TransitionOut)?(t.Transition=="random"?"fade":t.Transition):s.TransitionOut;
        public static string Padding(AssemblyTemplate t,AssemblyScenePlan s) => t.SceneTimingVersion>0?"tpad=start_mode=clone:start_duration="+Core.N(s.IncomingPad)+":stop_mode=clone:stop_duration="+Core.N(s.OutgoingPad)+",":"";
        public static void ApplyVideoDuration(AssemblyTemplate t, AssemblyPlan p) {
            if (!t.HasVideoDuration) return;
            for (int i = 0; i < p.Scenes.Count; i++) {
                var scene = p.Scenes[i];
                if (t.UsesVideoDuration(scene.Scene)) {
                    var videoObject = scene.Layers.FirstOrDefault(l => l.Layer.Enabled && !l.Layer.IsText
                        && l.Asset != null && AssemblyOverlayMedia.IsAnimated(l.Asset.Path));
                    double duration;
                    if (videoObject != null) {
                        duration = videoObject.Layer.Start + AssemblyOverlayMedia.Duration(videoObject.Asset.Path);
                        if (videoObject.Layer.End > 0) duration = Math.Min(duration, videoObject.Layer.End);
                    } else duration = !scene.Scene.IsPreview && t.SplitLayout != "none" && !t.SplitPrimaryAudio(p) && p.SecondaryVideo != null
                        ? p.SecondaryVideo.Duration : scene.Video.Duration;
                    scene.ContentSeconds = duration;
                    scene.RenderDuration = duration;
                    scene.VideoStart = 0;
                    if (p.Scenes.Count == 1) { scene.IncomingPad = 0; scene.OutgoingPad = 0; }
                    scene.RenderDuration = scene.ContentSeconds + scene.IncomingPad + scene.OutgoingPad;
                }
            }
            double roll = 0;
            for (int i = 0; i < p.Scenes.Count; i++) {
                roll += SceneSeconds(p.Scenes[i]);
                if (i < p.Scenes.Count - 1 && t.SceneTimingVersion > 0 && t.Transition != "cut") roll += t.TransitionSeconds;
            }
            p.RenderDuration = roll;
        }
        public static void Complete(AssemblyTemplate t,AssemblyPlan p,Random random) {
            string previous="";
            for(int i=0;i<p.Scenes.Count;i++) {
                var s=p.Scenes[i];s.VideoStart=t.SceneTimingVersion>0?0:t.RandomVideoStart?random.NextDouble()*Math.Max(0,s.Video.Duration-s.RenderDuration):0;
                string effect=t.Transition;
                if(effect=="random") {var choices=RandomTransitions.Where(k=>k!=previous).ToArray();effect=choices[random.Next(choices.Length)];}
                s.TransitionOut=i<p.Scenes.Count-1?effect:"cut";previous=effect;
                if(t.SceneTimingVersion>0) {
                    s.IncomingPad=i>0 && t.Transition!="cut"?t.TransitionSeconds:0;
                    s.OutgoingPad=i<p.Scenes.Count-1 && t.Transition!="cut"?t.TransitionSeconds:0;
                    s.RenderDuration=s.Scene.Duration+s.IncomingPad+s.OutgoingPad;
                }
            }
        }
    }
    public class AssemblyHistory {
        public int LedgerVersion;
        public List<string> UsedImages = new List<string>(), Combinations = new List<string>();
        public List<string> Outputs = new List<string>();
        public List<AssemblyHistoryRecord> Records = new List<AssemblyHistoryRecord>();
    }
    public class AssemblyAsset { public string Path, Hash; }
    public class AssemblyVideo { public string Path, Hash, SourcePath; public double Duration; public bool HasAudio; }
    public class AssemblyLayerPlan { public AssemblyLayer Layer; public AssemblyAsset Asset; public string Raster; }
    public class AssemblyScenePlan {
        public AssemblyScene Scene;
        public AssemblyVideo Video;
        public List<AssemblySceneVideoSegment> VideoSegments = new List<AssemblySceneVideoSegment>();
        public double PrimaryVideoEnd;
        public double VideoStart, RenderDuration, IncomingPad, OutgoingPad;
        public string TransitionOut="";
        public string Music = "", MusicHash = "";
        public string Voiceover = "", VoiceoverHash = "";
        public double VoiceSeconds, ContentSeconds;
        public List<AssemblyLayerPlan> Layers = new List<AssemblyLayerPlan>();
    }
    public class AssemblyPlan {
        public List<AssemblyScenePlan> Scenes = new List<AssemblyScenePlan>();
        public List<string> UniqueImages = new List<string>();
        public string Signature, Music, MusicHash;
        public string Voiceover = "", VoiceoverHash = "";
        /// <summary>Effective duration for the selected voice file(s), with one second of visual tail.</summary>
        public double RenderDuration;
        public AssemblyAsset Background;
        public AssemblyVideo SecondaryVideo;
        /// <summary>Вторая половина экрана показывает основное видео сцены, первая — второе видео.</summary>
        public bool SplitSwapped;
        public bool SplitAudioBottom;
        public List<AssemblyLayerPlan> GlobalLayers = new List<AssemblyLayerPlan>();
        public List<WarmupCaption> Captions = new List<WarmupCaption>();
        public double RollDuration(AssemblyTemplate t) => RenderDuration > 0 ? RenderDuration : t.Duration;
    }
    public class AssemblyPlanBatch {
        public List<AssemblyPlan> Plans = new List<AssemblyPlan>();
        public string Limit = "";
    }
    public static class AssemblyPlanner {
        public static AssemblyPlanBatch Create(AssemblyTemplate t, IList<AssemblyVideo> videos, AssemblyHistory history, Random random, int count, AssemblyMixTracker mix = null, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) {
            t.Validate();
            return new AssemblyInventory(t,videos,history).CreateBatch(random,count,mix ?? new AssemblyMixTracker(),ct);
        }
    }
}
