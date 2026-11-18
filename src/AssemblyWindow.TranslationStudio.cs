using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        public bool HasTranslationWork => cancellation != null;
        public void SaveTranslationWorkspace() => Save();

        public AssemblyTemplate TranslationTemplateSnapshot() {
            Save();
            return Store.Clone(template);
        }

        // Call on the UI thread, after attaching the embedded studio to its parent.
        public async Task LoadTranslationSourceAsync(string video, CancellationToken ct) {
            if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
            video = PathUtil.Normalize(Path.GetFullPath(video));
            var media = Core.VideoInfo(await Core.Probe(probe, video, ct));
            ct.ThrowIfCancellationRequested();
            bool existingSource = template.Scenes.Count == 1 && template.Scenes[0].VideoMode == "own"
                && template.Scenes[0].DurationByVideo && !string.IsNullOrWhiteSpace(template.Scenes[0].Videos);
            ConfigureTranslationSource(template, video, media, preserveFormat: existingSource);
            previewCancellation?.Cancel(); ++previewVersion;
            timingCancellation?.Cancel(); ++timingVersion;
            timingAnchorDuration = media.VideoDuration;
            previewSpeechCaptions = null;
            previewImages.Clear();
            saveTimer.Stop();
            BindTemplateValues(); BuildScenes();
            ShowSceneWorkspace(); globalEditing = false; sceneIndex = 0; layerIndex = -1;
            loading = true;
            try { duration.Value = (decimal)template.Scenes[0].Duration; }
            finally { loading = false; }
            canvas.SetBackground(null); LoadLayer(); RefreshChips(); RefreshCanvas(); RefreshInfo();
            Save();
            Directory.CreateDirectory(previewFolder);
            string frame = Path.Combine(previewFolder, "translation-" + Guid.NewGuid().ToString("N") + ".png");
            try {
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-i", video, "-map", "0:" + media.VideoIndex,
                    "-frames:v", "1", "-vf", "scale=" + template.Width + ":" + template.Height + ",setsar=1", "-threads", "1", frame }, ct, null, 60);
                ct.ThrowIfCancellationRequested();
                using (var image = Image.FromFile(frame)) canvas.SetBackground(new Bitmap(image));
                status.Text = "Оригинал · " + Path.GetFileName(video) + " · " + media.VideoDuration.ToString("0.##", Core.Inv) + " с";
            } finally { if (File.Exists(frame)) File.Delete(frame); }
        }

        // Scene.Duration is an editor value limited to 60 seconds. DurationByVideo
        // makes AssemblyTimeline use the actual full input, including longer files.
        internal static void ConfigureTranslationSource(AssemblyTemplate t, string video, Media media, bool preserveFormat = false) {
            var scene = t.Scenes.FirstOrDefault() ?? new AssemblyScene { Name = "Оригинал" };
            if (t.GlobalLayers == null) t.GlobalLayers = new List<AssemblyLayer>();
            foreach (var extra in t.Scenes.Skip(1)) t.GlobalLayers.AddRange(extra.Layers);
            t.Scenes = new List<AssemblyScene> { scene };
            t.PackStudioVersion = t.ObjectStudioVersion = t.SceneTimingVersion = 1;
            t.Videos = video; t.Count = 1;
            if (!preserveFormat) {
                t.Width = media.Width; t.Height = media.Height;
                string[] rate = (media.Fps ?? "").Split('/');
                double fps = rate.Length == 2 ? Core.Parse(rate[0]) / Core.Parse(rate[1]) : Core.Parse(media.Fps);
                t.Fps = (int)Math.Round(fps, MidpointRounding.AwayFromZero);
            }
            t.Transition = "cut"; t.RandomVideoStart = t.LoopShortVideos = t.ContinuousVideo = false;
            t.DurationByVideo = false;
            t.SplitLayout = "none"; t.SecondaryVideos = ""; t.SplitSharedPool = false;
            t.PrimaryNativeAudio = true; t.PrimaryNativeVolume = 1;
            t.NativeAudioLeveled = false; t.NativeAudioLevels = new List<AssemblyAudioLevel>();
            t.VoicePitchSemitones = 0;
            t.Voiceover = ""; t.Speech = new AssemblySpeechSettings(); t.SpeechLines = new List<AssemblySpeechLine>();
            t.Captions = new List<AssemblyCaption>();
            scene.IsPreview = false; scene.Name = "Оригинал";
            scene.VideoMode = "own"; scene.Videos = video;
            scene.ContinuationVideos = ""; scene.VideoPacks = new List<AssemblySceneVideoPack>(); scene.MixVideos = false;
            scene.DurationByVideo = true;
            scene.Duration = Math.Max(.5, Math.Min(60, media.VideoDuration));
            scene.NativeAudioVolume = 1; scene.VoiceMode = "none"; scene.Voiceover = "";
            // Music, images, backgrounds, placements, insertions and output effects
            // belong to the workspace; loading another source must not reset them.
        }

        public static async Task<string> RenderTranslationStudioAsync(AssemblyTemplate settings, string translatedVideo,
            string output, string work, string ffmpeg, string probe, CancellationToken ct, Action<string> progress) {
            var t = Store.Clone(settings);
            translatedVideo = PathUtil.Normalize(Path.GetFullPath(translatedVideo));
            output = Path.GetFullPath(output);
            if (string.Equals(translatedVideo, output, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Результат студии должен отличаться от исходного переведённого видео.", nameof(output));
            var media = Core.VideoInfo(await Core.Probe(probe, translatedVideo, ct).ConfigureAwait(false));
            ConfigureTranslationSource(t, translatedVideo, media, preserveFormat: true);
            // Keep the workspace subtitle editor/style for the translation engine;
            // this pass receives captions already burned into translatedVideo.
            t.SubtitleStyle = new WarmupSubtitleStyle { Enabled = false };
            // AssemblyProcessing defaults include speed changes. Never let the
            // final pass move burned captions relative to the translated speech.
            t.OutputProcessing = Store.Clone(t.OutputProcessing ?? AssemblyProcessing.Defaults());
            t.OutputProcessing.Speed.Enabled = false;
            t.OutputProcessing.Trim.Enabled = t.OutputProcessing.Tempo.Enabled = false;
            t.Validate();
            Directory.CreateDirectory(work); Directory.CreateDirectory(Path.GetDirectoryName(output));
            var video = new AssemblyVideo { Path = translatedVideo, Duration = media.VideoDuration, HasAudio = media.HasAudio, Hash = AssemblyFiles.Hash(translatedVideo) };
            var random = new Random();
            var batch = AssemblyPlanner.Create(t, new[] { video }, new AssemblyHistory(), random, 1, ct: ct);
            if (batch.Plans.Count != 1) throw new Exception("Не удалось подготовить перевод для студии: " + batch.Limit);
            // No catalog: EnsureSegments must not substitute, concatenate or loop
            // any source. The plan contains exactly one full translated input.
            var plan = batch.Plans[0];
            await AssemblyEngine.Render(t, plan, ffmpeg, probe, output, work, AssemblyRaster.Save, ct, progress).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            progress?.Invoke("Обработка изображения и звука…");
            await AssemblyProcessing.Apply(t, ffmpeg, probe, output, random, ct).ConfigureAwait(false);
            string title = AssemblyOutputTags.PickTitle(t.TitleBank, random, null);
            await AssemblyOutputTags.Apply(ffmpeg, probe, output, title, t.OutputMetadata, random, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return output;
        }
    }
}
