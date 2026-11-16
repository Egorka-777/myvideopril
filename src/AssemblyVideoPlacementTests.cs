using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace VideoBatch {
    public static class AssemblyVideoPlacementTests {
        static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        static void Mouse(AssemblyCanvas canvas, string method, Point p) => typeof(AssemblyCanvas).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0) });
        static void Check(bool result, string message) { if (!result) throw new Exception(message); }
        public static bool RunFreePlacement(string ffmpeg, string ffprobe) {
            string root = Path.Combine(Path.GetTempPath(), "vb-free-placement-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                string input = Path.Combine(root, "source.mp4"), image = Path.Combine(root, "object.png");
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=160x240:r=25:d=1", "-c:v", "libx264", input }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                using (var b = new Bitmap(80,80)) { using (var g = Graphics.FromImage(b)) g.Clear(Color.Red); b.Save(image); }
                var t = new AssemblyTemplate { Materials = root, Width = 160, Height = 240, Fps = 25, Transition = "cut", SceneTimingVersion = 1, RenderMode = "cpu", PrimaryNativeAudio = false,
                    BaseVideoX = -.25, BaseVideoY = -.1, BaseVideoWidth = 1.5, BaseVideoHeight = 1.2, BaseVideoPreserveAspectRatio = false };
                var source = new AssemblyScene { Duration = 1 }; t.Scenes.Add(source);
                var layer = new AssemblyLayer { Source = image, X = -.25, Y = .3, Width = .5, Height = .2, PreserveAspectRatio = false }; source.Layers.Add(layer);
                var scene = new AssemblyScenePlan { Scene = source, Video = new AssemblyVideo { Path = input, Duration = 1 }, ContentSeconds = 1, RenderDuration = 1 };
                scene.Layers.Add(new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = image } });
                var plan = new AssemblyPlan { RenderDuration = 1 }; plan.Scenes.Add(scene);
                t.Width = 180; t.Validate(); t.Width = 160;
                var bounds = AssemblyRaster.LayerImageBounds(t, layer, new Size(80,80)); Check(bounds.Width == 80 && bounds.Height == 48, "Unlocked image did not stretch");
                string animated = Path.Combine(root, "object.mp4");
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-loop", "1", "-i", image, "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", animated }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                for (int mode = 0; mode < 4; mode++) {
                    t.ContinuousVideo = mode == 1;
                    if (mode == 3) { scene.VideoSegments.Clear(); scene.Layers[0].Asset.Path = animated; }
                    if (mode == 2) { scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = scene.Video, Duration = .5 }); scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = scene.Video, Duration = .5 }); }
                    string output = Path.Combine(root, "render" + mode + ".mp4"), png = Path.Combine(root, "frame" + mode + ".png");
                    AssemblyRender.Run(t, plan, ffmpeg, ffprobe, output, Path.Combine(root, "work" + mode), AssemblyRaster.Save, CancellationToken.None, null).GetAwaiter().GetResult();
                    Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", output, "-frames:v", "1", png }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                    using (var b = new Bitmap(png)) { Check(b.Size == new Size(160,240), "Crop output size"); Check(b.GetPixel(10,80).R > 220 && b.GetPixel(60,80).B > 220 && b.GetPixel(10,130).B > 220, "Off-canvas stretched image crop"); }
                }
                using (var canvas = new AssemblyCanvas { Size = new Size(176,256) }) {
                    canvas.SetScene(t,scene,null);
                    Mouse(canvas,"OnMouseDown",new Point(18,88)); Mouse(canvas,"OnMouseMove",new Point(-14,64)); Mouse(canvas,"OnMouseUp",new Point(-14,64));
                    Check(layer.X < -.4 && layer.Y < .3, "Layer drag still clamped");
                    layer.X = .1; layer.Y = .3; layer.Width = .5; layer.Height = .2;
                    Mouse(canvas,"OnMouseDown",new Point(103,127)); Mouse(canvas,"OnMouseMove",new Point(119,127)); Mouse(canvas,"OnMouseUp",new Point(119,127));
                    Check(layer.Width > .5 && Math.Abs(layer.Height - .2) < .001, "Unlocked layer resize");
                    t.BaseVideoX = t.BaseVideoY = .1; t.BaseVideoWidth = t.BaseVideoHeight = .5;
                    canvas.SelectBaseVideo(); Mouse(canvas,"OnMouseDown",new Point(104,152)); Mouse(canvas,"OnMouseMove",new Point(120,152)); Mouse(canvas,"OnMouseUp",new Point(120,152));
                    Check(t.BaseVideoWidth > .5 && t.BaseVideoHeight == .5, "Unlocked base video resize");
                    var preview = new Bitmap(160,240); using (var g = Graphics.FromImage(preview)) g.Clear(Color.Blue); canvas.SetBackground(preview);
                    using (var a = new Bitmap(176,256)) using (var b = new Bitmap(176,256)) {
                        canvas.SetPlatformGuide("Без разметки"); canvas.DrawToBitmap(a,new Rectangle(0,0,176,256));
                        canvas.SetPlatformGuide("TikTok"); canvas.DrawToBitmap(b,new Rectangle(0,0,176,256));
                        bool different = false; for (int y = 8; y < 248 && !different; y++) for (int x = 8; x < 168; x++) if (a.GetPixel(x,y) != b.GetPixel(x,y)) { different = true; break; }
                        Check(different, "Platform guide not visible");
                    }
                }
                layer.X = -.2;
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.Ru); string xml = Path.Combine(root,"template.xml"); AssemblyFiles.Save(xml,t);
                var saved = AssemblyFiles.Load<AssemblyTemplate>(xml); AssemblyMarketProfiles.Apply(saved,AssemblyProfile.Ru);
                Check(!saved.BaseVideoPreserveAspectRatio && !saved.Scenes[0].Layers[0].PreserveAspectRatio && saved.Scenes[0].Layers[0].X < 0, "Placement persistence");
                Console.WriteLine("Free placement PASS: real FFmpeg crop, stretched image, continuous/segments, mouse drag, unlocked resize, guide, profile persistence"); return true;
            } catch (Exception ex) { Console.WriteLine(ex); Console.WriteLine(root); return false; }
        }
        public static bool RunVoicePitch(string ffmpeg, string ffprobe) {
            string root = Path.Combine(Path.GetTempPath(), "vb-voice-pitch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                string input = Path.Combine(root, "source.mp4");
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=160x240:r=25:d=2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-c:v", "libx264", "-c:a", "aac", "-shortest", input }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                for (int mode = 0; mode < 7; mode++) {
                    double pitch = mode == 0 ? 0 : mode == 1 ? -12 : 12;
                    var t = new AssemblyTemplate { Materials = root, Width = 160, Height = 240, Fps = 25, Transition = "cut", SceneTimingVersion = 1, RenderMode = "cpu", VoicePitchSemitones = pitch, ContinuousVideo = mode == 4 };
                    var source = new AssemblyScene { Duration = 2, VideoMode = "shared" }; t.Scenes.Add(source);
                    var video = new AssemblyVideo { Path = input, Duration = 2, HasAudio = true };
                    var scene = new AssemblyScenePlan { Scene = source, Video = video, ContentSeconds = 2, RenderDuration = 2 };
                    var plan = new AssemblyPlan { RenderDuration = 2 }; plan.Scenes.Add(scene);
                    if (mode == 2 || mode == 3) { t.PrimaryNativeAudio = false; if (mode == 2) plan.Voiceover = input; else scene.Voiceover = input; }
                    if (mode == 5) { scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = video, Duration = 1 }); scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = video, Duration = 1 }); }
                    if (mode == 6) { t.PrimaryNativeAudio = false; t.SplitLayout = "horizontal"; plan.SecondaryVideo = video; }
                    string output = Path.Combine(root, "render" + mode + ".mp4");
                    AssemblyRender.Run(t, plan, ffmpeg, ffprobe, output, Path.Combine(root, "work" + mode), AssemblyRaster.Save, CancellationToken.None, null).GetAwaiter().GetResult();
                    var media = Core.VideoInfo(Core.Probe(ffprobe, output, CancellationToken.None).GetAwaiter().GetResult());
                    Check(media.HasAudio && Math.Abs(media.Duration - 2) < .15, "Pitch changed output duration: " + mode);
                    string pcm = Path.Combine(root, "audio" + mode + ".pcm");
                    Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", output, "-ss", "0.3", "-t", "0.5", "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", pcm }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                    byte[] data = File.ReadAllBytes(pcm); int crossings = 0;
                    for (int i = 2; i < data.Length; i += 2) if (BitConverter.ToInt16(data, i - 2) <= 0 && BitConverter.ToInt16(data, i) > 0) crossings++;
                    double frequency = crossings / (data.Length / 2d / 48000);
                    Check(Math.Abs(frequency - 440 * Math.Pow(2, pitch / 12)) < 12, "Wrong voice pitch: " + mode + " / " + frequency);
                    Console.WriteLine("Voice pitch FFmpeg PASS: mode=" + mode + ", semitones=" + pitch + ", Hz=" + frequency);
                }
                var routing = new AssemblyTemplate { Width = 160, Height = 240, Fps = 25, Transition = "cut", SceneTimingVersion = 1, VoicePitchSemitones = 6 };
                var routingScene = new AssemblyScene { Duration = 2 }; routing.Scenes.Add(routingScene);
                var routingPlan = new AssemblyPlan { RenderDuration = 2, Music = input, Voiceover = input };
                routingPlan.Scenes.Add(new AssemblyScenePlan { Scene = routingScene, Video = new AssemblyVideo { Path = input, Duration = 2, HasAudio = true }, ContentSeconds = 2, RenderDuration = 2, Music = input });
                string script = Path.Combine(root, "routing.txt");
                AssemblyRender.Arguments(routing, routingPlan, "libx264", script, Path.Combine(root, "routing.mp4"), new AssemblyRenderContext());
                foreach (string line in File.ReadAllLines(script)) {
                    if (line.Contains("[music]") || line.Contains("[sceneAudio") || line.Contains("[native")) Check(!line.Contains("asetrate="), "Pitch touched music or native audio with voiceover");
                    if (line.EndsWith("[voiceover];")) Check(line.Contains("asetrate="), "Voiceover pitch missing");
                }
                var saved = new AssemblyTemplate { VoicePitchSemitones = -3.5 };
                AssemblyMarketProfiles.Capture(saved, AssemblyProfile.Ru);
                string xml = Path.Combine(root, "template.xml"); AssemblyFiles.Save(xml, saved);
                saved = AssemblyFiles.Load<AssemblyTemplate>(xml); saved.VoicePitchSemitones = 0; AssemblyMarketProfiles.Apply(saved, AssemblyProfile.Ru);
                Check(saved.VoicePitchSemitones == -3.5, "Pitch profile persistence");
                Check(AssemblyRender.VoicePitchFilter(0) == "", "Neutral pitch must not process audio");
                return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
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
                    var red = new Bitmap(360,640); using (var g = Graphics.FromImage(red)) g.Clear(Color.Red); canvas.SetBackdrop(red);
                    using (var frame = canvas.SubtitleBackground()) { Check(frame.GetPixel(10,10).R == 255, "Backdrop missing behind resized video."); Check(frame.GetPixel(100,150).B == 255, "Backdrop covered main video."); }
                    t.BackgroundForeground=true;
                    using (var frame = canvas.SubtitleBackground()) Check(frame.GetPixel(100,150).R == 255, "Foreground backdrop did not cover video.");
                    t.BackgroundForeground=false;
                    canvas.SetBackdrop(null);
                    using (var frame = canvas.SubtitleBackground()) Check(frame.GetPixel(10,10).R == 0, "Clearing backdrop did not restore black margins.");
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
        public static bool RunBackgrounds(string ffmpeg, string ffprobe) {
            string root = Path.Combine(Path.GetTempPath(), "assembly-backgrounds-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                string video = Path.Combine(root, "black.mp4"), second = Path.Combine(root, "blue.mp4"), folder = Path.Combine(root, "backgrounds");
                Directory.CreateDirectory(folder);
                string image = Path.Combine(folder, "red.png"), motion = Path.Combine(folder, "red.mp4"), overlay = Path.Combine(root, "overlay.png");
                using (var bitmap = new Bitmap(180, 320)) { using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.Red); bitmap.Save(image); }
                File.Copy(image, Path.Combine(folder, "duplicate.png"));
                using (var bitmap = new Bitmap(180, 320)) { using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.Transparent); g.FillRectangle(Brushes.Lime, 130, 130, 30, 30); } bitmap.Save(overlay); }
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=180x320:r=15:d=2", "-c:v", "libx264", video }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=blue:s=180x320:r=15:d=2", "-c:v", "libx264", second }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=red:s=180x320:r=15:d=0.2", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.2", "-c:v", "libx264", "-c:a", "aac", "-shortest", motion }, CancellationToken.None, null, 60).GetAwaiter().GetResult();
                var t = new AssemblyTemplate { Materials=root, Videos=video, Backgrounds=folder, Width=180, Height=320, Fps=15, Transition="cut", SceneTimingVersion=1, PackStudioVersion=1, RenderMode="cpu", PrimaryNativeAudio=false, SecondaryNativeAudio=false,
                    BaseVideoX=.2, BaseVideoY=.2, BaseVideoWidth=.5, BaseVideoHeight=.5 };
                t.Scenes.Add(new AssemblyScene { VideoMode="shared", Duration=1 });
                var catalog = new List<AssemblyVideo> { new AssemblyVideo { Path=video, Hash=AssemblyFiles.Hash(video), Duration=2 }, new AssemblyVideo { Path=second, Hash=AssemblyFiles.Hash(second), Duration=2 } };
                var inventory = new AssemblyInventory(t, catalog, new AssemblyHistory());
                Check(inventory.Calculate().Variants == 2, "Background identity/capacity must deduplicate copies.");
                var batch = inventory.CreateBatch(new Random(4), 2, new AssemblyMixTracker(), CancellationToken.None);
                Check(batch.Plans.Count == 2 && batch.Plans[0].Background.Hash != batch.Plans[1].Background.Hash, "Background pack did not rotate.");
                Check(batch.Plans[0].Signature != batch.Plans[1].Signature, "Background missing from visual signature.");
                var record = AssemblyHistoryRecord.FromPlan(batch.Plans[0], "output.mp4", t.Id);
                Check(record.ToPlan().Background.Hash == batch.Plans[0].Background.Hash, "History lost background.");
                var history = new AssemblyHistory(); history.Combinations.Add(record.Signature); history.Records.Add(record);
                Check(new AssemblyInventory(t, catalog, history).Calculate().Variants == 1, "Background history not respected.");
                var largeCatalog = new List<AssemblyVideo>();
                for(int i=0;i<230;i++) largeCatalog.Add(new AssemblyVideo {Path=video,Hash="large-"+i,Duration=2});
                t.Scenes.Add(new AssemblyScene {VideoMode="shared",Duration=1});
                var large = new AssemblyInventory(t,largeCatalog,new AssemblyHistory());
                Check(large.Enumerate()==null && large.Calculate().Variants==230*229*2,"Large capacity omitted background variants.");
                var largeBatch=large.CreateBatch(new Random(7),2,new AssemblyMixTracker(),CancellationToken.None);
                Check(largeBatch.Plans.Count==2 && largeBatch.Plans[0].Background.Hash!=largeBatch.Plans[1].Background.Hash,"Large planner failed background rotation.");
                var largeHistory=new AssemblyHistory(); var largeRecord=AssemblyHistoryRecord.FromPlan(largeBatch.Plans[0],"output.mp4",t.Id);
                largeHistory.Combinations.Add(largeRecord.Signature);largeHistory.Records.Add(largeRecord);
                Check(new AssemblyInventory(t,largeCatalog,largeHistory).Calculate().Variants==230*229*2-1,"Large history omitted background identity.");
                t.Scenes.RemoveAt(1);
                t.Backgrounds="";
                Check(new AssemblyInventory(t,catalog,new AssemblyHistory()).Enumerate()[0].Signature == new AssemblyInventory(new AssemblyTemplate { Materials=root, Videos=video, Width=180, Height=320, Fps=15, Transition="cut", Scenes=t.Scenes },catalog,new AssemblyHistory()).Enumerate()[0].Signature, "Empty background changed legacy signatures.");
                t.Backgrounds=folder; AssemblyMarketProfiles.EnsureSeeded(t);
                Check(t.MarketEn.Backgrounds == folder && t.MarketRuInst.Backgrounds == folder, "Initial profile seeding lost backgrounds.");
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.Ru);
                AssemblyMarketProfiles.Apply(t, AssemblyProfile.En); t.Backgrounds=image; t.BackgroundForeground=true; AssemblyMarketProfiles.Capture(t, AssemblyProfile.En);
                var custom = AssemblyMarketProfiles.AddEmpty(t,"Background test"); AssemblyMarketProfiles.Apply(t,custom.Id);
                Check(t.Backgrounds == "", "Empty custom profile inherited backgrounds."); t.Backgrounds=motion; AssemblyMarketProfiles.Capture(t,custom.Id);
                string xml=Path.Combine(root,"template.xml"); AssemblyFiles.Save(xml,t); t=AssemblyFiles.Load<AssemblyTemplate>(xml);
                AssemblyMarketProfiles.Apply(t,AssemblyProfile.Ru); Check(t.Backgrounds==folder,"RU background persistence failed.");
                AssemblyMarketProfiles.Apply(t,AssemblyProfile.En); Check(t.Backgrounds==image && t.BackgroundForeground,"EN background isolation/order persistence failed.");
                AssemblyMarketProfiles.Apply(t,custom.Id); Check(t.Backgrounds==motion,"Custom background persistence failed.");
                AssemblyMarketProfiles.Apply(t,AssemblyProfile.Ru);
                for (int mode=0; mode<8; mode++) {
                    t.BackgroundForeground=mode==7;
                    if(mode==7) { t.BaseVideoX=0; t.BaseVideoY=0; t.BaseVideoWidth=1; t.BaseVideoHeight=1; }
                    t.ContinuousVideo=mode==1; t.SplitLayout=mode==3?"horizontal":"none"; t.SecondaryVideos=second;
                    t.Transition=mode==4?"fade":"cut"; t.TransitionDuration=.2;
                    var scene=new AssemblyScenePlan { Scene=t.Scenes[0], Video=catalog[0], ContentSeconds=1, RenderDuration=1 };
                    if(mode==2) { scene.VideoSegments.Add(new AssemblySceneVideoSegment {Video=catalog[0],Duration=.5}); scene.VideoSegments.Add(new AssemblySceneVideoSegment {Video=catalog[0],Duration=.5}); }
                    var plan=new AssemblyPlan { Background=mode==6?null:new AssemblyAsset {Path=mode==7?overlay:mode==0?image:motion}, RenderDuration=1 };
                    plan.Scenes.Add(scene);
                    if(mode==3) plan.SecondaryVideo=catalog[1];
                    if(mode==4) { var extra=new AssemblyScene {Duration=1,VideoMode="shared"}; t.Scenes.Add(extra); plan.Scenes.Add(new AssemblyScenePlan {Scene=extra, Video=catalog[0], ContentSeconds=1, RenderDuration=1}); AssemblyTimeline.Complete(t,plan,new Random(1)); plan.RenderDuration=2+t.TransitionSeconds; }
                    var layer=new AssemblyLayer {Source=overlay};
                    if(mode==0 || mode==4) scene.Layers.Add(new AssemblyLayerPlan {Layer=layer,Asset=new AssemblyAsset {Path=overlay}});
                    else if(mode!=7) plan.GlobalLayers.Add(new AssemblyLayerPlan {Layer=layer,Asset=new AssemblyAsset {Path=overlay}});
                    string output=Path.Combine(root,"render-"+mode+".mp4"), work=Path.Combine(root,"work-"+mode);
                    Action<AssemblyTemplate,AssemblyLayerPlan,string> raster=(settings,item,path)=>File.Copy(overlay,path);
                    if(mode==5) AssemblyEngine.RenderLegacy(t,plan,ffmpeg,ffprobe,output,work,raster,CancellationToken.None,null).GetAwaiter().GetResult();
                    else AssemblyRender.Run(t,plan,ffmpeg,ffprobe,output,work,raster,CancellationToken.None,null).GetAwaiter().GetResult();
                    Check(!Core.VideoInfo(Core.Probe(ffprobe,output,CancellationToken.None).GetAwaiter().GetResult()).HasAudio,"Background audio leaked into result.");
                    string frame=Path.Combine(root,"frame-"+mode+".png");
                    Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss",mode==4?"1.05":"0.8","-i",output,"-frames:v","1",frame},CancellationToken.None,null,60).GetAwaiter().GetResult();
                    Check(File.Exists(frame), "No extracted frame, mode " + mode + ", output duration " + Core.VideoInfo(Core.Probe(ffprobe,output,CancellationToken.None).GetAwaiter().GetResult()).VideoDuration);
                    using(var bitmap=new Bitmap(frame)) {
                        var margin=bitmap.GetPixel(10,10); var centre=bitmap.GetPixel(70,100); var obj=bitmap.GetPixel(140,140);
                        Check(centre.R<20 && centre.G<20 && centre.B<20,"Black video content was keyed out.");
                        if(mode!=3) Check(mode==6 || mode==7?margin.R<20:margin.R>220 && margin.G<30,"Background missing outside resized main video, mode "+mode);
                        else Check(margin.R<20 && margin.B<20,"Background covered full-canvas split video.");
                        if(mode!=4) Check(obj.G>220 && obj.R<30,"Background covered an overlay.");
                    }
                    if(mode==4) t.Scenes.RemoveAt(1);
                }
                Directory.Delete(root,true);
                return true;
            } catch (Exception e) { throw new Exception("Background regression files: " + root, e); }
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
                    var backdropButton = (Button)Field(window,"backgroundButton");
                    Check(backdropButton.Name == "BackgroundPool" && backdropButton.Text.StartsWith("Фон ·"), "Background batch button missing.");
                    string backdropPath = Path.Combine(root,"backdrop.png");
                    using (var image = new Bitmap(80,80)) { using (var g = Graphics.FromImage(image)) g.Clear(Color.Red); image.Save(backdropPath); }
                    typeof(AssemblyWindow).GetMethod("ImportPool",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[] {5,new[] {backdropPath},false});
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 10000 && (Field(canvas, "background") == null || Field(canvas,"backdrop") == null)) { Application.DoEvents(); Thread.Sleep(20); }
                    Check(canvas.Visible && canvas.Width > 20 && canvas.Height > 20, "Embedded preview canvas not laid out.");
                    Check(Field(canvas,"backdrop") != null && backdropButton.Text == "Фон · 1", "Imported backdrop not loaded in preview.");
                    using (var pool = (Form)typeof(AssemblyWindow).GetMethod("CreatePoolWindow",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[] {5})) {
                        Check(pool.Text == "Фон · изображения и видео", "Background batch opens wrong editor.");
                    }
                    Check(Field(canvas, "background") != null, "Embedded base-video preview not loaded. Status: " + ((Label)Field(window, "status")).Text + "; Videos: " + ((AssemblyTemplate)Field(window, "template")).Videos);
                    typeof(AssemblyWindow).GetMethod("SelectBaseVideo", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                    var active = (AssemblyTemplate)Field(window, "template");
                    active.BaseVideoX = .1; active.BaseVideoY = .1; active.BaseVideoWidth = .7; active.BaseVideoHeight = .7;
                    window.Hide(); window.Show(); Application.DoEvents();
                    var persisted = AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(root, "assembly-template.xml"));
                    Check(persisted.MarketRu.BaseVideoX == .1 && persisted.MarketRu.BaseVideoWidth == .7, "Window did not save placement to the active profile.");
                    Check(persisted.MarketRu.Backgrounds == active.Backgrounds && AssemblyFiles.Pool(active.Resolve(active.Backgrounds),AssemblyFiles.BackgroundExtensions).Count == 1, "Background batch did not persist to active profile.");
                    Check(Field(canvas, "background") != null, "Showing embedded editor cleared its working preview.");
                    string previousBackgrounds = active.Backgrounds;
                    typeof(AssemblyWindow).GetMethod("ClearPool",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[] {5});
                    Check(AssemblyFiles.Pool(active.Resolve(active.Backgrounds),AssemblyFiles.BackgroundExtensions).Count == 0 && Directory.Exists(active.Resolve(previousBackgrounds)), "Clear backdrop deleted old files or retained selection.");
                    window.Close(); host.Close(); Application.DoEvents();
                }
                return true;
            } finally { Store.Root = previous; if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        public static bool RunSceneAudioRouting() {
            string root=Path.Combine(Path.GetTempPath(),"scene-audio-routing-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                var t=new AssemblyTemplate {Width=180,Height=320,Fps=25,Transition="cut",SceneTimingVersion=1,RenderMode="cpu",MusicVolume=.23};
                var muted=new AssemblyScene {Duration=1,NativeAudioVolume=0,BedMusicVolume=0};
                var audible=new AssemblyScene {Duration=1,NativeAudioVolume=.42,BedMusicVolume=.17};
                t.Scenes.Add(muted); t.Scenes.Add(audible);
                var p=new AssemblyPlan {RenderDuration=2,Music=Path.Combine(root,"music.wav")};
                p.Scenes.Add(new AssemblyScenePlan {Scene=muted,Video=new AssemblyVideo {Path=Path.Combine(root,"one.mp4"),Duration=1,HasAudio=true},ContentSeconds=1,RenderDuration=1});
                p.Scenes.Add(new AssemblyScenePlan {Scene=audible,Video=new AssemblyVideo {Path=Path.Combine(root,"two.mp4"),Duration=1,HasAudio=true},ContentSeconds=1,RenderDuration=1});
                string graph=Path.Combine(root,"routing.ffgraph");
                var args=AssemblyRender.Arguments(t,p,"libx264",graph,Path.Combine(root,"out.mp4"),new AssemblyRenderContext());
                string text=File.ReadAllText(graph);
                Check(args.Count(x=>x.EndsWith("one.mp4"))==1 && args.Count(x=>x.EndsWith("two.mp4"))==2 && !text.Contains("bedMusic0"),"Muted scene still contributes audio");
                Check(text.Contains("volume=0.42"),"Second scene native audio level is not routed independently");
                Check(args.Count(x=>x.EndsWith("music.wav"))==1 && text.Contains("atrim=duration=2,volume='if(lt(t,1),0,0.17)':eval=frame") && text.Contains("[bedMusic]") && !text.Contains("bedMusic1"),"Background music must remain continuous with independent scene levels");
                Check(muted.BedMusicVolume==0 && audible.BedMusicVolume==.17 && t.MusicVolume==.23,"Scene music levels changed other settings");
                muted.BedMusicVolume=-1; audible.BedMusicVolume=-1;
                AssemblyRender.Arguments(t,p,"libx264",graph,Path.Combine(root,"inherited.mp4"),new AssemblyRenderContext());
                Check(File.ReadAllText(graph).Contains("if(lt(t,1),0.23,0.23)"),"Unchanged scenes must inherit the saved music level");
                t.MusicVolume=0;
                args=AssemblyRender.Arguments(t,p,"libx264",graph,Path.Combine(root,"muted.mp4"),new AssemblyRenderContext());
                Check(!args.Any(x=>x.EndsWith("music.wav")),"Global zero volume must disable background music");
                return true;
            } finally { Directory.Delete(root,true); }
        }
    }
}
