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
