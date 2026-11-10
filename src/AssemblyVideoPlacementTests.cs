using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace VideoBatch {
    public static class AssemblyVideoPlacementTests {
        static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        static void Mouse(AssemblyCanvas canvas, string method, Point p) => typeof(AssemblyCanvas).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0) });
        static void Check(bool result, string message) { if (!result) throw new Exception(message); }
        public static bool Run(string video, string ffmpeg, string ffprobe) {
            string root = Path.Combine(Path.GetTempPath(), "assembly-video-placement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                var t = new AssemblyTemplate { Materials = root, Width = 360, Height = 640, Fps = 30, Transition = "cut", SceneTimingVersion = 1,
                    BaseVideoX = .1, BaseVideoY = .1, BaseVideoWidth = .6, BaseVideoHeight = .6, PrimaryNativeAudio = false, RenderMode = "cpu" };
                var source = new AssemblyScene { Duration = 1, VideoMode = "shared" }; t.Scenes.Add(source);
                var layer = new AssemblyLayer { X = .7, Y = .4, Width = .2, Height = .2 };
                var scene = new AssemblyScenePlan { Scene = source, Video = new AssemblyVideo { Path = video, Duration = 1 }, ContentSeconds = 1, RenderDuration = 1 };
                var plan = new AssemblyPlan { RenderDuration = 1 }; plan.Scenes.Add(scene);
                // Real FFmpeg one-pass render for ordinary, continuous and multi-segment video.
                for (int mode = 0; mode < 3; mode++) {
                    t.ContinuousVideo = mode == 1;
                    if (mode == 2) {
                        scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = scene.Video, Duration = .5 });
                        scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = scene.Video, Duration = .5 });
                    }
                    string output = Path.Combine(root, "render" + mode + ".mp4");
                    AssemblyRender.Run(t, plan, ffmpeg, ffprobe, output, Path.Combine(root, "work" + mode), AssemblyRaster.Save, CancellationToken.None, null).GetAwaiter().GetResult();
                    string png = Path.Combine(root, "frame" + mode + ".png");
                    Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", output, "-frames:v", "1", png }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                    using (var frame = new Bitmap(png)) { Check(frame.GetPixel(10, 10).R < 12 && frame.GetPixel(10, 10).B < 12, "Render has no black margins."); Check(frame.GetPixel(100, 150).B > 230, "Render video position differs."); }
                }
                scene.VideoSegments.Clear(); t.ContinuousVideo = false;
                source.Layers.Add(layer); scene.Layers.Add(new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = "" } });
                using (var canvas = new AssemblyCanvas { Size = new Size(376, 656) }) {
                    canvas.SetScene(t, scene, null);
                    var blue = new Bitmap(360, 640); using (var g = Graphics.FromImage(blue)) g.Clear(Color.Blue); canvas.SetBackground(blue);
                    using (var frame = canvas.SubtitleBackground()) { Check(frame.GetPixel(10, 10).B == 0, "Preview margin missing."); Check(frame.GetPixel(100, 150).B == 255, "Preview video missing."); }
                    double x = layer.X, y = layer.Y, width = layer.Width, height = layer.Height;
                    int changes = 0; canvas.BaseVideoPlacementChanged += () => changes++;
                    canvas.SelectBaseVideo();
                    Mouse(canvas, "OnMouseDown", new Point(260, 456)); // Bottom-right of the video, not the object.
                    Mouse(canvas, "OnMouseMove", new Point(224, 392)); Mouse(canvas, "OnMouseUp", new Point(224, 392));
                    Check(t.BaseVideoWidth < .6 && t.BaseVideoHeight < .6, "Base video did not resize.");
                    Mouse(canvas, "OnMouseDown", new Point(80, 110)); Mouse(canvas, "OnMouseMove", new Point(116, 174)); Mouse(canvas, "OnMouseUp", new Point(116, 174));
                    Check(t.BaseVideoX > .19 && t.BaseVideoY > .19 && changes == 2, "Base video did not move independently.");
                    Check(layer.X == x && layer.Y == y && layer.Width == width && layer.Height == height, "Base drag changed object placement.");
                    Mouse(canvas, "OnMouseDown", new Point(280, 300)); Mouse(canvas, "OnMouseMove", new Point(298, 300)); Mouse(canvas, "OnMouseUp", new Point(298, 300));
                    Check(layer.X > x, "Object no longer moves independently.");
                }
                source.Layers.Clear(); scene.Layers.Clear();
                t.BaseVideoX = .1; t.BaseVideoY = .1; t.BaseVideoWidth = .6; t.BaseVideoHeight = .6;
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.Ru);
                string xml = Path.Combine(root, "template.xml"); AssemblyFiles.Save(xml, t);
                var saved = AssemblyFiles.Load<AssemblyTemplate>(xml);
                saved.BaseVideoWidth = 1; AssemblyMarketProfiles.Apply(saved, AssemblyProfile.Ru);
                Check(saved.BaseVideoX == .1 && saved.BaseVideoWidth == .6, "Profile placement did not persist.");
                saved.SplitLayout = "horizontal";
                Check(AssemblyVideoPlacement.Filter(saved) == "" && AssemblyVideoPlacement.Bounds(saved).Width == 360, "Base placement changed split layout.");
                Check(new AssemblyTemplate().BaseVideoWidth == 1, "Legacy template defaults changed.");
                return true;
            } finally { Directory.Delete(root, true); }
        }
        public static bool RunEmbeddedPreview(string video) {
            Check(ToolsLocator.TryResolve(out _, out _, out string hint), hint);
            string previous = Store.Root, root = Path.Combine(Path.GetTempPath(), "assembly-embedded-preview-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(root); Store.Root = root;
                var t = AssemblyTemplate.Defaults(); t.Materials = Path.Combine(root, "materials"); t.Output = Path.Combine(root, "output"); t.Videos = video;
                AssemblyFiles.Save(Path.Combine(root, "assembly-template.xml"), t);
                using (var host = new Form { Width = 1180, Height = 790, ShowInTaskbar = false })
                using (var window = new AssemblyWindow(true, true)) {
                    host.Controls.Add(window); host.Show(); window.Show();
                    var canvas = (AssemblyCanvas)Field(window, "canvas");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 10000 && Field(canvas, "background") == null) { Application.DoEvents(); Thread.Sleep(20); }
                    Check(canvas.Visible && canvas.Width > 20 && canvas.Height > 20, "Embedded preview canvas not laid out.");
                    Check(Field(canvas, "background") != null, "Embedded base-video preview not loaded. Status: " + ((Label)Field(window, "status")).Text + "; Videos: " + ((AssemblyTemplate)Field(window, "template")).Videos);
                    typeof(AssemblyWindow).GetMethod("SelectBaseVideo", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                    var active = (AssemblyTemplate)Field(window, "template");
                    active.BaseVideoX = .1; active.BaseVideoY = .1; active.BaseVideoWidth = .7; active.BaseVideoHeight = .7;
                    window.Hide(); window.Show(); Application.DoEvents();
                    var persisted = AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(root, "assembly-template.xml"));
                    Check(persisted.MarketRu.BaseVideoX == .1 && persisted.MarketRu.BaseVideoWidth == .7, "Window did not save placement to the active profile.");
                    Check(Field(canvas, "background") != null, "Showing embedded editor cleared its working preview.");
                    window.Close(); host.Close(); Application.DoEvents();
                }
                return true;
            } finally { Store.Root = previous; if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
