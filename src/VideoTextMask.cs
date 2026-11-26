using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class VideoTextRegion {
        public double Start, End;
        public int X, Y, Width, Height;
    }
    // Detection uses original coordinates/timestamps. Rasterize before processing and translated subtitles.
    public static class VideoTextMask {
        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public sealed class Detection { public int version; public List<VideoTextRegion> regions; }
        public sealed class Receipt { public string SourceHash; public int Width, Height; public Detection Detection; }
        public sealed class Runtime { public string python, version; }
        static string Scripts() {
            foreach (string root in new[] { Path.GetDirectoryName(typeof(VideoTextMask).Assembly.Location), AppDomain.CurrentDomain.BaseDirectory }.Concat(AppPaths.CandidateInstallRoots())) {
                string path = Path.Combine(root, "tools", "video-text");
                if (File.Exists(Path.Combine(path, "worker.py")) && File.Exists(Path.Combine(path, "prepare.ps1"))) return path;
            }
            throw new Exception("Не найдена папка tools/video-text. Обновите приложение вместе с этой папкой.");
        }
        internal static void ValidateImages(VideoTranslationOptions options) {
            if (!options.MaskVideoText) return;
            if (options.TextMaskImages == null || options.TextMaskImages.Count == 0) throw new Exception("Добавьте хотя бы одну картинку для закрытия текста в кадре.");
            foreach (string path in options.TextMaskImages) {
                if (!File.Exists(path)) throw new FileNotFoundException("Не найдена картинка-плашка", path);
                using (var image = Image.FromFile(path)) { if (image.Width <= 0 || image.Height <= 0) throw new Exception("Некорректная картинка-плашка: " + path); }
            }
        }
        public static async Task<List<VideoTextRegion>> ScanAsync(VideoTranslationProject project, string probe, CancellationToken ct, Action<string> progress) {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                var media = Core.VideoInfo(await Core.Probe(probe, project.Source, ct).ConfigureAwait(false));
                Directory.CreateDirectory(project.Directory);
                string cache = Path.Combine(project.Directory, "video-text.json");
                if (File.Exists(cache)) {
                    var saved = Json().Deserialize<Receipt>(File.ReadAllText(cache));
                    if (saved?.SourceHash == project.SourceHash && saved.Width == media.Width && saved.Height == media.Height && saved.Detection?.version == 1) {
                        ValidateRegions(saved.Detection, project.Duration, media.Width, media.Height);
                        progress?.Invoke("Текст в кадре: использую сохранённое сканирование, областей: " + saved.Detection.regions.Count);
                        return saved.Detection.regions;
                    }
                }
                string scripts = Scripts(), root = Path.Combine(Store.Root, "video-text"), runtimeFile = Path.Combine(root, "runtime.json");
                Runtime runtime = File.Exists(runtimeFile) ? Json().Deserialize<Runtime>(File.ReadAllText(runtimeFile)) : null;
                if (runtime?.version != "1" || !File.Exists(runtime.python)) {
                    progress?.Invoke("Первый запуск: загружаю локальную модель поиска текста…");
                    await LocalAudioTranscription.RunProcess("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(scripts, "prepare.ps1"), "-Root", root }, ct, progress).ConfigureAwait(false);
                    runtime = Json().Deserialize<Runtime>(File.ReadAllText(runtimeFile));
                }
                string job = Path.Combine(project.Directory, "video-text-job.json"), output = Path.Combine(project.Directory, "video-text-detection.json");
                try {
                    if (File.Exists(output)) File.Delete(output);
                    File.WriteAllText(job, Json().Serialize(new { source = project.Source, output, duration = project.Duration, width = media.Width, height = media.Height, ffprobe = probe }), new UTF8Encoding(false));
                    await LocalAudioTranscription.RunProcess(runtime.python, new[] { "-u", Path.Combine(scripts, "worker.py"), "--job", job }, ct, progress).ConfigureAwait(false);
                    var detection = Json().Deserialize<Detection>(File.ReadAllText(output));
                    ValidateRegions(detection, project.Duration, media.Width, media.Height);
                    File.WriteAllText(cache, Json().Serialize(new Receipt { SourceHash = project.SourceHash, Width = media.Width, Height = media.Height, Detection = detection }), new UTF8Encoding(false));
                    progress?.Invoke("Сканирование текста завершено. Областей: " + detection.regions.Count);
                    return detection.regions;
                } finally { if (File.Exists(job)) File.Delete(job); if (File.Exists(output)) File.Delete(output); }
            } finally { gate.Release(); }
        }
        static void ValidateRegions(Detection detection, double duration, int width, int height) {
            if (detection?.version != 1 || detection.regions == null || detection.regions.Any(r => r == null || double.IsNaN(r.Start) || double.IsNaN(r.End) || r.Start < 0 || r.End <= r.Start || r.End > duration + .001 || r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 || (long)r.X + r.Width > width || (long)r.Y + r.Height > height))
                throw new Exception("Сканирование вернуло некорректные координаты или таймкоды текста. Плашки не применены.");
        }
        internal static double OverlayStart(VideoTextRegion region) => Math.Max(0, region.Start - 1);
        internal static string Rasterize(List<VideoTextRegion> regions, IList<string> paths, string folder, int width, int height, double duration, CancellationToken ct) {
            Directory.CreateDirectory(folder);
            var ordered = regions.OrderBy(r => r.Start).ThenBy(r => r.Y).ThenBy(r => r.X).ToList();
            var images = new List<Image>();
            try {
                foreach (string path in paths) images.Add(Image.FromFile(path));
                var events = new SortedDictionary<double, List<Tuple<int, bool>>>();
                Action<double, int, bool> add = (time, index, enabled) => {
                    if (!events.TryGetValue(time, out var list)) events.Add(time, list = new List<Tuple<int, bool>>());
                    list.Add(Tuple.Create(index, enabled));
                };
                for (int i = 0; i < ordered.Count; i++) { add(OverlayStart(ordered[i]), i, true); add(Math.Min(duration, ordered[i].End), i, false); }
                add(0, -1, false); add(duration, -1, false);
                var times = events.Keys.ToList(); var active = new SortedSet<int>();
                string blank = Path.Combine(folder, "blank.png"), track = Path.Combine(folder, "masks.ffconcat");
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb)) bitmap.Save(blank, ImageFormat.Png);
                var text = new StringBuilder("ffconcat version 1.0\n");
                Action<string, double> frame = (path, seconds) => text.Append("file '").Append(Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''")).Append("'\noption framerate 1000\nduration ").Append(Core.N(seconds)).Append('\n');
                for (int n = 0; n + 1 < times.Count; n++) {
                    ct.ThrowIfCancellationRequested();
                    foreach (var change in events[times[n]]) if (change.Item1 >= 0) { if (change.Item2) active.Add(change.Item1); else active.Remove(change.Item1); }
                    string path = blank;
                    if (active.Count > 0) {
                        path = Path.Combine(folder, "mask-" + n + ".png");
                        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb)) {
                            using (var graphics = Graphics.FromImage(bitmap)) foreach (int i in active) {
                                var r = ordered[i]; graphics.DrawImage(images[i % images.Count], new Rectangle(r.X, r.Y, r.Width, r.Height));
                            }
                            bitmap.Save(path, ImageFormat.Png);
                        }
                    }
                    frame(path, times[n + 1] - times[n]);
                }
                frame(blank, .1);
                text.Append("file '").Append(Path.GetFullPath(blank).Replace('\\', '/').Replace("'", "'\\''")).Append("'\noption framerate 1000\n");
                File.WriteAllText(track, text.ToString(), new UTF8Encoding(false)); return track;
            } finally { foreach (var image in images) image.Dispose(); }
        }
        public static async Task ApplyAsync(string source, string output, string ffmpeg, List<VideoTextRegion> regions, IList<string> images, string work, int width, int height, double duration, CancellationToken ct, Action<string> progress) {
            string track = await Task.Run(() => Rasterize(regions, images, work, width, height, duration, ct), ct).ConfigureAwait(false);
            var args = new List<string> { "-v", "error", "-nostdin", "-y", "-filter_complex_threads", "1", "-i", source, "-f", "concat", "-safe", "0", "-i", track,
                "-filter_complex", "[1:v:0]format=rgba[mask];[0:v:0]scale=" + width + ":" + height + ",setsar=1[base];[base][mask]overlay=0:0:eof_action=pass[v]", "-map", "[v]", "-map", "0:a?", "-c:v", "libx264", "-preset", "fast", "-crf", "22", "-pix_fmt", "yuv420p", "-c:a", "copy", "-t", Core.N(duration), "-movflags", "+faststart", output };
            await ProcessingEngine.Accelerate(ffmpeg, args, width, height, ct, progress).ConfigureAwait(false);
            progress?.Invoke("Закрываю найденный текст картинками-плашками…");
            await Core.Tool(ffmpeg, args, ct, null, 7200).ConfigureAwait(false);
        }
    }
}
