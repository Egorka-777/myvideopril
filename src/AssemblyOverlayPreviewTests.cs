using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace VideoBatch {
    public static class AssemblyOverlayPreviewTests {
        // assetPath: 160x240 transparent looping clip, 40%-alpha red square alternating
        // between x=10..49 and x=90..129, y=80..119, with a one-second period.
        public static bool RunAnimatedOverlayUiTests(string assetPath) {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new Exception("UI test requires STA.");
            var layer = new AssemblyLayer { Source = assetPath, X = 0, Y = 0, Width = 1, Height = 1, Opacity = 1, UniformImageScale = false };
            var template = new AssemblyTemplate { Width = 160, Height = 240, SplitLayout = "none" };
            var sourceScene = new AssemblyScene(); sourceScene.Layers.Add(layer);
            var scene = new AssemblyScenePlan { Scene = sourceScene };
            scene.Layers.Add(new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = assetPath } });
            string failure = null;
            using (var form = new Form { Width = 220, Height = 320, ShowInTaskbar = false, Text = "Assembly animated preview test" })
            using (var canvas = new AssemblyCanvas { Dock = DockStyle.Fill }) {
                canvas.PreviewError += message => failure = message;
                form.Controls.Add(canvas);
                var background = new Bitmap(160, 240);
                using (var graphics = Graphics.FromImage(background)) graphics.Clear(Color.Blue);
                canvas.SetBackground(background);
                canvas.SetScene(template, scene, layer);
                form.Show();
                var processes = new List<Process>();
                bool left = false, right = false, returned = false;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 2600) {
                    Application.DoEvents();
                    CaptureProcesses(canvas, processes);
                    if (failure != null) throw new Exception(failure);
                    using (var frame = canvas.SubtitleBackground()) {
                        Color corner = frame.GetPixel(0, 0);
                        if (corner.B < 250 || corner.R > 3 || corner.G > 3) throw new Exception("Transparent background became opaque.");
                        Color a = frame.GetPixel(20, 90), b = frame.GetPixel(100, 90);
                        if (a.R > 80 && a.R < 130 && a.B > 125 && a.B < 180) { if (right) returned = true; left = true; }
                        if (b.R > 80 && b.R < 130 && b.B > 125 && b.B < 180) right = true;
                    }
                    Thread.Sleep(15);
                }
                if (!left || !right || !returned) throw new Exception("Animated alpha frames did not change and loop.");
                canvas.Hide(); Application.DoEvents();
                WaitForExit(processes); AssertNoPreviews(canvas);
                processes.Clear(); canvas.Show(); Application.DoEvents();
                clock.Restart();
                while (clock.ElapsedMilliseconds < 300) { Application.DoEvents(); CaptureProcesses(canvas, processes); Thread.Sleep(10); }
                if (processes.Count == 0) throw new Exception("Preview did not resume when shown.");
                canvas.ClearScene(); Application.DoEvents(); WaitForExit(processes); AssertNoPreviews(canvas);
                if (failure != null) throw new Exception(failure);
                processes.Clear(); canvas.SetScene(template, scene, layer);
                clock.Restart();
                while (clock.ElapsedMilliseconds < 300) { Application.DoEvents(); CaptureProcesses(canvas, processes); Thread.Sleep(10); }
                canvas.SetScene(template, scene, layer); WaitForExit(processes); processes.Clear();
                clock.Restart();
                while (clock.ElapsedMilliseconds < 300) { Application.DoEvents(); CaptureProcesses(canvas, processes); Thread.Sleep(10); }
                canvas.Dispose(); WaitForExit(processes);
                if (failure != null) throw new Exception(failure);
            }
            return true;
        }
        public static bool RunRoundedOverlayUiTests(string assetPath) {
            var layer=new AssemblyLayer {Source=assetPath,X=.1,Y=.1,Width=.8,Height=.8,CornerRadius=30,PreserveAspectRatio=false};
            var template=new AssemblyTemplate {Width=200,Height=200,Fps=30};
            var scene=new AssemblyScenePlan {Scene=new AssemblyScene {Duration=3}};
            scene.Layers.Add(new AssemblyLayerPlan {Layer=layer,Asset=new AssemblyAsset {Path=assetPath}});
            string failure=null;
            using(var form=new Form {ClientSize=new Size(240,240),ShowInTaskbar=false})
            using(var canvas=new AssemblyCanvas {Dock=DockStyle.Fill}) {
                form.Controls.Add(canvas); canvas.PreviewError+=message=>failure=message;
                var background=new Bitmap(200,200); using(var g=Graphics.FromImage(background))g.Clear(Color.Blue);
                canvas.SetBackground(background); canvas.SetScene(template,scene,layer); form.Show();
                bool rendered=false; var clock=Stopwatch.StartNew();
                while(clock.ElapsedMilliseconds<3000) {
                    Application.DoEvents(); if(failure!=null)throw new Exception(failure);
                    using(var image=canvas.SubtitleBackground()) {
                        var middle=image.GetPixel(100,100);
                        if(middle.R>200 && middle.B<40) {
                            var corner=image.GetPixel(21,21);
                            if(corner.B<245 || corner.R>10)throw new Exception("Rounded preview corner hides base video");
                            rendered=true; break;
                        }
                    }
                    Thread.Sleep(15);
                }
                if(!rendered)throw new Exception("Rounded animated preview did not produce a frame");
                canvas.ClearScene(); form.Close();
            }
            Console.WriteLine("Rounded overlay UI PASS: animated red video, transparent corners, blue base visible"); return true;
        }
        public static bool RunPlacementBoundsTests() {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overlay-bounds-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try {
                string small = System.IO.Path.Combine(root,"small.png"), large = System.IO.Path.Combine(root,"large.png");
                using(var b=new Bitmap(100,200)) b.Save(small);
                using(var b=new Bitmap(400,400)) b.Save(large);
                foreach(bool uniform in new[]{false,true}) {
                    var layer=new AssemblyLayer {Source=root,X=-.1,Y=.15,Width=1.5,Height=1.5,UniformImageScale=uniform,PreserveAspectRatio=true};
                    var t=new AssemblyTemplate {Width=200,Height=400};
                    var scene=new AssemblyScenePlan {Scene=new AssemblyScene()};
                    scene.Layers.Add(new AssemblyLayerPlan {Layer=layer,Asset=new AssemblyAsset {Path=small}});
                    using(var canvas=new AssemblyCanvas {Size=new Size(240,440)}) {
                        canvas.SetScene(t,scene,layer);
                        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                        var frame=(RectangleF)typeof(AssemblyCanvas).GetProperty("Frame",flags).GetValue(canvas);
                        Func<RectangleF> box=()=> (RectangleF)typeof(AssemblyCanvas).GetMethod("Box",flags).Invoke(canvas,new object[]{layer});
                        var expected=AssemblyRaster.LayerImageBounds(t,layer,new Size(100,200));
                        var before=box();
                        if(Math.Abs((before.X-frame.X)/frame.Width-expected.X/t.Width)>.0001 || Math.Abs(before.Width/frame.Width-expected.Width/t.Width)>.0001)
                            throw new Exception("Selection frame differs from render bounds, uniform="+uniform);
                        int x=(int)Math.Floor(before.Right)-2,y=(int)Math.Floor(before.Bottom)-2;
                        typeof(AssemblyCanvas).GetMethod("OnMouseDown",flags).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,x,y,0)});
                        typeof(AssemblyCanvas).GetMethod("OnMouseMove",flags).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,0,x+30,y+30,0)});
                        var after=box();
                        if(Math.Abs(after.X-before.X)>.01 || Math.Abs(after.Y-before.Y)>.01 || after.Width<=before.Width || layer.Width<=1)
                            throw new Exception("Resize moved visible anchor or clamped scale, uniform="+uniform);
                        var rendered=AssemblyRaster.LayerImageBounds(t,layer,new Size(100,200));
                        if(Math.Abs(after.Height/frame.Height-rendered.Height/t.Height)>.0001) throw new Exception("Resized frame differs from export");
                    }
                }
                Console.WriteLine("Placement bounds PASS: mixed source dimensions, fit/uniform, scale >100%, negative position, mouse resize anchor and export geometry");
                return true;
            } finally {System.IO.Directory.Delete(root,true);}
        }
        public static bool RunVideoPackBoundsTests(string packSource) {
            var template=new AssemblyTemplate {Width=1080,Height=1920};
            var layer=new AssemblyLayer {Source=packSource,X=-.24706306765973562,Y=.021382127970457132,Width=1.501,Height=1.501,UniformImageScale=true,PreserveAspectRatio=true};
            var small=AssemblyRaster.LayerImageBounds(template,layer,new Size(720,1280));
            var large=AssemblyRaster.LayerImageBounds(template,layer,new Size(1080,1920));
            if(Math.Abs(small.X-large.X)>.01 || Math.Abs(small.Y-large.Y)>.01 || Math.Abs(small.Width-large.Width)>.01 || Math.Abs(small.Height-large.Height)>.01)
                throw new Exception("Video pack resolution changed visible placement");
            var landscape=AssemblyRaster.LayerImageBounds(template,layer,new Size(1920,1080));
            if(landscape.Left<small.Left-.01 || landscape.Top<small.Top-.01 || landscape.Right>small.Right+.01 || landscape.Bottom>small.Bottom+.01)
                throw new Exception("Different aspect ratio escaped the shared video placement");
            layer.PreserveAspectRatio=false;
            var stretched=AssemblyRaster.LayerImageBounds(template,layer,new Size(720,1280));
            if(Math.Abs(stretched.Width-layer.Width*template.Width)>.01) throw new Exception("Stretch placement changed");
            Console.WriteLine("Video pack bounds PASS: resolution-independent placement, aspect fit and explicit stretch");
            return true;
        }
        static IDictionary Previews(AssemblyCanvas canvas) {
            return (IDictionary)typeof(AssemblyCanvas).GetField("animatedPreviews", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas);
        }
        static void CaptureProcesses(AssemblyCanvas canvas, List<Process> processes) {
            foreach (object preview in Previews(canvas).Values) {
                var process = (Process)preview.GetType().GetField("process", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(preview);
                if (process != null && !processes.Contains(process)) processes.Add(process);
            }
        }
        static void AssertNoPreviews(AssemblyCanvas canvas) {
            if (Previews(canvas).Count != 0) throw new Exception("Preview instances remained after cleanup.");
        }
        static void WaitForExit(List<Process> processes) {
            foreach (var process in processes) {
                try { if (!process.WaitForExit(2000)) throw new Exception("Preview process leaked after cleanup."); }
                catch (InvalidOperationException) { /* Worker already disposed the exited process. */ }
            }
        }
    }
}
