using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VideoBatch {
    public static class AssemblyRegressionTests {
        public static bool RunPlannerTests() {
            string temp = Path.Combine(Path.GetTempPath(), "assembly-tests-" + Guid.NewGuid().ToString("N"));
            try {
                var t = AssemblyTemplate.Defaults(); t.Materials = temp; t.Output = Path.Combine(temp, "output"); t.Validate();
                if (t.Scenes.Count != 3 || t.Duration != 17 || !t.Scenes[2].Layers[0].Fixed || !t.Scenes[0].Layers[0].Unique || !t.Scenes[1].Layers[0].Unique) return false;
                foreach (string folder in new[] { "Видео", "Торговля", "Заголовки", "Заголовки-2", "TG-бот", "Призыв" }) Directory.CreateDirectory(Path.Combine(temp, folder));
                for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(temp, "Видео", i + ".mp4"), "video" + i);
                for (int i = 0; i < 2; i++) File.WriteAllText(Path.Combine(temp, "Торговля", i + ".mp4"), "trade" + i);
                foreach (string folder in new[] { "Заголовки", "Заголовки-2" }) for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(temp, folder, i + ".txt"), folder + i);
                File.WriteAllText(Path.Combine(temp, "Заголовки", "renamed.txt"), "Заголовки0\r\n");
                File.WriteAllText(Path.Combine(temp, "TG-бот", "bot.txt"), "fixed bot"); File.WriteAllText(Path.Combine(temp, "Призыв", "cta.txt"), "shared CTA");
                var videos = Directory.GetFiles(temp, "*.mp4", SearchOption.AllDirectories).Select(p => new AssemblyVideo { Path = p, Hash = AssemblyFiles.Hash(p), Duration = 12 }).ToList();
                var history = new AssemblyHistory(); var batch = AssemblyPlanner.Create(t, videos, history, new Random(14), 2);
                if (batch.Plans.Count != 2 || batch.Plans.SelectMany(p => p.UniqueImages).Distinct().Count() != 4) return false;
                foreach (var plan in batch.Plans) {
                    if (plan.Scenes.Select(s => s.Video.Hash).Distinct().Count() != 3) return false;
                    if (Path.GetDirectoryName(plan.Scenes[1].Video.Path) != Path.Combine(temp, "Торговля")) return false;
                    if (plan.Scenes[0].RenderDuration != 5.35 || plan.Scenes[1].RenderDuration != 6.35 || plan.Scenes[2].RenderDuration != 6) return false;
                    history.UsedImages.AddRange(plan.UniqueImages); history.Combinations.Add(plan.Signature);
                }
                string path = Path.Combine(temp, "history.xml"); AssemblyFiles.Save(path, history); history = AssemblyFiles.Load<AssemblyHistory>(path);
                var next = AssemblyPlanner.Create(t, videos, history, new Random(22), 10);
                if (next.Plans.Count != 1 || next.Limit == "" || next.Plans[0].UniqueImages.Any(h => history.UsedImages.Contains(h))) return false;
                // One shared pool used twice in a video needs two distinct headline files.
                t.Scenes[2].Layers[1].Source = "Заголовки-2"; t.Scenes[2].Layers[1].Unique = true;
                var shared = AssemblyPlanner.Create(t, videos, new AssemblyHistory(), new Random(7), 10);
                if (shared.Plans.Count != 1) return false;
                // No opaque success for an invalid layout or an invalid timeline.
                t.Scenes[0].Layers[0].X = .95;
                try { t.Validate(); return false; } catch (Exception) { }
                t.Scenes[0].Layers[0].X = .08; t.Scenes[0].Layers[0].Start = 5;
                try { t.Validate(); return false; } catch (Exception) { }
                return true;
            } finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }
        public static bool RunRasterTests() {
            string temp = Path.Combine(Path.GetTempPath(), "assembly-raster-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try {
                string image = Path.Combine(temp, "transparent.png"), text = Path.Combine(temp, "text.txt");
                using (var b = new Bitmap(40, 20)) { using (var g = Graphics.FromImage(b)) { g.Clear(Color.Transparent); g.FillRectangle(Brushes.Red, 10, 5, 20, 10); } b.Save(image, System.Drawing.Imaging.ImageFormat.Png); }
                File.WriteAllText(text, "Проверка русского заголовка\nи переноса строки");
                var t = AssemblyTemplate.Defaults(); t.Width = 360; t.Height = 640;
                var layer = new AssemblyLayer { X = .1, Y = .2, Width = .8, Height = .3, Radius = 18, Padding = 12, FontSize = 42 };
                using (var b = AssemblyRaster.Create(t, new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = image } })) {
                    if (b.GetPixel(0, 0).A != 0 || b.GetPixel(180, 224).R < 200 || b.GetPixel(180, 224).A < 200) return false;
                }
                using (var b = AssemblyRaster.Create(t, new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = text } })) {
                    if (b.GetPixel(0, 0).A != 0 || b.GetPixel(36, 128).A != 0 || b.GetPixel(50, 150).A < 200) return false;
                }
                return true;
            } finally { Directory.Delete(temp, true); }
        }
        public static bool RunUiTests() {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(), "assembly-ui-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var t = AssemblyTemplate.Defaults(); t.Materials = Path.Combine(temp, "materials"); t.Output = Path.Combine(temp, "output");
                AssemblyFiles.Save(Path.Combine(temp, "assembly-template.xml"), t);
                using (var window = new AssemblyWindow(true)) {
                    window.CreateControl(); if (window.Text.IndexOf("Сборка роликов", StringComparison.Ordinal) < 0) return false;
                    typeof(AssemblyWindow).GetMethod("AddScene", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, new object[] { true });
                    var saved = AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp, "assembly-template.xml"));
                    if (saved.Scenes.Count != 4 || saved.Scenes[1].Name != "Новая сцена") return false;
                    if (!Directory.Exists(saved.Resolve(saved.Scenes[1].Videos)) || !Directory.Exists(saved.Resolve(saved.Scenes[1].Layers[0].Source))) return false;
                }
                var nav = new NavigationService(); int calls = 0; nav.Navigated += _ => calls++;
                nav.Navigate(NavSection.VideoAssembly); nav.Navigate(NavSection.VideoAssembly); return calls == 2;
            } finally { Store.Root = previous; if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }

        static object Field(object obj, string name) { return obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj); }
        static object Invoke(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args); }
        static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control c) {
            foreach (System.Windows.Forms.Control child in c.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; }
        }
        public static bool RunStudioUiTests() { return StudioCheck(null); }
        public static bool WriteStudioScreenshots(string folder) { return StudioCheck(folder); }
        static bool StudioCheck(string screenshots) {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(), "assembly-studio-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var t = AssemblyTemplate.Defaults(); t.Materials = Path.Combine(temp, "materials"); t.Output = Path.Combine(temp, "output");
                string asset = Path.Combine(temp, "headline.png");
                using (var b = new Bitmap(700, 160)) { using (var g = Graphics.FromImage(b)) { g.Clear(Color.Black); using (var font = new Font("Arial", 38, FontStyle.Bold)) g.DrawString("Проверяю сигналы", font, Brushes.White, 22, 40); } b.Save(asset, System.Drawing.Imaging.ImageFormat.Png); }
                AssemblyFiles.Save(Path.Combine(temp, "assembly-template.xml"), t);
                using (var window = new AssemblyWindow(true)) {
                    window.Show(); window.SetBounds(0, 0, 1240, 820); System.Windows.Forms.Application.DoEvents();
                    if (window.BackColor != Theme.Background || All(window).Any(c => c is System.Windows.Forms.TabControl)) return false;
                    var imageCard = (AssemblyAssetCard)Field(window, "imageCard"); var stack = (AssemblyScrollStack)imageCard.Parent;
                    int originalTop = imageCard.Top;
                    Invoke(stack, "OnMouseWheel", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, 10, 10, -120));
                    if (imageCard.Top >= originalTop) return false;
                    stack.PerformLayout();
                    Invoke(stack, "OnMouseWheel", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, 10, 10, 120));
                    if (imageCard.Top != originalTop) return false;
                    var timeline = (System.Windows.Forms.FlowLayoutPanel)Field(window, "timeline");
                    if (timeline.Controls.OfType<System.Windows.Forms.Button>().Count(b => b.Name.StartsWith("InsertScene")) != 4) return false;
                    var duration = (System.Windows.Forms.NumericUpDown)Field(window, "duration");
                    var slider = All(window).OfType<AssemblySlider>().First(c => c.Name == "SceneDurationSlider");
                    typeof(AssemblySlider).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(slider, new object[] { new System.Windows.Forms.KeyEventArgs(System.Windows.Forms.Keys.Right) });
                    if (duration.Value != 5.1m) return false;
                    Invoke(window, "ImportSceneFiles", false, new string[] { asset });
                    var current = (AssemblyTemplate)Field(window, "template");
                    if (AssemblyFiles.Pool(current.Resolve(current.Scenes[0].Layers[0].Source), AssemblyFiles.ImageExtensions).Count != 1 || !File.Exists(asset)) return false;
                    ((System.Windows.Forms.Button)Field(window, "permanent")).PerformClick();
                    if (!current.Scenes[0].Layers[0].Fixed || current.Scenes[0].Layers[0].Unique) return false;
                    ((System.Windows.Forms.Button)Field(window, "fresh")).PerformClick();
                    if (current.Scenes[0].Layers[0].Fixed || !current.Scenes[0].Layers[0].Unique) return false;
                    var width = (System.Windows.Forms.NumericUpDown)Field(window, "width"); var layer = current.Scenes[0].Layers[0]; double ratio = layer.Height / layer.Width;
                    width.Value = 70;
                    if (Math.Abs(layer.Height / layer.Width - ratio) > .001) return false;
                    var start = (System.Windows.Forms.NumericUpDown)Field(window, "start"); var end = (System.Windows.Forms.NumericUpDown)Field(window, "end");
                    start.Value = 1; end.Value = 4;
                    if (layer.Start != 1 || layer.End != 4) return false;
                    duration.Value = .5m; current.Validate();
                    if (layer.Start >= .5 || layer.End != 0 || current.TransitionDuration >= .25) return false;
                    duration.Value = 5; start.Value = 0;
                    // Exercise actual canvas drag, then verify that the saved template has the new placement.
                    var canvas = (AssemblyCanvas)Field(window, "canvas");
                    var frame = (RectangleF)typeof(AssemblyCanvas).GetProperty("Frame", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
                    int px = (int)(frame.X + (layer.X + layer.Width / 2) * frame.Width), py = (int)(frame.Y + (layer.Y + layer.Height / 2) * frame.Height); double before = layer.X;
                    typeof(AssemblyCanvas).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(canvas, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, px, py, 0) });
                    typeof(AssemblyCanvas).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(canvas, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 0, px + 12, py + 10, 0) });
                    typeof(AssemblyCanvas).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(canvas, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, px + 12, py + 10, 0) });
                    if (layer.X <= before) return false;
                    Invoke(window, "Save"); if (AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp, "assembly-template.xml")).Scenes[0].Layers[0].X != layer.X) return false;
                    if (screenshots != null) {
                        Directory.CreateDirectory(screenshots); var background = new Bitmap(360, 640);
                        using (var g = Graphics.FromImage(background)) { using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0,0,360,640), Color.FromArgb(33,46,44), Color.FromArgb(9,13,12), 90)) g.FillRectangle(brush, 0, 0, 360, 640); using (var pen = new Pen(Theme.Accent, 3)) g.DrawLines(pen, new[] { new Point(0,420), new Point(45,380), new Point(75,402), new Point(120,310), new Point(165,345), new Point(215,225), new Point(270,255), new Point(330,130), new Point(360,155) }); }
                        canvas.SetBackground(background);
                        Capture(window, Path.Combine(screenshots, "studio-scenes.png"));
                        Invoke(window, "SwitchInspector", true); Capture(window, Path.Combine(screenshots, "studio-export.png")); Invoke(window, "SwitchInspector", false);
                        window.SetBounds(0, 0, 1024, 730); System.Windows.Forms.Application.DoEvents();
                        if (imageCard.Top < 0 || imageCard.Bottom > stack.ClientSize.Height) throw new Exception("Video/image import card is clipped at compact window size");
                        Capture(window, Path.Combine(screenshots, "studio-compact.png"));
                    }
                    timeline.Controls.OfType<System.Windows.Forms.Button>().First(b => b.Name == "InsertScene1").PerformClick();
                    if (current.Scenes.Count != 4 || current.Scenes[1].Name != "Новая сцена" || !Directory.Exists(current.Resolve(current.Scenes[1].Videos))) return false;
                    Invoke(window, "MoveScene", 1); if (current.Scenes[2].Name != "Новая сцена") return false;
                    window.Close();
                }
                return true;
            } finally { Store.Root = previous; if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }
        static void Capture(System.Windows.Forms.Form form, string path) {
            form.PerformLayout(); System.Windows.Forms.Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }
    }
}
