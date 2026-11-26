using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class AssemblyAudioLevelingTests {
        public static bool Run(string ffmpeg, string ffprobe, string root) {
            string folder = Path.Combine(root, "audio-leveling-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try { RunAsync(ffmpeg, ffprobe, folder).GetAwaiter().GetResult(); return true; }
            finally { Directory.Delete(folder, true); }
        }
        public static bool RunObjectAudioGraphTests(string ffmpeg, string probe, string root) {
            string folder = Path.Combine(root, "object-audio-role-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                string selected = Path.Combine(folder, "selected.mp4"), candidate = Path.Combine(folder, "independent-candidate.mp4");
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=s=64x64:r=10:d=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", selected }, CancellationToken.None).GetAwaiter().GetResult();
                var t = new AssemblyTemplate { Materials = folder, Videos = "empty-background", NativeAudioLeveled = true,
                    PrimaryNativeAudio = true, PrimaryNativeVolume = 1, Width = 64, Height = 64, Fps = 10, Transition = "cut", SceneTimingVersion = 1 };
                var layer = new AssemblyLayer { Source = selected, AudioEnabled = true, AudioPitch = new ProcessingParameter(-3, -3) { Enabled = true }, X = 0, Y = 0, Width = 1, Height = 1 };
                var scene = new AssemblyScene { VideoMode = "shared", Duration = 1, Layers = new List<AssemblyLayer> { layer } };
                t.Scenes.Add(scene);
                var sp = new AssemblyScenePlan { Scene = scene, Video = new AssemblyVideo { Path = candidate, Duration = 1, HasAudio = true }, RenderDuration = 1,
                    Layers = new List<AssemblyLayerPlan> { new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = selected } } } };
                var p = new AssemblyPlan { Scenes = new List<AssemblyScenePlan> { sp } };
                string graph = Path.Combine(folder, "render.ffgraph"), output = Path.Combine(folder, "result.mp4");
                var args = AssemblyRender.Arguments(t, p, "libx264", graph, output, new AssemblyRenderContext());
                string text = File.ReadAllText(graph);
                Check(!args.Contains(candidate) && args.Count(x => x == selected) == 2, "Independent timing candidate became an extra picture/audio source");
                Check(text.Contains("asetrate=") && !text.Contains("dB:precision"), "Object pitch lost or native leveling applied to object audio");
                Core.Tool(ffmpeg, args, CancellationToken.None).GetAwaiter().GetResult();
                Check(Core.VideoInfo(Core.Probe(probe, output, CancellationToken.None).GetAwaiter().GetResult()).HasAudio, "Object-only render lost audio");
                layer.AudioEnabled = false;
                args = AssemblyRender.Arguments(t, p, "libx264", graph, output, new AssemblyRenderContext());
                Check(args.Contains("-an") && !args.Contains(candidate), "Muted object leaked timing candidate audio");
                layer.AudioEnabled = true; sp.Video.Path = selected;
                AssemblyRender.Arguments(t, p, "libx264", graph, output, new AssemblyRenderContext());
                Check(File.ReadAllText(graph).Split(new[] { "asetrate=" }, StringSplitOptions.None).Length == 2, "Same-path object sound duplicated");
                // Actual background video still requires the configured measurement; never silently use unity gain.
                sp.Layers.Clear(); t.Videos = selected;
                bool failed = false;
                try { AssemblyRender.Arguments(t, p, "libx264", graph, output, new AssemblyRenderContext()); }
                catch (Exception ex) { failed = ex.Message.Contains("не проверено по громкости"); }
                Check(failed, "Unmeasured real background silently bypassed leveling");
                var file = new FileInfo(selected);
                t.NativeAudioLevels.Add(new AssemblyAudioLevel { Path = selected, HasAudio = true, Loudness = -25, GainDb = 6, FileLength = file.Length, LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks });
                AssemblyRender.Arguments(t, p, "libx264", graph, output, new AssemblyRenderContext());
                Check(File.ReadAllText(graph).Contains("volume=6dB:precision=double"), "Measured background gain lost");
                t.NativeAudioLevels.Clear(); scene.IsPreview = true;
                AssemblyRender.Arguments(t, p, "libx264", graph, output, new AssemblyRenderContext());
                Check(!File.ReadAllText(graph).Contains("dB:precision"), "Preview required main-video leveling");
                Console.WriteLine("PASS: independent/same-path object audio, muted object, real 1-second render, strict background gain, preview exemption.");
                return true;
            } finally { Directory.Delete(folder, true); }
        }
        static void Check(bool ok, string message) { if (!ok) throw new Exception("Audio leveling regression: " + message); }
        static async Task RunAsync(string ffmpeg, string probe, string root) {
            var ct = CancellationToken.None;
            string loud = Path.Combine(root, "loud.mkv"), quiet = Path.Combine(root, "quiet.mkv"), silence = Path.Combine(root, "silence.mkv"),
                noAudio = Path.Combine(root, "no-audio.mkv"), longFile = Path.Combine(root, "long.mkv"), shortFile = Path.Combine(root, "short.mkv");
            await Generate(ffmpeg, loud, "sine=frequency=440:sample_rate=48000:duration=3", "volume=2", ct);
            await Generate(ffmpeg, quiet, "sine=frequency=440:sample_rate=48000:duration=3", "volume=0.5", ct);
            await Generate(ffmpeg, silence, "anullsrc=r=48000:cl=mono:d=3", "anull", ct);
            await Generate(ffmpeg, shortFile, "sine=frequency=440:sample_rate=48000:duration=1", "volume=2", ct);
            await Generate(ffmpeg, longFile, "aevalsrc=if(lt(t\\,20)\\,0.25\\,0.9)*sin(2*PI*440*t):s=48000:d=24", "anull", ct);
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", loud, "-c:v", "copy", "-an", noAudio }, ct);
            var t = new AssemblyTemplate { Materials = root, Videos = loud, SceneTimingVersion = 1, Transition = "cut", RenderMode = "cpu", Width = 180, Height = 320, Fps = 20 };
            t.Scenes.Add(new AssemblyScene { Videos = quiet, VideoMode = "own", Duration = 3, ContinuationVideos = silence,
                VideoPacks = new List<AssemblySceneVideoPack> { new AssemblySceneVideoPack { Source = noAudio }, new AssemblySceneVideoPack { Source = shortFile }, new AssemblySceneVideoPack { Source = longFile } } });
            t.Scenes.Add(new AssemblyScene { Videos = loud, VideoMode = "own", Duration = 3 });
            var levels = await AssemblyAudioLeveling.Measure(t, ffmpeg, probe, ct, null);
            Check(levels.Count == 6 && levels.All(l => string.IsNullOrEmpty(l.Error)), "Main/continuation pack enumeration or real measurement failed: " + string.Join("; ", levels.Select(l => l.Error)));
            var loudLevel = levels.Single(l => l.Path == loud); var quietLevel = levels.Single(l => l.Path == quiet);
            Check(Math.Abs(loudLevel.Loudness - quietLevel.Loudness - 12.04) < .2, "Known 12 dB source difference not measured");
            Check(levels.Single(l => l.Path == silence).IsSilent && !levels.Single(l => l.Path == noAudio).HasAudio, "Silence/no-audio distinction missing");
            Check(Math.Abs(levels.Single(l => l.Path == shortFile).Loudness - loudLevel.Loudness) < .2, "Short file not fully measured");
            Check(Math.Abs(levels.Single(l => l.Path == longFile).Loudness - loudLevel.Loudness) < .2, "Measurement included audio after first 20 seconds");
            AssemblyAudioLeveling.Align(t, levels);
            double gain = AssemblyAudioLeveling.GainFor(t, quiet);
            Check(Math.Abs(gain - 12.04) < .2 && t.NativeAudioLevels.Where(l => l.IsSilent || !l.HasAudio).All(l => l.GainDb == 0), "Wrong target gains or silence amplified");
            string adjusted = Path.Combine(root, "adjusted.wav"), reduced = Path.Combine(root, "reduced.wav"), clipped = Path.Combine(root, "limited.wav");
            await Native(ffmpeg, quiet, adjusted, 1, gain, ct);
            await Native(ffmpeg, quiet, reduced, .5, gain, ct);
            await Native(ffmpeg, loud, clipped, 1, 24, ct);
            double loudRms = await Rms(ffmpeg, loud, root, "loud", ct), quietRms = await Rms(ffmpeg, quiet, root, "quiet", ct), adjustedRms = await Rms(ffmpeg, adjusted, root, "adjusted", ct), reducedRms = await Rms(ffmpeg, reduced, root, "reduced", ct);
            Check(Math.Abs(Db(adjustedRms / loudRms)) < .2 && Math.Abs(Db(reducedRms / adjustedRms) + 6.02) < .1, "Output leveling or common-volume scaling incorrect");
            double[] clippedSamples = await Samples(ffmpeg, clipped, root, "clipped", ct);
            Check(clippedSamples.Max(x => Math.Abs(x)) <= .951 && clippedSamples.Max(x => Math.Abs(x)) >= .9, "Boosted native clipping not limited");
            Check(Math.Abs(Core.AudioDuration(await Core.Probe(probe, clipped, ct)) - 3) < .01, "Limiter changed audio timing");
            Console.WriteLine("Measured loud={0:0.00}, quiet={1:0.00} LUFS; gain={2:0.00} dB; output gain={3:0.00} dB; slider delta={4:0.00} dB; limited peak={5:0.000}", loudLevel.Loudness, quietLevel.Loudness, gain, Db(adjustedRms / quietRms), Db(reducedRms / adjustedRms), clippedSamples.Max(x => Math.Abs(x)));

            // Actual main render graph, including music. Native gain/limiter must remain before the mixer.
            string music = Path.Combine(root, "music.wav");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=2000:sample_rate=48000:duration=3", "-c:a", "pcm_f32le", music }, ct);
            t.Scenes = new List<AssemblyScene> { new AssemblyScene { Duration = 3 } }; t.ContinuousVideo = true; t.Videos = quiet; t.MusicVolume = .4;
            var video = new AssemblyVideo { Path = quiet, Duration = 3, HasAudio = true };
            var plan = new AssemblyPlan { Music = music, Scenes = new List<AssemblyScenePlan> { new AssemblyScenePlan { Scene = t.Scenes[0], Video = video, RenderDuration = 3 } } };
            string mixed = Path.Combine(root, "mixed.mp4"), baseline = Path.Combine(root, "baseline.mp4"), graph = Path.Combine(root, "render.ffgraph");
            await Core.Tool(ffmpeg, AssemblyRender.Arguments(t, plan, "libx264", graph, mixed, new AssemblyRenderContext()), ct);
            string text = File.ReadAllText(graph);
            Check(text.Contains("dB:precision=double") && text.Contains("alimiter=") && text.IndexOf("alimiter=", StringComparison.Ordinal) < text.IndexOf("amix=", StringComparison.Ordinal), "Main render integration missing");
            Check(text.Contains("volume=0.4,afade") && !text.Substring(text.IndexOf("[music]", StringComparison.Ordinal) + 7).Contains("alimiter="), "Music was subjected to native limiter");
            var secondaryArgs = new List<string>(); var secondaryGraph = new List<string>(); var secondaryAudio = new List<string>(); int secondaryIndex = 0;
            AssemblyRender.AppendNativeAudio(video, 0, 3, 0, false, .7, 3, secondaryArgs, ref secondaryIndex, secondaryGraph, secondaryAudio, 2);
            Check(!secondaryGraph[0].Contains("alimiter") && !secondaryGraph[0].Contains("dB:") && secondaryGraph[0].Contains("asetrate=") && secondaryGraph[0].Contains("volume=0.7"), "Secondary gain or pitch behavior changed");
            t.NativeAudioLeveled = false;
            await Core.Tool(ffmpeg, AssemblyRender.Arguments(t, plan, "libx264", graph, baseline, new AssemblyRenderContext()), ct);
            double musicAfter = await BandRms(ffmpeg, mixed, root, "music-after", 2000, ct), musicBefore = await BandRms(ffmpeg, baseline, root, "music-before", 2000, ct);
            double nativeAfter = await BandRms(ffmpeg, mixed, root, "native-after", 440, ct), nativeBefore = await BandRms(ffmpeg, baseline, root, "native-before", 440, ct);
            Check(Math.Abs(Db(musicAfter / musicBefore)) < .3 && Math.Abs(Db(nativeAfter / nativeBefore) - gain) < .4, "Mixed output changed music gain or missed native gain");
            Console.WriteLine("Real mixed render: native delta={0:0.00} dB, music delta={1:0.00} dB", Db(nativeAfter / nativeBefore), Db(musicAfter / musicBefore));

            t.NativeAudioLeveled = true;
            AssemblyMarketProfiles.EnsureSeeded(t);
            AssemblyMarketProfiles.Capture(t, AssemblyProfile.Ru);
            t.NativeAudioLevels[0].GainDb = 99;
            AssemblyMarketProfiles.Apply(t, AssemblyProfile.Ru);
            Check(t.NativeAudioLeveled && t.NativeAudioLevels[0].GainDb != 99, "Profile capture aliases gain list");
            var custom = AssemblyMarketProfiles.AddEmpty(t, "Empty audio profile");
            AssemblyMarketProfiles.Apply(t, custom.Id);
            Check(!t.NativeAudioLeveled && t.NativeAudioLevels.Count == 0, "Empty profile inherited leveling");
            string xml = Path.Combine(root, "template.xml"); AssemblyFiles.Save(xml, t);
            t = AssemblyFiles.Load<AssemblyTemplate>(xml); AssemblyMarketProfiles.Apply(t, AssemblyProfile.Ru);
            Check(t.NativeAudioLeveled && Math.Abs(AssemblyAudioLeveling.GainFor(t, quiet) - gain) < .01, "XML/profile gain roundtrip failed");
            Check(!new AssemblyTemplate().NativeAudioLeveled && new AssemblyTemplate().NativeAudioLevels.Count == 0, "Legacy defaults not disabled");
            string proxy = Path.Combine(root, "proxy.mp4"); File.Copy(quiet, proxy);
            File.WriteAllLines(proxy + ".source.txt", new[] { quiet, File.GetLastWriteTimeUtc(quiet).Ticks.ToString(Core.Inv) });
            Check(Math.Abs(AssemblyAudioLeveling.GainFor(t, proxy) - gain) < .01, "Existing HDR proxy source marker not resolved");
            string corrupt = Path.Combine(root, "corrupt.mp4"); File.WriteAllText(corrupt, "not a video");
            var badTemplate = new AssemblyTemplate { Materials = root, Videos = corrupt, ContinuousVideo = true };
            var badLevels = await AssemblyAudioLeveling.Measure(badTemplate, ffmpeg, probe, ct, null);
            Check(badLevels.Count == 1 && !string.IsNullOrEmpty(badLevels[0].Error), "Decoder error silently lost");
            var error = new AssemblyAudioLevel { Path = "bad", Error = "decode failed" };
            bool failed = false; try { AssemblyAudioLeveling.Align(t, levels.Concat(new[] { error }).ToList()); } catch (Exception) { failed = true; }
            Check(failed, "Partial measurement incorrectly aligned");
            failed = false; try { AssemblyAudioLeveling.Align(t, levels.Where(l => l.IsSilent || !l.HasAudio).ToList()); } catch (Exception) { failed = true; }
            Check(failed, "All-silent batch incorrectly aligned");
            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel(); failed = false;
                try { await AssemblyAudioLeveling.Measure(t, ffmpeg, probe, cancellation.Token, null); } catch (OperationCanceledException) { failed = true; }
                Check(failed, "Cancellation swallowed");
            }
            File.SetLastWriteTimeUtc(quiet, File.GetLastWriteTimeUtc(quiet).AddSeconds(2));
            failed = false; try { AssemblyAudioLeveling.GainFor(t, quiet); } catch (Exception) { failed = true; }
            Check(failed, "Changed source used stale gain");
            Console.WriteLine("PASS: first20/short/silent/no-audio, native+music actual render, clipping/timing, slider, profile/XML/copy, stale/error/cancellation.");
        }
        static Task Generate(string ffmpeg, string output, string audio, string filter, CancellationToken ct) {
            return Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=s=180x320:r=20:d=24", "-f", "lavfi", "-i", audio,
                "-af", filter, "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_s16le", "-shortest", output }, ct);
        }
        static Task Native(string ffmpeg, string inputFile, string output, double volume, double gain, CancellationToken ct) {
            var args = new List<string> { "-v", "error", "-y" }; var graph = new List<string>(); var audio = new List<string>(); int input = 0;
            AssemblyRender.AppendNativeAudio(new AssemblyVideo { Path = inputFile, HasAudio = true }, 0, 3, 0, false, volume, 3, args, ref input, graph, audio, 0, gain);
            args.AddRange(new[] { "-filter_complex", string.Join(";", graph), "-map", "[" + audio[0] + "]", "-c:a", "pcm_f32le", output });
            return Core.Tool(ffmpeg, args, ct);
        }
        static double Db(double value) => 20 * Math.Log10(value);
        static async Task<double[]> Samples(string ffmpeg, string input, string root, string name, CancellationToken ct, string filter = "anull") {
            string pcm = Path.Combine(root, name + ".f32");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", input, "-vn", "-af", filter, "-ac", "1", "-ar", "48000", "-f", "f32le", pcm }, ct);
            byte[] bytes = File.ReadAllBytes(pcm); var result = new double[bytes.Length / 4];
            for (int i = 0; i < result.Length; i++) result[i] = BitConverter.ToSingle(bytes, i * 4);
            return result;
        }
        static async Task<double> Rms(string ffmpeg, string input, string root, string name, CancellationToken ct) {
            var samples = await Samples(ffmpeg, input, root, name, ct); return Math.Sqrt(samples.Average(x => x * x));
        }
        static async Task<double> BandRms(string ffmpeg, string input, string root, string name, double hz, CancellationToken ct) {
            var samples = await Samples(ffmpeg, input, root, name, ct, "bandpass=f=" + Core.N(hz) + ":width_type=h:w=30,atrim=start=0.5:end=2.5");
            return Math.Sqrt(samples.Average(x => x * x));
        }
    }
}
