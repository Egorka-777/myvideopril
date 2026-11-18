using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace VideoBatch {
    public static class AssemblyTranslationStudioTests {
        public static bool Run(string ffmpeg, string probe) {
            var ct = CancellationToken.None;
            string root = Path.Combine(Path.GetTempPath(), "vb-translation-studio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                string input = Path.Combine(root, "translated.mp4"), ad = Path.Combine(root, "ad.mp4"), image = Path.Combine(root, "overlay.png");
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=25:d=2", "-f", "lavfi", "-i", "color=c=green:s=320x180:r=25:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-f", "lavfi", "-i", "sine=frequency=660:duration=2", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0,drawbox=x=80:y=140:w=160:h=20:color=white:t=fill[v];[2:a][3:a]concat=n=2:v=0:a=1[a]", "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-c:a", "aac", input }, ct, null, 60).GetAwaiter().GetResult();
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=320x180:r=25:d=1", "-f", "lavfi", "-i", "sine=frequency=880:duration=1", "-c:v", "libx264", "-c:a", "aac", ad }, ct, null, 60).GetAwaiter().GetResult();
                using (var bitmap = new Bitmap(40, 40)) { using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.Yellow); bitmap.Save(image, ImageFormat.Png); }
                var t = new AssemblyTemplate { Width = 320, Height = 180, Fps = 25, Materials = root, RenderMode = "cpu", OutputProcessing = new ProcessingSettings(), OutputMetadata = new AssemblyOutputMetadata { DeviceMetadata = false, CaptureDate = false, Software = "" } };
                var layer = new AssemblyLayer { Source = image, X = 0, Y = 0, Width = .125, Height = 40d / 180, Fixed = true, AppearanceTransition = "cut" };
                t.Scenes.Add(new AssemblyScene()); t.Scenes[0].Layers.Add(layer);
                t.Insertions.Add(new AssemblyInsertion { Source = ad, Start = 2 });
                t.OutputProcessing.Speed.Enabled = true; t.OutputProcessing.Speed.Values = new Range(200, 200);
                t.OutputProcessing.Brightness.Enabled = true; t.OutputProcessing.Brightness.Values = new Range(0, 0);
                t.SubtitleStyle.Enabled = true; t.Voiceover = Path.Combine(root, "must-not-be-used.wav");
                t.TitleBank.Add("Translation Studio Smoke");
                string output = Path.Combine(root, "studio.mp4");
                AssemblyWindow.RenderTranslationStudioAsync(t, input, output, Path.Combine(root, "work"), ffmpeg, probe, ct, Console.WriteLine).GetAwaiter().GetResult();
                var media = Core.VideoInfo(Core.Probe(probe, output, ct).GetAwaiter().GetResult());
                Check(media.Width == 320 && media.Height == 180 && media.HasAudio && Math.Abs(media.VideoDuration - 5) < .12, "full duration + insertion, dimensions and audio");
                Check(t.SubtitleStyle.Enabled && t.OutputProcessing.Speed.Enabled && !string.IsNullOrEmpty(t.Voiceover), "render must not mutate caller settings");
                foreach (double time in new[] { .5, 2.5, 4.5 }) {
                    string frame = Path.Combine(root, "check.png");
                    Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-ss", Core.N(time), "-i", output, "-frames:v", "1", frame }, ct, null, 60).GetAwaiter().GetResult();
                    using (var bitmap = new Bitmap(frame)) {
                        Color center = bitmap.GetPixel(160, 90), corner = bitmap.GetPixel(20, 20), caption = bitmap.GetPixel(160, 150);
                        Check(time < 1 ? center.B > 180 : time < 3 ? center.R > 180 : center.G > 80 && center.B < 70, "original video resumes, without loop/reorder @ " + time);
                        if (time != 2.5) { Check(corner.R > 180 && corner.G > 180, "user overlay @ " + time); Check(caption.R > 200 && caption.G > 200 && caption.B > 200, "burned caption retained @ " + time); }
                    }
                }
                string pcm = Path.Combine(root, "check.pcm");
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", output, "-ac", "1", "-ar", "8000", "-f", "s16le", pcm }, ct, null, 60).GetAwaiter().GetResult();
                byte[] samples = File.ReadAllBytes(pcm);
                foreach (double time in new[] { .5, 2.5, 4.5 }) {
                    int crossings = 0;
                    for (int i = (int)(time * 8000) + 1; i < (int)((time + .2) * 8000); i++) if (BitConverter.ToInt16(samples, (i - 1) * 2) <= 0 && BitConverter.ToInt16(samples, i * 2) > 0) crossings++;
                    double expected = time < 1 ? 440 : time < 3 ? 880 : 660;
                    Check(Math.Abs(crossings / .2 - expected) < 25, "translated/ad audio sync @ " + time);
                }
                t.Width = 640; t.Height = 360; t.Fps = 30;
                string customOutput = Path.Combine(root, "custom-format.mp4");
                AssemblyWindow.RenderTranslationStudioAsync(t, input, customOutput, Path.Combine(root, "custom-work"), ffmpeg, probe, ct, Console.WriteLine).GetAwaiter().GetResult();
                var custom = Core.VideoInfo(Core.Probe(probe, customOutput, ct).GetAwaiter().GetResult());
                Check(custom.Width == 640 && custom.Height == 360 && custom.Fps == "30/1" && Math.Abs(custom.VideoDuration - 5) < .12, "user output format survives render");
                AssemblyWindow.ConfigureTranslationSource(t, input, new Media { Width = 320, Height = 180, Fps = "25/1", VideoDuration = 125, HasAudio = true }, preserveFormat: true);
                Check(t.Width == 640 && t.Height == 360 && t.Fps == 30, "existing workspace format survives source reload");
                Check(t.Scenes.Count == 1 && t.Scenes[0].DurationByVideo && t.Scenes[0].Duration == 60 && t.Scenes[0].Layers[0] == layer && t.Insertions.Count == 1 && t.OutputProcessing.Speed.Enabled && t.SubtitleStyle.Enabled, "long-source editor duration and workspace/subtitle-style preservation");
                var plan = new AssemblyPlan(); plan.Scenes.Add(new AssemblyScenePlan { Scene = t.Scenes[0], Video = new AssemblyVideo { Duration = 125 } });
                AssemblyTimeline.ApplyVideoDuration(t, plan);
                Check(plan.RenderDuration == 125 && !AssemblyTimeline.LoopInput(t, plan.Scenes[0]), "long source stays full-length without loops");
                Console.WriteLine("PASS: real FFmpeg full source + overlay + 1s ad; burned-caption pixels and 440/880/660Hz audio resume together; speed disabled; caller untouched; 125s source timing.");
                return true;
            } finally { Directory.Delete(root, true); }
        }
        static void Check(bool value, string message) { if (!value) throw new Exception("Translation studio: " + message); }
    }
}
