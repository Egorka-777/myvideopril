using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class AssemblyVisualEstimateTests {
        static void Check(bool value, string message) { if (!value) throw new Exception("Visual estimate: " + message); }
        public static bool Run(string ffmpeg, string ffprobe, string work) {
            string root = Path.Combine(work, "visual-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { RunAsync(ffmpeg, ffprobe, root).GetAwaiter().GetResult(); return true; }
            finally { Directory.Delete(root, true); }
        }
        static async Task RunAsync(string ffmpeg, string probe, string root) {
            string video = Path.Combine(root, "black.mp4"), picture = Path.Combine(root, "white.png");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=200x200:r=20:d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", video }, CancellationToken.None);
            using (var image = new Bitmap(100, 100)) { using (var g = Graphics.FromImage(image)) g.Clear(Color.White); image.Save(picture, ImageFormat.Png); }
            var t = new AssemblyTemplate { Materials = root, Videos = video, Width = 200, Height = 200, Fps = 20, SceneTimingVersion = 1, Transition = "cut", PackStudioVersion = 1 };
            t.Scenes.Add(new AssemblyScene { Duration = 3 });
            async Task<AssemblyVisualEstimateReport> Analyze() {
                var r = await AssemblyVisualEstimate.Analyze(t, ffmpeg, probe, root, CancellationToken.None);
                Check(r.Completed && r.DifferencePercent.HasValue && r.MinimumPercent.HasValue, r.Summary + "\n" + r.Details);
                Check(!Directory.GetDirectories(root, "visual-estimate-*").Any(), "Temporary directory leaked");
                return r;
            }
            var unchanged = await Analyze(); Check(unchanged.DifferencePercent < .01, "Unchanged was not zero");
            var layer = new AssemblyLayer { Source = picture, X = 0, Y = 0, Width = .4, Height = 1, PreserveAspectRatio = false, AppearanceTransition = "cut", PlaybackMode = "continuous" };
            t.Scenes[0].Layers.Add(layer);
            var opaque = await Analyze(); Check(Math.Abs(opaque.DifferencePercent.Value - 40) < 1, "Opaque 40% coverage: " + opaque.DifferencePercent);
            layer.Opacity = .05;
            var translucent = await Analyze(); Check(translucent.DifferencePercent < 1, "Low opacity falsely counted as opaque");
            string transparentPicture = Path.Combine(root, "transparent.png");
            using (var image = new Bitmap(100, 100, PixelFormat.Format32bppArgb)) {
                using (var g = Graphics.FromImage(image)) g.Clear(Color.FromArgb(12, 255, 255, 255)); image.Save(transparentPicture, ImageFormat.Png);
            }
            layer.Opacity = 1; layer.Source = transparentPicture;
            Check((await Analyze()).DifferencePercent < 1, "Intrinsic PNG alpha falsely counted as opaque");
            layer.Source = picture; layer.End = .3;
            var brief = await Analyze(); Check(Math.Abs(brief.DifferencePercent.Value - 4) < .2 && brief.MinimumPercent < .01, "Brief overlay not time weighted: " + brief.DifferencePercent);
            layer.End = 0;
            t.Scenes.Add(new AssemblyScene { Duration = 3 });
            var two = await Analyze(); Check(Math.Abs(two.DifferencePercent.Value - 20) < .5 && two.MinimumPercent < .01, "Scene weighting/minimum");
            t.Scenes.RemoveAt(1); t.Scenes[0].Layers.Clear();
            t.Backgrounds = picture; t.BaseVideoWidth = .6; t.BaseVideoHeight = 1;
            var background = await Analyze(); Check(Math.Abs(background.DifferencePercent.Value - 40) < 1, "Background/placement area " + background.DifferencePercent);
            t.Backgrounds = ""; t.BaseVideoWidth = 1;
            string pattern = Path.Combine(root, "pattern.mp4");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=200x200:r=20:d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", pattern }, CancellationToken.None);
            t.Videos = pattern; Check((await Analyze()).DifferencePercent < .01, "Matched animated source frames are not identical");
            t.BaseVideoWidth = 1.4; t.BaseVideoHeight = 1.4; t.BaseVideoX = -.2; t.BaseVideoY = -.2;
            var crop = await Analyze(); Check(crop.DifferencePercent > 10, "Real crop had no measurable effect");
            t.BaseVideoWidth = 1; t.BaseVideoHeight = 1; t.BaseVideoX = 0; t.BaseVideoY = 0;
            layer.Source = pattern; t.Scenes[0].Layers.Add(layer);
            var unsupported = await Analyze(); Check(unsupported.DifferencePercent < .01 && unsupported.Details.Contains("не учтены"), "Unsupported dynamic asset received points");
            t.SplitLayout = "vertical";
            var split = await AssemblyVisualEstimate.Analyze(t, ffmpeg, probe, root, CancellationToken.None);
            Check(!split.Completed && !split.DifferencePercent.HasValue, "Unsupported split claimed success"); t.SplitLayout = "none";
            var before = Store.Clone(t);
            await Analyze(); Check(t.Scenes[0].ContinuationVideos == before.Scenes[0].ContinuationVideos && layer.Source == pattern, "Source settings mutated");
            using (var cancel = new CancellationTokenSource()) {
                cancel.Cancel(); bool cancelled = false;
                try { await AssemblyVisualEstimate.Analyze(t, ffmpeg, probe, root, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancellation swallowed");
            }
            using (var cancel = new CancellationTokenSource()) {
                cancel.CancelAfter(30); bool cancelled = false;
                try { await AssemblyVisualEstimate.Analyze(t, ffmpeg, probe, root, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !Directory.GetDirectories(root, "visual-estimate-*").Any(), "In-flight cancellation/cleanup failed");
            }
            t.Videos = "missing.mp4";
            var missing = await AssemblyVisualEstimate.Analyze(t, ffmpeg, probe, root, CancellationToken.None);
            Check(!missing.Completed && !missing.DifferencePercent.HasValue, "Missing source hidden");
            Console.WriteLine("Visual estimate actual FFmpeg fixtures: unchanged={0:0.00}, opaque={1:0.00}, low-alpha={2:0.00}, brief={3:0.00}, two-scenes={4:0.00}, background={5:0.00}, crop={6:0.00}",
                unchanged.DifferencePercent, opaque.DifferencePercent, translucent.DifferencePercent, brief.DifferencePercent, two.DifferencePercent, background.DifferencePercent, crop.DifferencePercent);
        }
    }
}
