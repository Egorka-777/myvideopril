using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace VideoBatch {
    public class AssemblyLayer {
        public string Name = "Изображение", Source = "";
        public bool Enabled = true, Unique = false, Fixed = false;
        public double X = .08, Y = .35, Width = .84, Height = .25, Opacity = 1;
        public double Start = 0, End = 0; // End=0 means until the end of this scene.
        public string Font = "Arial";
        public double FontSize = 54, Radius = 28, Padding = 24;
    }
    public class AssemblyScene {
        public string Name = "Сцена", Videos = ""; // Empty: use the common video folder.
        public double Duration = 4;
        public List<AssemblyLayer> Layers = new List<AssemblyLayer>();
        public override string ToString() { return Name + " · " + Duration.ToString("0.##") + " с"; }
    }
    public class AssemblyTemplate {
        public int Version = 1;
        public string Id = Guid.NewGuid().ToString("N"), Name = "Hook → торговля → TG-бот";
        public string Materials = "", Videos = "Видео", Music = "", Output = "";
        public int Width = 1080, Height = 1920, Fps = 30, Count = 10;
        public string Transition = "fade";
        public double TransitionDuration = .35, MusicVolume = .8;
        public bool RandomVideoStart = true;
        public List<AssemblyScene> Scenes = new List<AssemblyScene>();
        public double Duration { get { return Scenes.Sum(s => s.Duration); } }
        public string Resolve(string path) {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Materials, path));
        }
        public static AssemblyTemplate Defaults() {
            var t = new AssemblyTemplate();
            t.Scenes.Add(new AssemblyScene { Name = "1. Hook", Duration = 5, Layers = new List<AssemblyLayer> {
                new AssemblyLayer { Name = "Заголовок", Source = "Заголовки", Unique = true, Y = .38, Height = .22 }
            }});
            t.Scenes.Add(new AssemblyScene { Name = "2. Торговля", Videos = "Торговля", Duration = 6, Layers = new List<AssemblyLayer> {
                new AssemblyLayer { Name = "Второй заголовок", Source = "Заголовки-2", Unique = true, Y = .65, Height = .23 }
            }});
            t.Scenes.Add(new AssemblyScene { Name = "3. TG-бот и призыв", Duration = 6, Layers = new List<AssemblyLayer> {
                new AssemblyLayer { Name = "Скриншот TG-бота", Source = "TG-бот", Fixed = true, Y = .12, Height = .48 },
                new AssemblyLayer { Name = "Финальный призыв", Source = "Призыв", Y = .65, Height = .23 }
            }});
            return t;
        }
        public void Validate() {
            if (Version != 1) throw new Exception("Неизвестная версия шаблона.");
            if (string.IsNullOrWhiteSpace(Id) || Id.Length > 100 || Id.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new Exception("Некорректный ID шаблона.");
            if (Scenes == null || Scenes.Count < 2 || Scenes.Count > 12) throw new Exception("В шаблоне должно быть от 2 до 12 сцен.");
            if (Width < 180 || Width > 2160 || Height < 180 || Height > 3840 || Width % 2 != 0 || Height % 2 != 0) throw new Exception("Размер кадра должен быть чётным, от 180 до 2160 × 3840.");
            if (Fps < 15 || Fps > 60 || Count < 1 || Count > 1000) throw new Exception("FPS: 15–60. Количество роликов: 1–1000.");
            Check(MusicVolume, 0, 2, "Громкость музыки"); Check(TransitionDuration, 0, 2, "Длительность перехода");
            if (!Transitions.Contains(Transition)) throw new Exception("Неизвестный переход.");
            foreach (var scene in Scenes) {
                Check(scene.Duration, .5, 60, "Длительность сцены");
                if (Transition != "cut" && TransitionDuration >= scene.Duration / 2) throw new Exception("Переход должен быть короче половины каждой сцены.");
                if (scene.Layers == null || scene.Layers.Count > 8) throw new Exception("Допустимо до 8 изображений в сцене.");
                foreach (var l in scene.Layers.Where(l => l.Enabled)) {
                    if (string.IsNullOrWhiteSpace(l.Source)) throw new Exception(scene.Name + ": укажите источник для «" + l.Name + "».");
                    Check(l.X, 0, 1, "X"); Check(l.Y, 0, 1, "Y"); Check(l.Width, .02, 1, "Ширина"); Check(l.Height, .02, 1, "Высота");
                    if (l.X + l.Width > 1.000001 || l.Y + l.Height > 1.000001) throw new Exception("Изображение «" + l.Name + "» выходит за край кадра.");
                    Check(l.Opacity, 0, 1, "Непрозрачность"); Check(l.Start, 0, scene.Duration, "Появление");
                    Check(l.End, 0, scene.Duration, "Исчезновение");
                    if (l.Start >= (l.End == 0 ? scene.Duration : l.End)) throw new Exception("Время исчезновения должно быть позже появления.");
                    Check(l.FontSize, 12, 240, "Размер шрифта"); Check(l.Radius, 0, 200, "Скругление"); Check(l.Padding, 0, 150, "Отступ текста");
                    if (l.Fixed && l.Unique) throw new Exception("Фиксированное изображение не может одновременно быть без повторов.");
                }
            }
        }
        internal static void Check(double value, double min, double max, string label) {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max) throw new Exception(label + ": значение вне допустимых границ.");
        }
        public static readonly string[] Transitions = { "cut", "fade", "fadeblack", "fadewhite", "slideleft", "slideright", "smoothleft", "hblur", "zoomin" };
    }
    public static class AssemblyFiles {
        public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" };
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".txt" };
        public static List<string> Pool(string path, string[] extensions) {
            var items = File.Exists(path) ? new[] { path } : Directory.Exists(path) ? Directory.GetFiles(path) : new string[0];
            return items.Where(p => extensions.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static string Hash(string path) {
            // Normalize text so that renaming a file or changing line endings does not reuse a headline.
            using (var sha = SHA256.Create()) {
                if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Trim())));
                using (var stream = File.OpenRead(path)) return Hex(sha.ComputeHash(stream));
            }
        }
        public static string HashText(string text) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        public static T Load<T>(string path) {
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }
        public static void Save<T>(string path, T value) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    new XmlSerializer(typeof(T)).Serialize(stream, value); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public class AssemblyHistory {
        public List<string> UsedImages = new List<string>(), Combinations = new List<string>();
        public List<string> Outputs = new List<string>();
    }
    public class AssemblyAsset { public string Path, Hash; }
    public class AssemblyVideo { public string Path, Hash; public double Duration; }
    public class AssemblyLayerPlan { public AssemblyLayer Layer; public AssemblyAsset Asset; public string Raster; }
    public class AssemblyScenePlan {
        public AssemblyScene Scene;
        public AssemblyVideo Video;
        public double VideoStart, RenderDuration;
        public List<AssemblyLayerPlan> Layers = new List<AssemblyLayerPlan>();
    }
    public class AssemblyPlan {
        public List<AssemblyScenePlan> Scenes = new List<AssemblyScenePlan>();
        public List<string> UniqueImages = new List<string>();
        public string Signature, Music;
    }
    public class AssemblyPlanBatch {
        public List<AssemblyPlan> Plans = new List<AssemblyPlan>();
        public string Limit = "";
    }
    public static class AssemblyPlanner {
        public static AssemblyPlanBatch Create(AssemblyTemplate t, IList<AssemblyVideo> videos, AssemblyHistory history, Random random, int count) {
            t.Validate();
            var used = new HashSet<string>(history.UsedImages);
            var combinations = new HashSet<string>(history.Combinations);
            var pools = new Dictionary<AssemblyLayer, List<AssemblyAsset>>();
            foreach (var l in t.Scenes.SelectMany(s => s.Layers).Where(l => l.Enabled)) {
                var assets = AssemblyFiles.Pool(t.Resolve(l.Source), AssemblyFiles.ImageExtensions).Select(p => new AssemblyAsset { Path = p, Hash = AssemblyFiles.Hash(p) }).GroupBy(a => a.Hash).Select(g => g.First()).ToList();
                if (assets.Count == 0) throw new Exception("Нет изображений или TXT в источнике «" + l.Name + "»: " + t.Resolve(l.Source));
                pools.Add(l, assets);
            }
            int distinctVideos = videos.Select(v => v.Hash).Distinct().Count();
            if (distinctVideos < t.Scenes.Count) throw new Exception("Нужно минимум " + t.Scenes.Count + " разных видео: по одному на сцену. Найдено " + distinctVideos + ".");
            var batch = new AssemblyPlanBatch();
            for (int item = 0; item < count; item++) {
                AssemblyPlan accepted = null; string reason = "Исчерпаны разные сочетания материалов.";
                for (int attempt = 0; attempt < 400 && accepted == null; attempt++) {
                    var p = new AssemblyPlan(); var selectedVideos = new HashSet<string>(); var selectedImages = new HashSet<string>(); bool failed = false;
                    for (int i = 0; i < t.Scenes.Count; i++) {
                        var scene = t.Scenes[i]; double duration = scene.Duration + (i < t.Scenes.Count - 1 && t.Transition != "cut" ? t.TransitionDuration : 0);
                        string folder = t.Resolve(string.IsNullOrWhiteSpace(scene.Videos) ? t.Videos : scene.Videos);
                        var allowed = new HashSet<string>(AssemblyFiles.Pool(folder, AssemblyFiles.VideoExtensions), StringComparer.OrdinalIgnoreCase);
                        var videoPool = videos.Where(v => allowed.Contains(v.Path) && !selectedVideos.Contains(v.Hash) && v.Duration + .001 >= duration).ToList();
                        if (videoPool.Count == 0) { reason = "Не хватает разных видео нужной длины для «" + scene.Name + "» (нужно " + duration.ToString("0.##") + " с)."; failed = true; break; }
                        var video = videoPool[random.Next(videoPool.Count)]; selectedVideos.Add(video.Hash);
                        var sp = new AssemblyScenePlan { Scene = scene, Video = video, RenderDuration = duration, VideoStart = t.RandomVideoStart ? random.NextDouble() * Math.Max(0, video.Duration - duration) : 0 };
                        foreach (var l in scene.Layers.Where(l => l.Enabled)) {
                            var pool = pools[l].Where(a => !l.Unique || (!used.Contains(a.Hash) && !selectedImages.Contains(a.Hash))).ToList();
                            if (pool.Count == 0) { reason = "Закончились материалы без повторов: «" + l.Name + "» (" + l.Source + ")."; failed = true; break; }
                            var asset = l.Fixed ? pool[0] : pool[random.Next(pool.Count)];
                            sp.Layers.Add(new AssemblyLayerPlan { Layer = l, Asset = asset });
                            if (l.Unique) { selectedImages.Add(asset.Hash); p.UniqueImages.Add(asset.Hash); }
                        }
                        p.Scenes.Add(sp); if (failed) break;
                    }
                    if (failed) continue;
                    // Random start times do not create artificial "new" combinations.
                    p.Signature = AssemblyFiles.HashText(string.Join("|", p.Scenes.Select(s => s.Video.Hash + ":" + string.Join(",", s.Layers.Select(l => l.Asset.Hash)))));
                    if (!combinations.Contains(p.Signature)) accepted = p;
                }
                if (accepted == null) { batch.Limit = reason; break; }
                foreach (string h in accepted.UniqueImages) used.Add(h);
                combinations.Add(accepted.Signature); accepted.Music = t.Resolve(t.Music); batch.Plans.Add(accepted);
            }
            return batch;
        }
    }
}
