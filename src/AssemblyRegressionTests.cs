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
                var t = LegacyTemplate(); t.Materials = temp; t.Output = Path.Combine(temp, "output"); t.Validate();
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
        static AssemblyTemplate LegacyTemplate() {
            var t = new AssemblyTemplate();
            t.Scenes.Add(new AssemblyScene { Name = "1. Hook", Duration = 5, Layers = { new AssemblyLayer { Name = "Заголовок", Source = "Заголовки", Unique = true } } });
            t.Scenes.Add(new AssemblyScene { Name = "2. Торговля", Duration = 6, Videos = "Торговля", Layers = { new AssemblyLayer { Name = "Второй заголовок", Source = "Заголовки-2", Unique = true } } });
            t.Scenes.Add(new AssemblyScene { Name = "3. TG-бот и призыв", Duration = 6, Layers = { new AssemblyLayer { Name = "Скриншот TG-бота", Source = "TG-бот", Fixed = true }, new AssemblyLayer { Name = "Финальный призыв", Source = "Призыв" } } });
            return t;
        }
        public static bool RunPackTests() {
            string temp = Path.Combine(Path.GetTempPath(),"assembly-packs-" + Guid.NewGuid().ToString("N"));
            try {
                var t = AssemblyTemplate.Defaults(); t.Materials = temp; t.Output = Path.Combine(temp,"out"); AssemblyWorkspace.Prepare(t); t.Validate();
                if (t.Scenes.Count != 4 || t.Duration != 20 || t.Scenes.Select(s => s.Folder).Distinct().Count() != 4) return false;
                if (t.Scenes.Any(s => !Directory.Exists(t.Resolve(s.Layers[0].Source)))) return false;
                File.WriteAllText(t.Resolve("Видео/a.mp4"),"a"); File.WriteAllText(t.Resolve("Музыка/a.wav"),"music-a"); File.WriteAllText(t.Resolve("Музыка/b.wav"),"music-b");
                var videos = new[] { new AssemblyVideo { Path = t.Resolve("Видео/a.mp4"), Hash = "video-a", Duration = 1 } };
                // Four empty image packs and one short video must still produce a usable plan.
                var batch = AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(31),2);
                if (batch.Plans.Count != 2 || batch.Plans.Any(p => p.Scenes.Count != 4 || p.Scenes.Any(s => s.Layers.Count != 0))) return false;
                if (batch.Plans.Select(p => p.Music).Distinct().Count() != 2) return false;
                var history = new AssemblyHistory(); history.Combinations.AddRange(batch.Plans.Select(p => p.Signature));
                if (AssemblyPlanner.Create(t,videos,history,new Random(13),1).Plans.Count != 0) return false;
                // Empty packs are optional, but populated packs marked unique still honor history.
                var l = t.Scenes[0].Layers[0]; l.Unique = true;
                File.WriteAllText(t.Resolve(Path.Combine(l.Source,"a.png")),"picture-a"); File.WriteAllText(t.Resolve(Path.Combine(l.Source,"b.png")),"picture-b");
                File.WriteAllText(t.Resolve(Path.Combine(l.Source,"ignored.txt")),"This is not an image.");
                batch = AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(14),10);
                if (batch.Plans.Count != 2 || batch.Plans.SelectMany(p => p.UniqueImages).Distinct().Count() != 2 || batch.Limit == "") return false;
                // Legacy empty headline folders stop being required after migration, without losing used assets/history identity.
                var legacy = LegacyTemplate(); legacy.Materials = temp; legacy.Output = t.Output; string id = legacy.Id;
                AssemblyWorkspace.Prepare(legacy);
                if (legacy.Id != id || legacy.Scenes.Count != 3 || !legacy.Scenes[0].Layers[0].Unique || legacy.Scenes[0].Layers[0].Source != "Заголовки") return false;
                batch = AssemblyPlanner.Create(legacy,videos,new AssemblyHistory(),new Random(1),1); if (batch.Plans.Count != 1) return false;
                string folder = t.Scenes[1].Folder; AssemblyWorkspace.Insert(t,1); if (t.Scenes[2].Folder != folder) return false;
                string longStem = "ССЫЛКА НА AI БОТ — В ПРОФИЛЕ " + new string('X', 180);
                string musicFolder = t.Resolve("Музыка"); Directory.CreateDirectory(musicFolder);
                string safeDest = AssemblyFiles.SafeImportDestination(musicFolder, Path.Combine(musicFolder, longStem + ".mp3"));
                if (safeDest.Length > 259 || Path.GetFileName(safeDest).Length >= longStem.Length) return false;
                File.WriteAllText(safeDest, "music-long-name");
                if (!File.Exists(safeDest)) return false;
                return true;
            } finally { if (Directory.Exists(temp)) Directory.Delete(temp,true); }
        }
        public static bool RunUiTests() { return StudioCheck(null); }
        static object Field(object obj, string name) { return obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj); }
        static object Invoke(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args); }
        static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control c) {
            foreach (System.Windows.Forms.Control child in c.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; }
        }
        public static bool RunStudioUiTests() { return StudioCheck(null); }
        public static bool WriteStudioScreenshots(string folder) { return StudioCheck(folder); }
        static bool StudioCheck(string screenshots) {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(),"assembly-studio-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var t = AssemblyTemplate.Defaults(); t.Materials = Path.Combine(temp,"materials"); t.Output = Path.Combine(temp,"output");
                string asset = Path.Combine(temp,"image.png"), other = Path.Combine(temp,"second.png");
                using (var b = new Bitmap(700,160)) { using (var g = Graphics.FromImage(b)) { g.Clear(Color.Black); using (var font = new Font("Arial",38,FontStyle.Bold)) g.DrawString("Проверяю сигналы",font,Brushes.White,22,40); } b.Save(asset,System.Drawing.Imaging.ImageFormat.Png); }
                using (var b = new Bitmap(300,400)) { using (var g = Graphics.FromImage(b)) { g.Clear(Color.FromArgb(25,43,56)); using (var font = new Font("Arial",28)) g.DrawString("Мой бот",font,Brushes.White,35,150); } b.Save(other,System.Drawing.Imaging.ImageFormat.Png); }
                AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),t);
                using (var window = new AssemblyWindow(true)) {
                    window.Show(); window.SetBounds(0,0,1180,790); System.Windows.Forms.Application.DoEvents();
                    if (window.BackColor != Theme.Background || All(window).Any(c => c.Visible && (c is System.Windows.Forms.TabControl || c is System.Windows.Forms.TextBox))) throw new Exception("Studio checkpoint 1 failed");
                    var current = (AssemblyTemplate)Field(window,"template"); if (current.Scenes.Count != 4) throw new Exception("Studio checkpoint 2 failed");
                    Invoke(window,"ImportPool",2,new string[] {asset,other},false);
                    var l = current.Scenes[0].Layers[0]; string source = l.Source;
                    if (AssemblyFiles.Pool(current.Resolve(source),AssemblyFiles.PictureExtensions).Count != 2 || !File.Exists(asset)) throw new Exception("Studio checkpoint 3 failed");
                    Invoke(window,"SetImageMode",2); if (!l.Unique || l.Fixed) throw new Exception("Studio checkpoint 4 failed");
                    var size = (System.Windows.Forms.NumericUpDown)Field(window,"width"); double ratio = l.Height/l.Width; size.Value = 70;
                    if (Math.Abs(l.Height/l.Width-ratio) > .001) throw new Exception("Studio checkpoint 5 failed");
                    var canvas = (AssemblyCanvas)Field(window,"canvas");
                    var frame = (RectangleF)typeof(AssemblyCanvas).GetProperty("Frame",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(canvas);
                    int px = (int)(frame.X+(l.X+l.Width/2)*frame.Width), py = (int)(frame.Y+(l.Y+l.Height/2)*frame.Height); double before = l.Y;
                    Invoke(canvas,"OnMouseDown",new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,1,px,py,0));
                    Invoke(canvas,"OnMouseMove",new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,0,px+12,py+10,0));
                    Invoke(canvas,"OnMouseUp",new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,1,px+12,py+10,0));
                    if (l.Y <= before) throw new Exception("Studio checkpoint 6 failed");
                    var duration = (System.Windows.Forms.NumericUpDown)Field(window,"duration"); l.Start = 4; l.End = 5; duration.Value = .5m; current.Validate(); if (l.Start >= .5 || l.End != 0) throw new Exception("Studio checkpoint 7 failed"); duration.Value = 5;
                    Invoke(window,"SetFormat",1080,1350); Invoke(window,"Save"); if (AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml")).Width != 1080) throw new Exception("Studio checkpoint 8 failed"); Invoke(window,"SetFormat",1080,1920);
                    if (screenshots != null) {
                        Directory.CreateDirectory(screenshots); l.Start = 0; l.Unique = false;
                        var background = new Bitmap(360,640); using (var g = Graphics.FromImage(background)) { g.Clear(Color.FromArgb(16,26,25)); using (var pen = new Pen(Theme.Accent,3)) g.DrawLines(pen,new[] {new Point(0,420),new Point(70,380),new Point(120,430),new Point(190,250),new Point(240,270),new Point(320,130),new Point(360,155)}); } canvas.SetBackground(background);
                        Capture(window,Path.Combine(screenshots,"studio-scenes.png"));
                        using (var pool = (System.Windows.Forms.Form)Invoke(window,"CreatePoolWindow",2)) { pool.Show(window); System.Windows.Forms.Application.DoEvents(); Capture(pool,Path.Combine(screenshots,"studio-files.png")); pool.Close(); }
                        window.SetBounds(0,0,1000,700); System.Windows.Forms.Application.DoEvents();
                        var fourth = All(window).First(c => c.Name == "SceneFiles3"); var stack = (AssemblyScrollStack)Field(window,"sceneStack");
                        if (!fourth.Visible || fourth.Parent.Bottom > stack.ClientSize.Height) throw new Exception("Fourth scene files button is clipped at compact window size");
                        Capture(window,Path.Combine(screenshots,"studio-compact.png"));
                    }
                    // Replacement changes the pool atomically and retains old originals/copies.
                    string old = current.Resolve(source); Invoke(window,"ImportPool",2,new string[] {other},true);
                    if (l.Source == source || !Directory.Exists(old) || AssemblyFiles.Pool(current.Resolve(l.Source),AssemblyFiles.PictureExtensions).Count != 1) throw new Exception("Studio checkpoint 10 failed");
                    Invoke(window,"AddPack",(object)new string[] {asset}); if (current.Scenes[0].Layers.Count != 2) throw new Exception("Studio checkpoint 11 failed");
                    Invoke(window,"InsertScene",1); if (current.Scenes.Count != 5 || !Directory.Exists(current.Resolve(current.Scenes[1].Folder))) throw new Exception("Studio checkpoint 12 failed");
                    string addedFolder = current.Scenes[1].Folder; Invoke(window,"MoveScene",1); if (current.Scenes[2].Folder != addedFolder) throw new Exception("Studio checkpoint 13 failed");
                    Invoke(window,"RemoveScene"); if (current.Scenes.Count != 4 || !Directory.Exists(current.Resolve(addedFolder))) throw new Exception("Studio checkpoint 14 failed");
                    Invoke(window,"Save"); window.Close();
                }
                using (var reopened = new AssemblyWindow(true)) {
                    var saved = (AssemblyTemplate)Field(reopened,"template");
                    if (saved.Scenes.Count != 4 || saved.Scenes[0].Layers.Count != 2 || AssemblyFiles.Pool(saved.Resolve(saved.Scenes[0].Layers[0].Source),AssemblyFiles.PictureExtensions).Count != 1) throw new Exception("Studio checkpoint 15 failed");
                }
                // Exercise actual legacy load, automatic migration and backup.
                var legacy = LegacyTemplate(); legacy.Materials = t.Materials; legacy.Output = t.Output; AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),legacy);
                using (var migrated = new AssemblyWindow(true)) { var saved = (AssemblyTemplate)Field(migrated,"template"); if (saved.PackStudioVersion != 1 || saved.Id != legacy.Id || !Directory.GetFiles(temp,"assembly-template.xml.before-packs-*.xml").Any()) throw new Exception("Studio checkpoint 16 failed"); }
                var nav = new NavigationService(); int calls = 0; nav.Navigated += _ => calls++; nav.Navigate(NavSection.VideoAssembly); nav.Navigate(NavSection.VideoAssembly); return calls == 2;
            } finally { Store.Root = previous; if (Directory.Exists(temp)) Directory.Delete(temp,true); }
        }
        static void Capture(System.Windows.Forms.Form form, string path) {
            form.PerformLayout(); System.Windows.Forms.Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }
    }
}
