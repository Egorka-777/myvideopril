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
                using (var window = new AssemblyWindow()) {
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
    }
}
