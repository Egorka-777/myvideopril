using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    // Media objects use the actual joined timeline, including ads, without pausing.
    public static class CreativeMediaRender {
        internal sealed class AppendResult {
            internal string VideoLabel, AudioLabel;
            internal int NextInput;
        }
        // Labels are bare filter labels (without brackets); inputOffset is the next free input.
        // Only the planner's selected additions are prepared. The caller's native audio
        // stays in the existing graph and is never reopened, pitched or attenuated here.
        internal static async Task<AppendResult> AppendAsync(CreativeProject project, string ffmpeg, string probe,
            string work, double duration, string videoLabel, string audioLabel, List<string> args, List<string> graph,
            int inputOffset, CancellationToken ct, Action<string> progress, CreativeAudio audio) {
            ct.ThrowIfCancellationRequested();
            var result = new AppendResult { VideoLabel = videoLabel, AudioLabel = audioLabel, NextInput = inputOffset };
            var layers = project.Preset.MediaLayers;
            if (layers == null || !layers.Any(layer => layer.Enabled)) return result;
            var preset = project.Preset;
            string source = Path.GetFullPath(project.Source);
            var scene = new AssemblyScene {
                Name = "Креатив", Videos = source, VideoMode = "own", VoiceMode = "none",
                Duration = duration, NativeAudioVolume = 1, BedMusicVolume = 0,
                Layers = new List<AssemblyLayer>(layers)
            };
            var template = new AssemblyTemplate {
                PackStudioVersion = 1, ObjectStudioVersion = 1, SceneTimingVersion = 1,
                Materials = Directory.GetCurrentDirectory(), Videos = source,
                Width = preset.Width, Height = preset.Height, Fps = preset.Fps,
                Transition = "cut", TransitionDuration = 0, Count = 1,
                PrimaryNativeAudio = true, PrimaryNativeVolume = 1, MusicVolume = 0,
                ExportSubtitleText = false, Scenes = new List<AssemblyScene> { scene }
            };
            // The source is a planner identity/timing candidate only, not an FFmpeg input.
            var video = new AssemblyVideo {
                Path = source, SourcePath = source, Duration = duration, Hash = AssemblyFiles.Hash(source)
            };
            var random = new Random();
            var batch = new AssemblyInventory(template, new[] { video }, new AssemblyHistory())
                .CreateBatch(random, 1, new AssemblyMixTracker(), ct);
            if (batch.Plans.Count == 0) throw new Exception("Медиа креатива: " + batch.Limit);
            var selected = batch.Plans[0].Scenes[0];
            if (audio != null) {
                foreach (var layer in selected.Layers.Where(l => l.Layer.Enabled && l.Layer.AudioEnabled && !l.Layer.IsText && l.Asset != null)) {
                    ct.ThrowIfCancellationRequested();
                    string path = await audio.Prepare(layer.Asset.Path).ConfigureAwait(false);
                    layer.Asset = new AssemblyAsset { Path = path, Hash = layer.Asset.Hash };
                }
            }
            progress?.Invoke("Креатив: медиа на весь ролик…");
            string prefix = "creativeMedia" + inputOffset + "_";
            string rasterWork = Path.Combine(work, prefix + "rasters");
            Directory.CreateDirectory(rasterWork);
            int raster = 0;
            foreach (var layer in selected.Layers) {
                ct.ThrowIfCancellationRequested();
                if (AssemblyOverlayMedia.IsAnimated(layer.Layer.IsText ? "" : layer.Asset.Path)) continue;
                layer.Raster = Path.Combine(rasterWork, (raster++) + ".png");
                AssemblyRaster.Save(template, layer, layer.Raster);
            }
            int input = inputOffset;
            result.VideoLabel = AssemblyRender.AppendSceneLayers(template, selected, videoLabel, prefix + "overlay", "yuv420", args, ref input, graph);
            var mix = new List<string>();
            if (!string.IsNullOrEmpty(audioLabel)) mix.Add(audioLabel);
            AssemblyRender.AppendSceneLayerAudio(template, selected, 0, duration, random, args, ref input, graph, mix);
            if (mix.Count > (string.IsNullOrEmpty(audioLabel) ? 0 : 1)) {
                result.AudioLabel = prefix + "audio";
                graph.Add(string.Join("", mix.Select(label => "[" + label + "]")) + "amix=inputs=" + mix.Count
                    + ":duration=longest:normalize=0,atrim=duration=" + Core.N(duration) + "[" + result.AudioLabel + "]");
            }
            result.NextInput = input;
            return result;
        }
        public static async Task ApplyAsync(CreativeProject project, string ffmpeg, string probe,
            string inputFile, string outputFile, string work, CancellationToken ct, Action<string> progress = null) {
            ct.ThrowIfCancellationRequested();
            var layers = project.Preset.MediaLayers;
            if (layers == null || !layers.Any(layer => layer.Enabled)) {
                File.Copy(inputFile, outputFile, true);
                return;
            }
            inputFile = Path.GetFullPath(inputFile);
            var media = Core.VideoInfo(await Core.Probe(probe, inputFile, ct).ConfigureAwait(false));
            var preset = project.Preset;
            var scene = new AssemblyScene {
                Name = "Креатив", Videos = inputFile, VideoMode = "own", VoiceMode = "none",
                Duration = media.VideoDuration, NativeAudioVolume = 1, BedMusicVolume = 0,
                Layers = new List<AssemblyLayer>(layers)
            };
            var template = new AssemblyTemplate {
                PackStudioVersion = 1, ObjectStudioVersion = 1, SceneTimingVersion = 1,
                Materials = Directory.GetCurrentDirectory(), Videos = inputFile,
                Width = preset.Width, Height = preset.Height, Fps = preset.Fps,
                Transition = "cut", TransitionDuration = 0, Count = 1,
                PrimaryNativeAudio = true, PrimaryNativeVolume = 1, MusicVolume = 0,
                ExportSubtitleText = false, Scenes = new List<AssemblyScene> { scene }
            };
            var video = new AssemblyVideo {
                Path = inputFile, SourcePath = inputFile, Duration = media.VideoDuration,
                HasAudio = media.HasAudio, Hash = AssemblyFiles.Hash(inputFile)
            };
            // Inventory is the real Assembly planner (file/pool, content deduplication,
            // FixedAssetHash, Unique and optional empty packs). Do not apply Assembly's
            // editor-only scene limits (60 seconds / 8 objects / 15 FPS) to a joined creative.
            // Scene rather than GlobalLayers is intentional: Assembly routes object audio
            // through scene layers, while its global visual-only layers have no audio mix.
            var random = new Random();
            var batch = new AssemblyInventory(template, new[] { video }, new AssemblyHistory())
                .CreateBatch(random, 1, new AssemblyMixTracker(), ct);
            if (batch.Plans.Count == 0) throw new Exception("Медиа креатива: " + batch.Limit);
            progress?.Invoke("Креатив: медиа на весь ролик…");
            await AssemblyEngine.Render(template, batch.Plans[0], ffmpeg, probe, outputFile,
                Path.Combine(work, "creative-media"), AssemblyRaster.Save, ct, progress,
                segmentRandom: random).ConfigureAwait(false);
        }
    }
}
